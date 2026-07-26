using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Backup;

public class IndexModel : PageModel
{
    private readonly ContentTransferService _transfer;
    private readonly BackupManager _backups;
    private readonly AppDbContext _db;

    public IndexModel(ContentTransferService transfer, BackupManager backups, AppDbContext db)
    {
        _transfer = transfer;
        _backups = backups;
        _db = db;
    }

    // Which sections to include in the exported backup. The checkboxes render "checked" on GET
    // (default = all), but the bind props must default to false so that unchecking a box on POST
    // (which sends no value) actually excludes that section.
    [BindProperty] public bool IncTemplates { get; set; }
    [BindProperty] public bool IncPages { get; set; }
    [BindProperty] public bool IncMenus { get; set; }
    [BindProperty] public bool IncSettings { get; set; }
    [BindProperty] public bool IncSubmissions { get; set; }
    [BindProperty] public bool IncForms { get; set; }
    [BindProperty] public bool IncAssets { get; set; }
    /// <summary>Selected template names (empty = all templates in the section).</summary>
    [BindProperty] public List<string> TemplateNames { get; set; } = new();

    [BindProperty] public IFormFile? ImportFile { get; set; }
    [BindProperty] public bool Confirm { get; set; }

    [BindProperty] public BackupScheduleConfig Schedule { get; set; } = new();

    // View data.
    public List<Template> AllTemplates { get; private set; } = new();
    public BackupScheduleConfig ScheduleConfig { get; private set; } = new();
    public List<BackupManager.StoredBackup> StoredBackups { get; private set; } = new();

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        AllTemplates = await _db.Templates.AsNoTracking().OrderByDescending(t => t.IsActive).ThenBy(t => t.Name).ToListAsync();
        ScheduleConfig = await _backups.GetConfigAsync();
        Schedule = ScheduleConfig; // so asp-for checkboxes reflect the saved schedule
        StoredBackups = _backups.ListStored();
    }

    private ContentTransferService.BackupOptions BuildOptions() => new()
    {
        Templates = IncTemplates,
        Pages = IncPages,
        Menus = IncMenus,
        Settings = IncSettings,
        Submissions = IncSubmissions,
        Forms = IncForms,
        Assets = IncAssets,
        TemplateNames = TemplateNames is { Count: > 0 } ? TemplateNames : null
    };

    public async Task<IActionResult> OnPostExportAsync()
    {
        var options = BuildOptions();
        if (!options.Any)
        {
            TempData["FlashError"] = "Bitte mindestens einen Bereich für das Backup auswählen.";
            return RedirectToPage();
        }

        var bytes = await _transfer.ExportAsync(options);
        var name = $"matcms-backup-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.zip";
        return File(bytes, "application/zip", name);
    }

    /// <summary>Returns a JSON summary of the uploaded backup file (contents preview before restore).</summary>
    public async Task<IActionResult> OnPostInspectAsync()
    {
        if (ImportFile is null || ImportFile.Length == 0)
            return new JsonResult(new { ok = false, error = "Keine Datei." });
        try
        {
            byte[] data;
            using (var ms = new MemoryStream()) { await ImportFile.CopyToAsync(ms); data = ms.ToArray(); }
            var info = _transfer.Inspect(data);
            return new JsonResult(new { ok = true, info });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message });
        }
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        if (!Confirm)
        {
            TempData["FlashError"] = "Bitte bestätigen, dass die im Backup enthaltenen Bereiche überschrieben werden.";
            return RedirectToPage();
        }
        if (ImportFile is null || ImportFile.Length == 0)
        {
            TempData["FlashError"] = "Bitte eine Backup-Datei (.zip oder .json) auswählen.";
            return RedirectToPage();
        }

        byte[] data;
        using (var ms = new MemoryStream()) { await ImportFile.CopyToAsync(ms); data = ms.ToArray(); }

        try
        {
            var summary = await _transfer.ImportAsync(data);
            TempData["Flash"] = $"Wiederhergestellt: {summary}";
        }
        catch (Exception ex)
        {
            TempData["FlashError"] = $"Import fehlgeschlagen: {ex.Message}";
        }
        return RedirectToPage();
    }

    // ---- Scheduler ----

    public async Task<IActionResult> OnPostSaveScheduleAsync()
    {
        var existing = await _backups.GetConfigAsync();
        Schedule.LastRunUtc = existing.LastRunUtc; // never editable from the form
        Schedule.TemplateNames = TemplateNames ?? new();
        if (Schedule.IntervalHours < 1) Schedule.IntervalHours = 1;
        if (Schedule.Retain < 1) Schedule.Retain = 1;
        await _backups.SaveConfigAsync(Schedule);
        TempData["Flash"] = "Backup-Zeitplan gespeichert.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunNowAsync()
    {
        try
        {
            var cfg = await _backups.GetConfigAsync();
            var name = await _backups.RunAsync(cfg, "manual");
            TempData["Flash"] = $"Backup erstellt: {name}";
        }
        catch (Exception ex)
        {
            TempData["FlashError"] = $"Backup fehlgeschlagen: {ex.Message}";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetDownloadAsync(string name)
    {
        var data = await _backups.ReadStoredAsync(name);
        if (data is null) return NotFound();
        return File(data, "application/zip", Path.GetFileName(name));
    }

    public async Task<IActionResult> OnPostRestoreStoredAsync(string name)
    {
        var data = await _backups.ReadStoredAsync(name);
        if (data is null)
        {
            TempData["FlashError"] = "Backup-Datei nicht gefunden.";
            return RedirectToPage();
        }
        try
        {
            var summary = await _transfer.ImportAsync(data);
            TempData["Flash"] = $"Wiederhergestellt: {summary}";
        }
        catch (Exception ex)
        {
            TempData["FlashError"] = $"Import fehlgeschlagen: {ex.Message}";
        }
        return RedirectToPage();
    }

    public IActionResult OnPostDeleteStored(string name)
    {
        var ok = _backups.DeleteStored(name);
        TempData[ok ? "Flash" : "FlashError"] = ok ? "Backup gelöscht." : "Backup nicht gefunden.";
        return RedirectToPage();
    }
}
