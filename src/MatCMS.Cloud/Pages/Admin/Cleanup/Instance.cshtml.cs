using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Cleanup;

/// <summary>
/// Per-instance cleanup: shows the unused media/components/plugins the site reported (from the latest
/// <c>cleanup.analyze</c> op) and lets the operator delete a selection — in two steps (pick → confirm the
/// exact list → apply), mirroring the CMS's own Aufräumen page. Applying enqueues a <c>cleanup.apply</c>
/// op; the site re-checks "still unused" and (if its profile has "Backup vor KI-Änderung") backs up first.
/// </summary>
public class InstanceModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly InstanceService _instances;
    public InstanceModel(AppDbContext db, InstanceService instances) { _db = db; _instances = instances; }

    public record RMedia(int Id, string? Url, string? FileName, long SizeBytes);
    public record RComp(string? Type, string? Name);
    public record RPlug(string? Key, string? Name, bool Enabled);
    public record RReport(List<RMedia>? UnusedMedia, long UnusedMediaBytes, List<RComp>? UnusedComponents, List<RPlug>? Plugins);

    public string PublicId { get; private set; } = "";
    public string InstanceName { get; private set; } = "";
    public bool HasAnalysis { get; private set; }
    public DateTime? AnalyzedAt { get; private set; }
    public bool ApplyPending { get; private set; }
    public List<RMedia> Media { get; private set; } = new();
    public long MediaBytes { get; private set; }
    public List<RComp> Comps { get; private set; } = new();
    public List<RPlug> Plugins { get; private set; } = new();

    // Confirm step
    public bool Confirming { get; private set; }
    public List<RMedia> SelMedia { get; private set; } = new();
    public List<RComp> SelComps { get; private set; } = new();
    public List<RPlug> SelPlugins { get; private set; } = new();

    private async Task<Instance?> LoadAsync(string publicId)
    {
        var inst = await _db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.PublicId == publicId);
        if (inst is null) return null;
        PublicId = inst.PublicId; InstanceName = inst.Name;
        ApplyPending = await _db.ContentOps.AsNoTracking().AnyAsync(o => o.InstanceId == inst.Id && o.Kind == "cleanup.apply" && o.DoneAt == null);

        var op = await _db.ContentOps.AsNoTracking()
            .Where(o => o.InstanceId == inst.Id && o.Kind == "cleanup.analyze" && o.DoneAt != null && o.Outcome != "failed")
            .OrderByDescending(o => o.Id).FirstOrDefaultAsync();
        if (op?.ResultJson is not null)
        {
            HasAnalysis = true; AnalyzedAt = op.DoneAt;
            try
            {
                var r = JsonSerializer.Deserialize<RReport>(op.ResultJson, IndexModel.Json);
                Media = r?.UnusedMedia ?? new(); MediaBytes = r?.UnusedMediaBytes ?? 0;
                Comps = r?.UnusedComponents ?? new(); Plugins = r?.Plugins ?? new();
            }
            catch { HasAnalysis = false; }
        }
        return inst;
    }

    public async Task<IActionResult> OnGetAsync(string publicId)
    {
        if (await LoadAsync(publicId) is null) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostAnalyzeAsync(string publicId)
    {
        var inst = await _db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
        if (inst is null) return NotFound();
        await _instances.EnqueueContentOpAsync(inst, "cleanup.analyze", "{}", overwrite: false, reason: "Cleanup-Analyse (Cloud)");
        TempData["Flash"] = "Analyse angefragt – Ergebnis beim nächsten Kontakt.";
        return RedirectToPage(new { publicId });
    }

    // Step 1 → 2: show exactly what was picked.
    public async Task<IActionResult> OnPostReviewAsync(string publicId, int[]? mediaIds, string[]? componentTypes, string[]? pluginKeys)
    {
        if (await LoadAsync(publicId) is null) return NotFound();
        var mIds = (mediaIds ?? []).ToHashSet();
        var cTypes = (componentTypes ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pKeys = (pluginKeys ?? []).ToHashSet();
        SelMedia = Media.Where(m => mIds.Contains(m.Id)).ToList();
        SelComps = Comps.Where(c => c.Type != null && cTypes.Contains(c.Type)).ToList();
        SelPlugins = Plugins.Where(p => p.Key != null && pKeys.Contains(p.Key)).ToList();
        if (SelMedia.Count == 0 && SelComps.Count == 0 && SelPlugins.Count == 0)
        {
            TempData["FlashError"] = "Nichts ausgewählt.";
            return RedirectToPage(new { publicId });
        }
        Confirming = true;
        return Page();
    }

    // Step 2: enqueue the apply op (the instance re-checks "still unused" before deleting).
    public async Task<IActionResult> OnPostApplyAsync(string publicId, int[]? mediaIds, string[]? componentTypes, string[]? pluginKeys)
    {
        var inst = await _db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
        if (inst is null) return NotFound();
        var payload = JsonSerializer.Serialize(new
        {
            mediaIds = mediaIds ?? [],
            componentTypes = componentTypes ?? [],
            pluginKeys = pluginKeys ?? [],
        });
        await _instances.EnqueueContentOpAsync(inst, "cleanup.apply", payload, overwrite: true, reason: "Aufräumen (Cloud)");
        TempData["Flash"] = "Aufräumen eingereiht – wird beim nächsten Kontakt der Instanz angewendet.";
        return RedirectToPage("Index");
    }
}
