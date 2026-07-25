using System.Text;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Backup;

public class IndexModel : PageModel
{
    private readonly ContentTransferService _transfer;
    public IndexModel(ContentTransferService transfer) => _transfer = transfer;

    // Which sections to include in the exported backup (defaults: all).
    [BindProperty] public bool IncTemplates { get; set; } = true;
    [BindProperty] public bool IncPages { get; set; } = true;
    [BindProperty] public bool IncMenus { get; set; } = true;
    [BindProperty] public bool IncSettings { get; set; } = true;
    [BindProperty] public bool IncSubmissions { get; set; } = true;
    [BindProperty] public bool IncAssets { get; set; } = true;

    [BindProperty] public IFormFile? ImportFile { get; set; }
    [BindProperty] public bool Confirm { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostExportAsync()
    {
        var options = new ContentTransferService.BackupOptions
        {
            Templates = IncTemplates,
            Pages = IncPages,
            Menus = IncMenus,
            Settings = IncSettings,
            Submissions = IncSubmissions,
            Assets = IncAssets
        };
        if (!options.Any)
        {
            TempData["FlashError"] = "Bitte mindestens einen Bereich für das Backup auswählen.";
            return RedirectToPage();
        }

        var bytes = await _transfer.ExportAsync(options);
        var name = $"matcms-backup-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.zip";
        return File(bytes, "application/zip", name);
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
        using (var ms = new MemoryStream())
        {
            await ImportFile.CopyToAsync(ms);
            data = ms.ToArray();
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
}
