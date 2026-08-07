namespace MatCMS.Cloud.Pages.Shared;

/// <summary>
/// One tile in a payload list (profile users/plugins/components/templates). Deliberately flat: the
/// tile view is for SCANNING — click it and you are in the editor. Managing (removing a global
/// entry, deleting an own one) stays in the table view, which is what the toggle is for.
/// </summary>
/// <param name="Href">Where clicking the tile goes — the same editor the table's name links to.</param>
/// <param name="Title">Display name.</param>
/// <param name="Sub">Secondary line: plugin key, component type, e-mail, description.</param>
/// <param name="IsGlobal">Taken from the store rather than defined on the profile.</param>
/// <param name="Search">What the list's live search matches against; mirrors the table row's
/// <c>data-search</c> so both views filter identically.</param>
/// <param name="Accent">Optional colour strip (templates). Null = no strip.</param>
/// <param name="NoteKey">Optional extra badge, as a RESOURCE KEY — user-facing text belongs in
/// <c>Resources/*.json</c>, never in a page model.</param>
public sealed record PayloadTile(
    string Href, string Title, string Sub, bool IsGlobal, string Search,
    string? Accent = null, string? NoteKey = null);
