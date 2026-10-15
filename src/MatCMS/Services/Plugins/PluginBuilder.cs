using MatCMS.Content;
using MatCMS.Plugin;

namespace MatCMS.Services.Plugins;

/// <summary>
/// The host's <see cref="IPluginBuilder"/>: one per plugin load. Everything a plugin registers lands in
/// the plugin's private <see cref="PluginRegistry.Slice"/>; nothing is visible to requests until the
/// loader publishes the slice, and a plugin that throws while configuring contributes nothing.
/// </summary>
internal sealed class PluginBuilder : IPluginBuilder, IAdminBuilder, ISettingsBuilder, IDataBuilder, ILifecycleBuilder
{
    private static readonly System.Text.RegularExpressions.Regex TableName = new("^[a-z][a-z0-9_]{0,39}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SettingKey = new("^[a-zA-Z][a-zA-Z0-9_.-]{0,60}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex EventName = new("^[a-z][a-z0-9._-]{0,79}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly PluginRegistry _registry;
    private readonly PluginRegistry.Slice _slice;
    private readonly bool _builtIn;
    private bool _sectionDeclared;

    /// <param name="registry">The registry the slice will be published to.</param>
    /// <param name="slice">The plugin's private slice.</param>
    /// <param name="info">The plugin.</param>
    /// <param name="builtIn">True for a plugin compiled into the CMS itself: only those may adopt an existing table.</param>
    public PluginBuilder(PluginRegistry registry, PluginRegistry.Slice slice, PluginInfo info, bool builtIn = false)
    {
        _registry = registry; _slice = slice; Info = info; _builtIn = builtIn;
    }

    public ISettingsBuilder Settings => this;
    public IDataBuilder Data => this;
    public ILifecycleBuilder Lifecycle => this;

    // ---- settings ---------------------------------------------------------------------------------

    public ISettingsBuilder Add(SettingSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!SettingKey.IsMatch(spec.Key ?? "")) throw new InvalidOperationException($"Setting '{spec.Key}': a key starts with a letter and has letters, digits, '.', '_' or '-' (at most 61).");
        if (_slice.SettingSpecs.Any(s => string.Equals(s.Key, spec.Key, StringComparison.Ordinal))) throw new InvalidOperationException($"Setting '{spec.Key}' is declared twice.");
        if (spec.Kind == SettingKind.Choice && spec.Options.Count == 0) throw new InvalidOperationException($"Setting '{spec.Key}' is a choice without options.");
        if (spec.Kind == SettingKind.Number && spec.Default.Length > 0 && !double.TryParse(spec.Default, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException($"Setting '{spec.Key}': the default '{spec.Default}' is not a number.");
        _slice.SettingSpecs.Add(spec with { Label = string.IsNullOrWhiteSpace(spec.Label) ? spec.Key : spec.Label });
        return this;
    }

    // ---- data -------------------------------------------------------------------------------------

    public void Table(string name, Action<ITableBuilder> define)
    {
        ArgumentNullException.ThrowIfNull(define);
        if (!TableName.IsMatch(name ?? "")) throw new InvalidOperationException($"Table '{name}': a name is lower-case letters, digits and '_', starting with a letter (at most 40).");
        if (_slice.TableSpecs.Any(t => t.Name == name)) throw new InvalidOperationException($"Table '{name}' is declared twice.");
        var tb = new TableBuilder(_builtIn);
        define(tb);
        _slice.TableSpecs.Add(tb.Build(name));
    }

    private sealed class TableBuilder(bool builtIn) : ITableBuilder
    {
        private static readonly System.Text.RegularExpressions.Regex ColumnName = new("^[A-Za-z][A-Za-z0-9_]{0,39}$", System.Text.RegularExpressions.RegexOptions.Compiled);
        private readonly List<ColumnSpec> _columns = new();
        private readonly List<IndexSpec> _indexes = new();
        private string? _adopts;
        private string _key = "id";

        private ITableBuilder AddColumn(string name, ColumnType type, bool required, bool unique = false, string? def = null)
        {
            if (!ColumnName.IsMatch(name ?? "")) throw new InvalidOperationException($"Column '{name}': letters, digits and '_', starting with a letter (at most 40).");
            if (_columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"Column '{name}' is declared twice.");
            _columns.Add(new ColumnSpec(name!, type) { Required = required, Unique = unique, Default = def });
            return this;
        }

        public ITableBuilder Text(string name, bool required = false, bool unique = false, string? def = null) => AddColumn(name, ColumnType.Text, required, unique, def);
        public ITableBuilder Int(string name, bool required = false, string? def = null) => AddColumn(name, ColumnType.Int, required, false, def);
        public ITableBuilder Real(string name, bool required = false, string? def = null) => AddColumn(name, ColumnType.Real, required, false, def);
        public ITableBuilder Bool(string name, bool required = false, string? def = null) => AddColumn(name, ColumnType.Bool, required, false, def);
        public ITableBuilder Timestamp(string name, bool required = false) => AddColumn(name, ColumnType.DateTime, required);
        public ITableBuilder Json(string name, bool required = false, string? def = null) => AddColumn(name, ColumnType.Json, required, false, def);
        public ITableBuilder Index(params string[] columns) { _indexes.Add(new IndexSpec(columns, false)); return this; }
        public ITableBuilder UniqueIndex(params string[] columns) { _indexes.Add(new IndexSpec(columns, true)); return this; }

        public ITableBuilder Adopt(string existingTable, string keyColumn = "Id")
        {
            if (!builtIn) throw new InvalidOperationException("Only a plugin that is part of the CMS may take over an existing table.");
            if (!ColumnName.IsMatch(existingTable ?? "") || !ColumnName.IsMatch(keyColumn ?? "")) throw new InvalidOperationException("Adopt: a table and key name are letters, digits and '_'.");
            _adopts = existingTable; _key = keyColumn!;
            return this;
        }

        public TableSpec Build(string name)
        {
            foreach (var ix in _indexes)
            {
                if (ix.Columns.Count == 0) throw new InvalidOperationException($"Table '{name}': an index needs columns.");
                foreach (var c in ix.Columns)
                    if (!_columns.Any(x => string.Equals(x.Name, c, StringComparison.OrdinalIgnoreCase)) && !string.Equals(c, _key, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Table '{name}': the index names the unknown column '{c}'.");
            }
            if (_adopts is null && _columns.Any(c => string.Equals(c.Name, "id", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Table '{name}': 'id' is the table's key and implicit.");
            return new TableSpec(name, _columns.ToList(), _indexes.ToList()) { Adopts = _adopts, KeyColumn = _key };
        }
    }

    // ---- lifecycle, events, jobs ------------------------------------------------------------------

    public void OnInstall(PluginLifecycleHandler handler) { ArgumentNullException.ThrowIfNull(handler); _slice.OnInstall.Add(handler); }
    public void OnUpdate(PluginUpdateHandler handler) { ArgumentNullException.ThrowIfNull(handler); _slice.OnUpdate.Add(handler); }
    public void OnLoad(PluginLifecycleHandler handler) { ArgumentNullException.ThrowIfNull(handler); _slice.OnLoad.Add(handler); }

    public void On(string name, PluginEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!EventName.IsMatch(name ?? "")) throw new InvalidOperationException($"Event '{name}': a name is lower-case letters, digits, '.', '_' or '-', starting with a letter.");
        _slice.Events.Add(new PluginRegistry.EventEntry(Info.Key, Info, name!, handler));
    }

    public void Job(string name, JobSchedule schedule, PluginJobHandler handler)
    {
        ArgumentNullException.ThrowIfNull(schedule); ArgumentNullException.ThrowIfNull(handler);
        if (!EventName.IsMatch(name ?? "")) throw new InvalidOperationException($"Job '{name}': a name is lower-case letters, digits, '.', '_' or '-', starting with a letter.");
        if (_slice.Jobs.Any(j => j.Name == name)) throw new InvalidOperationException($"Job '{name}' is declared twice.");
        _slice.Jobs.Add(new PluginRegistry.JobEntry(Info.Key, Info, name!, schedule, handler));
    }

    public void Strings(string culture, IReadOnlyDictionary<string, string> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var c = (culture ?? "").Trim().ToLowerInvariant();
        if (c.Length is < 2 or > 10) throw new InvalidOperationException($"Strings: '{culture}' is not a language code.");
        if (!_slice.StringTables.TryGetValue(c, out var t)) _slice.StringTables[c] = t = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in table) t[kv.Key] = kv.Value;
    }

    public void Sitemap(PluginSitemapProvider provider) { ArgumentNullException.ThrowIfNull(provider); _slice.Sitemaps.Add(new(Info.Key, Info, provider)); }
    public void MediaReferences(PluginMediaProvider provider) { ArgumentNullException.ThrowIfNull(provider); _slice.MediaRefs.Add(new(Info.Key, Info, provider)); }

    public void Operation(string name, PluginOperationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!EventName.IsMatch(name ?? "")) throw new InvalidOperationException($"Operation '{name}': a name is lower-case letters, digits, '.', '_' or '-', starting with a letter.");
        if (_slice.Operations.Any(o => o.Name == name)) throw new InvalidOperationException($"Operation '{name}' is declared twice.");
        _slice.Operations.Add(new PluginRegistry.OperationEntry(Info.Key, Info, name!, handler));
    }

    private static readonly System.Text.RegularExpressions.Regex MailKey = new(@"^[a-z][a-z0-9_-]{0,40}(\.[a-z0-9_-]{1,40}){1,3}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public void MailTemplate(MailTemplateSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!MailKey.IsMatch(spec.Key ?? "") || !spec.Key!.StartsWith(Info.Key + ".", StringComparison.Ordinal))
            throw new InvalidOperationException($"Mail template '{spec.Key}': the key is '{Info.Key}.' followed by a name (lower-case letters, digits, '_', '-', '.').");
        if (_slice.MailTemplateSpecs.Any(m => m.Key == spec.Key)) throw new InvalidOperationException($"Mail template '{spec.Key}' is declared twice.");
        if (string.IsNullOrWhiteSpace(spec.Subject) && string.IsNullOrWhiteSpace(spec.Body)) throw new InvalidOperationException($"Mail template '{spec.Key}' has neither subject nor body.");
        _slice.MailTemplateSpecs.Add(spec);
    }

    // ---- dashboard ----------------------------------------------------------------------------------

    public void Tile(PluginTileProvider provider) { ArgumentNullException.ThrowIfNull(provider); _slice.Tiles.Add(new(Info.Key, Info, provider)); }
    public void RecentlyEdited(PluginRecentProvider provider) { ArgumentNullException.ThrowIfNull(provider); _slice.Recents.Add(new(Info.Key, Info, provider)); }
    public void Attention(PluginAttentionProvider provider) { ArgumentNullException.ThrowIfNull(provider); _slice.Attentions.Add(new(Info.Key, Info, provider)); }

    public PluginInfo Info { get; }
    public IAdminBuilder Admin => this;

    public void Route(string pattern, PluginHandler handler, PublicRouteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var p = PluginRoutePattern.TryParse(pattern, out var why)
                ?? throw new InvalidOperationException($"Route '{pattern}': {why}.");
        // A plugin claims a part of the public URL space: it must name it. A pattern that starts with a
        // parameter would answer for every page of the site, and the host's own areas are off limits.
        if (p.FirstLiteral.Length == 0)
            throw new InvalidOperationException($"Route '{pattern}': a public route must start with a fixed segment (e.g. /{Info.Key}/…).");
        if (p.IsReserved)
            throw new InvalidOperationException($"Route '{pattern}': '/{p.FirstLiteral}' belongs to the CMS.");
        var opts = options ?? new PublicRouteOptions();
        // A webhook is POSTed to; answering a GET for it would only give a stranger something to probe.
        if (opts.Webhook) opts = opts with { Methods = PluginMethod.Post };
        _slice.Routes.Add(new PluginRegistry.RouteEntry(Info.Key, Info, p, handler, opts));
    }

    public IAdminSection Section(string label, string icon = "ti-plug", AdminGroup group = AdminGroup.Plugins, int order = 100)
    {
        if (_sectionDeclared)
            throw new InvalidOperationException("A plugin has one admin section; further pages are tabs of it (Section(...).Page(...)).");
        _sectionDeclared = true;
        var pages = new List<PluginRegistry.SectionPage>();
        // A Tabler icon name; anything else gets the neutral plugin icon — the back office shows no emojis.
        var ic = !string.IsNullOrWhiteSpace(icon) && icon.StartsWith("ti-", StringComparison.Ordinal) ? icon : "ti-plug";
        var entry = new PluginRegistry.SectionEntry(Info.Key, Info,
            string.IsNullOrWhiteSpace(label) ? Info.Name : label, ic, group, order, pages);
        _slice.Sections.Add(entry);
        return new SectionBuilder(pages, entry);
    }

    public void Block(BlockSpec spec, PluginHandler render)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(render);
        if (string.IsNullOrWhiteSpace(spec.Type)) throw new InvalidOperationException("A block needs a type.");
        var info = Info;
        var registry = _registry;
        var type = spec.Type;
        // The block pipeline still hands over the host's established request type and expects a string
        // back; the adapter turns that into a context, runs the handler and unwraps the result.
        async Task<string> RenderAsync(PluginRequest legacy, CancellationToken ct)
        {
            var ctx = PluginRequestContext.FromBlock(info, type, legacy, registry);
            var result = await render(ctx, ct);
            return result.Kind is PluginResult.ResultKind.Page or PluginResult.ResultKind.Fragment
                ? PluginResults.Html(result, legacy.Services, MatCMS.Shared.Web.UiTarget.Public)
                : result.Kind == PluginResult.ResultKind.Text ? result.Content : "";
        }
        // The synchronous entry stays for the callers that cannot await; the page itself awaits RenderAsync.
        string Render(PluginRequest legacy) => RenderAsync(legacy, CancellationToken.None).GetAwaiter().GetResult();
        _slice.Blocks.Add(new PluginRegistry.PluginBlock(type, string.IsNullOrWhiteSpace(spec.Name) ? type : spec.Name,
            spec.Description ?? "", Render)
        {
            RenderAsync = RenderAsync,
            Category = spec.Category, IconSvg = spec.IconSvg,
            Owner = Info.Key,
            Fields = spec.Fields.Select(ToBlockField).ToList()
        });
    }

    public void OptionsSource(string name, Func<IPluginContext, CancellationToken, Task<IReadOnlyList<FieldOption>>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(name) || name.Contains(':')) throw new InvalidOperationException("An option source needs a name without a colon.");
        if (_slice.OptionSources.Any(o => o.Name == name)) throw new InvalidOperationException($"The option source '{name}' is registered twice.");
        _slice.OptionSources.Add(new PluginRegistry.OptionSourceEntry(Info.Key, Info, name, provider));
    }

    private BlockField ToBlockField(FieldSpec f)
    {
        var field = new BlockField
        {
            Id = f.Id, Label = string.IsNullOrWhiteSpace(f.Label) ? f.Id : f.Label,
            Type = f.Kind switch
            {
                FieldKind.Textarea => FieldType.Textarea,
                FieldKind.RichText => FieldType.RichText,
                FieldKind.Image => FieldType.Image,
                FieldKind.Url => FieldType.Url,
                FieldKind.Select => FieldType.Select,
                FieldKind.List => FieldType.List,
                FieldKind.MultiSelect => FieldType.MultiSelect,
                _ => FieldType.Text
            },
            Placeholder = f.Placeholder, Help = f.Help, Default = f.Default,
            // The editor knows a plugin's list by "plugin:{key}:{name}", so two plugins may both call theirs "categories".
            OptionsSource = string.IsNullOrWhiteSpace(f.OptionsSource) ? null : $"plugin:{Info.Key}:{f.OptionsSource}",
            ShowWhenField = f.ShowWhenField, ShowWhenValue = f.ShowWhenValue
        };
        foreach (var o in f.Options) field.Options.Add(new SelectOption(o.Value, o.Label));
        return field;
    }

    public void Head(string html) { if (!string.IsNullOrWhiteSpace(html)) _slice.Head.Add(html); }
    public void Body(string html) { if (!string.IsNullOrWhiteSpace(html)) _slice.Body.Add(html); }

    public void Script(string assetFile, bool inHead = false, bool defer = false)
    {
        var url = AssetUrl(assetFile);
        if (url.Length == 0) return;
        var tag = $"<script src=\"{System.Net.WebUtility.HtmlEncode(url)}\"{(defer ? " defer" : "")}></script>";
        if (inHead) Head(tag); else Body(tag);
    }

    public void Style(string assetFile)
    {
        var url = AssetUrl(assetFile);
        if (url.Length == 0) return;
        Head($"<link rel=\"stylesheet\" href=\"{System.Net.WebUtility.HtmlEncode(url)}\" />");
    }

    private string AssetUrl(string file)
    {
        var name = System.IO.Path.GetFileName((file ?? "").Trim());
        return string.IsNullOrEmpty(name) ? "" : $"/plugin-assets/{Info.Key}/{name}";
    }

    private sealed class SectionBuilder(List<PluginRegistry.SectionPage> pages, PluginRegistry.SectionEntry entry) : IAdminSection
    {
        public IAdminSection Badge(PluginBadgeProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (entry.Badge is not null) throw new InvalidOperationException("A section has one badge.");
            entry.Badge = provider;
            return this;
        }

        public IAdminSection Page(string pattern, string? tab, PluginHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            var p = PluginRoutePattern.TryParse(pattern, out var why)
                    ?? throw new InvalidOperationException($"Admin page '{pattern}': {why}.");
            if (p.Template == "/")
                throw new InvalidOperationException("An admin page needs a name (e.g. \"products\"); the section's start page is its first tab.");
            if (pages.Any(x => x.Pattern.Template.Equals(p.Template, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Admin page '{pattern}' is registered twice.");
            pages.Add(new PluginRegistry.SectionPage(p, string.IsNullOrWhiteSpace(tab) ? null : tab.Trim(), handler));
            return this;
        }
    }
}
