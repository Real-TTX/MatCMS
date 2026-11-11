namespace MatCMS.Pages.Shared;

/// <summary>
/// One offering in the cloud store dialog.
/// </summary>
/// <param name="Title">Display name.</param>
/// <param name="Sub">Identity line under the title (plugin key, component type, template name).</param>
/// <param name="Description">Free text; may be empty.</param>
/// <param name="RouteValue">Value passed to the install handler — the item's identity.</param>
/// <param name="InstalledVersion">Version already on this site, or null when it is not installed.
/// Drives the badge and whether the button reads "install" or "update".</param>
/// <param name="Version">Version offered by the store, or null for things that have none.</param>
/// <param name="Accent">Optional colour for the card's strip (templates).</param>
/// <param name="Images">Pictures (plugins): the first is the card's cover, all of them open as a gallery. Addresses on THIS site.</param>
/// <param name="Publisher">Who publishes it in the store ("Herausgeber"); empty = not shown.</param>
/// <param name="PublisherUrl">The publisher's website (http(s) only, checked again here), linked from the name.</param>
/// <param name="Note">When set, shown INSTEAD of the install button (e.g. "comes from your cloud's store" in the MatCMS Store tab).</param>
/// <param name="Badge">An extra pill after the title (e.g. "Native", "geprüft").</param>
public sealed record StoreItem(
    string Title, string Sub, string Description, string RouteValue,
    string? InstalledVersion = null, string? Version = null, string? Accent = null,
    IReadOnlyList<StoreImage>? Images = null, string? Publisher = null, string? PublisherUrl = null,
    string? Note = null, string? Badge = null, long? Downloads = null, string? Rating = null)
{
    /// <summary>The publisher's address when it is safe to link (absolute http/https), else null — it came from the cloud, not from us.</summary>
    public string? SafePublisherUrl => Uri.TryCreate(PublisherUrl ?? "", UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.ToString() : null;
}

/// <summary>The two tabs of a store dialog — the cloud's store and the MatCMS Store — as links (<c>?browse=true|matcms</c>), so only the store
/// looked at is asked. Null (no tabs) unless both are open to this site.</summary>
public static class StoreTabs
{
    public static IReadOnlyList<StoreTab>? For(Microsoft.AspNetCore.Http.HttpContext http, Microsoft.AspNetCore.Mvc.IUrlHelper url, bool both, string? browsing)
    {
        if (!both) return null;
        var t = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<MatCMS.Services.Localizer>(http.RequestServices);
        return new[]
        {
            new StoreTab(t["plugins.storeTab.cloud"], Microsoft.AspNetCore.Mvc.UrlHelperExtensions.Page(url, "Index", new { browse = "true" })!, browsing != "matcms"),
            new StoreTab(t["plugins.storeTab.matcms"], Microsoft.AspNetCore.Mvc.UrlHelperExtensions.Page(url, "Index", new { browse = "matcms" })!, browsing == "matcms")
        };
    }
}

/// <summary>One picture of a store offering: where this site serves it, and its caption.</summary>
public sealed record StoreImage(string Url, string Caption);

/// <summary>
/// Everything <c>_StoreDialog.cshtml</c> needs: the catalogue for ONE kind of thing, plus how to
/// install it. Rendered by the plugins, templates and components pages, which differ only in the
/// route parameter their install handler expects.
/// </summary>
/// <param name="TitleKey">Resource key for the dialog heading — user-facing text belongs in
/// <c>Resources/*.json</c>, and a page model has no localizer.</param>
/// <param name="IntroKey">Resource key for the one-line explanation.</param>
/// <param name="RouteName">Name of the install handler's route parameter ("key", "name", "type").</param>
/// <param name="Items">What the cloud offers. Empty = the dialog says so rather than showing nothing.</param>
/// <param name="Error">Set when the catalogue could not be fetched; shown instead of the list.</param>
/// <param name="Handler">The page handler that installs one item (default: from the cloud).</param>
/// <param name="Tabs">Tabs above the list when there is more than one store to browse (cloud store / MatCMS Store); each is a link that
/// reloads the page with its own <c>?browse=</c>, so only the store that is looked at is asked.</param>
public sealed record StoreDialog(
    string TitleKey, string IntroKey, string RouteName, List<StoreItem> Items, string? Error = null,
    string Handler = "InstallFromCloud", IReadOnlyList<StoreTab>? Tabs = null);

/// <summary>One tab of the store dialog: its words, its address, whether it is the one shown.</summary>
public sealed record StoreTab(string Label, string Url, bool Active);
