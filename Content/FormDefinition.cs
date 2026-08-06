using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatCMS.Content;

/// <summary>One element inside a form definition. Element types:
/// title | description | text | date | number | phone | email | select | group.
/// Groups may contain child <see cref="Fields"/> (one level of nesting only).</summary>
public class FormElement
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "text";
    public string Label { get; set; } = "";
    public string? Placeholder { get; set; }
    public string? Help { get; set; }
    public bool Required { get; set; }
    public string? Regex { get; set; }
    public FormCondition? Condition { get; set; }

    /// <summary>Options for a <c>select</c> element.</summary>
    public List<FormOption> Options { get; set; } = new();

    /// <summary>Child elements for a <c>group</c> element.</summary>
    public List<FormElement> Fields { get; set; } = new();
}

/// <summary>Optional visibility rule: show the element only when the referenced
/// field satisfies the operator (eq | neq | contains | filled).</summary>
public class FormCondition
{
    public string? Field { get; set; }
    public string Op { get; set; } = "eq";
    public string? Value { get; set; }

    [JsonIgnore] public bool IsSet => !string.IsNullOrWhiteSpace(Field);
}

public class FormOption
{
    public string Value { get; set; } = "";   // the stored key (e.g. "Apartment 1")
    public string Label { get; set; } = "";   // the visible title (required)

    // Extras for the rich-select control ("richselect"): image, description, small tags (e.g. "45 m²").
    public string? Image { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
}

/// <summary>Serialization helpers + validation logic shared by the public block,
/// the builder preview and the submission handler.</summary>
public static class FormDefinition
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Element types that render an actual input and hold a value.</summary>
    public static bool IsInput(string? type) =>
        type is "text" or "textarea" or "date" or "daterange" or "number" or "phone" or "email" or "select" or "richselect";

    public static List<FormElement> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<FormElement>>(json, Opts) ?? new(); }
        catch { return new(); }
    }

    public static string Serialize(IEnumerable<FormElement> elements) =>
        JsonSerializer.Serialize(elements, Opts);

    /// <summary>Yields top-level elements and the children of any group, in order.</summary>
    public static IEnumerable<FormElement> Flatten(IEnumerable<FormElement> elements)
    {
        foreach (var e in elements)
        {
            yield return e;
            if (e.Type == "group")
                foreach (var c in e.Fields)
                    yield return c;
        }
    }

    /// <summary>Evaluates whether an element is currently visible given the posted values.</summary>
    public static bool IsActive(FormElement el, IReadOnlyDictionary<string, string> values)
    {
        var c = el.Condition;
        if (c is null || !c.IsSet) return true;
        values.TryGetValue(c.Field!, out var src);
        src ??= "";
        var target = c.Value ?? "";
        return c.Op switch
        {
            "neq" => !string.Equals(src, target, StringComparison.OrdinalIgnoreCase),
            "contains" => src.Contains(target, StringComparison.OrdinalIgnoreCase),
            "filled" => !string.IsNullOrWhiteSpace(src),
            _ => string.Equals(src, target, StringComparison.OrdinalIgnoreCase) // "eq"
        };
    }
}
