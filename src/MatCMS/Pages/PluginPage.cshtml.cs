using MatCMS.Plugin;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

/// <summary>
/// Draws the answer of a public plugin route inside the site layout. Reached only through
/// <see cref="PluginRouteTransformer"/>, never by its own URL: a request that did not come through it has
/// no match and gets a 404.
/// <para>Antiforgery is off by default, as it was for the older <c>/plugin/{key}</c> endpoints: these are
/// anonymous visitor submissions with no authenticated state to forge, bounded by
/// <see cref="PluginPostLimiter"/> and by what each plugin validates. A route that acts for a signed-in customer asks for it
/// (<see cref="PublicRouteOptions.Antiforgery"/>) and the check happens here, BEFORE the handler runs.</para>
/// </summary>
[IgnoreAntiforgeryToken]
public class PluginPageModel : PageModel
{
    private readonly PluginRegistry _registry;
    private readonly IAntiforgery _antiforgery;
    public PluginPageModel(PluginRegistry registry, IAntiforgery antiforgery) { _registry = registry; _antiforgery = antiforgery; }

    /// <summary>The HTML the plugin answered with, drawn raw inside the layout.</summary>
    public string Html { get; private set; } = "";

    public Task<IActionResult> OnGetAsync() => RunAsync();
    public Task<IActionResult> OnPostAsync() => RunAsync();

    /// <summary>
    /// An error answer for a POST WITH a body. The site re-executes every empty error response as a status page (<c>UseStatusCodePagesWithReExecute</c>), and
    /// re-running a POST there ends in a bare 400 from that page's antiforgery check — a 413 or a 429 would arrive as "Bad Request". A body, however short,
    /// keeps the real status.
    /// </summary>
    private IActionResult Fail(int status) =>
        Request.Method == "POST"
            ? new ContentResult { StatusCode = status, Content = ReasonOf(status), ContentType = "text/plain; charset=utf-8" }
            : StatusCode(status);

    private static string ReasonOf(int status) => status switch
    {
        400 => "Bad request", 413 => "Payload too large", 429 => "Too many requests", 500 => "Internal server error", _ => "Error"
    };

    private async Task<IActionResult> RunAsync()
    {
        if (HttpContext.Items[PluginRouteTransformer.ItemKey] is not PluginMatch match) return NotFound();
        // Taken out at once: the match holds the plugin's handler, and an HttpContext can outlive its request (a response still being read, a pooled
        // context) — an unloaded native plugin must not be kept alive by a request that is long over.
        HttpContext.Items.Remove(PluginRouteTransformer.ItemKey);

        var route = match.Route;
        var options = route.Options;
        var client = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (Request.Method == "POST" && !(options.Webhook ? PluginPostLimiter.AllowWebhook(client) : PluginPostLimiter.Allow(client + "|" + route.Owner, options.PostsPerMinute)))
            return Fail(StatusCodes.Status429TooManyRequests);

        // A page for customers: nobody who is not signed in gets as far as the handler. The same sign-in page a members-only page uses.
        if (options.RequireMember)
        {
            var member = await PluginRequestContext.CurrentMemberAsync(HttpContext);
            var back = Uri.EscapeDataString(Request.Path + Request.QueryString);
            if (member is null) return Redirect("/anmelden?returnUrl=" + back);
            if (!string.IsNullOrWhiteSpace(options.RequiredRole) && !member.InRole(options.RequiredRole!))
                return Redirect("/anmelden?denied=true&returnUrl=" + back);
        }

        if (options.Antiforgery && Request.Method == "POST")
        {
            try { await _antiforgery.ValidateRequestAsync(HttpContext); }
            catch (AntiforgeryValidationException) { return Fail(StatusCodes.Status400BadRequest); }
        }

        PluginResult result;
        PluginRequestContext ctx;
        try
        {
            ctx = await PluginRequestContext.FromHttpAsync(HttpContext, route.Info, _registry, match.Path, match.Values,
                withAntiforgery: options.Antiforgery, webhook: options.Webhook);
            result = await route.Handler(ctx, HttpContext.RequestAborted);
        }
        catch (RequestBodyTooLargeException) { return Fail(StatusCodes.Status413PayloadTooLarge); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The error stays with the plugin: its log gets the detail, the visitor a neutral 500 — and the
            // rest of the site keeps working.
            _registry.AddLog(route.Owner, $"{Request.Method} {match.Path}: {ex.Message}");
            return Fail(StatusCodes.Status500InternalServerError);
        }

        PluginResults.Apply(result, Response, route.Owner, Request.IsHttps);

        // A POST that answered with a page would leave the browser on a re-submittable POST. When the route
        // asks for it, send the visitor back (to the form's __return field when that is a local path).
        if (Request.Method == "POST" && options.PostRedirectGet && result.Kind == PluginResult.ResultKind.Page)
        {
            var back = Request.HasFormContentType && Request.Form.TryGetValue("__return", out var r) ? r.ToString() : "";
            return LocalRedirect(PluginResults.SafeLocal(back));
        }

        // A webhook has no layout: whatever it answered (even "a page") goes out as the body.
        if (options.Webhook && result.Kind == PluginResult.ResultKind.Page)
            return new ContentResult { Content = PluginResults.Html(result, HttpContext.RequestServices, MatCMS.Shared.Web.UiTarget.Public), ContentType = "text/html; charset=utf-8", StatusCode = result.StatusCode };

        var direct = PluginResults.ToActionResult(result, HttpContext.RequestServices, MatCMS.Shared.Web.UiTarget.Public, ctx.Antiforgery);
        // A bare error status of a POST (a webhook that says "no", a form that is refused) keeps its status — see Fail.
        if (direct is StatusCodeResult { StatusCode: >= 400 } bare && Request.Method == "POST") return Fail(bare.StatusCode);
        if (direct is not null) return direct;

        Html = PluginResults.Html(result, HttpContext.RequestServices, MatCMS.Shared.Web.UiTarget.Public, ctx.Antiforgery);
        ViewData["Title"] = string.IsNullOrWhiteSpace(result.Title) ? route.Info.Name : result.Title;
        if (!string.IsNullOrWhiteSpace(result.Description)) ViewData["MetaDescription"] = result.Description;
        if (result.Meta.Count > 0) ViewData["HeadMeta"] = result.Meta.ToList();
        if (result.StatusCode != StatusCodes.Status200OK) Response.StatusCode = result.StatusCode;
        return Page();
    }
}
