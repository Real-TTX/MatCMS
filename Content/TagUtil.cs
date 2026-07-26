namespace MatCMS.Content;

/// <summary>Helpers for comma-separated tag strings (media library + gallery filtering).</summary>
public static class TagUtil
{
    public static IEnumerable<string> Split(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Normalize(string? input) =>
        string.Join(", ", Split(input).Distinct(StringComparer.OrdinalIgnoreCase));
}
