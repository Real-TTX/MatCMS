namespace MatCMS.Cloud.Pages.Shared;

/// <summary>
/// The single "Hinzufügen" entry point under a list, and the ways in it offers.
/// <para>One button instead of a row of them: an operator adding something decides WHAT KIND of
/// addition it is once, in one place, instead of reading three differently-worded buttons. The store
/// shows the same dialog minus the first option — the store IS the global side, so taking something
/// from it there would mean taking it from itself.</para>
/// </summary>
/// <param name="CreateUrl">Where "eigenes erstellen" goes.</param>
/// <param name="StorePickerKind">Payload key of the store picker to open ("plugins", "templates",
/// "components", "users"), or null where there is nothing global to take from.</param>
/// <param name="ImportTargetId">Element id of the page's own import form, revealed by the third
/// option. Null where that payload has no separate import — for plugins the create page IS the
/// bundle upload, so a second route to it would only be a second name for the same thing.</param>
/// <param name="CreateLabelKey">Resource key for the create option's label.</param>
/// <param name="Id">Suffix that keeps ids unique on a page rendering several of these.</param>
public sealed record AddMenu(
    string Id, string CreateUrl, string CreateLabelKey,
    string? StorePickerKind = null, string? ImportTargetId = null);
