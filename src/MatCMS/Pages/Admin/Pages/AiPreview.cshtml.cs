using MatCMS.Content;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Pages;

/// <summary>
/// Renders a set of AI-PROPOSED blocks through the REAL public layout (_Layout) so the operator sees a
/// faithful visual preview — the site's own header, template CSS, fonts and block styling — WITHOUT
/// anything being saved. Loaded into an iframe by the page/site generators via a target-form POST. The
/// blocks are validated exactly like on create (only known types/text fields), so the preview shows
/// what would actually be created. Admin-only (AuthorizeFolder) and READ-ONLY — no state changes, so
/// antiforgery is not required and it needs no AI gate of its own.
/// </summary>
[IgnoreAntiforgeryToken]
public class AiPreviewModel : PageModel
{
    private readonly BlockGenerator _blockGen;

    public AiPreviewModel(BlockGenerator blockGen) => _blockGen = blockGen;

    public List<ContentBlock> Blocks { get; private set; } = new();

    public IActionResult OnPost(string? blocksJson, string? title)
    {
        ViewData["Title"] = string.IsNullOrWhiteSpace(title) ? "Vorschau" : title!.Trim();

        var sort = 0;
        Blocks = _blockGen.ValidateBlocks(blocksJson)
            .Select(v => new ContentBlock { BlockType = v.Type, DataJson = v.Data.ToJsonString(), SortOrder = sort++ })
            .ToList();
        return Page();
    }
}
