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

    /// <summary>A nested child block: which partial renders it and its field data.</summary>
    public sealed record ChildBlock(string Partial, BlockData Data);
}
