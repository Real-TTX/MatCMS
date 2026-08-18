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
/// <param name="Remove">Title of the trash button on a field row. The SCRIPT writes this one, not
/// the markup — the rows are built in JavaScript.</param>
/// <param name="DbgPlaceholders">Debug row: every placeholder the template uses.</param>
/// <param name="DbgUnknown">Debug row: placeholders no field defines — the mistake this panel
/// exists for.</param>
/// <param name="DbgUnused">Debug row: fields the template never uses.</param>
/// <param name="DbgOutput">Debug row: heading above the rendered HTML.</param>
/// <param name="DbgOk">Shown when no placeholder is unknown.</param>
/// <param name="DbgEmpty">Stands in for an empty debug row.</param>
public sealed record ComponentEditorLabels(
    string TabGeneral, string TabFields, string TabTemplate,
    string Name, string Type, string TypeHelp, string Description,
    string FieldsHelp, string AddField,
    string TemplateLabel, string TemplateHelp,
    string SampleData, string Preview, string Debug,
    string Remove, string DbgPlaceholders, string DbgUnknown,
    string DbgUnused, string DbgOutput, string DbgOk, string DbgEmpty);

/// <summary>
/// One selectable field type: the value stored in the field list and the wording the dropdown shows.
/// The list travels from the page because only the application can translate it — and the SCRIPT
/// needs it even where no dropdown is ever seen (the thumbnail page), because the type decides
/// whether a sample value is escaped or inserted as HTML.
/// </summary>
public sealed record ComponentFieldType(string Value, string Label);

/// <summary>
/// The colours and fonts the live preview draws in. This is the one place the two applications
/// genuinely disagree, so it is a parameter rather than a branch: the CMS shows its admin's own
/// palette, while the cloud borrows the theme of the template the profile activates, so a block is
/// judged in the design it will actually live in.
/// <para>Every value is optional. What is not given is NOT emitted rather than defaulted here —
/// <see cref="Accent2"/> is set by the CMS alone, and inventing one for the cloud would silently
/// overwrite the stylesheet's own.</para>
/// </summary>
public sealed record ComponentPreviewTheme(
    string? Accent = null, string? AccentDark = null, string? Accent2 = null,
    string? Heading = null, string? Text = null,
    string? Background = null, string? AltBackground = null,
    string? ContainerWidth = null, string? ButtonRadius = null,
    string? HeadingFont = null, string? BodyFont = null);

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
/// <param name="FieldTypes">The selectable field types, translated by the page.</param>
/// <param name="PreviewTheme">The design the live preview draws in. Null means the script's neutral
/// defaults.</param>
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
    string TypePlaceholder = "",
    IReadOnlyList<ComponentFieldType>? FieldTypes = null,
    ComponentPreviewTheme? PreviewTheme = null)
{
    /// <summary>The field types as the script reads them: <c>[[value, label], …]</c>.</summary>
    public string FieldTypesJson => ComponentEditorJson.FieldTypes(FieldTypes);

    /// <summary>The wording the SCRIPT writes (the rows are built in JavaScript, so these cannot sit
    /// in the markup).</summary>
    public string ScriptLabelsJson => ComponentEditorJson.Labels(Labels);

    /// <summary>The preview's colours and fonts.</summary>
    public string PreviewThemeJson => ComponentEditorJson.Theme(PreviewTheme);
}

/// <summary>
/// Turns the editor's parameters into the JSON that rides on <c>#field-rows</c> as
/// <c>data-field-types</c>, <c>data-labels</c> and <c>data-preview-theme</c>.
/// <para>Why on the ELEMENT and no longer in <c>window.MATCMS_*</c> / <c>window.CLOUD_*</c>: the
/// script now lives once, so its only interface must not depend on which application rendered the
/// page. It is also the only interface a page without the shared partial can serve — the component
/// thumbnail (<c>Admin/ComponentPreview</c>) writes the same attributes by hand and runs the same
/// renderer, which is what keeps a tile and the editor from disagreeing about a component.</para>
/// <para>Public and static so that page can reach it without building a whole
/// <see cref="ComponentEditor"/>.</para>
/// </summary>
public static class ComponentEditorJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        // Nulls are LEFT OUT rather than written: the script tells "not given" from "given empty"
        // and only fills in a default for the former.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string FieldTypes(IReadOnlyList<ComponentFieldType>? types) =>
        System.Text.Json.JsonSerializer.Serialize(
            (types ?? []).Select(t => new[] { t.Value, t.Label }), Options);

    public static string Labels(ComponentEditorLabels labels) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["remove"] = labels.Remove,
            ["dbgPlaceholders"] = labels.DbgPlaceholders,
            ["dbgUnknown"] = labels.DbgUnknown,
            ["dbgUnused"] = labels.DbgUnused,
            ["dbgOutput"] = labels.DbgOutput,
            ["dbgOk"] = labels.DbgOk,
            ["dbgEmpty"] = labels.DbgEmpty
        }, Options);

    public static string Theme(ComponentPreviewTheme? theme) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["accent"] = theme?.Accent,
            ["accentDark"] = theme?.AccentDark,
            ["accent2"] = theme?.Accent2,
            ["heading"] = theme?.Heading,
            ["text"] = theme?.Text,
            ["background"] = theme?.Background,
            ["altBackground"] = theme?.AltBackground,
            ["containerWidth"] = theme?.ContainerWidth,
            ["buttonRadius"] = theme?.ButtonRadius,
            ["headingFont"] = theme?.HeadingFont,
            ["bodyFont"] = theme?.BodyFont
        }, Options);
}
