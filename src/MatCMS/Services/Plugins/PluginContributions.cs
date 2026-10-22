using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Services.Plugins;

/// <summary>
/// What loaded plugins contribute to the host's own screens and channels — dashboard tiles, "recently edited" rows, attention
/// lines, sitemap entries, the media files in use, and named operations from the cloud. The host asks; a plugin that is slow or
/// throws is skipped (and logged on its own log) so one plugin can never break the dashboard, the sitemap or the heartbeat.
/// <para>One deliberate exception: <see cref="MediaInUseAsync"/> does <b>not</b> swallow a failure. The cleanup offers files for
/// deletion; "I could not find out what the shop uses" must stop it, not shrink the list of what is protected.</para>
/// </summary>
public sealed class PluginContributions
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);
    private readonly PluginRegistry _registry;

    /// <summary>Creates the service.</summary>
    public PluginContributions(PluginRegistry registry) => _registry = registry;

    private async Task<T?> CallAsync<T>(string owner, PluginInfo info, Func<IPluginContext, CancellationToken, Task<T?>> call, IServiceProvider sp, CancellationToken ct, bool strict = false)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Budget);
            var ctx = PluginRequestContext.ForBackground(info, sp, _registry);
            var task = call(ctx, cts.Token);
            if (await Task.WhenAny(task, Task.Delay(Budget, ct)) != task)
            {
                cts.Cancel();
                _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException($"antwortete nicht innerhalb von {Budget.TotalSeconds:0} s");
            }
            return await task;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _registry.AddLog(owner, ex.Message);
            if (strict) throw new InvalidOperationException($"Das Plugin „{owner}“ konnte nicht sagen, welche Dateien es nutzt: {ex.Message}", ex);
            return default;
        }
    }

    private static readonly TimeSpan MenuBudget = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The entries plugins add to a public menu, as menu items the layout draws like the site's own (negative ids, so they never collide with a stored one). Asked with the REQUEST's
    /// context. One plugin that is slow (400 ms), throws or sends nonsense is skipped and logged; a link may only lead to a local path or to http(s).
    /// </summary>
    public async Task<IReadOnlyList<MatCMS.Models.MenuItem>> MenuItemsAsync(HttpContext http, string menu, string locale, CancellationToken ct = default)
    {
        var list = new List<MatCMS.Models.MenuItem>();
        var next = -1;
        foreach (var c in _registry.Menus)
        {
            try
            {
                var ctx = await PluginRequestContext.FromHttpAsync(http, c.Info, _registry, "/", new Dictionary<string, string>(), withAntiforgery: false);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(MenuBudget);
                var task = c.Provider(ctx, menu, cts.Token);
                if (await Task.WhenAny(task, Task.Delay(MenuBudget, ct)) != task)
                {
                    cts.Cancel();
                    _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    _registry.AddLog(c.Owner, $"Menü „{menu}“: keine Antwort innerhalb von {MenuBudget.TotalMilliseconds:0} ms, übersprungen.");
                    continue;
                }
                var order = 0;
                foreach (var e in (await task).Where(e => e is not null).OrderBy(e => e.Order))
                {
                    if (Item(e, menu, locale, ref next, null, 100_000 + order++ * 100) is not { } parent) continue;
                    list.Add(parent);
                    // The entry's dropdown, as deep as a menu may go (a category tree: category, subcategory, …).
                    void Below(MenuEntry entry, int parentId, int level)
                    {
                        if (level >= MatCMS.Content.MenuTree.MaxLevels) return;
                        var child = 0;
                        foreach (var ch in entry.Children.Take(30))
                            if (list.Count < 500 && Item(ch, menu, locale, ref next, parentId, child++) is { } item) { list.Add(item); Below(ch, item.Id, level + 1); }
                    }
                    Below(e, parent.Id, 1);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _registry.AddLog(c.Owner, $"Menü „{menu}“: {ex.Message}"); }
        }
        return list;
    }

    // ---- link targets and menu elements ------------------------------------------------------------------

    /// <summary>One group of link targets for the picker.</summary>
    public sealed record LinkGroup(string Group, IReadOnlyList<LinkTarget> Items);

    private const int MaxLinkTargets = 300;

    /// <summary>A link may only lead to a local address or to http(s).</summary>
    internal static bool SafeLink(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0 || u.Any(char.IsControl) || u.IndexOfAny(new[] { '"', '\'', '<', '>', ' ' }) >= 0) return false;
        return (u.StartsWith('/') && !u.StartsWith("//") && !u.StartsWith("/\\")) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The link targets plugins offer, one group each (empty groups and plugins that fail are left out).</summary>
    public async Task<IReadOnlyList<LinkGroup>> LinkTargetsAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var groups = new List<LinkGroup>();
        foreach (var e in _registry.LinkTargetSets)
        {
            if (await CallAsync<IEnumerable<LinkTarget>>(e.Owner, e.Info, (ctx, t) => e.Provider(ctx, t)!, sp, ct) is not { } rows) continue;
            var items = rows.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Label) && SafeLink(r.Url))
                .Take(MaxLinkTargets).Select(r => new LinkTarget(r.Label.Trim().Length > 120 ? r.Label.Trim()[..120] : r.Label.Trim(), r.Url.Trim())).ToList();
            if (items.Count > 0) groups.Add(new LinkGroup(PluginStrings.Label(_registry.SliceOf(e.Owner), PluginRequestContext.CurrentLocale(), e.Group), items));
        }
        return groups;
    }

    /// <summary>The menu elements the editor can add, with the value a menu item stores (<c>plugin:shop.cart</c>).</summary>
    public IReadOnlyList<(string Label, string Value)> MenuElementChoices() =>
        _registry.MenuElements.Select(e => (PluginStrings.Label(_registry.SliceOf(e.Owner), PluginRequestContext.CurrentLocale(), e.Spec.Label), "plugin:" + e.Key)).ToList();

    /// <summary>True when a loaded plugin offers the element (<c>shop.cart</c>).</summary>
    public bool HasMenuElement(string key) => _registry.MenuElements.Any(e => e.Key == key);

    /// <summary>
    /// Turns the stored menu items that stand for a plugin element (<c>plugin:shop.cart</c>) into what to draw for THIS visitor: the element's label, address, count and markup, its
    /// dropdown entries as children. An item whose plugin is off, whose element says "nothing for this visitor" or fails is left out (with what hangs under it) — the site never
    /// shows a dead link. The item's own icon (the editor's choice) wins over the element's.
    /// </summary>
    public async Task<List<MatCMS.Models.MenuItem>> ResolveElementsAsync(HttpContext http, IReadOnlyList<MatCMS.Models.MenuItem> stored, CancellationToken ct = default)
    {
        var result = new List<MatCMS.Models.MenuItem>(stored.Count);
        var dropped = new HashSet<int>();
        var next = -1_000_000;
        foreach (var it in stored)
        {
            if (it.ParentId is int pid && dropped.Contains(pid)) { dropped.Add(it.Id); continue; }
            if (!it.Url.StartsWith("plugin:", StringComparison.Ordinal)) { result.Add(it); continue; }
            var entry = _registry.MenuElements.FirstOrDefault(e => e.Key == it.Url["plugin:".Length..]);
            MenuEntry? view = null;
            if (entry is not null)
            {
                try
                {
                    var ctx = await PluginRequestContext.FromHttpAsync(http, entry.Info, _registry, "/", new Dictionary<string, string>(), withAntiforgery: false);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(MenuBudget);
                    var task = entry.Provider(ctx, cts.Token);
                    if (await Task.WhenAny(task, Task.Delay(MenuBudget, ct)) == task) view = await task;
                    else
                    {
                        cts.Cancel();
                        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        _registry.AddLog(entry.Owner, $"Menü-Element „{entry.Spec.Name}“: keine Antwort innerhalb von {MenuBudget.TotalMilliseconds:0} ms, übersprungen.");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _registry.AddLog(entry.Owner, $"Menü-Element „{entry.Spec.Name}“: {ex.Message}"); }
            }
            var url = (view?.Url ?? "").Trim();
            var hasHtml = !string.IsNullOrWhiteSpace(view?.Html);
            if (view is null || string.IsNullOrWhiteSpace(view.Label) || (!SafeLink(url) && !hasHtml)) { dropped.Add(it.Id); continue; }
            string? Icon(string? i) => i is { Length: > 0 } && System.Text.RegularExpressions.Regex.IsMatch(i, "^[a-z][a-z0-9-]*$") ? i : null;
            result.Add(new MatCMS.Models.MenuItem
            {
                Id = it.Id, Menu = it.Menu, Locale = it.Locale, ParentId = it.ParentId, SortOrder = it.SortOrder, OpenInNewTab = it.OpenInNewTab,
                Label = view.Label.Trim().Length > 80 ? view.Label.Trim()[..80] : view.Label.Trim(), Url = SafeLink(url) ? url : "#",
                Icon = it.Icon ?? Icon(view.Icon) ?? Icon(entry!.Spec.Icon),
                Badge = string.IsNullOrWhiteSpace(view.Badge) ? null : (view.Badge.Trim().Length > 6 ? view.Badge.Trim()[..6] : view.Badge.Trim()),
                Html = hasHtml ? view.Html : null, Element = it.Url["plugin:".Length..]
            });
            // Its dropdown, as deep as a menu may go (a category tree is category, subcategory, …); an entry without a label or a good address is dropped with what is below it.
            void Below(MenuEntry entry, int parentId, int level)
            {
                if (level >= MatCMS.Content.MenuTree.MaxLevels) return;
                var order = 0;
                foreach (var ch in entry.Children.Take(30))
                {
                    if (result.Count >= 500 || string.IsNullOrWhiteSpace(ch.Label) || !SafeLink(ch.Url)) continue;
                    var id = next--;
                    result.Add(new MatCMS.Models.MenuItem { Id = id, Menu = it.Menu, Locale = it.Locale, ParentId = parentId, SortOrder = order++, Label = ch.Label.Trim().Length > 80 ? ch.Label.Trim()[..80] : ch.Label.Trim(), Url = ch.Url.Trim() });
                    Below(ch, id, level + 1);
                }
            }
            Below(view, it.Id, 1);
        }
        // A child may be listed BEFORE its parent (the list is in the editor's order): what hangs under a dropped item goes with it, however deep.
        int removed;
        do removed = result.RemoveAll(i => i.ParentId is int p && dropped.Contains(p) && dropped.Add(i.Id)); while (removed > 0);
        return result;
    }

    /// <summary>
    /// One menu element drawn for a layout that places it itself (<c>{{element:shop.search}}</c>), independent of any menu: the element's own markup, else its symbol as an icon link, else
    /// (a dropdown, a plain link) the default menu markup. Empty when the plugin is off or has nothing for this visitor.
    /// </summary>
    public async Task<string> ElementHtmlAsync(HttpContext http, string key, CancellationToken ct = default)
    {
        var stub = new MatCMS.Models.MenuItem { Id = 1, Menu = "", Locale = "", Label = "", Url = "plugin:" + key };
        var items = await ResolveElementsAsync(http, new[] { stub }, ct);
        if (items.Count == 0) return "";
        if (items.Count == 1 && items[0].Html is { Length: > 0 } own) return own;
        if (items.Count == 1 && !string.IsNullOrEmpty(items[0].Icon)) return MatCMS.Content.LayoutRenderer.IconLink(items[0]);
        return MatCMS.Content.LayoutRenderer.RenderInlineMenu(items);
    }

    private static MatCMS.Models.MenuItem? Item(MenuEntry e, string menu, string locale, ref int id, int? parent, int sort)
    {
        var url = (e.Url ?? "").Trim();
        var local = url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");
        if (string.IsNullOrWhiteSpace(e.Label) || !(local || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))) return null;
        var icon = e.Icon is { Length: > 0 } i && System.Text.RegularExpressions.Regex.IsMatch(i, "^[a-z][a-z0-9-]*$") ? i : null;
        return new MatCMS.Models.MenuItem
        {
            Id = id--, Menu = menu, Locale = locale, ParentId = parent, Label = e.Label.Trim().Length > 80 ? e.Label.Trim()[..80] : e.Label.Trim(), Url = url, Icon = icon, SortOrder = sort,
            Badge = string.IsNullOrWhiteSpace(e.Badge) ? null : (e.Badge.Trim().Length > 6 ? e.Badge.Trim()[..6] : e.Badge.Trim())
        };
    }

    /// <summary>The figures of the dashboard tiles plugins offer.</summary>
    public async Task<IReadOnlyList<UiStat>> TilesAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var list = new List<UiStat>();
        foreach (var c in _registry.Tiles)
            if (await CallAsync<UiStat>(c.Owner, c.Info, (ctx, t) => c.Provider(ctx, t), sp, ct) is { } s) list.Add(s);
        return list;
    }

    /// <summary>The rows plugins add to "recently edited".</summary>
    public async Task<IReadOnlyList<RecentItem>> RecentAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var list = new List<RecentItem>();
        foreach (var c in _registry.Recents)
            if (await CallAsync<IEnumerable<RecentItem>>(c.Owner, c.Info, (ctx, t) => c.Provider(ctx, t)!, sp, ct) is { } rows) list.AddRange(rows);
        return list;
    }

    /// <summary>The plugins' own dashboard cards. A row or "all" link that does not lead to a local admin address is dropped; at most 30 rows a card.</summary>
    public async Task<IReadOnlyList<(string Id, DashboardCard Card)>> CardsAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        static bool Local(string? u) => !string.IsNullOrWhiteSpace(u) && u.StartsWith('/') && !u.StartsWith("//") && !u.StartsWith("/\\") && !u.Any(char.IsControl);
        var list = new List<(string Id, DashboardCard Card)>();
        var counts = new Dictionary<string, int>();
        foreach (var c in _registry.Cards)
        {
            if (await CallAsync<DashboardCard>(c.Owner, c.Info, (ctx, t) => c.Provider(ctx, t), sp, ct) is not { } card || string.IsNullOrWhiteSpace(card.Title)) continue;
            // The id is what a user's dashboard layout remembers: the plugin's key, and a number for a second card of the same plugin.
            var n = counts[c.Owner] = counts.GetValueOrDefault(c.Owner) + 1;
            list.Add(("card:" + c.Owner + (n > 1 ? ":" + n : ""), card with
            {
                Rows = (card.Rows ?? Array.Empty<DashboardRow>()).Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Text) && Local(r.Url)).Take(30).ToList(),
                AllUrl = Local(card.AllUrl) ? card.AllUrl : null
            }));
        }
        return list;
    }

    /// <summary>The lines plugins add to "Braucht Aufmerksamkeit".</summary>
    public async Task<IReadOnlyList<AttentionNote>> AttentionAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var list = new List<AttentionNote>();
        foreach (var c in _registry.Attentions)
            if (await CallAsync<IEnumerable<AttentionNote>>(c.Owner, c.Info, (ctx, t) => c.Provider(ctx, t)!, sp, ct) is { } rows) list.AddRange(rows);
        return list;
    }

    /// <summary>The pages plugins add to the sitemap.</summary>
    public async Task<IReadOnlyList<SitemapEntry>> SitemapAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var list = new List<SitemapEntry>();
        foreach (var c in _registry.Sitemaps)
            if (await CallAsync<IEnumerable<SitemapEntry>>(c.Owner, c.Info, (ctx, t) => c.Provider(ctx, t)!, sp, ct) is { } rows) list.AddRange(rows);
        return list;
    }

    /// <summary>The media files the loaded plugins say they use. A plugin that fails stops the whole question (see the class remark).</summary>
    public async Task<IReadOnlyList<string>> MediaInUseAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var list = new List<string>();
        foreach (var c in _registry.MediaRefs)
            if (await CallAsync<IEnumerable<string>>(c.Owner, c.Info, (ctx, t) => c.Provider(ctx, t)!, sp, ct, strict: true) is { } rows) list.AddRange(rows);
        return list;
    }

    /// <summary>The choices of a block field's option source ("plugin:{key}:{name}"). Empty when the plugin is off, the source is unknown or the provider fails.</summary>
    public async Task<List<MatCMS.Content.SelectOption>> OptionsAsync(IServiceProvider sp, string source, CancellationToken ct = default)
    {
        var parts = source.Split(':', 3);
        if (parts.Length != 3 || parts[0] != "plugin" || _registry.OptionSourceOf(parts[1], parts[2]) is not { } entry) return new();
        var rows = await CallAsync<IReadOnlyList<FieldOption>>(entry.Owner, entry.Info, (ctx, t) => entry.Provider(ctx, t)!, sp, ct);
        return rows?.Select(o => new MatCMS.Content.SelectOption(o.Value, o.Label)).ToList() ?? new();
    }

    /// <summary>True when a loaded plugin offers an operation of this name.</summary>
    public bool HasOperation(string name) => _registry.Operations.Any(o => o.Name == name);

    /// <summary>The operations the site's AI assistant may use: described ones, neither internal nor carrying customers' data (every answer the
    /// assistant reads goes to an outside model provider).</summary>
    public IReadOnlyList<PluginRegistry.OperationEntry> AssistantOperations =>
        _registry.Operations.Where(o => o.Spec is { Internal: false, PersonalData: false }).ToList();

    /// <summary>Runs the operation of this name. Throws when none is loaded; what the handler throws propagates (the caller reports the op failed).
    /// An INTERNAL operation (an undo) runs only when the host itself asks (<paramref name="allowInternal"/>).</summary>
    public async Task<JsonNode?> RunOperationAsync(IServiceProvider sp, string name, JsonNode? args, CancellationToken ct = default, bool allowInternal = false)
    {
        var op = _registry.Operations.FirstOrDefault(o => o.Name == name) ?? throw new InvalidOperationException($"Keine geladene Plugin-Aktion '{name}'.");
        if (op.Spec?.Internal == true && !allowInternal) throw new InvalidOperationException($"Die Plugin-Aktion '{name}' ist nur intern nutzbar.");
        var ctx = PluginRequestContext.ForBackground(op.Info, sp, _registry);
        return await op.Handler(ctx, args, ct);
    }
}
