namespace MatCMS.Shared.Web;

/// <summary>A token the editor can insert at the cursor.</summary>
/// <param name="T">The token itself, e.g. <c>{{content}}</c>.</param>
/// <param name="L">What it does — shown as the button's tooltip.</param>
public sealed record TemplateToken(string T, string L);

/// <summary>
/// One pseudo-file in the template editor. They are not real files: each is a column on the template
/// row, presented as <c>body.html</c>, <c>styles.css</c> and so on because that is how somebody
/// editing a theme thinks about them.
/// </summary>
/// <param name="Name">Displayed file name.</param>
/// <param name="Ext">"html" | "css" | "js" — drives the coloured badge.</param>
/// <param name="Description">One line under the name.</param>
/// <param name="FieldName">Form field this file posts as (e.g. <c>LayoutHtml</c>,
/// <c>Parts[post]</c>). The hidden textarea carrying it is what the form actually submits.</param>
/// <param name="Value">Current content.</param>
/// <param name="Mode">CodeMirror mode: "htmlmixed", "css" or "javascript".</param>
/// <param name="Tokens">Insertable tokens offered under the editor.</param>
/// <param name="InsertDefault">Content for the "insert the default" button; empty = no button.</param>
/// <param name="Baseline">Content that counts as "not customised" — a file equal to it is badged
/// empty. Usually "" , but a part that ships with a default has that default as its baseline.</param>
public sealed record TemplateFile(
    string Name, string Ext, string Description, string FieldName, string? Value,
    string Mode, IReadOnlyList<TemplateToken> Tokens,
    string InsertDefault = "", string Baseline = "");

/// <summary>
/// The editor's own wording. Passed in rather than looked up, because this partial is shared and
/// the two applications have separate <c>Localizer</c> types — a shared view cannot reference
/// either. The resource KEYS are identical in both (<c>tplfiles.*</c>); only the lookup happens in
/// the page.
/// </summary>
public sealed record TemplateFileLabels(
    string Edit, string Tokens, string InsertDefault, string Apply, string Cancel,
    string BadgeCustom, string BadgeEmpty);

/// <summary>
/// The whole "Dateien" tab: the file list, the hidden fields the form posts, and the CodeMirror
/// modal that edits them. Shared so the CMS and the cloud offer the same editor — the pages differ
/// only in which fields their files post as.
/// </summary>
/// <param name="Intro">Raw HTML shown above the list (may contain markup).</param>
/// <param name="Files">The pseudo-files, in display order.</param>
/// <param name="Labels">Wording for the list and the modal.</param>
public sealed record TemplateFiles(string Intro, IReadOnlyList<TemplateFile> Files, TemplateFileLabels Labels);
