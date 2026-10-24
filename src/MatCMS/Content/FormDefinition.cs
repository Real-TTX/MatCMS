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

    /// <summary>
    /// What separates the selected values in the stored string (multi-select only). Default ", ".
    /// <para>Configurable because the stored value is read by whatever consumes the field afterwards:
    /// a list joined with ", " is a different string from one joined with "|", and only the owner of
    /// the field knows which one the other side expects. Reading uses the SAME separator, so a field
    /// still recognises what it wrote.</para>
    /// </summary>
    public string? Separator { get; set; }
    public string? Help { get; set; }
    public bool Required { get; set; }
    public string? Regex { get; set; }

    /// <summary>For date/daterange: allow imprecise entries — shows the "± days" flexibility chips.</summary>
    public bool Flex { get; set; }

    /// <summary>For date/daterange: per-field overrides for the picker's button/label texts
    /// (keys: ok, clear, today, cancel, flexTitle, exact). Empty/missing = use the localized default.
    /// Stored on the element so they are translatable per language version like any other field text.</summary>
    public Dictionary<string, string> Texts { get; set; } = new();

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
        type is "text" or "textarea" or "date" or "daterange" or "number" or "phone" or "email" or "select" or "richselect" or "multiselect";

    /// <summary>Every element type the form builder offers (inputs, plus the heading, the text paragraph and the group).</summary>
    public static readonly string[] Types = ["title", "description", "text", "textarea", "date", "daterange", "number", "phone", "email", "select", "richselect", "multiselect", "group"];

    /// <summary>The types that carry a list of options. multiselect is one of them: the builder edits its options, and a check that did not count it
    /// threw them away on every save (and the submission skipped the field).</summary>
    public static bool HasOptions(string? type) => type is "select" or "richselect" or "multiselect";

    /// <summary>
    /// Cleans one element before it is stored — the builder's save and the AI assistant's form tools go through the same rules: an unknown type
    /// becomes a text field, a condition without a field is dropped, only option types keep options (without empty values), a group holds no group.
    /// </summary>
    public static void Sanitize(FormElement el, Func<string> newId)
    {
        el.Type = Types.Contains(el.Type) ? el.Type : "text";
        if (el.Condition is not null && !el.Condition.IsSet) el.Condition = null;
        if (!HasOptions(el.Type)) el.Options = new();
        else el.Options = el.Options.Where(o => !string.IsNullOrWhiteSpace(o.Value)).ToList();

        if (el.Type == "group")
        {
            foreach (var child in el.Fields)
            {
                if (string.IsNullOrWhiteSpace(child.Id)) child.Id = newId();
                child.Type = Types.Contains(child.Type) && child.Type != "group" ? child.Type : "text";   // no nested groups
                child.Condition = child.Condition is not null && child.Condition.IsSet ? child.Condition : null;
                if (!HasOptions(child.Type)) child.Options = new();
                else child.Options = child.Options.Where(o => !string.IsNullOrWhiteSpace(o.Value)).ToList();
                child.Fields = new();
            }
        }
        else el.Fields = new();
    }

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
