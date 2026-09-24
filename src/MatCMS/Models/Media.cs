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

    /// <summary>Manual ordering for galleries/library (ascending; new uploads get the next value).</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>For an image produced by CROPPING: the id of the original media it was cut from. The crop is
    /// always a NEW file (the original is never touched), and this link lets the editor re-crop from the full
    /// original later. Null for a normal upload.</summary>
    public int? SourceMediaId { get; set; }

    /// <summary>The crop rectangle used, as JSON {"x","y","width","height"} in ORIGINAL pixels — so
    /// "re-crop" can reopen the original with the last selection. Null for a normal upload.</summary>
    public string? CropJson { get; set; }
}
