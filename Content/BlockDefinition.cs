namespace MatCMS.Content;

public class BlockDefinition
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Inner SVG markup (paths/rects) for a 0 0 24 24 stroke icon.</summary>
    public string Svg { get; set; } = "";

    /// <summary>Razor partial used to render this block (under Pages/Shared/Blocks).</summary>
    public string Partial { get; set; } = "";

    public List<BlockField> Fields { get; set; } = new();
}
