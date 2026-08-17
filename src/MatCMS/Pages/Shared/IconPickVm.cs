namespace MatCMS.Pages.Shared;

/// <summary>
/// What the CMS hands to <c>_IconPicker.cshtml</c>. That partial is only an adapter: it resolves the
/// legacy aliases (<see cref="MatCMS.Content.MenuIcons"/>) and looks up the wording, then renders the
/// SHARED <c>_IconField</c> from <c>MatCMS.Shared.Web</c>, which the cloud renders too. The two
/// things a shared view cannot do are exactly these — it can reference neither application's
/// <c>Localizer</c> nor the CMS's icon aliases.
/// </summary>
/// <param name="Value">The stored icon value, before alias resolution.</param>
/// <param name="FieldName">Name of the posted form field — unchanged from what the page used before.</param>
/// <param name="LabelKey">Resource key of the field label.</param>
/// <param name="HelpKey">Resource key of the hint under the field; empty = no hint.</param>
/// <param name="MenuOnly">Menu items only: an icon applies to the toolbar menu, so the field is
/// shown only while the <c>Menu</c> select stands on <c>toolbar</c>.</param>
public sealed record IconPickVm(
    string? Value, string FieldName = "Icon", string LabelKey = "menus.icon",
    string HelpKey = "", bool MenuOnly = false);
