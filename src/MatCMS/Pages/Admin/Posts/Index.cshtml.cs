using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Posts;

/// <summary>The blog's back office used to live here (<c>/admin/posts</c>, <c>/admin/posts/edit/{id}</c>). It is a plugin now, at
/// <c>/admin/p/blog</c>; this page only forwards old links and bookmarks.</summary>
public class IndexModel : PageModel
{
    public IActionResult OnGet(string? rest)
    {
        var parts = (rest ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // /admin/posts/edit/7 → /admin/p/blog/posts/7
        if (parts.Length == 2 && parts[0].Equals("edit", StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[1], out var id))
            return Redirect("/admin/p/blog/posts/" + id);
        return Redirect("/admin/p/blog");
    }
}
