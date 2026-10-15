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

    /// <summary>Runs the operation of this name. Throws when none is loaded; what the handler throws propagates (the caller reports the op failed).</summary>
    public async Task<JsonNode?> RunOperationAsync(IServiceProvider sp, string name, JsonNode? args, CancellationToken ct = default)
    {
        var op = _registry.Operations.FirstOrDefault(o => o.Name == name) ?? throw new InvalidOperationException($"Keine geladene Plugin-Aktion '{name}'.");
        var ctx = PluginRequestContext.ForBackground(op.Info, sp, _registry);
        return await op.Handler(ctx, args, ct);
    }
}
