using MatCMS.Plugin;
using MatCMS.Services.Plugins;

namespace MatCMS.Services;

/// <summary>
/// What the loaded plugins contribute, shared by all requests (singleton).
/// <para><b>One slice per plugin.</b> A load builds a private <see cref="Slice"/> and publishes it in one
/// step (<see cref="Publish"/>); an unload removes it in one step. Readers see either the old or the new
/// set of a plugin, never a half-registered one — which is what lets ONE plugin be reloaded (a script
/// saved, a native updated) without clearing and re-running all the others, as the old
/// reset-everything registry did.</para>
/// <para>The merged view (<see cref="AdminMenu"/>, <see cref="Blocks"/>, …) is an immutable snapshot swapped
/// under a lock, so the layouts and the request hot path read it without locking.</para>
/// </summary>
public sealed class PluginRegistry
{
    /// <summary>One sidebar entry. <see cref="Group"/> and <see cref="Order"/> place it; older plugins that
    /// register a bare menu line get the defaults (own block, order 100).</summary>
    public sealed record AdminMenuEntry(string Label, string Url, string Icon)
    {
        /// <summary>Key of the plugin that registered it.</summary>
        public string Owner { get; init; } = "";
        /// <summary>Where in the sidebar it sits.</summary>
        public AdminGroup Group { get; init; } = AdminGroup.Plugins;
        /// <summary>Position inside the group.</summary>
        public int Order { get; init; } = 100;
    }

    /// <summary>A block offered in the page editor. <see cref="Render"/> keeps the host's established
    /// signature; blocks registered through the new API are adapted to it.</summary>
    public sealed record PluginBlock(string Type, string Name, string Description, Func<PluginRequest, string> Render)
    {
        /// <summary>The asynchronous render (null for a block registered the old way).</summary>
        public Func<PluginRequest, CancellationToken, Task<string>>? RenderAsync { get; init; }
        /// <summary>Editable fields shown in the block editor (empty = none).</summary>
        public List<MatCMS.Content.BlockField> Fields { get; init; } = new();
        /// <summary>Key of the plugin that registered it.</summary>
        public string Owner { get; init; } = "";
        /// <summary>The picker group (null = "plugins").</summary>
        public string? Category { get; init; }
        /// <summary>The picker icon's inner SVG markup (null = the plugin icon).</summary>
        public string? IconSvg { get; init; }
    }

    /// <summary>A public route registered through <see cref="IPluginBuilder.Route"/>.</summary>
    public sealed record RouteEntry(string Owner, PluginInfo Info, PluginRoutePattern Pattern, PluginHandler Handler, PublicRouteOptions Options);

    /// <summary>A page inside an admin section. <see cref="Tab"/> null = a detail page without a tab.</summary>
    public sealed record SectionPage(PluginRoutePattern Pattern, string? Tab, PluginHandler Handler);

    /// <summary>A link of a section's own sidebar block (<see cref="IAdminSection.Link"/>).</summary>
    public sealed record SectionLink(string Label, string Path, string Icon, string? Heading, bool Badge)
    {
        /// <summary>Further pages of the section the link also stands for (active on them).</summary>
        public IReadOnlyList<string> Also { get; init; } = Array.Empty<string>();
    }

    /// <summary>An admin section: one sidebar line (or a block of links, when it declares any) and the pages behind it.</summary>
    public sealed record SectionEntry(string Owner, PluginInfo Info, string Label, string Icon, AdminGroup Group, int Order, IReadOnlyList<SectionPage> Pages)
    {
        /// <summary>The links of the section's own sidebar block; empty = one plain line and the tab bar.</summary>
        public List<SectionLink> Links { get; } = new();
        /// <summary>The pages that appear as tabs, in registration order.</summary>
        public IEnumerable<SectionPage> Tabs => Pages.Where(p => p.Tab is not null);
        /// <summary>URL of the section's start page.</summary>
        public string Url => "/admin/p/" + Info.Key;
        /// <summary>Computes the counter next to the sidebar entry; null when the section has none. Set while the plugin configures.</summary>
        public PluginBadgeProvider? Badge { get; set; }
    }

    /// <summary>A template part a plugin declared. <see cref="Key"/> is how a template stores its version of it.</summary>
    public sealed record TemplatePartEntry(string Owner, PluginInfo Info, TemplatePartSpec Spec)
    {
        public string Key => Owner + "." + Spec.Name;
        /// <summary>The name of the file in the template editor.</summary>
        public string FileName => Owner + "-" + Spec.Name + ".html";
    }

    /// <summary>A group of link targets a plugin offers (<see cref="IPluginBuilder.LinkTargets"/>).</summary>
    public sealed record LinkTargetEntry(string Owner, PluginInfo Info, string Group, LinkTargetProvider Provider);

    /// <summary>A menu element a plugin offers. <see cref="Key"/> is what a menu item stores after "plugin:".</summary>
    public sealed record MenuElementEntry(string Owner, PluginInfo Info, MenuElementSpec Spec, MenuElementProvider Provider)
    {
        public string Key => Owner + "." + Spec.Name;
    }

    /// <summary>A page of a plugin the editors lay out in the block editor.</summary>
    public sealed record PageRoleEntry(string Owner, PluginInfo Info, PageRoleSpec Spec);

    /// <summary>A scheduled job.</summary>
    public sealed record JobEntry(string Owner, PluginInfo Info, string Name, JobSchedule Schedule, PluginJobHandler Handler);

    /// <summary>An event listener.</summary>
    public sealed record EventEntry(string Owner, PluginInfo Info, string Name, PluginEventHandler Handler);

    /// <summary>A contribution of one plugin to a host extension point, with its owner (for the log and for isolating failures).</summary>
    public sealed record Contribution<T>(string Owner, PluginInfo Info, T Provider);

    /// <summary>A list of choices a block field draws from, computed when the page editor opens.</summary>
    public sealed record OptionSourceEntry(string Owner, PluginInfo Info, string Name, Func<IPluginContext, CancellationToken, Task<IReadOnlyList<FieldOption>>> Provider);

    /// <summary>A named operation (cloud / MCP).</summary>
    public sealed record OperationEntry(string Owner, PluginInfo Info, string Name, PluginOperationHandler Handler);

    /// <summary>One plugin's registrations. Built privately during a load, then published whole.</summary>
    public sealed class Slice
    {
        internal Slice(string key, int order) { Key = key; Order = order; }
        /// <summary>The plugin's key.</summary>
        public string Key { get; }
        /// <summary>Load order (the plugin's id): fixes the order of entries across plugins, whatever order they were reloaded in.</summary>
        public int Order { get; }
        internal List<AdminMenuEntry> Menu { get; } = new();
        internal List<PluginBlock> Blocks { get; } = new();
        internal Dictionary<string, Func<PluginRequest, string>> Pages { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, Func<PluginRequest, string>> PublicPages { get; } = new(StringComparer.Ordinal);
        internal List<RouteEntry> Routes { get; } = new();
        internal List<SectionEntry> Sections { get; } = new();
        internal List<string> Head { get; } = new();
        internal List<string> Body { get; } = new();
        internal List<SettingSpec> SettingSpecs { get; } = new();
        internal List<TableSpec> TableSpecs { get; } = new();
        internal List<JobEntry> Jobs { get; } = new();
        internal List<EventEntry> Events { get; } = new();
        internal Dictionary<string, Dictionary<string, string>> StringTables { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<Contribution<PluginTileProvider>> Tiles { get; } = new();
        internal List<Contribution<PluginRecentProvider>> Recents { get; } = new();
        internal List<Contribution<PluginCardProvider>> Cards { get; } = new();
        internal List<Contribution<PluginAttentionProvider>> Attentions { get; } = new();
        internal List<Contribution<PluginSitemapProvider>> Sitemaps { get; } = new();
        internal List<Contribution<PluginMenuProvider>> Menus { get; } = new();
        internal List<TemplatePartEntry> TemplateParts { get; } = new();
        internal List<LinkTargetEntry> LinkTargetSets { get; } = new();
        internal List<MenuElementEntry> MenuElements { get; } = new();
        internal List<PageRoleEntry> PageRoles { get; } = new();
        internal List<Contribution<PluginMediaProvider>> MediaRefs { get; } = new();
        internal List<OperationEntry> Operations { get; } = new();
        internal List<OptionSourceEntry> OptionSources { get; } = new();
        internal List<MailTemplateSpec> MailTemplateSpecs { get; } = new();
        internal List<PluginLifecycleHandler> OnInstall { get; } = new();
        internal List<PluginUpdateHandler> OnUpdate { get; } = new();
        internal List<PluginLifecycleHandler> OnLoad { get; } = new();

        /// <summary>Adds texts of one language (from a language file or from the plugin's code).</summary>
        internal void AddStrings(string culture, IReadOnlyDictionary<string, string> table)
        {
            if (!StringTables.TryGetValue(culture, out var t)) StringTables[culture] = t = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in table) t[kv.Key] = kv.Value;
        }

        /// <summary>The settings the plugin declared.</summary>
        public IReadOnlyList<SettingSpec> Settings => SettingSpecs;
        /// <summary>The tables the plugin declared.</summary>
        public IReadOnlyList<TableSpec> Tables => TableSpecs;
        /// <summary>The mails the plugin declared.</summary>
        public IReadOnlyList<MailTemplateSpec> MailTemplates => MailTemplateSpecs;
        /// <summary>The plugin's texts: culture → key → text.</summary>
        public IReadOnlyDictionary<string, Dictionary<string, string>> Strings => StringTables;
        /// <summary>True when the plugin declared any install/update/load handler.</summary>
        public bool HasLifecycle => OnInstall.Count + OnUpdate.Count + OnLoad.Count > 0;
        /// <summary>The plugin's install handlers.</summary>
        public IReadOnlyList<PluginLifecycleHandler> InstallHandlers => OnInstall;
        /// <summary>The plugin's update handlers.</summary>
        public IReadOnlyList<PluginUpdateHandler> UpdateHandlers => OnUpdate;
        /// <summary>The plugin's load handlers.</summary>
        public IReadOnlyList<PluginLifecycleHandler> LoadHandlers => OnLoad;
    }

    private sealed class Snapshot
    {
        public IReadOnlyList<AdminMenuEntry> AdminMenu = Array.Empty<AdminMenuEntry>();
        public IReadOnlyList<PluginBlock> Blocks = Array.Empty<PluginBlock>();
        public IReadOnlyList<RouteEntry> Routes = Array.Empty<RouteEntry>();
        public IReadOnlyList<SectionEntry> Sections = Array.Empty<SectionEntry>();
        public IReadOnlyDictionary<string, Func<PluginRequest, string>> Pages = new Dictionary<string, Func<PluginRequest, string>>();
        public IReadOnlyDictionary<string, Func<PluginRequest, string>> PublicPages = new Dictionary<string, Func<PluginRequest, string>>();
        public IReadOnlyList<string> Head = Array.Empty<string>();
        public IReadOnlyList<string> Body = Array.Empty<string>();
        public IReadOnlyDictionary<string, Slice> Slices = new Dictionary<string, Slice>();
        public IReadOnlyList<JobEntry> Jobs = Array.Empty<JobEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<EventEntry>> Events = new Dictionary<string, IReadOnlyList<EventEntry>>();
        public IReadOnlyList<Contribution<PluginTileProvider>> Tiles = Array.Empty<Contribution<PluginTileProvider>>();
        public IReadOnlyList<Contribution<PluginRecentProvider>> Recents = Array.Empty<Contribution<PluginRecentProvider>>();
        public IReadOnlyList<Contribution<PluginCardProvider>> Cards = Array.Empty<Contribution<PluginCardProvider>>();
        public IReadOnlyList<Contribution<PluginAttentionProvider>> Attentions = Array.Empty<Contribution<PluginAttentionProvider>>();
        public IReadOnlyList<Contribution<PluginSitemapProvider>> Sitemaps = Array.Empty<Contribution<PluginSitemapProvider>>();
        public IReadOnlyList<Contribution<PluginMenuProvider>> Menus = Array.Empty<Contribution<PluginMenuProvider>>();
        public IReadOnlyList<TemplatePartEntry> TemplateParts = Array.Empty<TemplatePartEntry>();
        public IReadOnlyList<LinkTargetEntry> LinkTargetSets = Array.Empty<LinkTargetEntry>();
        public IReadOnlyList<MenuElementEntry> MenuElements = Array.Empty<MenuElementEntry>();
        public IReadOnlyList<PageRoleEntry> PageRoles = Array.Empty<PageRoleEntry>();
        public IReadOnlyList<Contribution<PluginMediaProvider>> MediaRefs = Array.Empty<Contribution<PluginMediaProvider>>();
        public IReadOnlyList<OperationEntry> Operations = Array.Empty<OperationEntry>();
    }

    /// <summary>Creates the registry.</summary>
    public PluginRegistry() => AdminMenu = new AdminMenuView(this);

    private readonly object _lock = new();
    private readonly Dictionary<string, Slice> _slices = new(StringComparer.Ordinal);
    private volatile Snapshot _snap = new();
    private readonly Dictionary<int, string> _errors = new();
    private readonly List<string> _log = new();
    private readonly Dictionary<string, List<string>> _pluginLogs = new(StringComparer.Ordinal);

    /// <summary>Starts a slice for a plugin. Nothing is visible until <see cref="Publish"/>.</summary>
    public Slice Begin(string key, int order) => new(key, order);

    /// <summary>Publishes a slice, replacing the plugin's previous one in a single step.</summary>
    public void Publish(Slice slice)
    {
        lock (_lock) { _slices[slice.Key] = slice; Rebuild(); }
    }

    /// <summary>Removes everything a plugin contributed. True when it had anything.</summary>
    public bool Remove(string key)
    {
        lock (_lock)
        {
            var had = _slices.Remove(key);
            if (had) Rebuild();
            return had;
        }
    }

    /// <summary>The keys of all loaded plugins.</summary>
    public IReadOnlyCollection<string> LoadedKeys { get { lock (_lock) return _slices.Keys.ToList(); } }

    /// <summary>True while the plugin has a published slice (it is loaded and running).</summary>
    public bool IsLoaded(string key) { lock (_lock) return _slices.ContainsKey(key); }

    private void Rebuild()
    {
        var slices = _slices.Values.OrderBy(s => s.Order).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
        var menu = new List<AdminMenuEntry>();
        foreach (var s in slices)
        {
            menu.AddRange(s.Menu);
            menu.AddRange(s.Sections.Select(x => new AdminMenuEntry(x.Label, x.Url, x.Icon) { Owner = x.Owner, Group = x.Group, Order = x.Order }));
        }
        var pages = new Dictionary<string, Func<PluginRequest, string>>(StringComparer.Ordinal);
        var publicPages = new Dictionary<string, Func<PluginRequest, string>>(StringComparer.Ordinal);
        foreach (var s in slices)
        {
            foreach (var kv in s.Pages) pages[kv.Key] = kv.Value;
            foreach (var kv in s.PublicPages) publicPages[kv.Key] = kv.Value;
        }
        _snap = new Snapshot
        {
            // A stable sort: entries of the same group and order keep the plugins' load order.
            AdminMenu = menu.OrderBy(m => m.Group).ThenBy(m => m.Order).ToList(),
            Blocks = slices.SelectMany(s => s.Blocks).ToList(),
            // Most specific first (more literal segments), a catch-all last; ties keep load order.
            Routes = slices.SelectMany(s => s.Routes)
                .OrderBy(r => r.Pattern.HasCatchAll).ThenByDescending(r => r.Pattern.Specificity).ToList(),
            Sections = slices.SelectMany(s => s.Sections).ToList(),
            Pages = pages,
            PublicPages = publicPages,
            Head = slices.SelectMany(s => s.Head).ToList(),
            Body = slices.SelectMany(s => s.Body).ToList(),
            Slices = slices.ToDictionary(s => s.Key, s => s, StringComparer.Ordinal),
            Jobs = slices.SelectMany(s => s.Jobs).ToList(),
            Events = slices.SelectMany(s => s.Events).GroupBy(e => e.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<EventEntry>)g.ToList(), StringComparer.Ordinal),
            Tiles = slices.SelectMany(s => s.Tiles).ToList(),
            Recents = slices.SelectMany(s => s.Recents).ToList(),
            Cards = slices.SelectMany(s => s.Cards).ToList(),
            Attentions = slices.SelectMany(s => s.Attentions).ToList(),
            Sitemaps = slices.SelectMany(s => s.Sitemaps).ToList(),
            Menus = slices.SelectMany(s => s.Menus).ToList(),
            TemplateParts = slices.SelectMany(s => s.TemplateParts).ToList(),
            LinkTargetSets = slices.SelectMany(s => s.LinkTargetSets).ToList(),
            MenuElements = slices.SelectMany(s => s.MenuElements).ToList(),
            PageRoles = slices.SelectMany(s => s.PageRoles).ToList(),
            MediaRefs = slices.SelectMany(s => s.MediaRefs).ToList(),
            Operations = slices.SelectMany(s => s.Operations).ToList()
        };
    }

    // ---- merged view (lock-free reads) ----------------------------------------------------------

    /// <summary>The sidebar entries of all plugins, ordered by group, then order. Readable like a list, and
    /// — for the plugins that already do it — an entry can be REPLACED by index (the Leserstimmen plugin
    /// swaps its entry to show "(2)" after every review). New plugins use a section badge instead.</summary>
    public AdminMenuView AdminMenu { get; }
    /// <summary>The blocks of all plugins.</summary>
    public IReadOnlyList<PluginBlock> Blocks => _snap.Blocks;
    /// <summary>The public routes of all plugins, most specific first.</summary>
    public IReadOnlyList<RouteEntry> Routes => _snap.Routes;
    /// <summary>The admin sections of all plugins.</summary>
    public IReadOnlyList<SectionEntry> Sections => _snap.Sections;
    /// <summary>The published slice of a plugin, or null when it is not loaded.</summary>
    public Slice? SliceOf(string key) => _snap.Slices.TryGetValue(key, out var s) ? s : null;
    /// <summary>A text of any loaded plugin's string tables: the given language, else the site's, else English. Null when no plugin has the key.</summary>
    public string? TextOf(string key, string culture)
    {
        if (string.IsNullOrEmpty(key) || !key.Contains('.')) return null;
        foreach (var c in new[] { culture, Localizer.DefaultCulture, "en" })
            foreach (var slice in _snap.Slices.Values)
                if (slice.Strings.TryGetValue(c, out var table) && table.TryGetValue(key, out var v)) return v;
        return null;
    }

    /// <summary>The option source <paramref name="name"/> of a loaded plugin, or null.</summary>
    public OptionSourceEntry? OptionSourceOf(string key, string name) => SliceOf(key)?.OptionSources.FirstOrDefault(o => o.Name == name);
    /// <summary>The scheduled jobs of all loaded plugins.</summary>
    public IReadOnlyList<JobEntry> Jobs => _snap.Jobs;
    /// <summary>The listeners of an event, in plugin load order (empty when none).</summary>
    public IReadOnlyList<EventEntry> ListenersOf(string name) => _snap.Events.TryGetValue(name, out var l) ? l : Array.Empty<EventEntry>();
    /// <summary>The names of the events a plugin listens for.</summary>
    public IEnumerable<string> ListensOf(string key) => SliceOf(key)?.Events.Select(e => e.Name).Distinct() ?? Enumerable.Empty<string>();

    /// <summary>Dashboard tiles.</summary>
    public IReadOnlyList<Contribution<PluginTileProvider>> Tiles => _snap.Tiles;
    /// <summary>Providers of the dashboard's "recently edited" rows.</summary>
    public IReadOnlyList<Contribution<PluginRecentProvider>> Recents => _snap.Recents;
    /// <summary>Providers of the plugins' own dashboard cards.</summary>
    public IReadOnlyList<Contribution<PluginCardProvider>> Cards => _snap.Cards;
    /// <summary>Providers of "Braucht Aufmerksamkeit" lines.</summary>
    public IReadOnlyList<Contribution<PluginAttentionProvider>> Attentions => _snap.Attentions;
    /// <summary>The template parts loaded plugins declared.</summary>
    public IReadOnlyList<TemplatePartEntry> TemplateParts => _snap.TemplateParts;
    /// <summary>The groups of link targets loaded plugins offer.</summary>
    public IReadOnlyList<LinkTargetEntry> LinkTargetSets => _snap.LinkTargetSets;
    /// <summary>The menu elements loaded plugins offer.</summary>
    public IReadOnlyList<MenuElementEntry> MenuElements => _snap.MenuElements;
    /// <summary>The pages loaded plugins let the editors lay out.</summary>
    public IReadOnlyList<PageRoleEntry> PageRoles => _snap.PageRoles;
    /// <summary>Providers of the entries plugins add to the public menus.</summary>
    public IReadOnlyList<Contribution<PluginMenuProvider>> Menus => _snap.Menus;
    /// <summary>Sitemap providers.</summary>
    public IReadOnlyList<Contribution<PluginSitemapProvider>> Sitemaps => _snap.Sitemaps;
    /// <summary>Providers of the media files plugins use.</summary>
    public IReadOnlyList<Contribution<PluginMediaProvider>> MediaRefs => _snap.MediaRefs;
    /// <summary>The operations the cloud or an agent may call.</summary>
    public IReadOnlyList<OperationEntry> Operations => _snap.Operations;

    /// <summary>Raw HTML for every public page's head.</summary>
    public IReadOnlyList<string> HeadHtml => _snap.Head;
    /// <summary>Raw HTML for the end of every public page's body.</summary>
    public IReadOnlyList<string> BodyHtml => _snap.Body;

    /// <summary>Looks up a page registered with the legacy <c>AddAdminPage</c> (served at <c>/admin/plugin/{key}</c>).</summary>
    public bool TryGetPage(string key, out Func<PluginRequest, string>? handler)
    {
        var ok = _snap.Pages.TryGetValue(key, out var h);
        handler = h;
        return ok;
    }

    /// <summary>Looks up a legacy public endpoint (served at <c>/plugin/{key}</c>).</summary>
    public bool TryGetPublicPage(string key, out Func<PluginRequest, string>? handler)
    {
        var ok = _snap.PublicPages.TryGetValue(key, out var h);
        handler = h;
        return ok;
    }

    /// <summary>Finds the public route for a path (no culture prefix) and method, or null.</summary>
    public (RouteEntry Route, Dictionary<string, string> Values)? MatchRoute(string path, string method)
    {
        var wanted = HttpMethods.Contains(method, "POST") ? PluginMethod.Post : PluginMethod.Get;
        foreach (var r in _snap.Routes)
        {
            if ((r.Options.Methods & wanted) == 0) continue;
            var v = r.Pattern.Match(path);
            if (v is not null) return (r, v);
        }
        return null;
    }

    /// <summary>Finds an admin page: the section for <paramref name="key"/> and the page matching <paramref name="path"/>
    /// (relative to the section; empty = the start page).</summary>
    public (SectionEntry Section, SectionPage Page, Dictionary<string, string> Values)? MatchAdminPage(string key, string path)
    {
        var section = _snap.Sections.FirstOrDefault(s => string.Equals(s.Info.Key, key, StringComparison.Ordinal));
        if (section is null) return null;
        if (string.IsNullOrWhiteSpace(path))
        {
            // A section with a menu of its own opens on its FIRST page (the shop's dashboard); one with tabs, on its first tab.
            var start = section.Links.Count > 0 ? section.Pages.FirstOrDefault() : section.Tabs.FirstOrDefault() ?? section.Pages.FirstOrDefault();
            return start is null ? null : (section, start, new Dictionary<string, string>());
        }
        foreach (var p in section.Pages.OrderBy(p => p.Pattern.HasCatchAll).ThenByDescending(p => p.Pattern.Specificity))
        {
            var v = p.Pattern.Match(path);
            if (v is not null) return (section, p, v);
        }
        return null;
    }

    /// <summary>Replaces the menu entry at <paramref name="index"/> of the merged view with
    /// <paramref name="entry"/>, in the slice that owns it. An entry that belongs to an admin section (not a
    /// plain menu line) cannot be replaced this way and is left alone.</summary>
    private void ReplaceMenuEntry(int index, AdminMenuEntry entry)
    {
        lock (_lock)
        {
            var current = _snap.AdminMenu[index];
            if (!_slices.TryGetValue(current.Owner, out var slice)) return;
            var at = slice.Menu.FindIndex(m => ReferenceEquals(m, current));
            if (at < 0) return;
            slice.Menu[at] = entry with { Owner = current.Owner, Group = current.Group, Order = current.Order };
            Rebuild();
        }
    }

    /// <summary>The merged admin menu as a list that reads from the current snapshot and, for older
    /// plugins, accepts an assignment by index.</summary>
    public sealed class AdminMenuView : IReadOnlyList<AdminMenuEntry>
    {
        private readonly PluginRegistry _registry;
        internal AdminMenuView(PluginRegistry registry) => _registry = registry;

        /// <summary>Number of entries.</summary>
        public int Count => _registry._snap.AdminMenu.Count;

        /// <summary>The entry at <paramref name="index"/>; assigning replaces it.</summary>
        public AdminMenuEntry this[int index]
        {
            get => _registry._snap.AdminMenu[index];
            set => _registry.ReplaceMenuEntry(index, value);
        }

        /// <inheritdoc />
        public IEnumerator<AdminMenuEntry> GetEnumerator() => _registry._snap.AdminMenu.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static class HttpMethods
    {
        public static bool Contains(string method, string wanted) => string.Equals(method, wanted, StringComparison.OrdinalIgnoreCase);
    }

    // ---- errors and log -------------------------------------------------------------------------

    /// <summary>The last load error per plugin id.</summary>
    public IReadOnlyDictionary<int, string> Errors { get { lock (_lock) return new Dictionary<int, string>(_errors); } }

    /// <summary>Records why a plugin failed to load.</summary>
    public void SetError(int pluginId, string message) { lock (_lock) _errors[pluginId] = message; }

    /// <summary>Clears a plugin's error (it loaded fine, or was unloaded on purpose).</summary>
    public void ClearError(int pluginId) { lock (_lock) _errors.Remove(pluginId); }

    /// <summary>All log lines, newest last. Survives reloads.</summary>
    public IReadOnlyList<string> Log { get { lock (_lock) return _log.ToList(); } }

    /// <summary>One plugin's own log lines, newest last.</summary>
    public IReadOnlyList<string> LogOf(string key)
    {
        lock (_lock) return _pluginLogs.TryGetValue(key, out var l) ? l.ToList() : new List<string>();
    }

    /// <summary>Writes a line to the shared log.</summary>
    public void AddLog(string message) => AddLog(null, message);

    /// <summary>Writes a line to the shared log and, when <paramref name="key"/> is given, to that plugin's own log.</summary>
    public void AddLog(string? key, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {(key is null ? "" : "[" + key + "] ")}{message}";
        lock (_lock)
        {
            Append(_log, line);
            if (key is not null)
            {
                if (!_pluginLogs.TryGetValue(key, out var l)) _pluginLogs[key] = l = new List<string>();
                Append(l, line);
            }
        }
    }

    private static void Append(List<string> list, string line)
    {
        list.Add(line);
        if (list.Count > 200) list.RemoveRange(0, list.Count - 200);
    }
}
