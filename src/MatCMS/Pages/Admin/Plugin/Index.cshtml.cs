using MatCMS.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Plugin;

// Serves plugin-registered admin pages at /admin/plugin/{key}.
public class IndexModel : PageModel
{
    private readonly PluginRegistry _registry;
    private readonly IAntiforgery _antiforgery;
    public IndexModel(PluginRegistry registry, IAntiforgery antiforgery)
    {
        _registry = registry;
        _antiforgery = antiforgery;
    }

    public string Key { get; private set; } = "";
    /// <summary>Human page title for the topbar — the plugin's admin-menu label, not the raw route key.</summary>
    public string Title { get; private set; } = "";
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
        // Topbar title: use the plugin's admin-menu label (its human name), matched by this page's URL —
        // not the lowercase route key. Strip a trailing " (N)" badge (e.g. "Bewertungen (3)"). Falls back to the key.
        var url = "/admin/plugin/" + Key;
        var label = _registry.AdminMenu
            .FirstOrDefault(m => string.Equals(m.Url, url, StringComparison.OrdinalIgnoreCase))?.Label;
        if (!string.IsNullOrWhiteSpace(label))
            label = System.Text.RegularExpressions.Regex.Replace(label, @"\s*\(\d+\)\s*$", "");
        Title = string.IsNullOrWhiteSpace(label) ? Key : label!;

        if (!_registry.Pages.TryGetValue(Key, out var handler))
        {
            Found = false;
            return;
        }
        Found = true;

        var query = new Dictionary<string, string>();
        foreach (var kv in Request.Query)
            query[kv.Key] = kv.Value.ToString();

        // A valid antiforgery field so plugin-built POST forms pass the /admin auto-validation.
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        var csrf = $"<input type=\"hidden\" name=\"{tokens.FormFieldName}\" value=\"{System.Net.WebUtility.HtmlEncode(tokens.RequestToken)}\" />";

        var pr = new PluginRequest
        {
            Services = HttpContext.RequestServices,
            Registry = _registry,
            Method = method,
            Query = query,
            Form = (IReadOnlyDictionary<string, string>)form,
            Antiforgery = csrf
        };
        try { Html = handler(pr); }
        catch (Exception ex) { Html = "<div class=\"alert alert-error\">Plugin-Fehler: " + System.Net.WebUtility.HtmlEncode(ex.Message) + "</div>"; }
    }
}
