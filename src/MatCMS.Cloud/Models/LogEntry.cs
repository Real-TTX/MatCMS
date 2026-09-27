namespace MatCMS.Cloud.Models;

/// <summary>A recorded log/error event, shown under Admin → Protokoll. Written fail-safe by
/// <see cref="MatCMS.Cloud.Services.RequestLogMiddleware"/> (unhandled exceptions and 5xx responses)
/// and prunable so the table cannot grow without bound.</summary>
public class LogEntry
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>"Error" | "Warning" | "Info".</summary>
    public string Level { get; set; } = "Error";

    public string Message { get; set; } = "";
    public string? Category { get; set; }
    public string? Exception { get; set; }
    public string? Path { get; set; }
    public string? Method { get; set; }
    public int? StatusCode { get; set; }
}
