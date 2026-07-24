using System.Text.RegularExpressions;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages.Admin.Pages;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;

    public EditModel(AppDbContext db, BlockRegistry registry)
    {
        _db = db;
        Registry = registry;
    }

    public BlockRegistry Registry { get; }
    public PageEntity Current { get; private set; } = default!;

    [BindProperty] public PageMetaInput Meta { get; set; } = new();

    public class PageMetaInput
    {
        public string Title { get; set; } = "";
        public string Slug { get; set; } = "";
        public string? MetaDescription { get; set; }
        public bool IsPublished { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var page = await Load(id);
        if (page is null) return NotFound();

        Current = page;
        Meta = new PageMetaInput
        {
            Title = page.Title,
            Slug = page.Slug,
            MetaDescription = page.MetaDescription,
            IsPublished = page.IsPublished
        };
        return Page();
    }

    public async Task<IActionResult> OnPostMetaAsync(int id)
    {
        var page = await _db.Pages.FindAsync(id);
        if (page is null) return NotFound();

        var slug = IndexModel.Slugify(string.IsNullOrWhiteSpace(Meta.Slug) ? Meta.Title : Meta.Slug);
        if (string.IsNullOrWhiteSpace(Meta.Title) || string.IsNullOrWhiteSpace(slug))
        {
            TempData["FlashError"] = "Titel und Slug dürfen nicht leer sein.";
            return RedirectToPage(new { id });
        }
        if (IndexModel.IsReserved(slug))
        {
            TempData["FlashError"] = $"Der Slug „{slug}“ ist reserviert und kann nicht verwendet werden.";
            return RedirectToPage(new { id });
        }
        if (await _db.Pages.AnyAsync(p => p.Slug == slug && p.Id != id))
        {
            TempData["FlashError"] = $"Der Slug „{slug}“ ist bereits vergeben.";
            return RedirectToPage(new { id });
        }

        page.Title = Meta.Title.Trim();
        page.Slug = slug;
        page.MetaDescription = Meta.MetaDescription;
        page.IsPublished = Meta.IsPublished;
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Seiteneinstellungen gespeichert.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddBlockAsync(int id, string type)
    {
        var def = Registry.Get(type);
        var page = await _db.Pages.Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);
        if (page is null || def is null) return NotFound();

        var order = page.Blocks.Count == 0 ? 0 : page.Blocks.Max(b => b.SortOrder) + 1;
        var block = new ContentBlock { PageId = id, BlockType = def.Type, SortOrder = order, DataJson = "{}" };
        _db.ContentBlocks.Add(block);
        await _db.SaveChangesAsync();

        return RedirectToPage("/Admin/Blocks/Edit", new { id = block.Id });
    }

    // New order arrives as a sequence of block ids (drag & drop in the editor).
    public async Task<IActionResult> OnPostReorderAsync(int id, int[] order)
    {
        var page = await _db.Pages.Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);
        if (page is null) return NotFound();

        if (order is { Length: > 0 })
        {
            var pos = 0;
            foreach (var blockId in order)
            {
                var block = page.Blocks.FirstOrDefault(b => b.Id == blockId);
                if (block is not null) block.SortOrder = pos++;
            }
            await _db.SaveChangesAsync();
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteBlockAsync(int id, int blockId)
    {
        var block = await _db.ContentBlocks.FirstOrDefaultAsync(b => b.Id == blockId && b.PageId == id);
        if (block is not null)
        {
            _db.ContentBlocks.Remove(block);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Block gelöscht.";
        }
        return RedirectToPage(new { id });
    }

    public string BlockSummary(ContentBlock b)
    {
        var data = new BlockData(b.DataJson);
        foreach (var key in new[] { "heading", "title", "text", "body", "subheading" })
        {
            var v = data.Str(key);
            if (!string.IsNullOrWhiteSpace(v))
                return Truncate(StripHtml(v), 90);
        }
        return "(leer)";
    }

    private static string StripHtml(string s) =>
        Regex.Replace(s, "<.*?>", " ").Replace("\n", " ").Replace("  ", " ").Trim();

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : string.Concat(s.AsSpan(0, n).TrimEnd(), "…");

    private Task<PageEntity?> Load(int id) =>
        _db.Pages.Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);
}
