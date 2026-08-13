using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The list behind the switcher in the top bar: the cloud itself, then every instance.
///
/// <para>The cloud is the FIRST entry, not a separate control, because "which site am I looking at"
/// and "am I looking at the control plane" are the same question — and answering them with two
/// different widgets means an operator has to know which one they are in before they can leave it.</para>
///
/// <para>Its own scoped service rather than a property on each page model: the switcher now sits in
/// the layout, so every admin page needs the list, and none of them should have to remember to load
/// it. Cached per request, since the layout may ask more than once while rendering.</para>
/// </summary>
public class ContextSwitcher
{
    private readonly AppDbContext _db;
    private List<Instance>? _cache;

    public ContextSwitcher(AppDbContext db) => _db = db;

    /// <summary>
    /// Every instance worth switching to.
    /// <para>Rejected ones stay out — they were refused, so they are not somewhere to go. Offline ones
    /// stay IN: offline is exactly when somebody goes looking, and a list that hides them answers the
    /// wrong question.</para>
    /// </summary>
    /// <summary>Cookie holding which of the two views the operator last CHOSE.</summary>
    public const string ViewCookie = "matcmscloud.view";

    public const string ViewSite = "site";
    public const string ViewCloud = "cloud";

    /// <summary>
    /// Remembers a chosen view, so stepping to the next instance lands in the same one.
    ///
    /// <para>Written only when the view TOGGLE was used — the pages behind it are reached by other
    /// routes too (a row in the instance list, a link from a backup), and letting those count would
    /// make the memory follow navigation rather than intent.</para>
    ///
    /// <para>A cookie rather than localStorage because the choice decides where the switcher's links
    /// POINT, and those are built on the server: with the value in the browser only, every menu would
    /// render the wrong targets and need rewriting after the fact.</para>
    /// </summary>
    public static void Remember(HttpContext ctx, string view)
    {
        if (view != ViewSite && view != ViewCloud) return;
        ctx.Response.Cookies.Append(ViewCookie, view, new CookieOptions
        {
            HttpOnly = true,                    // nothing in the browser reads it
            IsEssential = true,                 // a UI preference the operator asked for
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            Path = "/"
        });
    }

    /// <summary>Which view to send the switcher's links to. The site is the default: it is what the
    /// menu did before there was a choice, and it is why somebody opens an instance.</summary>
    public static string CurrentView(HttpContext ctx) =>
        ctx.Request.Cookies[ViewCookie] == ViewCloud ? ViewCloud : ViewSite;

    public async Task<List<Instance>> InstancesAsync(CancellationToken ct = default) =>
        _cache ??= await _db.Instances.AsNoTracking()
            .Where(i => i.Status != InstanceStatus.Rejected)
            .OrderBy(i => i.Name)
            .ToListAsync(ct);
}
