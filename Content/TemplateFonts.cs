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

    /// <summary>Validate/normalize a hex color; fall back to <paramref name="fallback"/> on garbage.</summary>
    public static string NormalizeColorOr(string? value, string fallback)
    {
        var v = (value ?? "").Trim();
        return Regex.IsMatch(v, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$") ? v.ToLowerInvariant() : fallback;
    }

    /// <summary>Optional hex color: empty stays empty, garbage becomes empty, valid is normalized.</summary>
    public static string OptionalColor(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return "";
        return Regex.IsMatch(v, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$") ? v.ToLowerInvariant() : "";
    }

    /// <summary>Digits-only integer (clamped); returns <paramref name="fallback"/> on garbage.</summary>
    public static string Int(string? value, string fallback, int min, int max)
    {
        var v = (value ?? "").Trim();
        if (int.TryParse(v, out var n)) return Math.Clamp(n, min, max).ToString();
        return fallback;
    }

    /// <summary>Trim advanced CSS/JS/HTML, normalize newlines to LF, and cap length to keep pages sane.
    /// LF-normalization matters because browsers submit textarea content with CRLF: without it, a value
    /// could never compare equal to an LF-only default constant (e.g. the template's default page part).</summary>
    public static string Code(string? value, int max = 20000)
    {
        var v = (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        return v.Length > max ? v[..max] : v;
    }
}
