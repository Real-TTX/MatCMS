using System.Globalization;
using System.Threading.RateLimiting;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Storage locations (persisted via Docker volume at /app/appdata) ---
// NOTE: folder is "appdata" (not "data") to avoid clashing with the source "Data/" folder
// in .dockerignore on case-insensitive (Windows) build hosts.
var dataDir = StoragePaths.Root(builder.Environment);
Directory.CreateDirectory(dataDir);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=appdata/matcmscloud.db";

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

// Persist data-protection keys so auth cookies survive container restarts.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("MatCMS.Cloud");

// --- Authentication: cookie based, login only via /login ---
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.Name = "matcmscloud.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireRole("Admin"));
});

// --- Localization ---------------------------------------------------------
// Only ONE culture axis here (the admin UI language) — this app has no public content.
// Adding a language = drop Resources/<culture>.json and add the code to Localizer.SupportedCultures.
builder.Services.AddLocalization();
builder.Services.AddSingleton<Localizer>();

// Let Razor emit non-ASCII characters (umlauts, en-dash, ellipsis) literally instead of as HTML
// numeric entities — the entity form shows up raw when a localized string is assigned to
// element.textContent from a <script>.
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(options =>
    options.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    // Build CultureInfos defensively: under some runtime globalization modes a specific culture may
    // not be creatable — skip those rather than failing startup (the default must always work).
    var cultures = Localizer.SupportedCultures
        .Select(c => { try { return new CultureInfo(c); } catch { return null; } })
        .Where(c => c is not null).Select(c => c!).ToList();
    if (cultures.Count == 0) cultures.Add(new CultureInfo(Localizer.FallbackCulture));
    options.DefaultRequestCulture = new RequestCulture(Localizer.FallbackCulture);
    options.SupportedCultures = cultures;
    options.SupportedUICultures = cultures;
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CloudContext>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<InstanceService>();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddScoped<AdoptionService>();
builder.Services.AddScoped<VersionService>();

// Singletons: one registry poll and one Docker client for the whole process.
builder.Services.AddSingleton<GhcrClient>();
builder.Services.AddSingleton<ReleaseWatcher>();
builder.Services.AddSingleton<DockerHostService>();
builder.Services.AddHostedService<ReleaseWatcherService>();
builder.Services.AddHostedService<InstanceMonitorService>();

// Basic brute-force protection for the login endpoint (per client IP).
// Behind a reverse proxy, enable ForwardedHeaders so the real client IP is used.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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

    // The instance API is token-authenticated but anonymous at the transport level, so it gets its
    // own generous per-IP budget: a 60s heartbeat needs ~1/min, several instances may share one NAT.
    options.AddPolicy("instanceApi", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddRazorPages(options =>
{
    // Everything under /Admin requires the Admin role.
    options.Conventions.AuthorizeFolder("/Admin", "Admin");
});

var app = builder.Build();

// --- Bring the schema up to date + seed default data on startup ---
// EF MIGRATIONS, deliberately unlike MatCMS. The cloud stores instance links: a schema change that
// drops the database costs every connected site its token and forces a re-enrol. That happened four
// times while this was built on EnsureCreated — with real customer sites it is not an option.
// Adding a table now means `dotnet ef migrations add <Name>` and nothing else.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await DbSeeder.SeedAsync(scope.ServiceProvider);
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

app.UseRequestLocalization(app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>().Value);

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// --- Instance API ---------------------------------------------------------
// Anonymous at the transport level; every call is authenticated by the instance token. The instance
// always calls US, so a site behind NAT needs no inbound port.

// Enrollment. The instance presents a profile's join code and receives its id + token. No
// authentication yet by definition — the join code IS the credential, which is why an unknown code
// is refused outright instead of creating a record.
app.MapPost("/api/instances/register", async (
    HttpContext ctx, RegisterRequest request, InstanceService instances) =>
{
    var result = await instances.RegisterAsync(request, ctx.RequestAborted);
    if (result.Instance is null || result.Token is null)
        return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status401Unauthorized);

    return Results.Ok(new RegisterResponse
    {
        InstanceId = result.Instance.PublicId,
        Token = result.Token,
        Status = result.Instance.Status.ToString(),
        ProfileName = result.Instance.Profile?.Name,
        DisplayName = result.Instance.Name
    });
}).RequireRateLimiting("instanceApi");

app.MapPost("/api/instances/{publicId}/heartbeat", async (
    HttpContext ctx, string publicId, HeartbeatRequest beat, InstanceService instances) =>
{
    var token = ctx.Request.Headers[InstanceProtocol.TokenHeader].ToString();
    var instance = await instances.AuthenticateAsync(publicId, token);
    if (instance is null) return Results.Unauthorized();
    // A rejected instance is told so explicitly rather than being left to time out, so it can stop
    // beating and show its operator why.
    if (instance.Status == MatCMS.Cloud.Models.InstanceStatus.Rejected)
        return Results.Json(new { error = "Diese Instanz wurde abgelehnt." }, statusCode: StatusCodes.Status403Forbidden);

    var response = await instances.RecordHeartbeatAsync(instance, beat, ctx.RequestAborted);
    return Results.Ok(response);
}).RequireRateLimiting("instanceApi");

// The configuration an approved instance applies. Pending/rejected instances get nothing — that is
// the whole point of the approval gate.
app.MapGet("/api/instances/{publicId}/config", async (
    HttpContext ctx, string publicId, InstanceService instances, ProfileService profiles) =>
{
    var token = ctx.Request.Headers[InstanceProtocol.TokenHeader].ToString();
    var instance = await instances.AuthenticateAsync(publicId, token);
    if (instance is null) return Results.Unauthorized();
    if (instance.Status != MatCMS.Cloud.Models.InstanceStatus.Approved)
        return Results.Json(new { error = "Instanz ist nicht freigegeben." }, statusCode: StatusCodes.Status403Forbidden);
    if (instance.Profile is null)
        return Results.Ok(new InstanceConfig { Revision = 0 });

    return Results.Ok(await profiles.BuildConfigAsync(instance.Profile, ctx.RequestAborted));
}).RequireRateLimiting("instanceApi");

// One plugin bundle, fetched only when the instance's installed version differs. Kept out of the
// config payload so a profile with many plugins doesn't make every sync a multi-megabyte download.
app.MapGet("/api/instances/{publicId}/plugin/{key}", async (
    HttpContext ctx, string publicId, string key, InstanceService instances, AppDbContext db) =>
{
    var token = ctx.Request.Headers[InstanceProtocol.TokenHeader].ToString();
    var instance = await instances.AuthenticateAsync(publicId, token);
    if (instance is null) return Results.Unauthorized();
    if (instance.Status != MatCMS.Cloud.Models.InstanceStatus.Approved || instance.Profile is null)
        return Results.Json(new { error = "Instanz ist nicht freigegeben." }, statusCode: StatusCodes.Status403Forbidden);

    // Same precedence as the config itself: the profile's own plugin overrides the store entry it
    // selected, so an instance never gets a different bundle than the config promised.
    var bundle = (await db.ProfilePlugins.AsNoTracking()
        .FirstOrDefaultAsync(p => p.ProfileId == instance.Profile.Id && p.Key == key))?.Bundle;

    bundle ??= (await db.ProfileStorePlugins.AsNoTracking()
        .Where(x => x.ProfileId == instance.Profile.Id && x.StorePlugin!.Key == key)
        .Select(x => x.StorePlugin!)
        .FirstOrDefaultAsync())?.Bundle;

    if (bundle is null || bundle.Length == 0) return Results.NotFound();

    return Results.File(bundle, "application/zip", $"{key}.zip");
}).RequireRateLimiting("instanceApi");

// A deliberate disconnect on the instance side: mark it offline NOW instead of waiting out the
// 150s dead-man timeout, and suppress the offline mail (this outage is intentional).
app.MapPost("/api/instances/{publicId}/disconnect", async (
    HttpContext ctx, string publicId, InstanceService instances, AppDbContext db) =>
{
    var token = ctx.Request.Headers[InstanceProtocol.TokenHeader].ToString();
    var instance = await instances.AuthenticateAsync(publicId, token);
    if (instance is null) return Results.Unauthorized();

    instance.LastHeartbeatUtc = null;
    instance.OfflineNotified = true;
    instances.Log(instance, MatCMS.Cloud.Models.InstanceEventKind.Offline,
        "Verbindung von der Instanz getrennt.", notified: true);
    await db.SaveChangesAsync();
    return Results.Ok();
}).RequireRateLimiting("instanceApi");

// Language switcher: sets the culture cookie and redirects back to a safe, local URL.
app.MapPost("/set-language", async (HttpContext ctx) =>
{
    var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync() : null;
    var culture = form?["culture"].ToString() ?? ctx.Request.Query["culture"].ToString();
    var returnUrl = form?["returnUrl"].ToString() ?? ctx.Request.Query["returnUrl"].ToString();

    if (!string.IsNullOrEmpty(culture) && Localizer.SupportedCultures.Contains(culture))
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
        : "/admin";
    return Results.LocalRedirect(target);
});

// This app is admin-only — there is no public front end to land on.
app.MapGet("/", () => Results.Redirect("/admin"));

app.Run();
