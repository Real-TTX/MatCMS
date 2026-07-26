using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Plugin;

// Serves plugin-registered admin pages at /admin/plugin/{key}.
public class IndexModel : PageModel
{
    private readonly PluginRegistry _registry;
    public IndexModel(PluginRegistry registry) => _registry = registry;

    public string Key { get; private set; } = "";
    public string Html { get; private set; } = "";
    public bool Found { get; private set; }

    public IActionResult OnGet(string key)
    {
        Key = key;
        Run("GET", new Dictionary<string, string>());
        return Page();
    }

    public IActionResult OnPost(string key)
    {
        Key = key;
        var form = new Dictionary<string, string>();
        if (Request.HasFormContentType)
            foreach (var kv in Request.Form)
                form[kv.Key] = kv.Value.ToString();
        // Run the (mutating) handler, then redirect to GET so refreshes don't re-submit.
        Run("POST", form);
        return RedirectToPage(new { key });
    }

    private void Run(string method, IDictionary<string, string> form)
    {
        if (!_registry.Pages.TryGetValue(Key, out var handler))
        {
            Found = false;
            return;
        }
        Found = true;

        var query = new Dictionary<string, string>();
        foreach (var kv in Request.Query)
            query[kv.Key] = kv.Value.ToString();

        var pr = new PluginRequest
        {
            Services = HttpContext.RequestServices,
            Registry = _registry,
            Method = method,
            Query = query,
            Form = (IReadOnlyDictionary<string, string>)form
        };
        try { Html = handler(pr); }
        catch (Exception ex) { Html = "<div class=\"alert alert-error\">Plugin-Fehler: " + System.Net.WebUtility.HtmlEncode(ex.Message) + "</div>"; }
    }
}
