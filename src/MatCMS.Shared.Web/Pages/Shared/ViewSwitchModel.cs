namespace MatCMS.Shared.Web;

/// <summary>
/// The Cloud ↔ Website ↔ Admin view switcher, shown on all three surfaces (the cloud's view of an
/// instance, the instance's public site, and the instance's back-office). ONE segmented control so
/// moving between the three feels like flipping a tab rather than opening things — the current view is
/// marked active. Shared so the markup and look are identical everywhere.
/// <para>Each call site supplies the hrefs (the targets are cross-origin, so only the caller knows
/// them), which segment is <see cref="Active"/>, and the wording — the two applications have separate
/// <c>Localizer</c> types, so a shared view cannot look strings up itself.</para>
/// <para>Navigation is same-tab on purpose: it should feel like SWITCHING to the other view, not
/// opening a second window. A non-active segment with no URL is omitted rather than pointing nowhere
/// (e.g. the Cloud segment on an instance that is not linked to a cloud).</para>
/// </summary>
/// <param name="Active">The current view: <c>"cloud"</c>, <c>"web"</c> or <c>"admin"</c>. That segment
/// renders highlighted and as plain text, not a link.</param>
/// <param name="CloudUrl">Where the Cloud segment points (the control plane).</param>
/// <param name="WebUrl">Where the Website segment points (the instance's public site).</param>
/// <param name="AdminUrl">Where the Admin segment points (the instance's back-office).</param>
/// <param name="CloudLabel">Tooltip / accessible name of the Cloud segment.</param>
/// <param name="WebLabel">Tooltip / accessible name of the Website segment.</param>
/// <param name="AdminLabel">Tooltip / accessible name of the Admin segment.</param>
public sealed record ViewSwitch(
    string Active,
    string? CloudUrl, string? WebUrl, string? AdminUrl,
    string CloudLabel, string WebLabel, string AdminLabel);
