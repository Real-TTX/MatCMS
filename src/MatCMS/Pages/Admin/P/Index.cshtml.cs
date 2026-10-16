using MatCMS.Data;
using MatCMS.Plugin;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using MatCMS.Shared.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.P;

/// <summary>
/// Draws the admin pages that plugins register through <c>Plugin.Admin.Section(...).Page(...)</c>, at
/// <c>/admin/p/{key}/{path}</c>, inside the admin layout. The folder convention puts everything under
/// /Admin behind the Admin role, so a plugin page needs no check of its own.
/// <para><c>/admin/p/{key}/_settings</c> is the host's own page: the form for the settings the plugin declared. It belongs to every
/// plugin that has settings, with or without a section of its own.</para>
/// <para>The older <c>AddAdminPage</c> pages keep their own URL (<c>/admin/plugin/{key}</c>) and page.</para>
/// </summary>
// A plugin's admin page may carry an upload (a product's download file): the framework's default body limit (30 MB) is lifted to 1 GB for
// these pages. They are behind the Admin role, and the plugin checks what it stores.
[RequestSizeLimit(1_073_741_824)]
[RequestFormLimits(MultipartBodyLengthLimit = 1_073_741_824)]
public class IndexModel : PageModel
{
    /// <summary>The reserved path of the settings page.</summary>
    public const string SettingsPath = "_settings";

    private readonly PluginRegistry _registry;
    private readonly AppDbContext _db;
    private readonly Localizer _t;
    private readonly IDataProtectionProvider _protection;

    public IndexModel(PluginRegistry registry, AppDbContext db, Localizer t, IDataProtectionProvider protection)
    {
        _registry = registry; _db = db; _t = t; _protection = protection;
    }

    /// <summary>The section's pages that are tabs — the tab bar the host draws above the page.</summary>
    public IReadOnlyList<(string Label, string Url, bool Active)> Tabs { get; private set; } = Array.Empty<(string, string, bool)>();
    /// <summary>The HTML the plugin answered with.</summary>
    public string Html { get; private set; } = "";
    /// <summary>False when no such section or page exists (also: the plugin was unloaded meanwhile).</summary>
    public bool Found { get; private set; }

    public Task<IActionResult> OnGetAsync(string key, string? path) => RunAsync(key, path);
    public Task<IActionResult> OnPostAsync(string key, string? path) => RunAsync(key, path);

    private string Csrf()
    {
        var t = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>().GetAndStoreTokens(HttpContext);
        return $"<input type=\"hidden\" name=\"{t.FormFieldName}\" value=\"{System.Net.WebUtility.HtmlEncode(t.RequestToken)}\" />";
    }

    private string Draw(UiNode tree) => UiRenderer.Render(tree, new UiRenderOptions { Target = UiTarget.Admin, Antiforgery = Csrf(), T = k => _t[k] });

    private static string Locale => PluginRequestContext.CurrentLocale();

    private async Task<IActionResult> RunAsync(string key, string? path)
    {
        if (string.Equals((path ?? "").Trim('/'), SettingsPath, StringComparison.OrdinalIgnoreCase)) return await SettingsAsync(key);

        var match = _registry.MatchAdminPage(key, path ?? "");
        if (match is null) { Found = false; Response.StatusCode = StatusCodes.Status404NotFound; return Page(); }
        var (section, page, values) = match.Value;
        Found = true;
        var slice = _registry.SliceOf(key);

        PluginResult result;
        try
        {
            var ctx = await PluginRequestContext.FromHttpAsync(HttpContext, section.Info, _registry, "/" + (path ?? ""), values, withAntiforgery: true);
            result = await page.Handler(ctx, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _registry.AddLog(section.Owner, $"{Request.Method} /{path}: {ex.Message}");
            Html = "<div class=\"alert alert-error\">Plugin-Fehler: " + System.Net.WebUtility.HtmlEncode(ex.Message) + "</div>";
            result = PluginResult.Page(Html);
        }

        var csrf = Csrf();
        // A redirect may carry a message for the page it lands on (the banner the back office shows after a save).
        if (result.Kind == PluginResult.ResultKind.Redirect && !string.IsNullOrWhiteSpace(result.Notice))
            TempData[result.NoticeIsError ? "FlashError" : "Flash"] = result.Notice;
        var direct = PluginResults.ToActionResult(result, HttpContext.RequestServices, UiTarget.Admin, csrf);
        if (direct is not null) return direct;

        Html = PluginResults.Html(result, HttpContext.RequestServices, UiTarget.Admin, csrf);
        // The tab a page belongs to: its own, or — for a detail page without one — the tab whose first
        // segment it shares ("products/{id}" belongs to "products").
        var current = page.Tab is not null ? page : section.Tabs.FirstOrDefault(t => t.Pattern.FirstLiteral == page.Pattern.FirstLiteral);
        Tabs = TabsOf(section, slice, current, settingsActive: false);
        ViewData["Title"] = !string.IsNullOrWhiteSpace(result.Title) ? result.Title : PluginStrings.Label(slice, Locale, section.Label);
        return Page();
    }

    /// <summary>The tab bar: the section's own tabs, then "Einstellungen" when the plugin declared settings.</summary>
    private IReadOnlyList<(string Label, string Url, bool Active)> TabsOf(PluginRegistry.SectionEntry? section, PluginRegistry.Slice? slice,
        PluginRegistry.SectionPage? current, bool settingsActive)
    {
        if (section is null) return Array.Empty<(string, string, bool)>();
        // A section with its own sidebar block is navigated from the sidebar: no tab bar above its pages.
        if (section.Links.Count > 0) return Array.Empty<(string, string, bool)>();
        var tabs = section.Tabs.Select(t => (PluginStrings.Label(slice, Locale, t.Tab!), section.Url + t.Pattern.Template, !settingsActive && ReferenceEquals(t, current))).ToList();
        if (slice is { Settings.Count: > 0 }) tabs.Add((_t["plugins.settings.tab"], section.Url + "/" + SettingsPath, settingsActive));
        return tabs.Count < 2 ? Array.Empty<(string, string, bool)>() : tabs;
    }

    private async Task<IActionResult> SettingsAsync(string key)
    {
        var slice = _registry.SliceOf(key);
        var row = await _db.Plugins.FirstOrDefaultAsync(p => p.Key == key);
        if (slice is null || row is null || slice.Settings.Count == 0)
        {
            Found = false; Response.StatusCode = StatusCodes.Status404NotFound; return Page();
        }
        Found = true;
        var section = _registry.Sections.FirstOrDefault(s => s.Owner == key);
        var stored = PluginSettingsAccess.ParseConfig(row.ConfigJson);
        Tabs = TabsOf(section, slice, null, settingsActive: true);
        ViewData["Title"] = _t["plugins.settings.title", PluginStrings.Label(slice, Locale, section?.Label ?? row.Name)];

        // One group of the settings at a time, when the plugin's sidebar links name one (?group=…); without it, all of them.
        var group = Request.Query["group"].ToString();
        var groups = PluginSettingsUi.Groups(slice);
        if (group.Length > 0 && groups.All(g => g.Group != group)) group = "";
        // A plugin with several groups of settings shows ONE group at a time, with the groups as a sub-menu at the left — never one long form. Asked for without a group, the first one opens.
        if (group.Length == 0 && groups.Count >= 2 && Request.Method == "GET") return Redirect((section?.Url ?? "/admin/p/" + key) + "/" + SettingsPath + "?group=" + Uri.EscapeDataString(groups[0].Group));
        if (group.Length > 0) ViewData["Title"] = PluginStrings.Label(slice, Locale, group);

        Dictionary<string, string>? errors = null;
        Dictionary<string, string>? posted = null;
        if (Request.Method == "POST" && Request.HasFormContentType && Request.Form["action"] == "save-settings")
        {
            posted = Request.Form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            var (config, errs) = PluginSettingsUi.Apply(slice, stored, posted, _protection, k => _t[k], group.Length > 0 ? group : null);
            if (errs.Count == 0)
            {
                row.ConfigJson = PluginSettingsUi.ToJson(config);
                await _db.SaveChangesAsync();
                TempData["Flash"] = _t["plugins.settings.saved"];
                return Redirect(Request.Path + Request.QueryString);
            }
            errors = errs;
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            TempData["FlashError"] = _t["plugins.settings.invalid"];
        }

        var form = PluginSettingsUi.Build(slice, stored, Locale, _t["action.save"], _t["plugins.settings.clearSecret"], _t["plugins.settings.secretSet"], errors, posted, group.Length > 0 ? group : null);
        if (groups.Count >= 2 && group.Length > 0)
        {
            var baseUrl = (section?.Url ?? "/admin/p/" + key) + "/" + SettingsPath;
            var nav = groups.Select(g => Ui.NavItem(PluginStrings.Label(slice, Locale, g.Group), baseUrl + "?group=" + Uri.EscapeDataString(g.Group), active: g.Group == group)).ToList();
            Html = Draw(Ui.Split(nav, Ui.Card(PluginStrings.Label(slice, Locale, group), form)));
        }
        else Html = Draw(form);
        return Page();
    }
}
