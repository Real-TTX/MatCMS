namespace MatCMS.Models;

public class ContentBlock
{
    public int Id { get; set; }

    public int PageId { get; set; }
    public Page? Page { get; set; }

    /// <summary>Parent block for nested (container/child) blocks; null for top-level blocks.</summary>
    public int? ParentId { get; set; }
    public ContentBlock? Parent { get; set; }
    public List<ContentBlock> Children { get; set; } = new();

    /// <summary>Block type key, e.g. "hero", "servicegrid". Maps to a <see cref="Content.BlockDefinition"/>.</summary>
    public string BlockType { get; set; } = "";

    public int SortOrder { get; set; }

    /// <summary>Field values serialized as a JSON object.</summary>
    public string DataJson { get; set; } = "{}";
}
