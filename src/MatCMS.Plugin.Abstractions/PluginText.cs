using System.Text;

namespace MatCMS.Plugin;

/// <summary>Small text helpers every plugin needs the same way as the CMS (pure functions, no host behind them — API 2.1).</summary>
public static class PluginText
{
    /// <summary>The address-safe form of a text, exactly as the CMS makes page slugs: lower case a–z and 0–9, ä/ö/ü/ß written out,
    /// space, "-", "_" and "/" become one "-", everything else is dropped; no "-" at either end.</summary>
    public static string Slugify(string? input)
    {
        var text = (input ?? "").Trim().ToLowerInvariant();
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch == 'ä') sb.Append("ae");
            else if (ch == 'ö') sb.Append("oe");
            else if (ch == 'ü') sb.Append("ue");
            else if (ch == 'ß') sb.Append("ss");
            else if (ch is ' ' or '-' or '_' or '/') sb.Append('-');
        }
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
