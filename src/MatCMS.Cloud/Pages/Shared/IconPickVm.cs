namespace MatCMS.Cloud.Pages.Shared;

/// <summary>
/// What the cloud hands to <c>_IconPicker.cshtml</c>. That partial is only an adapter: it looks up
/// the wording and renders the SHARED <c>_IconField</c> from <c>MatCMS.Shared.Web</c>, which the CMS
/// renders too — the one thing a shared view cannot do is reference either application's
/// <c>Localizer</c>.
/// </summary>
/// <param name="Value">The stored icon value.</param>
/// <param name="FieldName">Name of the posted form field. The cloud's component pages post
/// <c>icon</c> in lower case; that name stays exactly as it was.</param>
/// <param name="LabelKey">Resource key of the field label.</param>
/// <param name="HelpKey">Resource key of the hint under the field; empty = no hint.</param>
public sealed record IconPickVm(
    string? Value, string FieldName = "icon", string LabelKey = "profiles.componentIcon",
    string HelpKey = "");
