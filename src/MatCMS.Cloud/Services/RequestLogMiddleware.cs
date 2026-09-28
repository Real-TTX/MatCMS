using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Records log entries to the LogEntry table for the Admin → Protokoll view. Always logs unhandled
/// exceptions and 5xx responses (category "request"); when <see cref="SettingKeys.LogRequests"/> is on
/// it additionally logs EVERY HTTP request (category "webrequest", opt-in because of the volume). Sits
/// inside the app's exception handler (the normal error page still renders) and is <b>fail-safe</b>:
/// any failure while logging is swallowed, and a fresh DI scope + DbContext is used so a faulted request
/// context is never touched.
/// </summary>
public class RequestLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServiceScopeFactory _scopes;
    private readonly IMemoryCache _cache;
    public RequestLogMiddleware(RequestDelegate next, IServiceScopeFactory scopes, IMemoryCache cache)
    {
        _next = next; _scopes = scopes; _cache = cache;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (Exception ex)
        {
            await SafeLog(ctx, "Error", "request", ex.GetType().Name + ": " + ex.Message, ex.ToString(), 500);
            throw;
        }

        var status = ctx.Response.StatusCode;
        if (status >= 500)
            await SafeLog(ctx, "Error", "request", $"HTTP {status}", null, status);
        else if (await RequestLoggingOnAsync() && ShouldLogRequest(ctx))
            await SafeLog(ctx, status >= 400 ? "Warning" : "Info", "webrequest", $"HTTP {status}", null, status);
    }

    /// <summary>Is the opt-in full request log on? Cached ~30 s so it costs one DB read per half-minute,
    /// not one per request.</summary>
    private async Task<bool> RequestLoggingOnAsync()
    {
        if (_cache.TryGetValue("log.requests.on", out bool on)) return on;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MatCMS.Cloud.Data.AppDbContext>();
            var v = await db.CloudSettings.AsNoTracking()
                .Where(s => s.Key == SettingKeys.LogRequests).Select(s => s.Value).FirstOrDefaultAsync();
            on = v == "on";
        }
        catch { on = false; }
        _cache.Set("log.requests.on", on, TimeSpan.FromSeconds(30));
        return on;
    }

    /// <summary>Skip the noise: static assets and the admin log page itself.</summary>
    private static bool ShouldLogRequest(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/img/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/admin/logs", StringComparison.OrdinalIgnoreCase))
            return false;
        var lastSlash = path.LastIndexOf('/');
        var last = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        return !last.Contains('.');
    }

    private async Task SafeLog(HttpContext ctx, string level, string category, string message, string? exception, int status)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MatCMS.Cloud.Data.AppDbContext>();
            db.Logs.Add(new MatCMS.Cloud.Models.LogEntry
            {
                Level = level,
                Message = Trim(message, 1000) ?? "",
                Category = category,
                Exception = Trim(exception, 8000),
                Path = Trim(ctx.Request.Path + ctx.Request.QueryString, 500),
                Method = ctx.Request.Method,
                StatusCode = status
            });
            await db.SaveChangesAsync();
        }
        catch { /* logging must never break the request */ }
    }

    private static string? Trim(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[..max]);
}
