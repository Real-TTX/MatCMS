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
    Secret
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
    /// <summary>A heading the setting is grouped under in the form (consecutive settings with the same group share it).</summary>
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
}
