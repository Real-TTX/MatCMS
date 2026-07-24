namespace MatCMS.Content;

/// <summary>Definition of a single editable field within a block (Shopify-style setting).</summary>
public class BlockField
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; } = FieldType.Text;
    public string? Placeholder { get; set; }
    public string? Help { get; set; }
    public string? Default { get; set; }

    /// <summary>Options for <see cref="FieldType.Select"/>.</summary>
    public List<SelectOption> Options { get; set; } = new();

    /// <summary>Sub-fields for a repeatable <see cref="FieldType.List"/>.</summary>
    public List<BlockField> ItemFields { get; set; } = new();

    /// <summary>Label for a single list item in the editor, e.g. "Leistung".</summary>
    public string ItemLabel { get; set; } = "Eintrag";
}

public class SelectOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";

    public SelectOption() { }
    public SelectOption(string value, string label) { Value = value; Label = label; }
}
