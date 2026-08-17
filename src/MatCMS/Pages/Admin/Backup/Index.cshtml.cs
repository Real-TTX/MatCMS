using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Backup;

// Kestrel's default 30 MB body cap makes multi-gallery backups fail as HTTP 400 mid-upload — the
// browser keeps streaming until done and only then sees the reject, which looks like an endless
// restore. Razor Pages ignores these filter attributes on handler METHODS, so they must sit on the
// PageModel CLASS (applies to all handlers of this page; only Import ever receives large bodies).
[RequestSizeLimit(200L * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 200L * 1024 * 1024)]
public class IndexModel : PageModel
{
    private readonly ContentTransferService _transfer;
    private readonly BackupManager _backups;
    private readonly AppDbContext _db;
    private readonly CloudService _cloud;
    private readonly CloudBackupService _cloudBackups;

    public IndexModel(ContentTransferService transfer, BackupManager backups, AppDbContext db,
        CloudService cloud, CloudBackupService cloudBackups)
    {
        _transfer = transfer;
        _backups = backups;
        _db = db;
        _cloud = cloud;
        _cloudBackups = cloudBackups;
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
    [BindProperty] public bool IncPlugins { get; set; }
    [BindProperty] public bool IncUsers { get; set; }
    // Granular within-section selection (empty = the whole section).
    [BindProperty] public List<string> TemplateNames { get; set; } = new();
    [BindProperty] public List<string> PageKeys { get; set; } = new();
    [BindProperty] public List<string> FormSlugs { get; set; } = new();

    [BindProperty] public IFormFile? ImportFile { get; set; }
    [BindProperty] public bool Confirm { get; set; }

    [BindProperty] public BackupScheduleConfig Schedule { get; set; } = new();

    // View data.
    public List<Template> AllTemplates { get; private set; } = new();
    public List<MatCMS.Models.Page> AllPages { get; private set; } = new();
    public List<Form> AllForms { get; private set; } = new();
    public BackupScheduleConfig ScheduleConfig { get; private set; } = new();
    public List<BackupManager.StoredBackup> StoredBackups { get; private set; } = new();

    /// <summary>Stable key for a page checkbox: "slug|locale".</summary>
    public static string PageKey(MatCMS.Models.Page p) => ContentTransferService.PageKey(p.Slug, p.Locale);

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        AllTemplates = await _db.Templates.AsNoTracking().OrderByDescending(t => t.IsActive).ThenBy(t => t.Name).ToListAsync();
        AllPages = await _db.Pages.AsNoTracking().OrderBy(p => p.Locale).ThenBy(p => p.Title).ToListAsync();
        AllForms = await _db.Forms.AsNoTracking().OrderBy(f => f.Name).ToListAsync();
        var link = await _cloudBackups.IsEnabledAsync();
        BackupToCloud = link;
        CloudConnected = (await _cloud.GetSettingsAsync()).Configured;
        ScheduleConfig = await _backups.GetConfigAsync();
        Schedule = ScheduleConfig; // so asp-for checkboxes reflect the saved schedule
        StoredBackups = _backups.ListStored();
    }

    /// <summary>A selection counts as "granular" (→ upsert on restore) only when it is a STRICT
    /// subset of what exists. All items selected (or none) means the whole section (→ replace-all).</summary>
    private static List<string>? Subset(List<string> selected, int total) =>
        selected.Count > 0 && selected.Count < total ? selected : null;

    private async Task<ContentTransferService.BackupOptions> BuildOptionsAsync()
    {
        var tplTotal = await _db.Templates.CountAsync();
        var pageTotal = await _db.Pages.CountAsync();
        var formTotal = await _db.Forms.CountAsync();
        return new()
        {
            Templates = IncTemplates,
            Pages = IncPages,
            Menus = IncMenus,
            Settings = IncSettings,
            Submissions = IncSubmissions,
            Forms = IncForms,
            Assets = IncAssets,
            Plugins = IncPlugins,
            Users = IncUsers,
            TemplateNames = Subset(TemplateNames, tplTotal),
            PageKeys = Subset(PageKeys, pageTotal),
            FormSlugs = Subset(FormSlugs, formTotal)
        };
    }

    public async Task<IActionResult> OnPostExportAsync()
    {
        var options = await BuildOptionsAsync();
        if (!options.Any)
        {
            TempData["FlashError"] = "Bitte mindestens einen Bereich für das Backup auswählen.";
            return RedirectToPage();
        }

        var bytes = await _transfer.ExportAsync(options);
        var name = $"{await _backups.SiteSlugAsync()}_{DateTime.UtcNow:yyyy-MM-dd-HHmm}.zip";
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
            return RedirectToPage(new { tab = "restore" });
        }
        if (ImportFile is null || ImportFile.Length == 0)
        {
            TempData["FlashError"] = "Bitte eine Backup-Datei (.zip oder .json) auswählen.";
            return RedirectToPage(new { tab = "restore" });
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
        return RedirectToPage(new { tab = "restore" });
    }

    // ---- Scheduler ----

    /// <summary>Whether a cloud is linked at all — the upload switch is hidden without one.</summary>
    public bool CloudConnected { get; private set; }

    /// <summary>Whether finished backups are handed to that cloud.</summary>
    public bool BackupToCloud { get; private set; }

    public async Task<IActionResult> OnPostSaveScheduleAsync(bool backupToCloud)
    {
        // Its own setting rather than part of the schedule config: the schedule is a file the
        // backup manager owns, and a site setting is what the cloud can roll out.
        var row = await _db.SiteSettings.FirstOrDefaultAsync(x => x.Key == SettingKeys.BackupToCloud);
        if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.BackupToCloud, Value = backupToCloud ? "1" : "0" });
        else row.Value = backupToCloud ? "1" : "0";
        await _db.SaveChangesAsync();

        var existing = await _backups.GetConfigAsync();
        Schedule.LastRunUtc = existing.LastRunUtc; // never editable from the form
        // Store granular keys only when a strict subset is selected; "all" is stored as empty (= all).
        var tplTotal = await _db.Templates.CountAsync();
        var pageTotal = await _db.Pages.CountAsync();
        var formTotal = await _db.Forms.CountAsync();
        Schedule.TemplateNames = Subset(TemplateNames, tplTotal) ?? new();
        Schedule.PageKeys = Subset(PageKeys, pageTotal) ?? new();
        Schedule.FormSlugs = Subset(FormSlugs, formTotal) ?? new();
        if (Schedule.IntervalHours < 1) Schedule.IntervalHours = 1;
        if (Schedule.Retain < 1) Schedule.Retain = 1;
        await _backups.SaveConfigAsync(Schedule);
        TempData["Flash"] = "Backup-Zeitplan gespeichert.";
        return RedirectToPage(new { tab = "schedule" });
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
        return RedirectToPage(new { tab = "schedule" });
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
            return RedirectToPage(new { tab = "schedule" });
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
        return RedirectToPage(new { tab = "schedule" });
    }

    public IActionResult OnPostDeleteStored(string name)
    {
        var ok = _backups.DeleteStored(name);
        TempData[ok ? "Flash" : "FlashError"] = ok ? "Backup gelöscht." : "Backup nicht gefunden.";
        return RedirectToPage(new { tab = "schedule" });
    }

    /// <summary>
    /// Pushes one stored backup to the cloud now, without waiting for the next scheduled run.
    /// <para>Useful on its own — an operator about to change something risky wants the copy up there
    /// BEFORE they do it, not tonight.</para>
    /// </summary>
    public async Task<IActionResult> OnPostUploadToCloudAsync(string name)
    {
        var (ok, error) = await _cloudBackups.UploadAsync(name, "manual");
        if (ok) TempData["Flash"] = $"„{name}“ in die Cloud hochgeladen.";
        else TempData["FlashError"] = $"Upload fehlgeschlagen: {error}";
        return RedirectToPage();
    }
}
