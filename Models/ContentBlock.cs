namespace MatCMS.Models;

public class ContentBlock
{
    public int Id { get; set; }

    public int PageId { get; set; }
    public Page? Page { get; set; }

    /// <summary>Block type key, e.g. "hero", "servicegrid". Maps to a <see cref="Content.BlockDefinition"/>.</summary>
    public string BlockType { get; set; } = "";

    public int SortOrder { get; set; }

    /// <summary>Field values serialized as a JSON object.</summary>
    public string DataJson { get; set; } = "{}";
}
