using System.Text.Json.Nodes;

namespace MatCMS.Plugin;

// What a plugin declares besides routes and screens (settings, events, jobs, lifecycle, strings, extension points)
// and what the context offers at run time. Declarations happen in Configure and have no side effects; the host
// reads them when the plugin is loaded and forgets them when it is unloaded.

// ---- settings -------------------------------------------------------------------------------------------------

/// <summary>The kinds of setting a plugin can declare. The admin draws the form from them.</summary>
public enum SettingKind
{
    /// <summary>One line of text.</summary>
    Text,
    /// <summary>Several lines of text.</summary>
    TextArea,
    /// <summary>A number.</summary>
    Number,
    /// <summary>A switch.</summary>
    Bool,
    /// <summary>One choice out of <see cref="SettingSpec.Options"/>.</summary>
    Choice,
    /// <summary>A secret (API key, password). Stored encrypted, never sent back to the browser.</summary>
    Secret,
    /// <summary>A picture chosen from the media library (or typed as an address): stored as a local path or an https address, empty = none.</summary>
    Image,
    /// <summary>A colour, stored as <c>#rrggbb</c>; <see cref="SettingSpec.Default"/> is what the picker starts with (a colour field cannot be empty).</summary>
    Color
}

/// <summary>One declared setting.</summary>
/// <param name="Key">The name it is stored and read under.</param>
/// <param name="Label">The label in the form.</param>
/// <param name="Kind">The kind of control.</param>
public sealed record SettingSpec(string Key, string Label, SettingKind Kind = SettingKind.Text)
{
    /// <summary>The value while nothing was saved.</summary>
    public string Default { get; init; } = "";
    /// <summary>A help line under the field.</summary>
    public string? Help { get; init; }
    /// <summary>The choices of a <see cref="SettingKind.Choice"/>.</summary>
    public IReadOnlyList<FieldOption> Options { get; init; } = Array.Empty<FieldOption>();
    /// <summary>Lower bound of a number.</summary>
    public double? Min { get; init; }
    /// <summary>Upper bound of a number.</summary>
    public double? Max { get; init; }
    /// <summary>A value is required.</summary>
    public bool Required { get; init; }
    /// <summary>A heading the setting is grouped under in the form. Name it on the first setting of the group: the settings after it, without a name, belong to it until the next one is named.</summary>
    public string? Group { get; init; }
}

/// <summary>Declares the settings of a plugin. The host shows them as a form (a tab of the plugin's section, and under
/// Plugins), validates what is saved and offers the values through <see cref="IPluginContext.Settings"/>.</summary>
public interface ISettingsBuilder
{
    /// <summary>Declares a setting.</summary>
    ISettingsBuilder Add(SettingSpec spec);
}

/// <summary>Shorthands for declaring settings.</summary>
public static class SettingsBuilderExtensions
{
    /// <summary>A one-line text setting.</summary>
    public static ISettingsBuilder Text(this ISettingsBuilder s, string key, string label, string def = "", string? help = null, bool required = false) =>
        s.Add(new SettingSpec(key, label) { Default = def, Help = help, Required = required });
    /// <summary>A multi-line text setting.</summary>
    public static ISettingsBuilder TextArea(this ISettingsBuilder s, string key, string label, string def = "", string? help = null) =>
        s.Add(new SettingSpec(key, label, SettingKind.TextArea) { Default = def, Help = help });
    /// <summary>A number setting.</summary>
    public static ISettingsBuilder Number(this ISettingsBuilder s, string key, string label, double def = 0, double? min = null, double? max = null, string? help = null) =>
        s.Add(new SettingSpec(key, label, SettingKind.Number) { Default = def.ToString(System.Globalization.CultureInfo.InvariantCulture), Min = min, Max = max, Help = help });
    /// <summary>A switch.</summary>
    public static ISettingsBuilder Bool(this ISettingsBuilder s, string key, string label, bool def = false, string? help = null) =>
        s.Add(new SettingSpec(key, label, SettingKind.Bool) { Default = def ? "true" : "false", Help = help });
    /// <summary>A choice out of a list.</summary>
    public static ISettingsBuilder Choice(this ISettingsBuilder s, string key, string label, string def, IEnumerable<FieldOption> options, string? help = null) =>
        s.Add(new SettingSpec(key, label, SettingKind.Choice) { Default = def, Options = options.ToList(), Help = help });
    /// <summary>A secret.</summary>
    public static ISettingsBuilder Secret(this ISettingsBuilder s, string key, string label, string? help = null) =>
        s.Add(new SettingSpec(key, label, SettingKind.Secret) { Help = help });
}

/// <summary>A plugin's saved settings at run time.</summary>
public interface IPluginSettings
{
    /// <summary>A value as saved, else the declared default, else "". Secrets come back decrypted.</summary>
    string Get(string key);
    /// <summary>A switch: true for <c>true</c>, <c>1</c>, <c>on</c>, <c>yes</c>.</summary>
    bool GetBool(string key);
    /// <summary>A whole number, or <paramref name="fallback"/> when it is not one.</summary>
    long GetInt(string key, long fallback = 0);
    /// <summary>A number, or <paramref name="fallback"/> when it is not one.</summary>
    double GetNumber(string key, double fallback = 0);
}

// ---- events ---------------------------------------------------------------------------------------------------

/// <summary>Handles an event. Plugins talk through a NAME and a JSON payload, never through shared types: every native
/// plugin has its own load context, so a type of plugin A is a different type to plugin B. An answer (the payload of an
/// extension point such as <c>shop.payment</c>) is optional.</summary>
public delegate Task<JsonNode?> PluginEventHandler(IPluginContext ctx, JsonNode? payload, CancellationToken ct);

/// <summary>The events the host itself raises. A plugin listens with <see cref="IPluginBuilder.On(string, PluginEventHandler)"/>.</summary>
public static class HostEvents
{
    /// <summary>A public form was submitted and stored. Payload: <c>{ "form": "name", "id": 12, "fields": { … } }</c>.</summary>
    public const string FormSubmitted = "host.form.submitted";
    /// <summary>A page was saved in the editor. Payload: <c>{ "id": 3, "slug": "kontakt", "locale": "de", "published": true }</c>.</summary>
    public const string PageSaved = "host.page.saved";
    /// <summary>A customer account was created (by a visitor on <c>/registrieren</c>, by an administrator, or by a plugin). Payload: <c>{ "id": 5, "email": "…", "username": "…" }</c>.</summary>
    public const string MemberRegistered = "host.member.registered";
    /// <summary>The owner of an account followed the link in the confirmation mail. Payload as <see cref="MemberRegistered"/>. A plugin may now trust the address (attach earlier guest orders to the account).</summary>
    public const string MemberConfirmed = "host.member.confirmed";
    /// <summary>An account is being deleted. Payload as <see cref="MemberRegistered"/>. Raised before the row is removed.</summary>
    public const string MemberDeleted = "host.member.deleted";
    /// <summary>Somebody asked for everything the site holds about one account (the data-protection export). A listener ANSWERS (<see cref="IPluginEvents.CollectAsync"/>)
    /// with <c>{ "plugin": "shop", "data": { … } }</c>; the payload is <c>{ "id": 5, "email": "…" }</c>.</summary>
    public const string MemberExport = "host.member.export";
    /// <summary>Delete or anonymise what the plugin holds about this account (payload as <see cref="MemberRegistered"/>). Raised with <see cref="MemberDeleted"/>; a plugin that must keep a record by law (invoices) anonymises instead of deleting.</summary>
    public const string MemberErase = "host.member.erase";
}

/// <summary>Raises and collects events across plugins.</summary>
public interface IPluginEvents
{
    /// <summary>Calls every handler of <paramref name="name"/>, in plugin load order. A handler that throws is logged to its own
    /// plugin and does not stop the others; the call itself never throws because of a handler.</summary>
    Task EmitAsync(string name, JsonNode? payload = null, CancellationToken ct = default);

    /// <summary>Like <see cref="EmitAsync"/>, and returns what the handlers answered (not null) — the way an extension point
    /// gathers its contributions (payment methods, shipping options).</summary>
    Task<IReadOnlyList<JsonNode>> CollectAsync(string name, JsonNode? payload = null, CancellationToken ct = default);
}

// ---- jobs and lifecycle ---------------------------------------------------------------------------------------

/// <summary>When a job runs.</summary>
public sealed record JobSchedule
{
    private JobSchedule() { }

    /// <summary>The interval, for an interval schedule.</summary>
    public TimeSpan? Every { get; private init; }
    /// <summary>The time of day (UTC), for a daily schedule.</summary>
    public TimeOnly? DailyAt { get; private init; }

    /// <summary>Every <paramref name="interval"/> (at least one minute).</summary>
    public static JobSchedule EveryInterval(TimeSpan interval) => new() { Every = interval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : interval };
    /// <summary>Once a day at <paramref name="utc"/>.</summary>
    public static JobSchedule Daily(TimeOnly utc) => new() { DailyAt = utc };
}

/// <summary>A scheduled job. It runs one at a time (a run that is still going is not started again), its last run and
/// last error are shown under Plugins, and an exception is logged to the plugin, not thrown at the host.</summary>
public delegate Task PluginJobHandler(IPluginContext ctx, CancellationToken ct);

/// <summary>Runs when the plugin is installed or loaded.</summary>
public delegate Task PluginLifecycleHandler(IPluginContext ctx, CancellationToken ct);

/// <summary>Runs when a newer version of the plugin is loaded for the first time, with the version it replaces.</summary>
public delegate Task PluginUpdateHandler(IPluginContext ctx, string fromVersion, CancellationToken ct);

/// <summary>The moments in a plugin's life. All of them run AFTER the plugin's tables exist and its settings are readable, and
/// a handler that throws keeps the plugin from loading (its error is shown under Plugins), so a half-installed plugin does not run.</summary>
public interface ILifecycleBuilder
{
    /// <summary>Once, the first time the plugin loads on this site. Typical: seed default data.</summary>
    void OnInstall(PluginLifecycleHandler handler);
    /// <summary>When the version of the plugin is higher than the one that last ran. Typical: a data migration the table
    /// reconciliation cannot do (rename, retype, backfill).</summary>
    void OnUpdate(PluginUpdateHandler handler);
    /// <summary>On every load (after install or update, when those apply).</summary>
    void OnLoad(PluginLifecycleHandler handler);
}

// ---- extension points -----------------------------------------------------------------------------------------

/// <summary>A page for the sitemap.</summary>
/// <param name="Url">The path (<c>/blog/hallo</c>) or an absolute address.</param>
/// <param name="LastModifiedUtc">When it last changed, if known.</param>
public sealed record SitemapEntry(string Url, DateTime? LastModifiedUtc = null);

/// <summary>A row on the dashboard card "recently edited".</summary>
/// <param name="Kind">The kind, shown as an icon name (<c>ti-news</c>).</param>
/// <param name="Title">The title.</param>
/// <param name="Published">False shows a draft badge.</param>
/// <param name="UpdatedUtc">When it was last edited.</param>
/// <param name="Url">The admin address that opens it.</param>
public sealed record RecentItem(string Kind, string Title, bool Published, DateTime UpdatedUtc, string Url);

/// <summary>One line of a plugin's dashboard card.</summary>
/// <param name="Text">What it is ("Bestellung R-1007 · 23,70 €").</param>
/// <param name="Url">The admin address that opens it.</param>
public sealed record DashboardRow(string Text, string Url)
{
    /// <summary>A Tabler icon (<c>ti-receipt</c>); null = none.</summary>
    public string? Icon { get; init; }
    /// <summary>A short muted text at the right (a date).</summary>
    public string? Meta { get; init; }
    /// <summary>A small badge after the text ("Bezahlt").</summary>
    public string? Badge { get; init; }
}

/// <summary>
/// A card of the plugin's own on the dashboard — "Shop · Bestellungen" with the latest orders — for what is not "edited" and so does not belong into "Zuletzt bearbeitet".
/// The title is shown as given (name the plugin in it); a long list is cut and "all" links to <see cref="AllUrl"/>.
/// </summary>
public sealed record DashboardCard(string Title, IReadOnlyList<DashboardRow> Rows)
{
    /// <summary>Where the full list is (an admin address); null = no link.</summary>
    public string? AllUrl { get; init; }
    /// <summary>Text of that link.</summary>
    public string? AllLabel { get; init; }
    /// <summary>What to say when there are no rows.</summary>
    public string EmptyText { get; init; } = "";
}

/// <summary>Delivers a dashboard card; null = no card (nothing to show yet).</summary>
public delegate Task<DashboardCard?> PluginCardProvider(IPluginContext ctx, CancellationToken ct);

/// <summary>A line of "Braucht Aufmerksamkeit".</summary>
/// <param name="Title">What it is about.</param>
/// <param name="Text">What to do.</param>
/// <param name="Url">The admin address that deals with it.</param>
public sealed record AttentionNote(string Title, string Text, string Url)
{
    /// <summary>How urgent it is.</summary>
    public UiTone Tone { get; init; } = UiTone.Info;
    /// <summary>A Tabler icon name.</summary>
    public string Icon { get; init; } = "ti-alert-circle";
}

/// <summary>A placeholder of a template part: <c>{{name}}</c> in the part's HTML. A value is HTML-encoded when it is put in, unless the placeholder is declared <see cref="Html"/> (the plugin hands over finished markup).</summary>
public sealed record TemplatePlaceholder(string Name, string Help, bool Html = false);

/// <summary>
/// A named HTML file a plugin draws a page (or a piece of one) with — the product page of a shop, a product card. The plugin ships the DEFAULT; every site template may carry its own version of
/// it (in the template editor it is one more file of the template, next to <c>body.html</c> and <c>article.html</c>), and the plugin renders whichever the active template has
/// (<see cref="IPluginContext.RenderPart"/>). In the part, <c>{{name}}</c> puts a value in, <c>{{#if name}}…{{/if}}</c> keeps a piece only when the value is not empty and
/// <c>{{#unless name}}…{{/unless}}</c> only when it is.
/// </summary>
/// <param name="Name">Lower case letters, digits and dashes (<c>product</c>). The template stores it as <c>{plugin key}.{name}</c>.</param>
/// <param name="Label">What the editor calls it (a key of the plugin's strings is translated).</param>
/// <param name="DefaultHtml">The part as it is without a template's own version.</param>
public sealed record TemplatePartSpec(string Name, string Label, string DefaultHtml)
{
    /// <summary>The placeholders the plugin fills, listed in the editor as chips to insert.</summary>
    public IReadOnlyList<TemplatePlaceholder> Placeholders { get; init; } = Array.Empty<TemplatePlaceholder>();
    /// <summary>One line under the file's name in the editor (a key of the plugin's strings is translated).</summary>
    public string? Help { get; init; }
}

/// <summary>
/// One entry a plugin adds to a menu of the PUBLIC site. <see cref="Children"/> make a dropdown (one level); <see cref="Badge"/> is a small counter next to the label (a cart's
/// item count) — null shows none. <see cref="Icon"/> is a Tabler icon name without the prefix (<c>shopping-cart</c>), used by the icon strip ("toolbar").
/// </summary>
public sealed record MenuEntry(string Label, string Url)
{
    /// <summary>A Tabler icon name (<c>user</c>, <c>shopping-cart</c>); null = none.</summary>
    public string? Icon { get; init; }
    /// <summary>A counter shown next to the entry; null or empty = none.</summary>
    public string? Badge { get; init; }
    /// <summary>Position among the plugin's entries of this menu (the site's own entries come first).</summary>
    public int Order { get; init; } = 100;
    /// <summary>Entries of a dropdown under this one.</summary>
    public IReadOnlyList<MenuEntry> Children { get; init; } = Array.Empty<MenuEntry>();
    /// <summary>
    /// Markup to put in the menu INSTEAD of a link — a search box. Drawn as it is, in the place of the entry, so build it with <see cref="IPluginContext.RenderPart"/>:
    /// the site's template can then replace it and style it. <see cref="Label"/> still names the entry (for the editor and screen readers).
    /// </summary>
    public string? Html { get; init; }
}

/// <summary>A place a link can point at, offered wherever the editor picks a link target (menus, buttons, links in blocks).</summary>
/// <param name="Label">What the editor sees ("Beitrag: Neues Sortiment").</param>
/// <param name="Url">A local address (<c>/blog/neues-sortiment</c>) or an https address.</param>
public sealed record LinkTarget(string Label, string Url);

/// <summary>Delivers the link targets of one group. At most 300 are taken; the picker has a search box.</summary>
public delegate Task<IEnumerable<LinkTarget>> LinkTargetProvider(IPluginContext ctx, CancellationToken ct);

/// <summary>
/// A piece a plugin offers for the site's menus that is more than a link: the shop's cart with its count, "Anmelden"/"Mein Konto", a search box, "Shop" with its categories as a dropdown.
/// The editor adds it to any menu like a menu item (Admin → Menü → "Plugin-Element"), moves, renames and removes it there; the plugin draws it per visitor
/// (<see cref="MenuElementProvider"/>). The look comes from the site's template: a link-like element goes through the template's menu markup, a search box through a template part.
/// </summary>
/// <param name="Name">Lower case letters, digits and dashes; stored as <c>plugin:{plugin}.{name}</c> in the menu item.</param>
/// <param name="Label">What the editor sees in the list (a string key of the plugin or plain text).</param>
public sealed record MenuElementSpec(string Name, string Label)
{
    /// <summary>One line telling what it is.</summary>
    public string? Help { get; init; }
    /// <summary>A Tabler icon name without the prefix (<c>shopping-cart</c>) used until the editor picks another.</summary>
    public string? Icon { get; init; }
    /// <summary>The menu ("header", "footer", "toolbar") the host adds the element to ONCE when the plugin is first loaded on a site; empty = the editor adds it. Removing it sticks.</summary>
    public string DefaultMenu { get; init; } = "";
}

/// <summary>Draws a menu element for the request's visitor; null = nothing for this visitor. Asked on every page view and must be quick (a slow answer is skipped for that page).</summary>
public delegate Task<MenuEntry?> MenuElementProvider(IPluginContext ctx, CancellationToken ct);

/// <summary>
/// Answers which entries the plugin adds to a menu. Asked on every page view for <c>"header"</c>, <c>"footer"</c>, <c>"toolbar"</c> (the icon strip) and any menu the
/// site defines, with the request's own context — the signed-in member, the plugin's cookies — so an entry can differ per visitor ("Anmelden" or "Mein Konto").
/// It must be quick: a slow answer is skipped for that page, a failing one is logged and skipped.
/// </summary>
public delegate Task<IEnumerable<MenuEntry>> PluginMenuProvider(IPluginContext ctx, string menu, CancellationToken ct);

/// <summary>Computes the figure of a dashboard tile.</summary>
public delegate Task<UiStat?> PluginTileProvider(IPluginContext ctx, CancellationToken ct);

/// <summary>Delivers the entries of the dashboard's "recently edited" list.</summary>
public delegate Task<IEnumerable<RecentItem>> PluginRecentProvider(IPluginContext ctx, CancellationToken ct);

/// <summary>Delivers lines for "Braucht Aufmerksamkeit".</summary>
public delegate Task<IEnumerable<AttentionNote>> PluginAttentionProvider(IPluginContext ctx, CancellationToken ct);

/// <summary>Delivers sitemap entries.</summary>
public delegate Task<IEnumerable<SitemapEntry>> PluginSitemapProvider(IPluginContext ctx, CancellationToken ct);

/// <summary>Delivers the media files (<c>/uploads/…</c> addresses) the plugin uses, so the cleanup does not offer them as unused.</summary>
public delegate Task<IEnumerable<string>> PluginMediaProvider(IPluginContext ctx, CancellationToken ct);

/// <summary>
/// A described operation (<see cref="IPluginBuilder.Operation(PluginOperationSpec, PluginOperationHandler)"/>). <c>Arguments</c> is an example
/// argument object as text — the clearest description of a JSON shape. <c>Writes</c>: it changes data (the assistant shows it as a change, a
/// read-only run refuses it). <c>PersonalData</c>: its answer carries customers' data (orders, addresses) — never offered to the AI assistant,
/// whose every answer goes to an outside model provider. <c>Internal</c>: only the host may run it — the undo of an earlier write (see the
/// <c>undo</c> an answer may carry); neither the cloud, MCP nor the assistant can ask for it, because it writes back stored values unchecked.
/// </summary>
public sealed record PluginOperationSpec(string Name, string Description, string Arguments = "{}", bool Writes = true, bool PersonalData = false, bool Internal = false);

/// <summary>Carries out a named operation sent by the cloud or an agent (MCP) with a JSON argument, and answers with JSON.</summary>
public delegate Task<JsonNode?> PluginOperationHandler(IPluginContext ctx, JsonNode? args, CancellationToken ct);

// ---- the site ---------------------------------------------------------------------------------------------------

/// <summary>The site a plugin runs in.</summary>
public interface IPluginSite
{
    /// <summary>The site's name.</summary>
    string Name { get; }
    /// <summary>The canonical address of the site without a trailing slash (<c>https://example.org</c>); falls back to the request's.</summary>
    string Origin { get; }
    /// <summary>The languages the site publishes in, the default first.</summary>
    IReadOnlyList<string> Locales { get; }
    /// <summary>Draws one of the CMS's own content blocks (<c>gallery</c>, <c>text</c>, …) with the given field values, as the page editor would.</summary>
    Task<string> RenderBlockAsync(string blockType, JsonNode values, CancellationToken ct = default);
    /// <summary>The HTML of a layout part of the active template with its <c>{{token}}</c> placeholders still in it — to see which
    /// tokens a template uses before filling them (a template older than the gallery has no place for it).</summary>
    string TemplatePart(string part);

    /// <summary>
    /// Draws a layout part of the active template (<c>post</c>, the blog detail page): the template's own version when it has
    /// one, otherwise the built-in default. <c>{{token}}</c> placeholders are replaced with <paramref name="tokens"/> (the caller
    /// encodes what needs encoding). Parts a plugin defines itself are not supported yet.
    /// </summary>
    string RenderTemplatePart(string part, IReadOnlyDictionary<string, string> tokens);

    /// <summary>
    /// A picture of the media library (<c>/uploads/…</c>, PNG, JPEG, WebP or GIF; never SVG) as a JPEG of at most <paramref name="maxWidth"/> pixels across, transparency on white — what a plugin needs to
    /// put it on paper (a PDF). Null for anything else: an address outside the library, a file that is gone or too big, a picture that cannot be read.
    /// </summary>
    Task<PluginImage?> LoadImageAsync(string url, int maxWidth = 600, CancellationToken ct = default);
}

/// <summary>A picture ready for a document: JPEG bytes and the size in pixels.</summary>
public sealed record PluginImage(byte[] Jpeg, int Width, int Height);
