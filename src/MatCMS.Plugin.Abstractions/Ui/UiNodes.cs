namespace MatCMS.Plugin;

// The component model. A plugin does not write HTML; it describes a screen as a tree of these records and
// the host draws it with the admin's own building blocks (or, for a public page, the site's). That is what
// makes a plugin's pages conform by construction — dark mode, phone layout, icons, dialogs, antiforgery —
// and lets the library change without a plugin being rebuilt. Plain immutable data, no framework behind it.

/// <summary>The tone of an alert, badge or tile.</summary>
public enum UiTone
{
    /// <summary>Neutral.</summary>
    Default,
    /// <summary>Greyed out: "draft", "off".</summary>
    Muted,
    /// <summary>Informative.</summary>
    Info,
    /// <summary>Something went well.</summary>
    Success,
    /// <summary>Needs attention.</summary>
    Warning,
    /// <summary>Something failed.</summary>
    Error
}

/// <summary>The look of a button.</summary>
public enum UiButtonKind
{
    /// <summary>The main action of a screen.</summary>
    Primary,
    /// <summary>A secondary action.</summary>
    Ghost,
    /// <summary>A destructive action.</summary>
    Danger
}

/// <summary>The input types a <see cref="UiInput"/> offers.</summary>
public enum UiInputType
{
    /// <summary>Single-line text.</summary>
    Text,
    /// <summary>E-mail address.</summary>
    Email,
    /// <summary>Web address.</summary>
    Url,
    /// <summary>A number.</summary>
    Number,
    /// <summary>A secret; the value is never sent back to the browser.</summary>
    Password,
    /// <summary>A date.</summary>
    Date,
    /// <summary>A date and time.</summary>
    DateTime,
    /// <summary>A colour.</summary>
    Color,
    /// <summary>A telephone number.</summary>
    Tel
}

/// <summary>Any component. Build them with the <see cref="Ui"/> factory.</summary>
public abstract record UiNode;

/// <summary>Components one below the other.</summary>
/// <param name="Children">The components.</param>
public sealed record UiStack(IReadOnlyList<UiNode> Children) : UiNode;

/// <summary>Components next to each other, wrapping on a narrow screen.</summary>
/// <param name="Children">The components.</param>
public sealed record UiInline(IReadOnlyList<UiNode> Children) : UiNode;

/// <summary>Components in a responsive grid of equal columns.</summary>
/// <param name="Children">The components.</param>
public sealed record UiGrid(IReadOnlyList<UiNode> Children) : UiNode
{
    /// <summary>The narrowest a column may get, in pixels, before the grid wraps. Default 240.</summary>
    public int MinWidth { get; init; } = 240;
}

/// <summary>A panel.</summary>
/// <param name="Children">The content.</param>
public sealed record UiCard(IReadOnlyList<UiNode> Children) : UiNode
{
    /// <summary>Optional heading.</summary>
    public string? Title { get; init; }
}

/// <summary>The head of a screen: title, an optional line below and actions at the right. In the back
/// office the topbar already shows the title, so a screen usually leaves it out and uses this for actions only.</summary>
public sealed record UiPageHead : UiNode
{
    /// <summary>Heading; null leaves it to the topbar.</summary>
    public string? Title { get; init; }
    /// <summary>A muted line.</summary>
    public string? Subtitle { get; init; }
    /// <summary>Buttons at the right.</summary>
    public IReadOnlyList<UiNode> Actions { get; init; } = Array.Empty<UiNode>();
}

/// <summary>A heading.</summary>
/// <param name="Text">The text.</param>
/// <param name="Level">2 to 4.</param>
public sealed record UiHeading(string Text, int Level = 2) : UiNode;

/// <summary>A paragraph.</summary>
/// <param name="Text">The text (encoded for you).</param>
public sealed record UiText(string Text) : UiNode
{
    /// <summary>Greyed out.</summary>
    public bool Muted { get; init; }
    /// <summary>Monospaced.</summary>
    public bool Mono { get; init; }
    /// <summary>Inline (no paragraph around it), for a table cell.</summary>
    public bool Inline { get; init; }
    /// <summary>Emphasised (the name in a list row).</summary>
    public bool Strong { get; init; }
}

/// <summary>A link in running text.</summary>
/// <param name="Text">The visible text.</param>
/// <param name="Href">The target.</param>
public sealed record UiLink(string Text, string Href) : UiNode
{
    /// <summary>Opens in a new tab.</summary>
    public bool NewTab { get; init; }
}

/// <summary>A picture, e.g. a thumbnail in a table.</summary>
/// <param name="Src">The address.</param>
/// <param name="Alt">The alternative text.</param>
public sealed record UiImg(string Src, string Alt = "") : UiNode
{
    /// <summary>Edge length in pixels of the square the picture is cropped into; 0 = natural size, at most the width of its container.</summary>
    public int Size { get; init; } = 48;
}

/// <summary>Raw HTML. The way out when no component fits; the tests warn about fixed colours and style blocks in it.
/// Never put visitor input in here unencoded.</summary>
/// <param name="Html">The markup, emitted as is.</param>
public sealed record UiHtml(string Html) : UiNode;

/// <summary>A status banner.</summary>
/// <param name="Text">The message.</param>
/// <param name="Tone">Its tone.</param>
public sealed record UiAlert(string Text, UiTone Tone = UiTone.Info) : UiNode;

/// <summary>A row of small pills, e.g. the tags of a post.</summary>
/// <param name="Items">The texts.</param>
public sealed record UiChips(IReadOnlyList<string> Items) : UiNode;

/// <summary>A small pill, e.g. "active".</summary>
/// <param name="Text">The text.</param>
/// <param name="Tone">Its tone.</param>
public sealed record UiBadge(string Text, UiTone Tone = UiTone.Default) : UiNode;

/// <summary>The "nothing here yet" state.</summary>
/// <param name="Text">What to say.</param>
public sealed record UiEmpty(string Text) : UiNode;

/// <summary>One line of a <see cref="UiKeyValue"/>.</summary>
/// <param name="Label">The name.</param>
/// <param name="Value">The value.</param>
public sealed record UiKeyValueItem(string Label, string Value);

/// <summary>Label/value pairs.</summary>
/// <param name="Items">The lines.</param>
public sealed record UiKeyValue(IReadOnlyList<UiKeyValueItem> Items) : UiNode;

/// <summary>One figure of a <see cref="UiStats"/> row.</summary>
/// <param name="Label">What it counts.</param>
/// <param name="Value">The figure.</param>
public sealed record UiStat(string Label, string Value)
{
    /// <summary>A small line below the figure.</summary>
    public string? Hint { get; init; }
    /// <summary>Warning or error colours the tile's edge.</summary>
    public UiTone Tone { get; init; }
    /// <summary>Makes the tile a link.</summary>
    public string? Href { get; init; }
}

/// <summary>A row of figures.</summary>
/// <param name="Items">The tiles.</param>
public sealed record UiStats(IReadOnlyList<UiStat> Items) : UiNode;

/// <summary>A link that looks like a button, or — inside a <see cref="UiForm"/> without an <see cref="Href"/> — a submit button.</summary>
/// <param name="Label">The text.</param>
public sealed record UiButton(string Label) : UiNode
{
    /// <summary>Target; null makes it a submit button of the surrounding form.</summary>
    public string? Href { get; init; }
    /// <summary>A Tabler icon name (<c>ti-plus</c>).</summary>
    public string? Icon { get; init; }
    /// <summary>The look.</summary>
    public UiButtonKind Kind { get; init; } = UiButtonKind.Primary;
    /// <summary>The compact size.</summary>
    public bool Small { get; init; }
    /// <summary>Only the icon is drawn (the label stays as tooltip and for screen readers). Needs an <see cref="Icon"/>.</summary>
    public bool IconOnly { get; init; }
    /// <summary>Submit buttons: the field name that tells several buttons of one form apart (usually <c>action</c>).</summary>
    public string? Name { get; init; }
    /// <summary>Submit buttons: the value sent under <see cref="Name"/>.</summary>
    public string? Value { get; init; }
    /// <summary>Asks first, in the admin's own dialog.</summary>
    public string? Confirm { get; init; }
    /// <summary>A link opens in a new tab.</summary>
    public bool NewTab { get; init; }
    /// <summary>Not a submit button although it has no <see cref="Href"/>: a button whose click a script handles (a <c>data-</c> hook).</summary>
    public bool NoSubmit { get; init; }
    /// <summary>Opens the <see cref="UiDialog"/> with this id instead of navigating.</summary>
    public string? Opens { get; init; }
    /// <summary><c>data-*</c> attributes (key without the prefix) — the documented hooks of the library's scripts.</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }
}

/// <summary>A one-button POST on its own: a row action such as "delete" or "publish", with the antiforgery token.</summary>
/// <param name="Label">The text.</param>
/// <param name="Hidden">The fields it posts (usually <c>action</c> and an id).</param>
public sealed record UiAction(string Label, IReadOnlyDictionary<string, string> Hidden) : UiNode
{
    /// <summary>The look.</summary>
    public UiButtonKind Kind { get; init; } = UiButtonKind.Ghost;
    /// <summary>A Tabler icon instead of text (the label stays as tooltip).</summary>
    public string? Icon { get; init; }
    /// <summary>Asks first.</summary>
    public string? Confirm { get; init; }
}

/// <summary>A modal dialog opened by a <see cref="UiButton"/> with <see cref="UiButton.Opens"/>.</summary>
/// <param name="Id">Its id; unique on the page.</param>
/// <param name="Title">The heading.</param>
/// <param name="Children">The content.</param>
public sealed record UiDialog(string Id, string Title, IReadOnlyList<UiNode> Children) : UiNode;

/// <summary>A form. Posts to the current page unless <see cref="Action"/> is set; the host adds the antiforgery token.</summary>
/// <param name="Children">Fields and buttons.</param>
public sealed record UiForm(IReadOnlyList<UiNode> Children) : UiNode
{
    /// <summary>Fields sent with every submit.</summary>
    public IReadOnlyDictionary<string, string>? Hidden { get; init; }
    /// <summary>Another local path to post to.</summary>
    public string? Action { get; init; }
    /// <summary>The form carries file inputs.</summary>
    public bool Upload { get; init; }
    /// <summary>Narrow column (a settings card) instead of the wide editor column.</summary>
    public bool Narrow { get; init; }
    /// <summary>An id for scripts and anchors.</summary>
    public string? Id { get; init; }
}

/// <summary>A row of the buttons at the bottom of a form.</summary>
/// <param name="Children">The buttons.</param>
public sealed record UiFormActions(IReadOnlyList<UiNode> Children) : UiNode;

/// <summary>A hidden form field.</summary>
/// <param name="Name">Field name.</param>
/// <param name="Value">Value.</param>
public sealed record UiHidden(string Name, string Value) : UiNode;

/// <summary>A labelled form field.</summary>
/// <param name="Name">The field name the value is posted under.</param>
/// <param name="Label">The label.</param>
public abstract record UiField(string Name, string Label) : UiNode
{
    /// <summary>A small help line below the field.</summary>
    public string? Help { get; init; }
    /// <summary>The form is not accepted without a value.</summary>
    public bool Required { get; init; }
    /// <summary>Not editable.</summary>
    public bool Disabled { get; init; }
    /// <summary>An id for scripts and for the label; generated from the name when empty.</summary>
    public string? Id { get; init; }
}

/// <summary>A single-line input.</summary>
/// <param name="Name">Field name.</param>
/// <param name="Label">Label.</param>
public sealed record UiInput(string Name, string Label) : UiField(Name, Label)
{
    /// <summary>Current value. Ignored for <see cref="UiInputType.Password"/>.</summary>
    public string Value { get; init; } = "";
    /// <summary>The kind of input.</summary>
    public UiInputType Type { get; init; }
    /// <summary>Greyed hint while empty.</summary>
    public string? Placeholder { get; init; }
    /// <summary>Lower bound of a number.</summary>
    public string? Min { get; init; }
    /// <summary>Upper bound of a number.</summary>
    public string? Max { get; init; }
    /// <summary>Step of a number (<c>0.01</c> for money).</summary>
    public string? Step { get; init; }
    /// <summary>Longest accepted text.</summary>
    public int MaxLength { get; init; }
}

/// <summary>A multi-line input.</summary>
/// <param name="Name">Field name.</param>
/// <param name="Label">Label.</param>
public sealed record UiTextArea(string Name, string Label) : UiField(Name, Label)
{
    /// <summary>Current value.</summary>
    public string Value { get; init; } = "";
    /// <summary>Visible lines.</summary>
    public int Rows { get; init; } = 4;
    /// <summary>Greyed hint while empty.</summary>
    public string? Placeholder { get; init; }
}

/// <summary>One choice of a <see cref="UiSelect"/>.</summary>
/// <param name="Value">The value posted.</param>
/// <param name="Label">The text shown.</param>
public sealed record UiOption(string Value, string Label);

/// <summary>One choice out of a list.</summary>
/// <param name="Name">Field name.</param>
/// <param name="Label">Label.</param>
public sealed record UiSelect(string Name, string Label) : UiField(Name, Label)
{
    /// <summary>The selected value.</summary>
    public string Value { get; init; } = "";
    /// <summary>The choices.</summary>
    public IReadOnlyList<UiOption> Options { get; init; } = Array.Empty<UiOption>();
}

/// <summary>A switch. Posts <c>true</c> when ticked and nothing when not — read it with <c>ctx.Request.Form.ContainsKey</c> or <c>F(name) == "true"</c>.</summary>
/// <param name="Name">Field name.</param>
/// <param name="Label">The text next to the box.</param>
public sealed record UiCheckbox(string Name, string Label) : UiField(Name, Label)
{
    /// <summary>Ticked.</summary>
    public bool Checked { get; init; }
}

/// <summary>An image from the media library, with preview and picker. Posts the file's URL.</summary>
/// <param name="Name">Field name.</param>
/// <param name="Label">Label.</param>
public sealed record UiImage(string Name, string Label) : UiField(Name, Label)
{
    /// <summary>The URL of the current image, or "".</summary>
    public string Value { get; init; } = "";
}

/// <summary>Several images (gallery) or files (attachments) from the media library, ordered and removable.
/// Posts a JSON array: <c>[{"url","alt"}]</c> for images, <c>[{"url","name"}]</c> for files.</summary>
/// <param name="Name">Field name.</param>
/// <param name="Label">Label.</param>
public sealed record UiMediaList(string Name, string Label) : UiField(Name, Label)
{
    /// <summary>The current list as the JSON described above.</summary>
    public string Value { get; init; } = "[]";
    /// <summary>Files (attachments) instead of images.</summary>
    public bool Files { get; init; }
}

/// <summary>Formatted text with a small toolbar. Posts HTML; the host sanitises nothing, so treat it like any editor input.</summary>
/// <param name="Name">Field name.</param>
/// <param name="Label">Label.</param>
public sealed record UiRichText(string Name, string Label) : UiField(Name, Label)
{
    /// <summary>The current HTML.</summary>
    public string Value { get; init; } = "";
    /// <summary>Minimum height in pixels.</summary>
    public int MinHeight { get; init; } = 220;
}

/// <summary>Tags as chips. Posts the tags comma-separated.</summary>
/// <param name="Name">Field name.</param>
/// <param name="Label">Label.</param>
public sealed record UiTags(string Name, string Label) : UiField(Name, Label)
{
    /// <summary>The current tags, comma-separated.</summary>
    public string Value { get; init; } = "";
    /// <summary>Existing tags offered as one-click chips.</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();
}

/// <summary>A column of a <see cref="UiTable"/>.</summary>
/// <param name="Header">The heading.</param>
public sealed record UiColumn(string Header)
{
    /// <summary>Right-aligned (numbers, money).</summary>
    public bool Right { get; init; }
    /// <summary>The cells carry actions: no wrapping, narrow.</summary>
    public bool Actions { get; init; }
}

/// <summary>A row of a <see cref="UiTable"/>.</summary>
/// <param name="Cells">One component per column.</param>
public sealed record UiTableRow(IReadOnlyList<UiNode> Cells)
{
    /// <summary>The text the live search matches against; the cells' own text when null.</summary>
    public string? Search { get; init; }
}

/// <summary>A table with optional live search and paging in the browser.</summary>
/// <param name="Columns">The columns.</param>
/// <param name="Rows">The rows.</param>
public sealed record UiTable(IReadOnlyList<UiColumn> Columns, IReadOnlyList<UiTableRow> Rows) : UiNode
{
    /// <summary>Shows a search box with this placeholder.</summary>
    public string? SearchPlaceholder { get; init; }
    /// <summary>Rows per page; 0 = all on one page.</summary>
    public int PageSize { get; init; }
    /// <summary>What to say when there are no rows (the header stays).</summary>
    public string EmptyText { get; init; } = "";
}

/// <summary>One tab of a <see cref="UiTabs"/>.</summary>
/// <param name="Label">The text.</param>
/// <param name="Href">Where it leads.</param>
/// <param name="Active">The current one.</param>
public sealed record UiTab(string Label, string Href, bool Active = false);

/// <summary>A tab bar for switching between views of one screen (the section's own tabs are drawn by the host).</summary>
/// <param name="Tabs">The tabs.</param>
public sealed record UiTabs(IReadOnlyList<UiTab> Tabs) : UiNode;

/// <summary>Links to other pages of a server-side list.</summary>
/// <param name="Page">The current page, from 1.</param>
/// <param name="Pages">The number of pages.</param>
/// <param name="HrefTemplate">The target with <c>{0}</c> for the page number, e.g. <c>?page={0}</c>.</param>
public sealed record UiPager(int Page, int Pages, string HrefTemplate) : UiNode;
