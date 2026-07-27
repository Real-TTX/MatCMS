using System.Globalization;
using System.Threading.RateLimiting;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Localization ---------------------------------------------------------
// Supported cultures (UI language + public content locales). Adding a language = drop
// Resources/<culture>.json and add the code to Localizer.SupportedCultures — the switcher, cookie
// provider, content routing and Localizer all pick it up automatically.
string[] supportedCultures = Localizer.SupportedCultures;

// --- Storage locations (persisted via Docker volume at /app/appdata) ---
// NOTE: folder is "appdata" (not "data") to avoid clashing with the source "Data/" folder
// in .dockerignore on case-insensitive (Windows) build hosts.
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "appdata");
Directory.CreateDirectory(dataDir);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=appdata/feusys.db";

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

// Persist data-protection keys so auth cookies survive container restarts.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("MatCMS");

// --- Authentication: cookie based, login only via /login ---
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.Name = "matcms.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireRole("Admin"));
});

builder.Services.AddLocalization();
builder.Services.AddSingleton<Localizer>();

// Let Razor emit non-ASCII characters (umlauts, en-dash, ellipsis) literally instead of as HTML
// numeric entities. The entity form is fine in HTML body text but shows up raw when a localized
// string is placed into a <script> and assigned to element.textContent (e.g. the update check).
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(options =>
    options.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var cultures = supportedCultures.Select(c => new CultureInfo(c)).ToList();
    options.DefaultRequestCulture = new RequestCulture(Localizer.DefaultCulture);
    options.SupportedCultures = cultures;
    options.SupportedUICultures = cultures;
    // Cookie first (explicit user choice), then Accept-Language header.
    options.RequestCultureProviders =
    [
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider()
    ];
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<BlockRegistry>();
builder.Services.AddScoped<SiteContext>();
builder.Services.AddScoped<ContentTransferService>();
builder.Services.AddScoped<BackupManager>();
builder.Services.AddHostedService<BackupSchedulerService>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<VersionService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddScoped<PluginRunner>();

// Basic brute-force protection for the login endpoint (per client IP).
// Behind a reverse proxy, enable ForwardedHeaders so the real client IP is used.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddRazorPages(options =>
{
    // Everything under /Admin requires the Admin role.
    options.Conventions.AuthorizeFolder("/Admin", "Admin");

    // Multilingual content routing: the default locale (de) keeps its existing root URLs
    // (handled by the page's own "/{slug?}" route). Every non-default locale gets a second
    // route on the same View page: "/{culture}/{slug?}" (e.g. /en, /en/about), constrained to
    // the supported non-default cultures so it never shadows the default slug route.
    if (Localizer.NonDefaultCultures.Count > 0)
    {
        var pattern = string.Join("|", Localizer.NonDefaultCultures
            .Select(System.Text.RegularExpressions.Regex.Escape));
        options.Conventions.AddPageRoute("/View", $"{{culture:regex(^({pattern})$)}}/{{slug?}}");
    }
});

var app = builder.Build();

// --- Create schema + seed default data on startup ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    await DbSeeder.SeedAsync(scope.ServiceProvider);
    // Run enabled plugins once at startup so their registrations are available.
    await scope.ServiceProvider.GetRequiredService<PluginRunner>().RunAllAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff"
});

// Serve uploaded media from the persisted data volume (appdata/uploads) at /uploads, so a single
// volume mounted at /app/appdata holds everything. One-time migration from the legacy wwwroot/uploads.
var uploadsDir = MatCMS.Services.StoragePaths.Uploads(app.Environment);
Directory.CreateDirectory(uploadsDir);
var legacyUploads = Path.Combine(app.Environment.WebRootPath, "uploads");
if (Directory.Exists(legacyUploads) && !string.Equals(legacyUploads, uploadsDir, StringComparison.OrdinalIgnoreCase))
{
    foreach (var src in Directory.GetFiles(legacyUploads))
    {
        var dest = Path.Combine(uploadsDir, Path.GetFileName(src));
        if (!File.Exists(dest))
            try { File.Copy(src, dest); } catch { /* best-effort migration */ }
    }
}
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsDir),
    RequestPath = "/uploads",
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff"
});

// Set CultureInfo.Current(UI)Culture per request (cookie / Accept-Language / default "de").
app.UseRequestLocalization(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>().Value);

// 404 / server-error → admin-assigned page (Settings → Fehlerhandling) or a built-in fallback.
app.UseStatusCodePagesWithReExecute("/_status", "?code={0}");

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// Language switcher: sets the culture cookie and redirects back to a safe, local URL.
// Values arrive as form fields (posted by the switcher), read them directly to avoid
// minimal-API form-binding/antiforgery coupling.
app.MapPost("/set-language", async (HttpContext ctx) =>
{
    var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync() : null;
    var culture = form?["culture"].ToString() ?? ctx.Request.Query["culture"].ToString();
    var returnUrl = form?["returnUrl"].ToString() ?? ctx.Request.Query["returnUrl"].ToString();

    if (!string.IsNullOrEmpty(culture) && supportedCultures.Contains(culture))
    {
        ctx.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax });
    }

    // Only ever redirect to a local path ("/..." but not "//...") to avoid open-redirect abuse.
    var target = !string.IsNullOrEmpty(returnUrl)
                 && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
                 && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
        ? returnUrl
        : "/";
    return Results.LocalRedirect(target);
});

// Simple image upload endpoint used by the block editor / settings / media library (admin only).
app.MapPost("/admin/api/upload", async (HttpRequest request, IWebHostEnvironment env, MatCMS.Data.AppDbContext db) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Ungültige Anfrage." });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Keine Datei erhalten." });

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    // SVG is intentionally excluded: it can carry active content (stored XSS on direct navigation).
    string[] allowed = [".png", ".jpg", ".jpeg", ".gif", ".webp"];
    if (!allowed.Contains(ext))
        return Results.BadRequest(new { error = "Dateityp nicht erlaubt (erlaubt: PNG, JPG, GIF, WEBP)." });
    if (file.Length > 8 * 1024 * 1024)
        return Results.BadRequest(new { error = "Datei zu groß (max. 8 MB)." });

    var uploads = MatCMS.Services.StoragePaths.Uploads(env);
    Directory.CreateDirectory(uploads);
    var name = $"{Guid.NewGuid():N}{ext}";
    await using (var stream = File.Create(Path.Combine(uploads, name)))
        await file.CopyToAsync(stream);

    var url = $"/uploads/{name}";
    // Record it in the media library.
    db.Media.Add(new MatCMS.Models.Media
    {
        Url = url,
        FileName = file.FileName,
        ContentType = file.ContentType ?? "",
        SizeBytes = file.Length
    });
    await db.SaveChangesAsync();

    return Results.Ok(new { url });
}).RequireAuthorization("Admin");

// Media library listing (admin only) — used by the image picker.
app.MapGet("/admin/api/media", async (MatCMS.Data.AppDbContext db) =>
{
    var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
        db.Media.AsNoTracking().OrderByDescending(m => m.Id)
            .Select(m => new { url = m.Url, name = m.FileName }));
    return Results.Ok(items);
}).RequireAuthorization("Admin");

app.Run();
