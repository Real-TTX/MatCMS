namespace MatCMS.Models;

/// <summary>A file in the media library (a record of an upload under wwwroot/uploads).</summary>
public class Media
{
    public int Id { get; set; }

    /// <summary>Public URL, e.g. "/uploads/ab12….png".</summary>
    public string Url { get; set; } = "";

    /// <summary>Original file name (for display).</summary>
    public string FileName { get; set; } = "";

    public string? Alt { get; set; }

    /// <summary>Comma-separated tags for filtering (e.g. "produkt, team").</summary>
    public string Tags { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long SizeBytes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
