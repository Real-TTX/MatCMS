using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace MatCMS.Content;

/// <summary>
/// The icon of a card or timeline step. A Tabler name ("beach", also "ti-beach") renders as the icon — the
/// public layout loads the Tabler font; anything else is shown as typed, so pages that were built with a
/// character or emoji (the field's earlier meaning) keep looking exactly as before.
/// </summary>
public static partial class BlockIcon
{
    [GeneratedRegex("^(?:ti-)?([a-z0-9]+(?:-[a-z0-9]+)*)$")]
    private static partial Regex TablerName();

    public static IHtmlContent Render(string? value)
    {
        var v = (value ?? "").Trim();
        // Single characters stay text: "1", "A" or "x" were typed as the symbol itself, not as an icon name.
        var m = TablerName().Match(v.ToLowerInvariant());
        return m.Success && m.Groups[1].Value.Length > 1 && m.Groups[1].Value.Any(char.IsLetter)
            ? new HtmlString($"<i class=\"ti ti-{m.Groups[1].Value}\"></i>")
            : new HtmlString(System.Net.WebUtility.HtmlEncode(v));
    }
}
