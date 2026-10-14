using MatCMS.Plugin;
using MatCMS.Services.Plugins;

namespace MatCMS.Services;

/// <summary>One line of the back-office sidebar, whoever contributed it.</summary>
/// <param name="Label">The text; for a core entry the localisation key (<see cref="IsCore"/>).</param>
/// <param name="Url">Where it leads.</param>
/// <param name="Icon">A Tabler icon class (<c>ti-star</c>).</param>
/// <param name="Order">Position inside its group; lower comes first.</param>
/// <param name="IsCore">True for the CMS's own entries, whose label is a key to translate.</param>
/// <param name="Badge">A counter, or null.</param>
public sealed record AdminNavEntry(string Label, string Url, string Icon, int Order, bool IsCore, string? Badge = null);

/// <summary>
/// The sidebar as a registry. The layout used to hard-code three groups of links with the plugins' entries
/// appended below as loose lines; now the core entries and the plugins' entries are the same kind of thing in
/// the same list, so a plugin takes a <see cref="AdminGroup"/> and an order and stands BETWEEN the core entries
/// where it belongs (the blog sits between pages and menus) — and the blog, a plugin itself, is not special.
/// <para>Core entries are spaced 10 apart, a plugin defaults to order 100 (after the core entries of its group).</para>
/// </summary>
public sealed class AdminNav
{
    private sealed record Core(string LabelKey, string Url, string Icon, AdminGroup Group, int Order);

    private static readonly Core[] CoreEntries =
    {
        new("nav.pages", "/admin/pages", "ti-file-text", AdminGroup.Content, 10),
        new("nav.menus", "/admin/menus", "ti-menu-2", AdminGroup.Content, 30),
        new("nav.forms", "/admin/forms", "ti-forms", AdminGroup.Content, 40),
        new("nav.media", "/admin/media", "ti-photo", AdminGroup.Content, 50),

        new("nav.templates", "/admin/templates", "ti-palette", AdminGroup.Design, 10),
        new("nav.components", "/admin/components", "ti-puzzle", AdminGroup.Design, 20),
        new("nav.plugins", "/admin/plugins", "ti-plug", AdminGroup.Design, 30),
        new("nav.mailtemplates", "/admin/mailtemplates", "ti-mail", AdminGroup.Design, 40),
        new("nav.settings", "/admin/settings", "ti-settings", AdminGroup.Design, 50),

        new("nav.stats", "/admin/stats", "ti-chart-bar", AdminGroup.System, 10),
        new("nav.users", "/admin/users", "ti-users", AdminGroup.System, 20),
        new("nav.backup", "/admin/backup", "ti-device-floppy", AdminGroup.System, 30),
        new("nav.logs", "/admin/logs", "ti-list-details", AdminGroup.System, 40),
        new("nav.cleanup", "/admin/cleanup", "ti-eraser", AdminGroup.System, 50),
    };

    private readonly PluginRegistry _registry;
    private readonly Dictionary<string, (PluginRegistry.SectionEntry Section, DateTime At, string? Value)> _badges = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>How long a computed counter is reused: the sidebar is drawn on every admin page.</summary>
    private static readonly TimeSpan BadgeTtl = TimeSpan.FromSeconds(15);
    /// <summary>A counter that takes longer than this is skipped for this page — a slow plugin must not slow the back office.</summary>
    private static readonly TimeSpan BadgeBudget = TimeSpan.FromMilliseconds(400);

    /// <summary>Creates the navigation.</summary>
    public AdminNav(PluginRegistry registry) => _registry = registry;

    /// <summary>The entries of one group, ordered, core and plugin entries together.</summary>
    public async Task<IReadOnlyList<AdminNavEntry>> GroupAsync(AdminGroup group, IServiceProvider services, CancellationToken ct = default)
    {
        var list = new List<AdminNavEntry>();
        list.AddRange(CoreEntries.Where(c => c.Group == group).Select(c => new AdminNavEntry(c.LabelKey, c.Url, c.Icon, c.Order, true)));
        foreach (var m in _registry.AdminMenu.Where(m => m.Group == group))
        {
            var icon = m.Icon is { } ic && ic.StartsWith("ti-", StringComparison.Ordinal) ? ic : "ti-plug";
            var section = _registry.Sections.FirstOrDefault(s => s.Url == m.Url && s.Owner == m.Owner);
            var badge = section?.Badge is { } provider ? await BadgeAsync(section, provider, services, ct) : null;
            // A label that is a key of the plugin's own strings is shown in the admin's language.
            var label = PluginStrings.Label(_registry.SliceOf(m.Owner), PluginRequestContext.CurrentLocale(), m.Label);
            list.Add(new AdminNavEntry(label, m.Url, icon, m.Order, false, badge));
        }
        // Stable: entries of the same order keep core-first, then plugin load order.
        return list.OrderBy(e => e.Order).ToList();
    }

    /// <summary>True when any plugin entry sits in the group — the layout draws the plugin block only then.</summary>
    public bool HasPluginEntries(AdminGroup group) => _registry.AdminMenu.Any(m => m.Group == group);

    private async Task<string?> BadgeAsync(PluginRegistry.SectionEntry section, PluginBadgeProvider provider, IServiceProvider services, CancellationToken ct)
    {
        var key = section.Owner;
        lock (_lock)
            // The section object changes when the plugin is reloaded, which drops the old counter at once.
            if (_badges.TryGetValue(key, out var hit) && ReferenceEquals(hit.Section, section) && DateTime.UtcNow - hit.At < BadgeTtl) return hit.Value;
        string? value = null;
        try
        {
            var ctx = PluginRequestContext.ForBackground(section.Info, services, _registry);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = provider(ctx, cts.Token);
            // A provider that ignores its token must not hold the page up either: wait for the budget, not for it.
            if (await Task.WhenAny(task, Task.Delay(BadgeBudget, ct)) == task)
            {
                var text = await task;
                value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                if (value is { Length: > 6 }) value = value[..6];
            }
            else
            {
                cts.Cancel();
                _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // observed, not rethrown
                _registry.AddLog(key, "Badge took longer than " + BadgeBudget.TotalMilliseconds + " ms and was skipped.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _registry.AddLog(key, "Badge: " + ex.Message); }
        lock (_lock) _badges[key] = (section, DateTime.UtcNow, value);
        return value;
    }

}
