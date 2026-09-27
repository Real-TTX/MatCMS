namespace MatCMS.Cloud.Services;

/// <summary>
/// Records unhandled exceptions and 5xx responses to the LogEntry table for the Admin → Protokoll view.
/// Sits inside the app's exception handler (the normal error page still renders) and is <b>fail-safe</b>:
/// any failure while logging is swallowed, and a fresh DI scope + DbContext is used so a faulted request
/// context is never touched.
/// </summary>
public class RequestLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServiceScopeFactory _scopes;
    public RequestLogMiddleware(RequestDelegate next, IServiceScopeFactory scopes) { _next = next; _scopes = scopes; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (Exception ex)
        {
            await SafeLog(ctx, "Error", ex.GetType().Name + ": " + ex.Message, ex.ToString(), 500);
            throw;
        }

        if (ctx.Response.StatusCode >= 500)
            await SafeLog(ctx, "Error", $"HTTP {ctx.Response.StatusCode}", null, ctx.Response.StatusCode);
    }

    private async Task SafeLog(HttpContext ctx, string level, string message, string? exception, int status)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MatCMS.Cloud.Data.AppDbContext>();
            db.Logs.Add(new MatCMS.Cloud.Models.LogEntry
            {
                Level = level,
                Message = Trim(message, 1000) ?? "",
                Category = "request",
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
