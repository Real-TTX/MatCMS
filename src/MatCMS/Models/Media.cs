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

    /// <summary>The edit that made this picture from its original, as the image editor's own JSON (crop rectangle in ORIGINAL
    /// pixels, rotation, flip, target width) — so "Bearbeiten" reopens the original with the last settings. Older rows hold
    /// only {"x","y","width","height"}, which reads the same. Null for a picture that was never edited.</summary>
    public string? CropJson { get; set; }

    /// <summary>Focal point in PERCENT (0–100) — the spot that must stay visible when the image is
    /// cropped to fit a box (object-fit: cover / a background). Null = centre (50/50). Set once per
    /// image in the media library and honoured everywhere the image is shown cropped (hero, cards,
    /// gallery, image+text …), so a face/subject is never cut off by the crop.</summary>
    public int? FocalX { get; set; }
    public int? FocalY { get; set; }

    /// <summary>For an image EDITED IN PLACE ("Übernehmen" in the image editor): the untouched original, kept as its own file
    /// (<c>/uploads/…</c>, no library row of its own). The edit always starts from it again — a third crop of a crop of a crop
    /// would lose quality and area every time — and "Auf Original zurücksetzen" copies it back. Null = never edited in place.
    /// The media keeps its <see cref="Url"/>, so every page that shows the picture shows the edit.</summary>
    public string? OriginalUrl { get; set; }

    /// <summary>Pixel size of the picture (the viewBox for an SVG), for the library's info and for the editor. Null = unknown
    /// (an upload from before it was recorded; filled the first time the library sees it).</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
}
