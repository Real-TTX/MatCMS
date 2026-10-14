using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using MatCMS.Data;
using Microsoft.AspNetCore.Antiforgery;

namespace MatCMS.Services.Plugins;

/// <summary>
/// The host's <see cref="IPluginContext"/>: one per request. Built here from an HTTP request, or from the
/// request type the older block pipeline still passes around, so a handler sees the same thing wherever
/// it is called from.
/// </summary>
internal sealed class PluginRequestContext : IPluginContext
{
    private readonly PluginRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly PluginRegistry.Slice? _slice;
    private IPluginSettings? _settings;
    private IPluginData? _data;
    private IPluginEvents? _events;
    private IPluginSite? _site;

    /// <param name="slice">The plugin's slice when it is not published yet (while it is being activated); otherwise looked up.</param>
    public PluginRequestContext(PluginInfo plugin, RequestData request, PluginUser user, string locale,
        BlockData? block, string antiforgery, IServiceProvider services, PluginRegistry registry, PluginRegistry.Slice? slice = null)
    {
        Plugin = plugin; Request = request; User = user; Locale = locale; Block = block;
        Antiforgery = antiforgery; _services = services; _registry = registry; _slice = slice;
    }

    private PluginRegistry.Slice? Slice => _slice ?? _registry.SliceOf(Plugin.Key);

    public IPluginSettings Settings => _settings ??= new PluginSettingsAccess(Plugin.Key, Slice, _services);

    public IPluginData Data => _data ??= CreateData();

    private IPluginData CreateData()
    {
        var db = _services.GetService(typeof(AppDbContext)) as AppDbContext
                 ?? throw new InvalidOperationException("Die Datenbank ist in diesem Zusammenhang nicht verfügbar.");
        return new PluginDataAccess(db, Plugin.Key, Slice?.Tables ?? Array.Empty<TableSpec>());
    }

    public IPluginEvents Events => _events ??= new PluginEventBus(_registry, _services, User, Locale);
    public IPluginSite Site => _site ??= new PluginSiteFacade(_services);
    public string T(string key, params object[] args) => PluginStrings.Translate(Slice, Locale, key, args);

    public PluginInfo Plugin { get; }
    public RequestData Request { get; }
    public PluginUser User { get; }
    public string Locale { get; }
    public BlockData? Block { get; }
    public string Antiforgery { get; }
    public IPluginUnsafe Unsafe => new UnsafeAccess(_services);

    public string AssetUrl(string file)
    {
        var name = System.IO.Path.GetFileName((file ?? "").Trim());
        return string.IsNullOrEmpty(Plugin.Key) || string.IsNullOrEmpty(name) ? "" : $"/plugin-assets/{Plugin.Key}/{name}";
    }

    public void Log(string message) => _registry.AddLog(Plugin.Key, message ?? "");

    private sealed class UnsafeAccess(IServiceProvider services) : IPluginUnsafe
    {
        public IServiceProvider Services => services;
    }

    /// <summary>The current page language, from the request culture the localisation middleware already set.</summary>
    public static string CurrentLocale()
    {
        var l = CultureInfo.CurrentUICulture.Name.Split('-')[0];
        return string.IsNullOrEmpty(l) ? Localizer.DefaultCulture : l;
    }

    /// <summary>Builds the context of an HTTP request (public route or admin page).</summary>
    public static async Task<PluginRequestContext> FromHttpAsync(HttpContext http, PluginInfo info, PluginRegistry registry,
        string pathWithoutCulture, IReadOnlyDictionary<string, string> routeValues, bool withAntiforgery)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        if (http.Request.HasFormContentType)
            foreach (var kv in await http.Request.ReadFormAsync(http.RequestAborted))
                form[kv.Key] = kv.Value.ToString();
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in http.Request.Query) query[kv.Key] = kv.Value.ToString();

        var csrf = "";
        if (withAntiforgery)
        {
            var t = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
            csrf = $"<input type=\"hidden\" name=\"{t.FormFieldName}\" value=\"{System.Net.WebUtility.HtmlEncode(t.RequestToken)}\" />";
        }

        var request = new RequestData
        {
            Method = http.Request.Method.ToUpperInvariant(), Path = pathWithoutCulture,
            Query = query, Form = form, Route = new Dictionary<string, string>(routeValues, StringComparer.OrdinalIgnoreCase)
        };
        var user = new PluginUser
        {
            IsAdmin = http.User?.IsInRole("Admin") == true,
            Name = http.User?.Identity?.Name ?? ""
        };
        return new PluginRequestContext(info, request, user, CurrentLocale(), null, csrf, http.RequestServices, registry);
    }

    /// <summary>A context for work that belongs to no HTTP request of the plugin's own: a sidebar counter, a job, an install handler.
    /// No request data, nobody signed in.</summary>
    public static PluginRequestContext ForBackground(PluginInfo info, IServiceProvider services, PluginRegistry registry, PluginRegistry.Slice? slice = null) =>
        new(info, new RequestData(), new PluginUser(), CurrentLocale(), null, "", services, registry, slice);

    /// <summary>A context for an event listener: the listener's own plugin, the caller's user and language.</summary>
    public static PluginRequestContext ForListener(PluginInfo info, IServiceProvider services, PluginRegistry registry, PluginUser user, string locale) =>
        new(info, new RequestData(), user, locale, null, "", services, registry);

    /// <summary>Builds the context of a block render from the request type the block pipeline passes.</summary>
    public static PluginRequestContext FromBlock(PluginInfo info, string blockType, PluginRequest legacy, PluginRegistry registry)
    {
        JsonObject values;
        try { values = JsonNode.Parse(string.IsNullOrWhiteSpace(legacy.Data) ? "{}" : legacy.Data) as JsonObject ?? new JsonObject(); }
        catch { values = new JsonObject(); }
        var request = new RequestData
        {
            Method = legacy.Method, Path = legacy.Path,
            Query = legacy.Query, Form = legacy.Form
        };
        var user = new PluginUser { IsAdmin = legacy.IsAdmin };
        return new PluginRequestContext(info, request, user, CurrentLocale(),
            new BlockData { Type = blockType, Values = values }, legacy.Antiforgery, legacy.Services, registry);
    }
}
