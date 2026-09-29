using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Cleanup;

/// <summary>
/// Fleet-wide "Aufräumen" overview. The cloud can't reach into a site, so this drives the same content-op
/// channel the MCP tools use: it enqueues a <c>cleanup.analyze</c> op per instance (read-only), the site
/// answers on its next heartbeat, and the result (unused media/components/plugins) lands in
/// <see cref="ContentOp.ResultJson"/> — which this page parses to show counts. The actual deletion is done
/// per instance on the detail page (with a two-step confirm), so a blanket "delete everything everywhere"
/// is deliberately not one click.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly InstanceService _instances;
    public IndexModel(AppDbContext db, InstanceService instances) { _db = db; _instances = instances; }

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public record Row(string PublicId, string Name, bool Approved, string State,
        DateTime? AnalyzedAt, int Media, long MediaBytes, int Comps, int Plugins, bool ApplyPending, string? Detail);
    public List<Row> Rows { get; private set; } = new();
    public int TotalMedia { get; private set; }

    // The report the instance serialises back (PascalCase by default on the instance side).
    public record RepMedia(int Id, string? Url, string? FileName, long SizeBytes);
    public record RepItem(string? Type, string? Key, string? Name);
    public record Report(List<RepMedia>? UnusedMedia, long UnusedMediaBytes, List<RepItem>? UnusedComponents, List<RepItem>? Plugins);

    public async Task OnGetAsync()
    {
        var insts = await _db.Instances.AsNoTracking().OrderBy(i => i.Name).ToListAsync();
        foreach (var i in insts)
        {
            var analyze = await _db.ContentOps.AsNoTracking()
                .Where(o => o.InstanceId == i.Id && o.Kind == "cleanup.analyze")
                .OrderByDescending(o => o.Id).FirstOrDefaultAsync();
            var applyPending = await _db.ContentOps.AsNoTracking()
                .AnyAsync(o => o.InstanceId == i.Id && o.Kind == "cleanup.apply" && o.DoneAt == null);
            var approved = i.Status == InstanceStatus.Approved;

            string state = "none"; DateTime? at = null; int media = 0, comps = 0, plugins = 0; long bytes = 0; string? detail = null;
            if (analyze is not null)
            {
                if (analyze.DoneAt == null) state = "pending";
                else if (analyze.Outcome == "failed") { state = "failed"; detail = analyze.Detail; at = analyze.DoneAt; }
                else
                {
                    state = "done"; at = analyze.DoneAt;
                    try
                    {
                        var r = JsonSerializer.Deserialize<Report>(analyze.ResultJson ?? "{}", Json);
                        media = r?.UnusedMedia?.Count ?? 0; bytes = r?.UnusedMediaBytes ?? 0;
                        comps = r?.UnusedComponents?.Count ?? 0; plugins = r?.Plugins?.Count ?? 0;
                    }
                    catch { state = "failed"; detail = "Ergebnis nicht lesbar."; }
                }
            }
            TotalMedia += media;
            Rows.Add(new Row(i.PublicId, i.Name, approved, state, at, media, bytes, comps, plugins, applyPending, detail));
        }
    }

    /// <summary>Enqueue a fresh analysis for every APPROVED instance. Read-only; results arrive on each
    /// site's next heartbeat.</summary>
    public async Task<IActionResult> OnPostAnalyzeAllAsync()
    {
        var insts = await _db.Instances.Where(i => i.Status == InstanceStatus.Approved).ToListAsync();
        foreach (var i in insts)
            await _instances.EnqueueContentOpAsync(i, "cleanup.analyze", "{}", overwrite: false, reason: "Cleanup-Analyse (Cloud)");
        TempData["Flash"] = $"Analyse für {insts.Count} Instanzen angefragt – Ergebnisse kommen beim nächsten Kontakt (~1 Min.).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAnalyzeOneAsync(string publicId)
    {
        var i = await _db.Instances.FirstOrDefaultAsync(x => x.PublicId == publicId);
        if (i is not null)
        {
            await _instances.EnqueueContentOpAsync(i, "cleanup.analyze", "{}", overwrite: false, reason: "Cleanup-Analyse (Cloud)");
            TempData["Flash"] = "Analyse angefragt – Ergebnis beim nächsten Kontakt.";
        }
        return RedirectToPage();
    }
}
