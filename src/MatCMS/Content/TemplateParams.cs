using System.Text.Json;
using System.Text.RegularExpressions;
using MatCMS.Models;

namespace MatCMS.Content;

/// <summary>One designer-published template parameter (a control the user can tune).</summary>
public sealed class TemplateParam
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>text | select | color | number | bool</summary>
    public string Type { get; set; } = "text";
    /// <summary>For <c>select</c>: comma-separated options.</summary>
    public string Options { get; set; } = "";
    public string Default { get; set; } = "";

    public List<string> OptionList() =>
        Options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>
/// Template parameters: the designer publishes a schema (<see cref="Template.ParametersJson"/>); the
/// user sets values (<see cref="Template.ParamValuesJson"/>). Both feed <c>{{param:id}}</c> placeholders
/// in the template's CustomCss / LayoutHtml.
/// </summary>
public static class TemplateParams
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex Ref = new(@"\{\{param:([a-zA-Z0-9_-]+)\}\}", RegexOptions.Compiled);

    public static List<TemplateParam> Schema(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<TemplateParam>>(json, Opts) ?? new(); }
        catch { return new(); }
    }

    public static Dictionary<string, string> Values(string? json)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return d;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? (p.Value.GetString() ?? "") : p.Value.ToString();
        }
        catch { /* ignore */ }
        return d;
    }

    /// <summary>Each declared parameter's user value, falling back to its default.</summary>
    public static Dictionary<string, string> Resolve(Template tpl)
    {
        var values = Values(tpl.ParamValuesJson);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in Schema(tpl.ParametersJson))
            if (!string.IsNullOrWhiteSpace(p.Id))
                d[p.Id] = values.TryGetValue(p.Id, out var v) && !string.IsNullOrEmpty(v) ? v : (p.Default ?? "");
        return d;
    }

    /// <summary>Replaces {{param:id}} with resolved values. <paramref name="css"/> strips angle brackets
    /// from values so a value can't break out of a &lt;style&gt; block.</summary>
    public static string Apply(string? text, IReadOnlyDictionary<string, string> resolved, bool css = false)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return Ref.Replace(text, m =>
        {
            if (!resolved.TryGetValue(m.Groups[1].Value, out var v) || v is null) return "";
            return css ? v.Replace("<", "").Replace(">", "") : v;
        });
    }
}
