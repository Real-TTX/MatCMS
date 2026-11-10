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
public sealed record StoreItem(
    string Title, string Sub, string Description, string RouteValue,
    string? InstalledVersion = null, string? Version = null, string? Accent = null,
    IReadOnlyList<StoreImage>? Images = null, string? Publisher = null, string? PublisherUrl = null)
{
    /// <summary>The publisher's address when it is safe to link (absolute http/https), else null — it came from the cloud, not from us.</summary>
    public string? SafePublisherUrl => Uri.TryCreate(PublisherUrl ?? "", UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.ToString() : null;
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
public sealed record StoreDialog(
    string TitleKey, string IntroKey, string RouteName, List<StoreItem> Items, string? Error = null);
