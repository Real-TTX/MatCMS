using System.Text;
using MatCMS.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Transfer;

public class IndexModel : PageModel
{
    private readonly ContentTransferService _transfer;
    public IndexModel(ContentTransferService transfer) => _transfer = transfer;

    [BindProperty] public IFormFile? ImportFile { get; set; }
    [BindProperty] public bool Confirm { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostExportAsync()
    {
        var json = await _transfer.ExportAsync();
        var bytes = Encoding.UTF8.GetBytes(json);
        var name = $"matcms-export-{DateTime.UtcNow:yyyy-MM-dd}.json";
        return File(bytes, "application/json", name);
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        if (!Confirm)
        {
            TempData["FlashError"] = "Bitte bestätigen Sie, dass alle Inhalte überschrieben werden.";
            return RedirectToPage();
        }

        if (ImportFile is null || ImportFile.Length == 0)
        {
            TempData["FlashError"] = "Bitte wählen Sie eine JSON-Datei aus.";
            return RedirectToPage();
        }

        string json;
        using (var reader = new StreamReader(ImportFile.OpenReadStream(), Encoding.UTF8))
            json = await reader.ReadToEndAsync();

        try
        {
            var summary = await _transfer.ImportAsync(json, replace: true);
            TempData["Flash"] = $"Import erfolgreich: {summary}";
        }
        catch (Exception ex)
        {
            TempData["FlashError"] = $"Import fehlgeschlagen: {ex.Message}";
        }

        return RedirectToPage();
    }
}
