using System.Threading.RateLimiting;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<BlockRegistry>();
builder.Services.AddScoped<SiteContext>();
builder.Services.AddScoped<ContentTransferService>();

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
});

var app = builder.Build();

// --- Create schema + seed default data on startup ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
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
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// Simple image upload endpoint used by the block editor / settings (admin only).
app.MapPost("/admin/api/upload", async (HttpRequest request, IWebHostEnvironment env) =>
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

    var uploads = Path.Combine(env.WebRootPath, "uploads");
    Directory.CreateDirectory(uploads);
    var name = $"{Guid.NewGuid():N}{ext}";
    await using (var stream = File.Create(Path.Combine(uploads, name)))
        await file.CopyToAsync(stream);

    return Results.Ok(new { url = $"/uploads/{name}" });
}).RequireAuthorization("Admin");

app.Run();
