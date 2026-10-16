using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MatCMS.Content;

/// <summary>
/// The variables of a page that is drawn with the data of a request (a plugin's product page, a customer's order): <c>{{product.title}}</c> in any text of a block becomes the value,
/// <c>{{#if product.on_sale}}…{{/if}}</c> keeps its inside only when the variable has a value, <c>{{#unless …}}</c> only when it has none, and a block can be shown only while a variable has one
/// (<see cref="Visible"/>).
/// <para><b>Encoding follows the field.</b> A plain field (text, textarea, link, image) receives the value as it is — the block's own markup encodes it where it is drawn, so a name with an
/// ampersand shows as an ampersand. A field that is drawn as HTML (rich text, the HTML block) receives it encoded, so a value can never bring markup into the page — except a variable the plugin
/// declared as HTML (<see cref="HtmlNames"/>: its gallery, its form), which is what such a field is for.</para>
/// <para>Only names with a dot are variables (<c>role.name</c>); a component's own <c>{{field}}</c> and anything else in a text is left alone. An empty scope changes nothing, so a page without one
/// costs nothing.</para>
/// </summary>
public sealed class BlockScope
{
    public static readonly BlockScope Empty = new(new Dictionary<string, string>(), Array.Empty<string>());

    private static readonly Regex If = new(@"\{\{#(if|unless) (!?)([a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+)\}\}(.*?)\{\{/\1\}\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Token = new(@"\{\{([a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+)\}\}", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Out = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public BlockScope(IReadOnlyDictionary<string, string> values, IEnumerable<string> htmlNames)
    {
        Values = values;
        HtmlNames = htmlNames.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Name → value, with the role in front (<c>product.title</c>).</summary>
    public IReadOnlyDictionary<string, string> Values { get; }
    /// <summary>The names whose values are HTML the plugin built.</summary>
    public IReadOnlySet<string> HtmlNames { get; }
    public bool IsEmpty => Values.Count == 0;

    /// <summary>A scope from a plugin's values: every name gets the role in front, <paramref name="extra"/> (already complete names such as <c>site.name</c>) is added as it is.</summary>
    public static BlockScope Of(string role, IReadOnlyDictionary<string, string> values, IEnumerable<string>? htmlNames, IReadOnlyDictionary<string, string>? extra = null)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        if (extra is not null) foreach (var kv in extra) all[kv.Key] = kv.Value;
        foreach (var kv in values) all[role + "." + kv.Key] = kv.Value ?? "";
        return new BlockScope(all, (htmlNames ?? Array.Empty<string>()).Select(n => role + "." + n));
    }

    /// <summary>True when the variable has a value that counts: not empty, not "0".</summary>
    public bool Has(string name) => Values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) && v != "0";

    /// <summary>Fills the variables of <paramref name="text"/>; <paramref name="html"/> says that the field is drawn as HTML (so a text value is encoded).</summary>
    public string Resolve(string text, bool html)
    {
        if (IsEmpty || string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal)) return text;
        string prev;
        var guard = 0;
        do
        {
            prev = text;
            text = If.Replace(text, m => (m.Groups[1].Value == "if") == (Has(m.Groups[3].Value) != (m.Groups[2].Value == "!")) ? m.Groups[4].Value : "");
        } while (text != prev && guard++ < 8);
        return Token.Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (!Values.TryGetValue(name, out var v) || v is null) return "";
            return html && !HtmlNames.Contains(name) ? WebUtility.HtmlEncode(v) : v;
        });
    }

    /// <summary>
    /// "Show only when": space- or comma-separated variable names, each optionally with a leading <c>!</c> (shown when it has NO value), braces allowed (<c>{{product.on_sale}}</c>). Every one must hold.
    /// An empty condition always holds; an unknown name has no value.
    /// </summary>
    public bool Visible(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        foreach (var raw in condition.Split(new[] { ' ', ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim('{', '}');
            var negate = t.StartsWith('!');
            if (negate) t = t[1..];
            if (t.Length == 0) continue;
            if (Has(t) == negate) return false;
        }
        return true;
    }

    /// <summary>
    /// The block's data with every variable in its text fields filled — by the schema, so the encoding follows the field's kind. Fields the schema does not know are left as they are.
    /// </summary>
    public string Apply(BlockDefinition def, string dataJson)
    {
        if (IsEmpty || string.IsNullOrEmpty(dataJson) || !dataJson.Contains("{{", StringComparison.Ordinal)) return dataJson;
        JsonObject? obj;
        try { obj = JsonNode.Parse(dataJson) as JsonObject; }
        catch (JsonException) { return dataJson; }
        if (obj is null) return dataJson;
        foreach (var f in def.Fields) ApplyField(obj, f);
        return obj.ToJsonString(Out);
    }

    private void ApplyField(JsonObject obj, BlockField f)
    {
        switch (f.Type)
        {
            case FieldType.Select or FieldType.MultiSelect:
                return;                                                  // a choice holds a value of the list, never text with variables
            case FieldType.List:
                if (obj[f.Id] is JsonArray arr)
                    foreach (var item in arr)
                        if (item is JsonObject o)
                            foreach (var sub in f.ItemFields) ApplyField(o, sub);
                return;
            default:
                if (obj[f.Id] is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("{{", StringComparison.Ordinal))
                    obj[f.Id] = Resolve(s, f.Type == FieldType.RichText || f.RawHtml);
                return;
        }
    }
}
