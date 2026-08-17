namespace MatCMS.Shared.Web;

/// <summary>
/// The editor's own wording. Passed in rather than looked up, for the same reason as
/// <see cref="TemplateFileLabels"/>: this partial is shared and the two applications have separate
/// <c>Localizer</c> types — a shared view cannot reference either. Only the lookup happens in the
/// page's thin adapter; the markup lives once.
/// </summary>
/// <param name="TabGeneral">First tab — what every component starts with.</param>
/// <param name="TabFields">Second tab — the field designer.</param>
/// <param name="TabTemplate">Third tab — the HTML template and its live preview.</param>
/// <param name="Name">Label of the display name.</param>
/// <param name="Type">Label of the type/identity.</param>
/// <param name="TypeHelp">One line under the type: what it is for and whether it can still change.</param>
/// <param name="Description">Label of the free description.</param>
/// <param name="FieldsHelp">Paragraph above the field rows — it explains where the placeholders
/// come from.</param>
/// <param name="AddField">Caption of the "add a field" button (without the leading plus).</param>
/// <param name="TemplateLabel">Label above the template textarea.</param>
/// <param name="TemplateHelp">Line under the template textarea; the live placeholder list is
/// appended to it by the editor script.</param>
/// <param name="SampleData">Line above the sample inputs — it has to say that these values are for
/// looking at and are NOT saved.</param>
/// <param name="Preview">Names the preview pane and the frame (its accessible title).</param>
/// <param name="Debug">Caption of the switch that opens the placeholder/output panel.</param>
public sealed record ComponentEditorLabels(
    string TabGeneral, string TabFields, string TabTemplate,
    string Name, string Type, string TypeHelp, string Description,
    string FieldsHelp, string AddField,
    string TemplateLabel, string TemplateHelp,
    string SampleData, string Preview, string Debug);

/// <summary>
/// What each field posts as. The CMS and the cloud write into different records and have always
/// used different names for them (<c>Name</c> vs <c>name</c>, <c>FieldsJson</c> vs
/// <c>fieldsJson</c>) — so the names travel with the model, exactly like
/// <see cref="TemplateFile.FieldName"/>. The element IDs do NOT: <c>FieldsJson</c>,
/// <c>TemplateHtml</c>, <c>field-rows</c>, <c>cp-*</c> are what the editor script binds to, they are
/// the same on both sides and they stay hard-coded in the partial.
/// </summary>
public sealed record ComponentEditorNames(
    string Name, string Type, string Description, string FieldsJson, string TemplateHtml);

/// <summary>
/// The whole component editor: the three tabs, the fields the form posts, the field designer and the
/// live preview. Shared so the CMS and the cloud offer the SAME editor — a component authored in a
/// profile has to behave exactly like one authored on an instance, and three copies of this markup
/// meant every change had to be made three times (and was, twice, until one of them was forgotten).
/// <para>
/// It renders everything INSIDE the form and nothing around it. The <c>&lt;form&gt;</c>, the card and
/// the action row stay in the page, because they genuinely differ: the cloud's form carries route
/// values and has its save button outside (a delete form may not nest inside it), the CMS's does not.
/// The rule the pages have to keep is the old one — every tab panel stays INSIDE the form. A hidden
/// tab field is still submitted; a field outside the form is not, and would arrive EMPTY and
/// overwrite what is stored.
/// </para>
/// </summary>
/// <param name="Names">What each field posts as.</param>
/// <param name="Labels">The wording, looked up by the page.</param>
/// <param name="NameValue">Current display name.</param>
/// <param name="TypeValue">Current type — the identity a placed block refers to.</param>
/// <param name="DescriptionValue">Current description.</param>
/// <param name="FieldsJson">The stored field list; the designer seeds its rows from it and writes
/// it back into the posted field on every keystroke.</param>
/// <param name="TemplateHtml">The stored HTML template.</param>
/// <param name="IconPicker">The application's OWN icon-picker view model, rendered through its own
/// <c>_IconPicker</c> partial. Deliberately untyped: the two applications each have their own
/// <c>IconPickVm</c>, and the CMS's adapter additionally resolves legacy icon aliases
/// (<c>MenuIcons</c>), which the cloud must not do. Handing the shared view a common type would have
/// meant choosing one of those behaviours for both.</param>
/// <param name="TypeReadOnly">True where the type cannot be changed any more (the CMS edits an
/// existing component, and re-typing its identity would orphan every block already placed with it).
/// False in the cloud, where the type is what the operator enters when creating one.</param>
/// <param name="CodeMirror">True to upgrade the template textarea to CodeMirror
/// (<c>data-code="html"</c>). The cloud ships the bundle on this page, the CMS does not — and a
/// data-code field without the bundle is just a textarea, so this is a fact about the page, not a
/// preference.</param>
/// <param name="TypePlaceholder">Example type shown in the empty field; only useful where the type
/// is still editable.</param>
public sealed record ComponentEditor(
    ComponentEditorNames Names,
    ComponentEditorLabels Labels,
    string? NameValue,
    string? TypeValue,
    string? DescriptionValue,
    string? FieldsJson,
    string? TemplateHtml,
    object? IconPicker = null,
    bool TypeReadOnly = false,
    bool CodeMirror = false,
    string TypePlaceholder = "");
