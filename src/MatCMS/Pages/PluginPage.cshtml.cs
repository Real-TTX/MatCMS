using MatCMS.Plugin;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

/// <summary>
/// Draws the answer of a public plugin route inside the site layout. Reached only through
/// <see cref="PluginRouteTransformer"/>, never by its own URL: a request that did not come through it has
/// no match and gets a 404.
/// <para>Antiforgery is off on purpose, as it was for the older <c>/plugin/{key}</c> endpoints: these are
/// anonymous visitor submissions with no authenticated state to forge, bounded by
/// <see cref="PluginPostLimiter"/> and by what each plugin validates.</para>
/// </summary>
[IgnoreAntiforgeryToken]
public class PluginPageModel : PageModel
{
    private readonly PluginRegistry _registry;
    public PluginPageModel(PluginRegistry registry) => _registry = registry;

    /// <summary>The HTML the plugin answered with, drawn raw inside the layout.</summary>
    public string Html { get; private set; } = "";

    public Task<IActionResult> OnGetAsync() => RunAsync();
    public Task<IActionResult> OnPostAsync() => RunAsync();

    private async Task<IActionResult> RunAsync()
    {
        if (HttpContext.Items[PluginRouteTransformer.ItemKey] is not PluginMatch match) return NotFound();

        if (Request.Method == "POST" && !PluginPostLimiter.Allow(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"))
            return StatusCode(StatusCodes.Status429TooManyRequests);

        var route = match.Route;
        PluginResult result;
        try
        {
            var ctx = await PluginRequestContext.FromHttpAsync(HttpContext, route.Info, _registry, match.Path, match.Values, withAntiforgery: false);
            result = await route.Handler(ctx, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The error stays with the plugin: its log gets the detail, the visitor a neutral 500 — and the
            // rest of the site keeps working.
            _registry.AddLog(route.Owner, $"{Request.Method} {match.Path}: {ex.Message}");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        // A POST that answered with a page would leave the browser on a re-submittable POST. When the route
        // asks for it, send the visitor back (to the form's __return field when that is a local path).
        if (Request.Method == "POST" && route.Options.PostRedirectGet && result.Kind == PluginResult.ResultKind.Page)
        {
            var back = Request.HasFormContentType && Request.Form.TryGetValue("__return", out var r) ? r.ToString() : "";
            return LocalRedirect(PluginResults.SafeLocal(back));
        }

        var direct = PluginResults.ToActionResult(result, HttpContext.RequestServices, MatCMS.Shared.Web.UiTarget.Public);
        if (direct is not null) return direct;

        Html = PluginResults.Html(result, HttpContext.RequestServices, MatCMS.Shared.Web.UiTarget.Public);
        ViewData["Title"] = string.IsNullOrWhiteSpace(result.Title) ? route.Info.Name : result.Title;
        if (!string.IsNullOrWhiteSpace(result.Description)) ViewData["MetaDescription"] = result.Description;
        if (result.Meta.Count > 0) ViewData["HeadMeta"] = result.Meta.ToList();
        return Page();
    }
}
