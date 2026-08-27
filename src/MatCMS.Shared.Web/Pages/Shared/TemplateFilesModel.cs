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
/// <para>
/// <c>Apply</c> and <c>Cancel</c> are gone with the modal they belonged to: the editor writes into
/// the posted field on every keystroke, so there is nothing left to confirm or to take back. The
/// resource keys stay where they are — this record only stops asking for them.
/// </para>
/// </summary>
/// <param name="Edit">"Bearbeiten" — the node's own menu entry for opening a file.</param>
/// <param name="Tokens">Label above the insertable placeholders.</param>
/// <param name="InsertDefault">Label of the "insert the default" command.</param>
/// <param name="InsertDefaultConfirm">Asked before the default replaces content the editor already
/// has — without the modal there is no "cancel" left to undo it with.</param>
/// <param name="BadgeCustom">Badge on a file that differs from its baseline.</param>
/// <param name="BadgeEmpty">Badge on a file that still comes from the theme.</param>
/// <param name="Pick">Shown in the editor pane while no file is open.</param>
/// <param name="Menu">Tooltip of the node menu ("…", right-click, context-menu key).</param>
/// <param name="Toggle">Tooltip of the expander in front of a node.</param>
/// <param name="Raw">Label of the switch to the plain form field of the open file.</param>
/// <param name="Root">Fallback label for the tree's root when the template has no name yet.</param>
/// <param name="Copy">"Inhalt kopieren" — puts the open file's text on the system clipboard. The only
/// one of the plugin editor's new file commands that means anything here: a template's files are
/// columns of the record, so renaming, moving, duplicating and pasting have nothing to act on.</param>
/// <param name="Copied">Confirmation shown after the content went to the clipboard.</param>
public sealed record TemplateFileLabels(
    string Edit, string Tokens, string InsertDefault, string InsertDefaultConfirm,
    string BadgeCustom, string BadgeEmpty,
    string Pick, string Menu, string Toggle, string Raw, string Root,
    string Copy, string Copied);

/// <summary>
/// The whole "Dateien" tab: the file tree, the fields the form posts and the editor that writes into
/// them. Shared so the CMS and the cloud offer the same editor — the pages differ only in which
/// fields their files post as.
/// </summary>
/// <param name="Intro">Raw HTML shown above the tree (may contain markup).</param>
/// <param name="Files">The pseudo-files, in display order — the tree keeps that order rather than
/// sorting, because <c>body.html</c> is where one starts.</param>
/// <param name="Labels">Wording for the tree and the editor.</param>
/// <param name="RootLabel">The tree's single root — the template's name. Empty falls back to
/// <see cref="TemplateFileLabels.Root"/>, which a template being created has to have.</param>
public sealed record TemplateFiles(
    string Intro, IReadOnlyList<TemplateFile> Files, TemplateFileLabels Labels, string RootLabel = "",
    TemplateAssetsPanel? Assets = null);

/// <summary>One uploaded template asset shown in the tree's "assets" folder. Unlike a pseudo-file it is
/// a REAL file (served at <see cref="Url"/>, referenced by <see cref="Token"/> = <c>{{asset:name}}</c>)
/// and is not editable inline — clicking it opens a small info/delete view, mirroring the plugin editor.</summary>
/// <param name="Id">Backing row id — put into the delete form so the server knows which file to drop.</param>
/// <param name="Kind">"image" | "js" | "css" | "font" | "file" — drives the icon/badge and preview.</param>
/// <param name="SizeText">Human-readable size, formatted by the page ("12 KB").</param>
public sealed record TemplateAssetEntry(int Id, string Name, string Url, string Kind, string SizeText, string Token);

/// <summary>
/// The optional "assets" folder for the template file tree: the template's uploaded files plus the
/// wiring to upload and delete them. Null on an editor with no asset storage (the cloud's template
/// views), so nothing extra renders there and those pages are unchanged.
/// <para>Upload and delete run through their own forms — multipart, and OUTSIDE the editor's save form —
/// which the page renders with the ids named here; the tree's controls reach them via the HTML
/// <c>form=</c> attribute (a form owner can sit elsewhere in the document).</para>
/// </summary>
/// <param name="FolderLabel">The folder's display name (e.g. "assets").</param>
/// <param name="Files">The uploaded assets.</param>
/// <param name="UploadFormId">Id of the page's multipart upload form the file input/button target.</param>
/// <param name="DeleteFormId">Id of the page's delete form; its hidden <paramref name="DeleteInputName"/>
/// gets the asset id before submit.</param>
public sealed record TemplateAssetsPanel(
    string FolderLabel, IReadOnlyList<TemplateAssetEntry> Files,
    string UploadFormId, string DeleteFormId, string DeleteInputName,
    string UploadLabel, string DeleteLabel, string UrlLabel, string TokenLabel,
    string EmptyLabel, string DeleteConfirm, string Help);
