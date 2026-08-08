using System.Globalization;
using System.Threading.RateLimiting;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Services;
using MatCMS.Shared;
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

// Legacy DB rename: earlier versions named the SQLite file "feusys.db". Rename on the way up so
// existing volumes keep working without a manual step. Only runs when the new name doesn't exist
// yet — never overwrites current data. Journals (-shm/-wal/-journal) travel with it.
{
    var oldDb = Path.Combine(dataDir, "feusys.db");
    var newDb = Path.Combine(dataDir, "matcms.db");
    if (!File.Exists(newDb) && File.Exists(oldDb))
    {
        try
        {
            File.Move(oldDb, newDb);
            foreach (var suffix in new[] { "-shm", "-wal", "-journal" })
            {
                var oldSide = oldDb + suffix;
                var newSide = newDb + suffix;
                if (File.Exists(oldSide) && !File.Exists(newSide)) File.Move(oldSide, newSide);
            }
        }
        catch { /* If the rename fails (locked / permissions), EnsureCreated will create a fresh
                   matcms.db and no data is lost — the old file stays put until the operator fixes it. */ }
    }
}

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=appdata/matcms.db";

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

// Apply the per-site DEFAULT (root) content language BEFORE routing/localization are configured
// (both capture Localizer.DefaultCulture at startup). Read directly from the settings table; a missing
// DB/table/row leaves the built-in "de". An env var wins (handy for containers). A change needs a restart.
{
    var configuredDefault = Environment.GetEnvironmentVariable("MATCMS_DEFAULT_LANG");
    if (string.IsNullOrWhiteSpace(configuredDefault))
    {
        try
        {
            var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            using var probe = new AppDbContext(opts);
            configuredDefault = probe.SiteSettings.AsNoTracking()
                .FirstOrDefault(s => s.Key == SettingKeys.DefaultLanguage)?.Value;
        }
        catch { /* fresh DB / not migrated yet → keep the built-in default */ }
    }
    Localizer.SetDefaultCulture(configuredDefault);
}

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
    // Build CultureInfos defensively: under some runtime globalization modes a specific culture may
    // not be creatable — skip those rather than failing startup (the default must always work).
    var cultures = supportedCultures
        .Select(c => { try { return new CultureInfo(c); } catch { return null; } })
        .Where(c => c is not null).Select(c => c!).ToList();
    if (cultures.Count == 0) cultures.Add(new CultureInfo(Localizer.ResourceFallbackCulture));
    // UI default = the resource-authoring language (admin chrome), independent of the site's content
    // root language (Localizer.DefaultCulture) — a cookie / Accept-Language still overrides per request.
    options.DefaultRequestCulture = new RequestCulture(Localizer.ResourceFallbackCulture);
    options.SupportedCultures = cultures;
    options.SupportedUICultures = cultures;
    // Our provider decides first (admin → English default + cookie override; public → content locale
    // from the URL). It always yields a result; cookie/Accept-Language remain as harmless fallbacks.
    options.RequestCultureProviders =
    [
        new MatCMS.Services.MatCmsCultureProvider(),
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
builder.Services.AddScoped<TranslationService>();
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddScoped<PluginRunner>();

// MatCMS.Cloud link: state is a singleton (the admin UI reads the last result), the worker sends an
// outbound heartbeat once a minute. Unconfigured = the service does nothing at all.
builder.Services.AddSingleton<CloudState>();
builder.Services.AddScoped<CloudService>();
builder.Services.AddScoped<CloudSyncService>();
builder.Services.AddScoped<CloudCatalogService>();
builder.Services.AddHostedService<CloudConnectionService>();

// Basic brute-force protection for the login endpoint (per client IP).
// Behind a reverse proxy, enable ForwardedHeaders so the real client IP is used.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // On rejection, report 429 where possible. (A body-bearing POST rejected before its body is read
    // can still surface as 400 from Kestrel; the request is rejected either way, so abuse stays bounded.)
    options.OnRejected = (context, _) =>
    {
        if (!context.HttpContext.Response.HasStarted)
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Anonymous public plugin endpoints (/plugin/{key}) — throttle per client IP so a visitor-facing
    // write endpoint (e.g. review submission) can't be hammered to bloat storage.
    // Cloud-initiated adoption (/api/cloud/link) takes admin credentials, so it is a login endpoint
    // in all but name and gets the same treatment: a tight per-IP budget.
    options.AddPolicy("cloudLink", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("publicPlugin", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
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

// --- Create/upgrade schema + seed default data on startup ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await EnsureSchemaCurrentAsync(db, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
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

// Serve plugin asset files (uploaded JS/CSS libraries etc.) from appdata/plugin-assets at /plugin-assets.
var pluginAssetsDir = MatCMS.Services.StoragePaths.PluginAssets(app.Environment);
Directory.CreateDirectory(pluginAssetsDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(pluginAssetsDir),
    RequestPath = "/plugin-assets",
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

// Remember the address this site is actually reached at. A site with no canonical URL configured
// otherwise has no address to report to MatCMS.Cloud, which then cannot link to it or preview it.
// Cheap: an in-memory string, written only when it changes.
//
// ONLY from an authenticated admin request. Request.Host is whatever the client sent, and with
// AllowedHosts "*" that is anything at all — an anonymous visitor sending "Host: attacker.example"
// would otherwise become the URL the cloud stores, links to, and frames in its admin. Requiring an
// admin session means the value can only be set by someone who is already logged in here.
app.Use(async (ctx, next) =>
{
    if (ctx.User?.Identity?.IsAuthenticated == true)
    {
        var state = ctx.RequestServices.GetRequiredService<MatCMS.Services.CloudState>();
        var seen = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        if (state.ObservedBaseUrl != seen) state.ObservedBaseUrl = seen;
    }
    await next();
});

// --- Maintenance mode -----------------------------------------------------
// When enabled (Settings → Wartung), public visitors get a themed maintenance page (HTTP 503). Admins
// and the admin/login/status/language paths always pass through so the site stays manageable while
// "down". Static assets are already served by UseStaticFiles earlier, so they never reach here.
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path.Value ?? "/";
    var exempt =
        ctx.User?.IsInRole("Admin") == true ||
        p.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith("/login", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith("/logout", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith("/_status", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith("/set-language", StringComparison.OrdinalIgnoreCase);
    if (exempt)
    {
        await next();
        return;
    }

    var site = ctx.RequestServices.GetRequiredService<SiteContext>();
    if (!site.MaintenanceEnabled)
    {
        await next();
        return;
    }

    var t = ctx.RequestServices.GetRequiredService<Localizer>();
    var html = MaintenancePage.Render(site, t);
    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    ctx.Response.Headers["Retry-After"] = "3600";
    // Setting Content-Type here also stops the earlier StatusCodePages re-execute from hijacking the 503.
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.WriteAsync(html);
});

app.MapRazorPages();

// Admin-only preview of the maintenance page (admins bypass the middleware, so this lets them see it).
app.MapGet("/admin/maintenance/preview", (SiteContext site, Localizer t) =>
    Results.Content(MaintenancePage.Render(site, t), "text/html; charset=utf-8"))
    .RequireAuthorization("Admin");

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

// Public plugin endpoints at /plugin/{key} — anonymous, for visitor-facing actions (e.g. submitting
// a review). Antiforgery is intentionally not required (public submission, no authenticated state);
// abuse is bounded by the "publicPlugin" rate limit + each plugin's own validation/caps. The handler
// runs with Form/Query/Method/Path populated; a POST then redirects (PRG) to the form's __return field
// when it is a safe local path, otherwise to "/".
app.MapMethods("/plugin/{key}", new[] { "GET", "POST" }, async (HttpContext ctx, string key, MatCMS.Services.PluginRegistry registry) =>
{
    // Thread-safe read: a plugin re-run (admin save) may be clearing/repopulating PublicPages concurrently.
    if (!registry.TryGetPublicPage(key, out var handler) || handler is null)
        return Results.NotFound();

    var form = new Dictionary<string, string>(StringComparer.Ordinal);
    if (ctx.Request.HasFormContentType)
        foreach (var kv in await ctx.Request.ReadFormAsync())
            form[kv.Key] = kv.Value.ToString();
    var query = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var kv in ctx.Request.Query)
        query[kv.Key] = kv.Value.ToString();

    var pr = new MatCMS.Services.PluginRequest
    {
        Services = ctx.RequestServices,
        Registry = registry,
        Method = ctx.Request.Method,
        Path = ctx.Request.Path.Value ?? "",
        Query = query,
        Form = form
    };

    string html;
    try { html = handler(pr) ?? ""; }
    catch (Exception ex)
    {
        registry.AddLog("Public-Endpoint '" + key + "' Fehler: " + ex.Message);
        html = "";
    }

    if (HttpMethods.IsPost(ctx.Request.Method))
    {
        // Only ever redirect to a local path ("/..." but not "//...") to avoid open-redirect abuse.
        var r = form.TryGetValue("__return", out var rv) ? rv : "";
        var ret = !string.IsNullOrEmpty(r) && r.StartsWith('/') && !r.StartsWith("//")
                  && Uri.IsWellFormedUriString(r, UriKind.Relative) ? r : "/";
        return Results.LocalRedirect(ret);
    }
    return Results.Content(html, "text/html; charset=utf-8");
}).RequireRateLimiting("publicPlugin");

// --- Cloud-initiated adoption ---------------------------------------------
// The ONE inbound call in the cloud link: a MatCMS.Cloud hands over the connection, authenticating
// with an ADMIN ACCOUNT OF THIS INSTANCE. The credentials are verified against our own user table
// exactly like a login, so this cannot be used to take over a site by anyone who isn't already an
// admin here. Anonymous by necessity (there is no cloud session yet), rate-limited like /login.
app.MapPost("/api/cloud/link", async (
    HttpContext ctx, MatCMS.Shared.LinkRequest request,
    MatCMS.Services.AuthService auth, MatCMS.Services.CloudService cloud) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password)
        || string.IsNullOrWhiteSpace(request.CloudUrl) || string.IsNullOrWhiteSpace(request.InstanceId)
        || string.IsNullOrWhiteSpace(request.Token))
        return Results.BadRequest(new { error = "Unvollständige Anfrage." });

    var user = await auth.ValidateAsync(request.Username, request.Password);
    if (user is null || user.Role != "Admin")
        return Results.Unauthorized();

    await cloud.AcceptLinkAsync(request.CloudUrl, request.InstanceId, request.Token, ctx.RequestAborted);

    // Hand back what the cloud needs to label us straight away, so its instance list is populated
    // before our first scheduled heartbeat.
    var site = ctx.RequestServices.GetRequiredService<MatCMS.Services.SiteContext>();
    var version = ctx.RequestServices.GetRequiredService<MatCMS.Services.VersionService>();
    return Results.Ok(new
    {
        siteName = site.SiteName,
        version = version.Current,
        containerId = MatCMS.Services.ContainerIdentity.Current,
        url = site.CanonicalBaseUrl(ctx.Request)
    });
}).RequireRateLimiting("cloudLink");

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
    string[] images = [".png", ".jpg", ".jpeg", ".gif", ".webp"];
    string[] docs = [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".zip"];
    var isImage = images.Contains(ext);
    if (!isImage && !docs.Contains(ext))
        return Results.BadRequest(new { error = "Dateityp nicht erlaubt (Bilder: PNG/JPG/GIF/WEBP · Dateien: PDF, DOC(X), XLS(X), PPT(X), TXT, CSV, ZIP)." });
    // Images stay small; documents may be larger.
    var maxMb = isImage ? 8 : 25;
    if (file.Length > maxMb * 1024 * 1024)
        return Results.BadRequest(new { error = $"Datei zu groß (max. {maxMb} MB)." });

    var uploads = MatCMS.Services.StoragePaths.Uploads(env);
    Directory.CreateDirectory(uploads);
    var name = $"{Guid.NewGuid():N}{ext}";
    await using (var stream = File.Create(Path.Combine(uploads, name)))
        await file.CopyToAsync(stream);

    var url = $"/uploads/{name}";
    // Record it in the media library, appended after existing media (highest SortOrder).
    var nextOrder = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
        .MaxAsync(db.Media, m => (int?)m.SortOrder) ?? 0) + 1;
    db.Media.Add(new MatCMS.Models.Media
    {
        Url = url,
        FileName = file.FileName,
        ContentType = file.ContentType ?? "",
        SizeBytes = file.Length,
        SortOrder = nextOrder
    });
    await db.SaveChangesAsync();

    return Results.Ok(new { url });
}).RequireAuthorization("Admin");

// Media library listing (admin only) — used by the image picker.
app.MapGet("/admin/api/media", async (MatCMS.Data.AppDbContext db) =>
{
    var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
        db.Media.AsNoTracking().OrderByDescending(m => m.Id)
            .Select(m => new { url = m.Url, name = m.FileName, tags = m.Tags }));
    return Results.Ok(items);
}).RequireAuthorization("Admin");

// Published pages (admin only) — used by the internal link picker for URL fields.
app.MapGet("/admin/api/pages", async (MatCMS.Data.AppDbContext db) =>
{
    var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
        db.Pages.AsNoTracking().Where(p => p.IsPublished)
            .OrderBy(p => p.Locale).ThenBy(p => p.Title)
            .Select(p => new { title = p.Title, url = p.Slug == "home" ? "/" : "/" + p.Slug, locale = p.Locale }));
    return Results.Ok(items);
}).RequireAuthorization("Admin");

// --- SEO: XML sitemap + robots.txt (both served only when "sitemap.enabled" is on) ---
app.MapGet("/sitemap.xml", async (HttpContext ctx, MatCMS.Data.AppDbContext db, MatCMS.Services.SiteContext site) =>
{
    if (!site.SitemapEnabled) return Results.NotFound();

    // Only the site's ACTIVE content languages (admin setting) — so the sitemap never advertises a
    // /{locale}/… URL for a language that isn't turned on.
    var supported = site.ActiveLocales.ToHashSet(StringComparer.OrdinalIgnoreCase);

    // The pages assigned as the 404 / server-error page must never be indexed — drop them.
    var errorSlugs = (await db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == MatCMS.Services.SettingKeys.NotFoundPage
                     || s.Key == MatCMS.Services.SettingKeys.ErrorPage)
            .Select(s => s.Value).ToListAsync())
        .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var pages = (await db.Pages.AsNoTracking()
        .Where(p => p.IsPublished && supported.Contains(p.Locale))
        .OrderBy(p => p.Locale).ThenBy(p => p.NavOrder).ThenBy(p => p.Title)
        .Select(p => new { p.Slug, p.Locale, p.UpdatedAt })
        .ToListAsync())
        .Where(p => !errorSlugs.Contains(p.Slug))
        .ToList();

    var baseUrl = site.CanonicalBaseUrl(ctx.Request);
    var sb = new System.Text.StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    sb.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
    foreach (var p in pages)
    {
        var loc = baseUrl + MatCMS.Services.SiteContext.LocalizedUrl(p.Locale, p.Slug);
        sb.Append("  <url><loc>").Append(System.Security.SecurityElement.Escape(loc)).Append("</loc>")
          .Append("<lastmod>").Append(p.UpdatedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append("</lastmod></url>\n");
    }
    sb.Append("</urlset>\n");
    return Results.Content(sb.ToString(), "application/xml", System.Text.Encoding.UTF8);
});

app.MapGet("/robots.txt", (HttpContext ctx, MatCMS.Services.SiteContext site) =>
{
    if (!site.SitemapEnabled) return Results.NotFound();
    var body = $"User-agent: *\nDisallow: /admin/\n\nSitemap: {site.CanonicalBaseUrl(ctx.Request)}/sitemap.xml\n";
    return Results.Text(body, "text/plain", System.Text.Encoding.UTF8);
});

app.Run();

/// <summary>
/// Brings the schema up to date with EF migrations — including databases created by the earlier
/// <c>EnsureCreated()</c>, which have no <c>__EFMigrationsHistory</c> table at all.
/// <para>Those are <b>baselined</b>: the history table is created and the initial migration is
/// recorded as applied WITHOUT running it, because the tables it would create are already there.
/// Running it would fail on "table already exists" and leave a live site down. Only migrations added
/// after the switch then actually execute.</para>
/// <para>This is what ends the old "a model change needs <c>docker compose down -v</c>" rule, which
/// on a CMS meant throwing away the customer's content to add a column.</para>
/// </summary>
static async Task EnsureSchemaCurrentAsync(AppDbContext db, ILoggerFactory loggerFactory)
{
    var log = loggerFactory.CreateLogger("Schema");
    var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();

    if (applied.Count == 0)
    {
        // "Has tables" distinguishes a pre-migrations database from a genuinely empty one. Only the
        // former needs the baseline; the latter is created by Migrate() below in the normal way.
        var creator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>(db.Database);
        if (await creator.HasTablesAsync())
        {
            var initial = db.Database.GetMigrations().FirstOrDefault();
            if (initial is not null)
            {
                var history = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository>(db.Database);
                await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(
                    new Microsoft.EntityFrameworkCore.Migrations.HistoryRow(
                        initial, Microsoft.EntityFrameworkCore.Infrastructure.ProductInfo.GetVersion())));
                log.LogInformation("Existing database baselined at migration {Migration}.", initial);
            }
        }
    }

    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
        log.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, string.Join(", ", pending));

    await db.Database.MigrateAsync();
}
