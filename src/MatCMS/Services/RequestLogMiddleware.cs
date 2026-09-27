namespace MatCMS.Services;

/// <summary>
/// Records unhandled exceptions and 5xx responses to the LogEntry table for the Admin → Protokoll view.
/// It sits inside the app's exception handler (so the normal error page still renders) and is
/// <b>fail-safe</b>: writing a log entry can never break the request — any failure while logging is
/// swallowed, and a fresh DI scope + DbContext is used so a faulted request context is never touched.
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
            throw; // let the app's exception handler render the error page as before
        }

        if (ctx.Response.StatusCode >= 500)
            await SafeLog(ctx, "Error", $"HTTP {ctx.Response.StatusCode}", null, ctx.Response.StatusCode);
    }

    private async Task SafeLog(HttpContext ctx, string level, string message, string? exception, int status)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
            db.Logs.Add(new MatCMS.Models.LogEntry
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
