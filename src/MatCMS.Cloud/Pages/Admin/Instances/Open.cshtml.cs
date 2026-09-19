using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Instances;

/// <summary>
/// Deep-link target for an instance's own "Cloud" switch. An instance knows only its PUBLIC id (the one
/// it uses on the API), NOT the numeric row id the <see cref="DetailsModel"/> page routes on — so the
/// switch on the site / its back-office cannot link straight to Details. This resolves the public id to
/// the instance and forwards to its Details page, landing the operator on THIS instance in the cloud
/// rather than the cloud's home. Unknown/inaccessible id falls back to the instance list.
/// </summary>
public class OpenModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly OperatorScope _scope;
    public OpenModel(AppDbContext db, OperatorScope scope) { _db = db; _scope = scope; }

    public async Task<IActionResult> OnGetAsync(string publicId)
    {
        var id = await _db.Instances.AsNoTracking()
            .Where(i => i.PublicId == publicId)
            .Select(i => (int?)i.Id)
            .FirstOrDefaultAsync();
        if (id is not int rowId || !await _scope.CanAccessInstanceAsync(rowId))
            return RedirectToPage("Index");
        return RedirectToPage("Details", new { id = rowId });
    }
}
