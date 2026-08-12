namespace MatCMS.Content;

/// <summary>Definition of a single editable field within a block (Shopify-style setting).</summary>
public class BlockField
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; } = FieldType.Text;
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
    public string? Default { get; set; }

    /// <summary>Options for <see cref="FieldType.Select"/>.</summary>
    public List<SelectOption> Options { get; set; } = new();

    /// <summary>Sub-fields for a repeatable <see cref="FieldType.List"/>.</summary>
    public List<BlockField> ItemFields { get; set; } = new();

    /// <summary>Label for a single list item in the editor, e.g. "Leistung".</summary>
    public string ItemLabel { get; set; } = "Eintrag";

    /// <summary>
    /// For <see cref="FieldType.Select"/>: a dynamic option source resolved at edit time
    /// instead of the static <see cref="Options"/> list. Currently supported: "forms".
    /// </summary>
    public string? OptionsSource { get; set; }

    /// <summary>Conditional visibility: only show this field when another field (<see cref="ShowWhenField"/>)
    /// currently equals <see cref="ShowWhenValue"/>. Null = always shown.</summary>
    public string? ShowWhenField { get; set; }
    public string? ShowWhenValue { get; set; }
}

public class SelectOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";

    public SelectOption() { }
    public SelectOption(string value, string label) { Value = value; Label = label; }
}
