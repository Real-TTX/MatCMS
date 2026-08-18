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

    /// <summary>
    /// Length limit for the code fields that are INLINED into every public page render: the template's
    /// <c>CustomCss</c> goes into a <c>&lt;style&gt;</c> and <c>CustomJs</c> into a <c>&lt;script&gt;</c>
    /// on every request (<c>Pages/Shared/_Layout.cshtml</c>). That — page weight, not storage — is the
    /// whole reason a limit exists here. It is NOT a transport limit: the form carries half a million
    /// characters without complaining, and only ASP.NET's own ~4-MiB form-value limit refuses (measured:
    /// 500 000 characters → 302, five million → 400). Nor is it SQLite, which stores a gigabyte per cell.
    /// <para>Whoever raises it is deciding how much CSS every visitor downloads on every page.</para>
    /// </summary>
    public const int MaxInlineCode = 20000;

    /// <summary>Length limit for layout HTML — <c>LayoutHtml</c> and the per-page-type parts. Higher than
    /// <see cref="MaxInlineCode"/> because markup is bulkier per idea than a stylesheet, and it replaces
    /// the page body instead of adding to it.</summary>
    public const int MaxLayoutHtml = 50000;

    /// <summary>Trim advanced CSS/JS/HTML and normalize newlines to LF.
    /// LF-normalization matters because browsers submit textarea content with CRLF: without it, a value
    /// could never compare equal to an LF-only default constant (e.g. the template's default page part).
    /// <para><b>This deliberately does not cap any more.</b> It used to return <c>v[..max]</c>, which
    /// turned a 30 000-character stylesheet into 20 000 characters ending mid-declaration and reported
    /// "Template gespeichert." — the operator found out from the broken site. Silent truncation is never
    /// the right answer to "too long": the caller checks the length against
    /// <see cref="MaxInlineCode"/> / <see cref="MaxLayoutHtml"/> and says so instead.</para></summary>
    public static string Code(string? value) =>
        (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();
}
