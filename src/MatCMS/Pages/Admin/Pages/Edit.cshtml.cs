using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Pages;

/// <summary>
/// The address of the former classic page editor (<c>/admin/pages/edit/{id}</c>). The classic editor was removed
/// (2026-11-11); bookmarks, old cloud links and links in mails still point here, so every request is sent on to the
/// page editor (<see cref="EditorModel"/>) — a block named with <c>?block=</c> is opened there. 302, not 301: a
/// cached permanent redirect could not be taken back if this address is ever used again.
/// </summary>
public class EditModel : PageModel
{
    public IActionResult OnGet(int id, int? block) =>
        block is int b ? RedirectToPage("Editor", new { id, block = b }) : RedirectToPage("Editor", new { id });

    /// <summary>A post to one of the old handlers (a form left open in a tab) is not carried out any more; it
    /// lands on the editor instead of on an empty page.</summary>
    public IActionResult OnPost(int id) => RedirectToPage("Editor", new { id });
}
