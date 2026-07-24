using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MatCMS.Content;
using MatCMS.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Blocks;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly BlockRegistry _registry;

    public EditModel(AppDbContext db, BlockRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    public BlockDefinition Definition { get; private set; } = default!;
    public int PageId { get; private set; }
    public string PageTitle { get; private set; } = "";
    public string SchemaJson { get; private set; } = "[]";
    public string CurrentJson { get; private set; } = "{}";

    [BindProperty] public string DataJson { get; set; } = "{}";

    private static readonly JsonSerializerOptions SchemaOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var block = await _db.ContentBlocks.Include(b => b.Page).FirstOrDefaultAsync(b => b.Id == id);
        if (block is null) return NotFound();

        var def = _registry.Get(block.BlockType);
        if (def is null) return NotFound();

        Definition = def;
        PageId = block.PageId;
        PageTitle = block.Page?.Title ?? "";
        SchemaJson = JsonSerializer.Serialize(def.Fields, SchemaOpts);
        CurrentJson = string.IsNullOrWhiteSpace(block.DataJson) ? "{}" : block.DataJson;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var block = await _db.ContentBlocks.Include(b => b.Page).FirstOrDefaultAsync(b => b.Id == id);
        if (block is null) return NotFound();

        var json = string.IsNullOrWhiteSpace(DataJson) ? "{}" : DataJson;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject)
                throw new FormatException("Blockdaten müssen ein JSON-Objekt sein.");
        }
        catch
        {
            TempData["FlashError"] = "Die Blockdaten konnten nicht gespeichert werden (ungültiges Format).";
            return RedirectToPage(new { id });
        }

        block.DataJson = json;
        if (block.Page is not null) block.Page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Block gespeichert.";
        return RedirectToPage("/Admin/Pages/Edit", new { id = block.PageId });
    }
}
