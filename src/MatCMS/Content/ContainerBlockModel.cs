namespace MatCMS.Content;

/// <summary>Model handed to a container block's partial: its own data plus its rendered children.</summary>
public sealed class ContainerBlockModel
{
    public BlockData Data { get; }
    public IReadOnlyList<ChildBlock> Children { get; }

    /// <summary>Rendered for the editor preview: children carry a mark (data-block-id) so they can be
    /// clicked and highlighted one by one, not only the block they sit in.</summary>
    public bool Editor { get; init; }

    /// <summary>Nesting depth: 0 for a container standing at the top of the page. A layout container
    /// uses it to decide whether it has to bring its own section and page width (top level) or sits
    /// inside a section that already did (nested).</summary>
    public int Depth { get; init; }

    public ContainerBlockModel(BlockData data, IReadOnlyList<ChildBlock> children)
    {
        Data = data;
        Children = children;
    }

    /// <summary>A nested child block: which partial renders it and its field data. When the child is
    /// itself a container, <see cref="Container"/> carries its resolved sub-model (for recursive
    /// rendering, e.g. a cards block inside a section); it is null for ordinary leaf children.
    /// Render it through the shared <c>_ChildBlock</c> partial, which adds the editor mark.</summary>
    /// <param name="Rendered">The HTML of a plugin's block, drawn already (it has no partial); null for every other block.</param>
    /// <param name="HasVars">In the editor: the block's texts hold variables, so it is not typed into in the preview.</param>
    public sealed record ChildBlock(string Partial, BlockData Data, ContainerBlockModel? Container = null, int Id = 0, bool Editor = false, string Type = "", string? Rendered = null, bool HasVars = false);
}
