using System.Text.RegularExpressions;

namespace MatCMS.Content;

/// <summary>Curated Google-Fonts choices and small helpers for the Template editor.</summary>
public static class TemplateFonts
{
    /// <summary>Curated list of Google Fonts offered in the Template editor.</summary>
    public static readonly string[] Families =
    [
        "Geologica", "Inter", "Poppins", "Roboto",
        "Montserrat", "Lato", "Open Sans", "Nunito"
    ];

    /// <summary>Return the value if it is in the curated list, otherwise the fallback.</summary>
    public static string Coerce(string? value, string fallback)
    {
        var v = (value ?? "").Trim();
        return Families.Contains(v) ? v : fallback;
    }

    /// <summary>Validate/normalize a hex color; fall back to the FeuSys accent on garbage.</summary>
    public static string NormalizeColor(string? value)
    {
        var v = (value ?? "").Trim();
        return Regex.IsMatch(v, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$") ? v.ToLowerInvariant() : "#de7e11";
    }
}
