using System.Text.Json;

namespace MatCMS.Shared;

/// <summary>
/// Reading the JSON blobs the editors export. Every import path needs the same two things: parse
/// without throwing at the operator, and look properties up regardless of whether the producer wrote
/// <c>Name</c> or <c>name</c> — MatCMS's exports are PascalCase, hand-written JSON usually is not,
/// and refusing one of them would be arbitrary.
/// <para>It lives here rather than in either application because both ends read the SAME payloads:
/// a template exported on a site is imported into a cloud profile, and a component built in a
/// profile is imported back onto a site. Two readers would eventually disagree about what the
/// format allows.</para>
/// </summary>
public static class JsonImport
{
    /// <summary>Parses a document. Returns null on anything malformed rather than throwing, so the
    /// caller can answer with a flash message instead of a 500.</summary>
    public static JsonDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); }
        catch { return null; }
    }

    /// <summary>
    /// Whether the document CONTAINS the property at all — the question every reader below cannot
    /// answer, because each of them folds "absent" and "present but unreadable" into the same
    /// fallback. That is fine for a fresh record and wrong for an update: an importer that treats a
    /// field the document never mentioned as "set it to the default" empties columns nobody asked it
    /// to touch. Ask this first wherever the import may land on an EXISTING row.
    /// </summary>
    public static bool Has(JsonElement root, string property)
    {
        foreach (var candidate in Candidates(property))
            if (root.TryGetProperty(candidate, out _)) return true;
        return false;
    }

    /// <summary>A string property, matched case-insensitively on the first character.</summary>
    public static string Text(JsonElement root, string property, string fallback = "")
    {
        foreach (var candidate in Candidates(property))
            if (root.TryGetProperty(candidate, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? fallback;
        return fallback;
    }

    /// <summary>An integer property.</summary>
    public static int Int(JsonElement root, string property, int fallback)
    {
        foreach (var candidate in Candidates(property))
            if (root.TryGetProperty(candidate, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var i))
                return i;
        return fallback;
    }

    /// <summary>A property kept as RAW JSON — for the nested blobs (field definitions, parameters)
    /// that are stored as a string on our side but may arrive as a real array or object.</summary>
    public static string Raw(JsonElement root, string property, string fallback)
    {
        foreach (var candidate in Candidates(property))
            if (root.TryGetProperty(candidate, out var v))
                return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback) : v.GetRawText();
        return fallback;
    }

    private static IEnumerable<string> Candidates(string property)
    {
        yield return property;
        if (property.Length > 0) yield return char.ToLowerInvariant(property[0]) + property[1..];
    }
}
