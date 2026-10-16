using System.Net;
using System.Text.RegularExpressions;
using MatCMS.Plugin;

namespace MatCMS.Services.Plugins;

/// <summary>
/// Fills a template part: <c>{{name}}</c> becomes the value (encoded, unless the placeholder is declared as HTML), <c>{{#if name}}…{{/if}}</c> keeps its inside only when the value is not
/// empty, <c>{{#unless name}}…{{/unless}}</c> only when it is. A name the plugin did not declare — or gave no value — is empty. One pass, nothing inserted is read again, so a value may contain braces.
/// The part is the OPERATOR's own text (a template of the site), the values are the visitor's: only the values are ever encoded.
/// </summary>
internal static class PartRenderer
{
    private static readonly Regex If = new(@"\{\{#(if|unless) ([a-z][a-z0-9_]*)\}\}(.*?)\{\{/\1\}\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Token = new(@"\{\{([a-z][a-z0-9_]*)\}\}", RegexOptions.Compiled);

    public static string Render(string html, IReadOnlyDictionary<string, string> values, IReadOnlyCollection<string> htmlNames)
    {
        if (string.IsNullOrEmpty(html)) return "";
        bool Has(string name) => values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v);
        // Conditions first (nested ones of different kinds work: the outer is resolved, then its kept inside is scanned again by the loop).
        string prev;
        var guard = 0;
        do
        {
            prev = html;
            html = If.Replace(html, m => (m.Groups[1].Value == "if") == Has(m.Groups[2].Value) ? m.Groups[3].Value : "");
        } while (html != prev && guard++ < 8);
        return Token.Replace(html, m =>
        {
            var name = m.Groups[1].Value;
            if (!values.TryGetValue(name, out var v) || v is null) return "";
            return htmlNames.Contains(name) ? v : WebUtility.HtmlEncode(v);
        });
    }
}
