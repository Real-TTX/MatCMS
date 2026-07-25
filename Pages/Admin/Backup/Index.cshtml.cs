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
            Submissions = IncSubmissions
        };
        if (!options.Any)
        {
            TempData["FlashError"] = "Bitte mindestens einen Bereich für das Backup auswählen.";
            return RedirectToPage();
        }

        var json = await _transfer.ExportAsync(options);
        var bytes = Encoding.UTF8.GetBytes(json);
        var name = $"matcms-backup-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.json";
        return File(bytes, "application/json", name);
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
            TempData["FlashError"] = "Bitte eine Backup-Datei (.json) auswählen.";
            return RedirectToPage();
        }

        string json;
        using (var reader = new StreamReader(ImportFile.OpenReadStream(), Encoding.UTF8))
            json = await reader.ReadToEndAsync();

        try
        {
            var summary = await _transfer.ImportAsync(json);
            TempData["Flash"] = $"Wiederhergestellt: {summary}";
        }
        catch (Exception ex)
        {
            TempData["FlashError"] = $"Import fehlgeschlagen: {ex.Message}";
        }
        return RedirectToPage();
    }
}
