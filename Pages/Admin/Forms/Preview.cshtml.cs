using MatCMS.Content;
using MatCMS.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Forms;

public class PreviewModel : PageModel
{
    private readonly AppDbContext _db;
    public PreviewModel(AppDbContext db) => _db = db;

    public FormRenderModel? Render { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, bool builder)
    {
        var form = await _db.Forms.FindAsync(id);
        if (form is null) return NotFound();

        Render = new FormRenderModel
        {
            FormId = form.Id,
            Slug = form.Slug,
            Name = form.Name,
            Elements = FormDefinition.Parse(form.DefinitionJson),
            SubmitLabel = form.SubmitLabel,
            Preview = true,
            Builder = builder
        };
        return Page();
    }
}
