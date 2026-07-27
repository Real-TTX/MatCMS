using MatCMS.Models;
using MatCMS.Services;

namespace MatCMS.Content;

/// <summary>
/// Model for the shared <c>_BlockList</c> partial, which renders a page's top-level blocks (and their
/// container children). Used both by the public page (<c>View.cshtml</c>, DB rows) and by the editor's
/// live-preview endpoint (in-memory draft rows), so there is a single block-render code path.
/// </summary>
public sealed record BlockListModel(IReadOnlyList<ContentBlock> Blocks, BlockRegistry Registry, bool Editor);
