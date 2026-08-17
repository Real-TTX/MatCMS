namespace MatCMS.Shared.Web;

/// <summary>
/// The picker's own wording. Passed in rather than looked up, because this partial is shared and the
/// two applications have separate <c>Localizer</c> types — a shared view cannot reference either.
/// The resource KEYS are identical in both (<c>icons.*</c>); only the lookup happens in the page.
/// </summary>
/// <param name="Pick">Tooltip and accessible name of the button that shows the current icon.</param>
/// <param name="DialogTitle">Heading of the dialog.</param>
/// <param name="Search">Placeholder of the dialog's search box.</param>
/// <param name="None">Label of "no icon" — both the button next to the preview and the entry that
/// clears the selection inside the dialog.</param>
/// <param name="NoResults">Shown in the grid when the search matches nothing.</param>
/// <param name="Apply">The one button that writes the choice into the posted field.</param>
/// <param name="Cancel">Leaves the field exactly as it was.</param>
/// <param name="Unknown">Note next to a stored name the shipped font does not contain (any more).
/// It is SHOWN and KEPT, never silently dropped — see the partial for why.</param>
/// <param name="Current">Note under the icon pinned at the top of the grid: the one that is set.
/// Pinning it is what lets the dialog open without drawing its way down to it.</param>
/// <param name="Empty">What the name line reads when no icon is set.</param>
/// <param name="Count">"{0} von {1}" — how much of the filtered list is drawn so far.</param>
public sealed record IconFieldLabels(
    string Pick, string DialogTitle, string Search, string None, string NoResults,
    string Apply, string Cancel, string Unknown, string Current, string Empty, string Count);

/// <summary>
/// A Tabler-icon field: the current icon as a BUTTON, a dialog to pick another one, and the plain
/// text field that is what actually gets posted.
/// <para>
/// Shared so the CMS and the cloud offer the same picker — the pages differ only in what their field
/// posts as (<c>Icon</c> in the CMS, <c>icon</c> in the cloud), which is why <see cref="FieldName"/>
/// is on the model.
/// </para>
/// </summary>
/// <param name="Value">The stored value. Rendered as-is: a name the font no longer has must survive
/// an open-and-cancel unchanged, so nothing is normalised or dropped here.</param>
/// <param name="FieldName">Name of the posted form field. Keeps whatever the page used before —
/// the dialog writes INTO that field, it does not replace it.</param>
/// <param name="Label">Field label.</param>
/// <param name="Labels">Wording for the button and the dialog.</param>
/// <param name="Help">Optional hint under the field (rendered as text, not markup).</param>
/// <param name="WhenSelectId">Optional: id of a &lt;select&gt; that decides whether this field is
/// shown at all. Used by the menu-item form, where an icon only means something for the toolbar
/// menu. Empty = always visible.</param>
/// <param name="WhenSelectValue">The value of that select which makes the field visible.</param>
public sealed record IconField(
    string? Value, string FieldName, string Label, IconFieldLabels Labels,
    string Help = "", string WhenSelectId = "", string WhenSelectValue = "");
