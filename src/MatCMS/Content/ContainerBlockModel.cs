namespace MatCMS.Content;

/// <summary>Model handed to a container block's partial: its own data plus its rendered children.</summary>
public sealed class ContainerBlockModel
{
    public BlockData Data { get; }
    public IReadOnlyList<ChildBlock> Children { get; }

    public ContainerBlockModel(BlockData data, IReadOnlyList<ChildBlock> children)
    {
        Data = data;
        Children = children;
    }

    /// <summary>A nested child block: which partial renders it and its field data. When the child is
    /// itself a container, <see cref="Container"/> carries its resolved sub-model (for recursive
    /// rendering, e.g. a cards block inside a section); it is null for ordinary leaf children.</summary>
    public sealed record ChildBlock(string Partial, BlockData Data, ContainerBlockModel? Container = null);
}
