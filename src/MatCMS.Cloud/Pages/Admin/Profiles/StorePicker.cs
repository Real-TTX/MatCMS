namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>One line in the "Aus Global hinzufügen" picker.</summary>
/// <param name="Id">Store row id — what the add handler receives.</param>
/// <param name="Label">Display name.</param>
/// <param name="Sub">Secondary text (key, type, e-mail) shown muted next to the label.</param>
public sealed record PickerItem(int Id, string Label, string Sub);

/// <summary>
/// Everything <c>_StorePicker.cshtml</c> needs. One dialog per payload, so the ids on the overlay and
/// the checkbox names stay unique on a page that renders four of them.
/// </summary>
/// <param name="ProfileId">Profile the picked entries are added to.</param>
/// <param name="Kind">"plugins" | "templates" | "components" | "users" — routed to the add handler
/// and used as the tab to return to.</param>
/// <param name="Items">What is NOT in the profile yet. Empty means the button is still shown but the
/// dialog says there is nothing left to take — better than a button that silently does nothing.</param>
public sealed record StorePicker(int ProfileId, string Kind, List<PickerItem> Items);
