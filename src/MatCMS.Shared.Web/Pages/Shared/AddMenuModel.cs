namespace MatCMS.Shared.Web;

/// <summary>
/// One way of adding something, as an equally-weighted choice in the dialog.
/// </summary>
/// <param name="Title">The choice, named after what happens.</param>
/// <param name="Sub">One line saying what it means.</param>
/// <param name="Href">Where it goes. Null makes it a button that only acts through
/// <paramref name="Data"/> — revealing an import form, opening another dialog.</param>
/// <param name="Data">Extra <c>data-*</c> attributes, keyed WITHOUT the prefix
/// (<c>"add-import"</c> becomes <c>data-add-import</c>). This is what keeps the partial shared: the
/// CMS opens its cloud catalogue with a link, the cloud opens its store picker with an attribute its
/// own script listens for, and neither concept has to be named here.</param>
/// <param name="Icon">Tabler-Klasse, z. B. "ti-plus". Optional: eine Auswahl mit Symbolen liest sich
/// auf einen Blick, aber nicht jede Frage hat Antworten, die sich sinnvoll bebildern lassen.</param>
public sealed record AddOption(
/// <param name="Muted">Zeigt die Möglichkeit, ohne sie anzupreisen: sie ist gerade nicht nutzbar,
/// aber sie soll auffindbar bleiben. Der Href führt dann dorthin, wo man sie freischaltet — eine
/// ausgegraute Zeile, die nichts tut, wäre wieder eine Sackgasse.</param>
    string Title, string Sub, string? Href = null, IReadOnlyDictionary<string, string>? Data = null,
    string? Icon = null, bool Muted = false);

/// <summary>
/// The single "Hinzufügen" entry point under a list, and the ways it offers.
/// <para>One button instead of a row of them: somebody adding something decides WHAT KIND of
/// addition it is once, in one place, instead of reading three differently-worded buttons and a bare
/// file input.</para>
/// <para>Wording is passed in rather than looked up — a shared view cannot reference either
/// application's <c>Localizer</c> type, though the resource keys are the same in both.</para>
/// </summary>
/// <param name="Id">Ties the button to its dialog; must be unique on the page.</param>
/// <param name="WithButton">False renders the dialog alone. That is how a question gets a SECOND
/// step: an option carries <c>Data = { ["add-menu"] = "&lt;other id&gt;" }</c>, which closes this
/// dialog and opens that one — no new concept, the driver already does both halves. A step-two
/// dialog has no button of its own because nothing but the first step may open it.</param>
public sealed record AddMenu(
    string Id, string ButtonLabel, string DialogTitle, string CloseLabel,
    IReadOnlyList<AddOption> Options, bool WithButton = true);
