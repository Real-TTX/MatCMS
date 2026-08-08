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
public sealed record StoreItem(
    string Title, string Sub, string Description, string RouteValue,
    string? InstalledVersion = null, string? Version = null, string? Accent = null);

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
