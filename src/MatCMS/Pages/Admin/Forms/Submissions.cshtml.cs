using System.Text.Json;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Forms;

public class SubmissionsModel : PageModel
{
    private readonly AppDbContext _db;
    public SubmissionsModel(AppDbContext db) => _db = db;

    public Form Current { get; private set; } = default!;
    public List<Row> Items { get; private set; } = new();

    public record Field(string Label, string Value);
    public record Row(int Id, DateTime CreatedAt, bool IsRead, List<Field> Fields);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var form = await _db.Forms.FindAsync(id);
        if (form is null) return NotFound();
        Current = form;

        var subs = await _db.FormSubmissions
            .Where(s => s.FormId == id)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        Items = subs.Select(s => new Row(s.Id, s.CreatedAt, s.IsRead, ParseFields(s.DataJson))).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostToggleReadAsync(int id, int subId)
    {
        var s = await _db.FormSubmissions.FirstOrDefaultAsync(x => x.Id == subId && x.FormId == id);
        if (s is not null)
        {
            s.IsRead = !s.IsRead;
            await _db.SaveChangesAsync();
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, int subId)
    {
        var s = await _db.FormSubmissions.FirstOrDefaultAsync(x => x.Id == subId && x.FormId == id);
        if (s is not null)
        {
            _db.FormSubmissions.Remove(s);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Eintrag gelöscht.";
        }
        return RedirectToPage(new { id });
    }

    private static List<Field> ParseFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var raw = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json);
            if (raw is null) return new();
            return raw.Select(d =>
                new Field(
                    d.TryGetValue("label", out var l) ? l : (d.TryGetValue("id", out var i) ? i : ""),
                    d.TryGetValue("value", out var v) ? v : "")).ToList();
        }
        catch { return new(); }
    }
}
