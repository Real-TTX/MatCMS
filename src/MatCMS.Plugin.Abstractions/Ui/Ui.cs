namespace MatCMS.Plugin;

/// <summary>
/// Builds component trees. Everything returns a plain record, so a detail is set with <c>with</c>:
/// <c>Ui.Input("title", "Title") with { Required = true }</c>. Text arguments are encoded by the renderer;
/// the one place that is not is <see cref="Html"/>.
/// </summary>
public static class Ui
{
    private static IReadOnlyList<UiNode> L(IEnumerable<UiNode?> nodes) => nodes.Where(n => n is not null).Select(n => n!).ToList();

    // ---- structure --------------------------------------------------------------------------------

    /// <summary>Components one below the other.</summary>
    public static UiStack Stack(params UiNode?[] children) => new(L(children));
    /// <summary>Components next to each other.</summary>
    public static UiInline Inline(params UiNode?[] children) => new(L(children));
    /// <summary>A responsive grid.</summary>
    public static UiGrid Grid(int minWidth, params UiNode?[] children) => new(L(children)) { MinWidth = minWidth };
    /// <summary>An entry of a screen's sub-menu.</summary>
    public static UiNavItem NavItem(string label, string href, string? icon = null, bool active = false, string? heading = null, string? badge = null) =>
        new(label, href) { Icon = icon, Active = active, Heading = heading, Badge = badge };
    /// <summary>A screen with a sub-menu at the left and the content at the right.</summary>
    public static UiSplit Split(IEnumerable<UiNavItem> nav, params UiNode?[] children) => new(nav.ToList(), L(children));
    /// <summary>A main column with a side column.</summary>
    public static UiColumns Columns(IEnumerable<UiNode?> main, IEnumerable<UiNode?> side) => new(L(main), L(side));
    /// <summary>A panel with an optional heading.</summary>
    public static UiCard Card(string? title, params UiNode?[] children) => new(L(children)) { Title = title };
    /// <summary>A panel without a heading.</summary>
    public static UiCard Card(params UiNode?[] children) => new(L(children));
    /// <summary>The head of a screen: actions at the right, an optional title and subtitle.</summary>
    public static UiPageHead PageHead(string? subtitle = null, string? title = null, params UiNode?[] actions) =>
        new() { Title = title, Subtitle = subtitle, Actions = L(actions) };

    // ---- text and feedback ------------------------------------------------------------------------

    /// <summary>A heading (level 2 to 4).</summary>
    public static UiHeading Heading(string text, int level = 2) => new(text, Math.Clamp(level, 2, 4));
    /// <summary>A paragraph.</summary>
    public static UiText Text(string text) => new(text);
    /// <summary>A greyed-out paragraph.</summary>
    public static UiText Muted(string text) => new(text) { Muted = true };
    /// <summary>A link in running text.</summary>
    public static UiLink Link(string text, string href, bool newTab = false) => new(text, href) { NewTab = newTab };
    /// <summary>A picture.</summary>
    public static UiImg Img(string src, string alt = "", int size = 48) => new(src, alt) { Size = size };
    /// <summary>Raw HTML — the way out, see <see cref="UiHtml"/>.</summary>
    public static UiHtml Html(string html) => new(html);
    /// <summary>A status banner.</summary>
    public static UiAlert Alert(string text, UiTone tone = UiTone.Info) => new(text, tone);
    /// <summary>A row of small pills (tags).</summary>
    public static UiChips Chips(IEnumerable<string> items) => new(items.ToList());
    /// <summary>A small pill.</summary>
    public static UiBadge Badge(string text, UiTone tone = UiTone.Default) => new(text, tone);
    /// <summary>The "nothing here yet" state.</summary>
    public static UiEmpty Empty(string text) => new(text);
    /// <summary>Label/value pairs.</summary>
    public static UiKeyValue KeyValue(params (string Label, string Value)[] items) =>
        new(items.Select(i => new UiKeyValueItem(i.Label, i.Value)).ToList());
    /// <summary>One figure.</summary>
    public static UiStat Stat(string label, string value, string? hint = null, UiTone tone = UiTone.Default, string? href = null) =>
        new(label, value) { Hint = hint, Tone = tone, Href = href };
    /// <summary>A row of figures.</summary>
    public static UiStats Stats(params UiStat[] items) => new(items);

    // ---- actions ----------------------------------------------------------------------------------

    /// <summary>A link that looks like a button.</summary>
    public static UiButton Button(string label, string href, UiButtonKind kind = UiButtonKind.Primary, string? icon = null, bool small = false) =>
        new(label) { Href = href, Kind = kind, Icon = icon, Small = small };

    /// <summary>A submit button for the surrounding form. <paramref name="name"/>/<paramref name="value"/> tell several buttons apart (<c>action</c> = <c>save</c>).</summary>
    public static UiButton Submit(string label, UiButtonKind kind = UiButtonKind.Primary, string? name = null, string? value = null,
        string? confirm = null, string? icon = null, bool small = false) =>
        new(label) { Kind = kind, Name = name, Value = value, Confirm = confirm, Icon = icon, Small = small };

    /// <summary>A one-button POST (row action) with the fields it posts.</summary>
    public static UiAction Action(string label, IReadOnlyDictionary<string, string> hidden, UiButtonKind kind = UiButtonKind.Ghost,
        string? confirm = null, string? icon = null) =>
        new(label, hidden) { Kind = kind, Confirm = confirm, Icon = icon };

    /// <summary>A one-button POST that sends <c>action=</c><paramref name="action"/> and, when given, <c>id=</c><paramref name="id"/>.</summary>
    public static UiAction Action(string label, string action, string? id = null, UiButtonKind kind = UiButtonKind.Ghost,
        string? confirm = null, string? icon = null)
    {
        var d = new Dictionary<string, string> { ["action"] = action };
        if (id is not null) d["id"] = id;
        return new UiAction(label, d) { Kind = kind, Confirm = confirm, Icon = icon };
    }

    /// <summary>A modal dialog; open it with a button whose <see cref="UiButton.Opens"/> is the same id.</summary>
    public static UiDialog Dialog(string id, string title, params UiNode?[] children) => new(id, title, L(children));

    // ---- forms ------------------------------------------------------------------------------------

    /// <summary>A form posting to the current page.</summary>
    public static UiForm Form(params UiNode?[] children) => new(L(children));
    /// <summary>A row of buttons at the bottom of a form.</summary>
    public static UiFormActions FormActions(params UiNode?[] children) => new(L(children));
    /// <summary>A hidden field.</summary>
    public static UiHidden Hidden(string name, string value) => new(name, value);
    /// <summary>A single-line input.</summary>
    public static UiInput Input(string name, string label, string value = "", UiInputType type = UiInputType.Text) =>
        new(name, label) { Value = value ?? "", Type = type };
    /// <summary>A multi-line input.</summary>
    public static UiTextArea TextArea(string name, string label, string value = "", int rows = 4) =>
        new(name, label) { Value = value ?? "", Rows = rows };
    /// <summary>One choice out of a list.</summary>
    public static UiSelect Select(string name, string label, string value, params UiOption[] options) =>
        new(name, label) { Value = value ?? "", Options = options };
    /// <summary>A choice for a <see cref="Select"/>.</summary>
    public static UiOption Option(string value, string label) => new(value, label);
    /// <summary>A switch.</summary>
    public static UiCheckbox Checkbox(string name, string label, bool isChecked = false) => new(name, label) { Checked = isChecked };
    /// <summary>A file picked on the visitor's computer (a form made with <c>Form(...) with { Upload = true }</c>). Arrives in <c>ctx.Request.Files</c>.</summary>
    public static UiFile File(string name, string label, string? accept = null) => new(name, label) { Accept = accept };
    /// <summary>An image from the media library.</summary>
    public static UiImage Image(string name, string label, string value = "") => new(name, label) { Value = value ?? "" };
    /// <summary>Several images or files from the media library.</summary>
    public static UiMediaList MediaList(string name, string label, string valueJson = "[]", bool files = false) =>
        new(name, label) { Value = string.IsNullOrWhiteSpace(valueJson) ? "[]" : valueJson, Files = files };
    /// <summary>Formatted text.</summary>
    public static UiRichText RichText(string name, string label, string value = "") => new(name, label) { Value = value ?? "" };
    /// <summary>Tags as chips.</summary>
    public static UiTags Tags(string name, string label, string value = "", IEnumerable<string>? suggestions = null) =>
        new(name, label) { Value = value ?? "", Suggestions = suggestions?.ToList() ?? new List<string>() };

    /// <summary>A column of a row editor.</summary>
    public static UiRowColumn RowColumn(string key, string header, UiRowCell kind = UiRowCell.Text, string? placeholder = null, string? width = null) =>
        new(key, header) { Kind = kind, Placeholder = placeholder, Width = width };
    /// <summary>A row of a row editor.</summary>
    public static UiRowValue RowValue(string id, params string[] values) => new(id ?? "", values);
    /// <summary>Rows to edit in place — add, change, delete (see <see cref="UiRowEditor"/> for what is posted).</summary>
    public static UiRowEditor RowEditor(string name, string label, IEnumerable<UiRowColumn> columns, IEnumerable<UiRowValue> rows, string addLabel = "+") =>
        new(name, label) { Columns = columns.ToList(), Rows = rows.ToList(), AddLabel = addLabel };

    // ---- lists ------------------------------------------------------------------------------------

    /// <summary>A column.</summary>
    public static UiColumn Column(string header, bool right = false, bool actions = false) => new(header) { Right = right, Actions = actions };

    /// <summary>A row. A string becomes text, a <see cref="UiNode"/> stays as is, anything else is shown with <c>ToString()</c>.</summary>
    public static UiTableRow Row(params object?[] cells) => new(cells.Select(Cell).ToList());

    /// <summary>A row with the text its live search should match.</summary>
    public static UiTableRow RowFor(string search, params object?[] cells) => new(cells.Select(Cell).ToList()) { Search = search };

    private static UiNode Cell(object? c) => c switch
    {
        UiNode n => n,
        null => new UiText("") { Inline = true },
        _ => new UiText(Convert.ToString(c, System.Globalization.CultureInfo.InvariantCulture) ?? "") { Inline = true }
    };

    /// <summary>A table.</summary>
    public static UiTable Table(IEnumerable<UiColumn> columns, IEnumerable<UiTableRow> rows, string? searchPlaceholder = null,
        int pageSize = 0, string emptyText = "") =>
        new(columns.ToList(), rows.ToList()) { SearchPlaceholder = searchPlaceholder, PageSize = pageSize, EmptyText = emptyText };

    /// <summary>A filter for a table (see <see cref="UiListFilter"/>); the rows name their values with <see cref="UiTableRow.Tags"/>.</summary>
    public static UiListFilter ListFilter(string label, params (string Value, string Text)[] options) => new(label, options);

    /// <summary>A tab bar.</summary>
    public static UiTabs Tabs(params UiTab[] tabs) => new(tabs);
    /// <summary>A tab.</summary>
    public static UiTab Tab(string label, string href, bool active = false) => new(label, href, active);
    /// <summary>Links to the pages of a list.</summary>
    public static UiPager Pager(int page, int pages, string hrefTemplate) => new(page, pages, hrefTemplate);
}
