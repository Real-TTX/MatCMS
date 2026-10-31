using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Plugin;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace MatCMS.Services.Plugins;

/// <summary>A plugin's texts: the lookup <c>ctx.T</c> and the host's label translation share.</summary>
internal static class PluginStrings
{
    /// <summary>The text for <paramref name="key"/> in the page's language, else the site's default language, else English,
    /// else the key itself (which shows on screen as the missing translation it is).</summary>
    public static string Translate(PluginRegistry.Slice? slice, string locale, string key, params object[] args)
    {
        var text = Lookup(slice, locale, key) ?? key;
        if (args is { Length: > 0 })
        {
            try { text = string.Format(CultureInfo.CurrentUICulture, text, args); }
            catch (FormatException) { /* a table text with a stray brace: show it unformatted rather than fail the page */ }
        }
        return text;
    }

    /// <summary>A label a plugin wrote: translated when it is a key of the plugin's own tables, otherwise as written.</summary>
    public static string Label(PluginRegistry.Slice? slice, string locale, string label) => Lookup(slice, locale, label) ?? label;

    private static string? Lookup(PluginRegistry.Slice? slice, string locale, string key)
    {
        if (slice is null || string.IsNullOrEmpty(key)) return null;
        foreach (var c in new[] { Short(locale), Localizer.DefaultCulture, "en" })
            if (slice.Strings.TryGetValue(c, out var table) && table.TryGetValue(key, out var v)) return v;
        return null;
    }

    private static string Short(string? culture) => (culture ?? "").Split('-')[0].ToLowerInvariant();
}

/// <summary>A plugin's saved settings (<see cref="IPluginSettings"/>), read from the plugin's row once per request.</summary>
internal sealed class PluginSettingsAccess : IPluginSettings
{
    /// <summary>The marker in front of an encrypted value in <c>ConfigJson</c>.</summary>
    internal const string SecretPrefix = "enc1:";
    internal const string SecretPurpose = "MatCMS.Plugin.Secret.v1";

    private readonly string _key;
    private readonly PluginRegistry.Slice? _slice;
    private readonly IServiceProvider _services;
    private Dictionary<string, string>? _values;

    public PluginSettingsAccess(string key, PluginRegistry.Slice? slice, IServiceProvider services)
    {
        _key = key; _slice = slice; _services = services;
    }

    private Dictionary<string, string> Values => _values ??= Load();

    private Dictionary<string, string> Load()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_services.GetService(typeof(AppDbContext)) is not AppDbContext db) return d;
        var json = db.Plugins.AsNoTracking().Where(p => p.Key == _key).Select(p => p.ConfigJson).FirstOrDefault();
        foreach (var kv in ParseConfig(json)) d[kv.Key] = kv.Value;
        return d;
    }

    internal static Dictionary<string, string> ParseConfig(string? json)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return d;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
        }
        catch (JsonException) { /* a damaged config reads as empty: the defaults apply */ }
        return d;
    }

    public string Get(string key)
    {
        var spec = _slice?.Settings.FirstOrDefault(s => s.Key == key);
        if (!Values.TryGetValue(key, out var stored)) return spec?.Default ?? "";
        if (spec?.Kind == SettingKind.Secret || stored.StartsWith(SecretPrefix, StringComparison.Ordinal)) return Decrypt(stored);
        return stored;
    }

    private string Decrypt(string stored)
    {
        if (!stored.StartsWith(SecretPrefix, StringComparison.Ordinal)) return stored;
        try
        {
            var provider = _services.GetService(typeof(IDataProtectionProvider)) as IDataProtectionProvider;
            return provider?.CreateProtector(SecretPurpose).Unprotect(stored[SecretPrefix.Length..]) ?? "";
        }
        catch (System.Security.Cryptography.CryptographicException) { return ""; }   // keys lost (a restore on another instance): the secret must be entered again
    }

    public bool GetBool(string key) => Get(key).Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes";
    public long GetInt(string key, long fallback = 0) => long.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public double GetNumber(string key, double fallback = 0) => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

/// <summary>Events across plugins (<see cref="IPluginEvents"/>).</summary>
internal sealed class PluginEventBus : IPluginEvents
{
    // A plugin that emits from its own handler could end in a loop with another. Eight levels is far more than any
    // honest chain needs; beyond it the event is dropped and logged rather than the process run out of stack.
    private static readonly AsyncLocal<int> Depth = new();
    private const int MaxDepth = 8;

    private readonly PluginRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly PluginUser _user;
    private readonly string _locale;

    public PluginEventBus(PluginRegistry registry, IServiceProvider services, PluginUser user, string locale)
    {
        _registry = registry; _services = services; _user = user; _locale = locale;
    }

    public async Task EmitAsync(string name, JsonNode? payload = null, CancellationToken ct = default) =>
        await RunAsync(name, payload, ct);

    public Task<IReadOnlyList<JsonNode>> CollectAsync(string name, JsonNode? payload = null, CancellationToken ct = default) =>
        RunAsync(name, payload, ct);

    private async Task<IReadOnlyList<JsonNode>> RunAsync(string name, JsonNode? payload, CancellationToken ct)
    {
        var answers = new List<JsonNode>();
        var listeners = _registry.ListenersOf(name);
        if (listeners.Count == 0) return answers;
        if (Depth.Value >= MaxDepth)
        {
            _registry.AddLog(null, $"Ereignis '{name}' wurde nicht zugestellt: mehr als {MaxDepth} verschachtelte Ereignisse (Schleife?).");
            return answers;
        }
        Depth.Value++;
        try
        {
            foreach (var l in listeners)
            {
                try
                {
                    var ctx = PluginRequestContext.ForListener(l.Info, _services, _registry, _user, _locale);
                    // Each listener gets its own copy: one that edits the payload must not change what the next one sees.
                    var answer = await l.Handler(ctx, payload?.DeepClone(), ct);
                    if (answer is not null) answers.Add(answer);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _registry.AddLog(l.Owner, $"Ereignis '{name}': {ex.Message}"); }
            }
        }
        finally { Depth.Value--; }
        return answers;
    }
}

/// <summary>The site as a plugin sees it (<see cref="IPluginSite"/>).</summary>
internal sealed class PluginSiteFacade : IPluginSite
{
    private readonly IServiceProvider _services;
    private readonly string _pluginKey;
    public PluginSiteFacade(IServiceProvider services, string pluginKey = "")
    {
        _services = services;
        _pluginKey = pluginKey;
    }

    private SiteContext Site => _services.GetService(typeof(SiteContext)) as SiteContext
                                ?? throw new InvalidOperationException("Die Website ist in diesem Zusammenhang nicht verfügbar.");

    public string Name => Site.SiteName;

    public string Origin
    {
        get
        {
            var http = (_services.GetService(typeof(IHttpContextAccessor)) as IHttpContextAccessor)?.HttpContext;
            var configured = Site.Get(SettingKeys.CanonicalUrl).TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            return http is null ? "" : Site.CanonicalBaseUrl(http.Request).TrimEnd('/');
        }
    }

    public IReadOnlyList<string> Locales => Site.ActiveLocales;

    public async Task<string> RenderBlockAsync(string blockType, JsonNode values, CancellationToken ct = default)
    {
        var registry = _services.GetService(typeof(BlockRegistry)) as BlockRegistry ?? throw new InvalidOperationException("Blöcke sind in diesem Zusammenhang nicht verfügbar.");
        var def = registry.Get(blockType);
        if (def is null || string.IsNullOrWhiteSpace(def.Partial)) throw new ArgumentException($"Es gibt keinen Block '{blockType}'.", nameof(blockType));
        if (def.IsContainer) throw new ArgumentException($"'{blockType}' ist ein Container-Block und lässt sich so nicht zeichnen.", nameof(blockType));
        var renderer = _services.GetService(typeof(RazorPartialRenderer)) as RazorPartialRenderer ?? throw new InvalidOperationException("Der Renderer ist nicht verfügbar.");
        return await renderer.RenderAsync(def.Partial, new MatCMS.Content.BlockData(values?.ToJsonString() ?? "{}"), ct);
    }

    public async Task<PluginImage?> LoadImageAsync(string url, int maxWidth = 600, CancellationToken ct = default)
    {
        var u = (url ?? "").Trim();
        if (!u.StartsWith("/uploads/", StringComparison.Ordinal) || u.Contains("..") || u.Contains('\\') || u.Contains('?')) return null;
        var name = Path.GetFileName(u);
        if (Path.GetExtension(name).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif")) return null;      // SVG is never read
        if (_services.GetService(typeof(IWebHostEnvironment)) is not IWebHostEnvironment env) return null;
        var path = Path.Combine(StoragePaths.Uploads(env), name);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8 * 1024 * 1024) return null;
            using var image = await Image.LoadAsync(path, ct);
            var width = Math.Clamp(maxWidth, 16, 2000);
            if (image.Width > width) image.Mutate(c => c.Resize(width, 0));
            image.Mutate(c => c.BackgroundColor(Color.White));              // a JPEG has no transparency
            using var ms = new MemoryStream();
            await image.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 90 }, ct);
            return new PluginImage(ms.ToArray(), image.Width, image.Height);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public string TemplatePart(string part) => TemplateSchema.EffectivePart(Site.ActiveTemplate, part);

    public bool AiEnabled => (_services.GetService(typeof(AiService)) as AiService)?.Enabled == true;

    public async Task<PluginAiAnswer> AskAiAsync(PluginAiCall call, CancellationToken ct = default)
    {
        if (_services.GetService(typeof(AiService)) is not AiService ai || !ai.Enabled) return new PluginAiAnswer(false, "", "KI ist für diese Website nicht eingerichtet.");
        var messages = (call?.Messages ?? Array.Empty<PluginAiMessage>())
            .Where(m => !string.IsNullOrEmpty(m.Content))
            .Select(m => new MatCMS.Shared.AiMessage { Role = m.Role is "system" or "assistant" ? m.Role : "user", Content = m.Content })
            .ToList();
        if (messages.Count == 0) return new PluginAiAnswer(false, "", "Keine Nachricht.");
        // The site's layers (operator, "Über diese Website", the operator's guidance for this plugin) go first — the plugin
        // cannot leave them out; its own system text follows and is the more specific one.
        var preamble = ai.Preamble(AiService.PluginPurpose(_pluginKey));
        if (preamble.Length > 0) messages.Insert(0, new MatCMS.Shared.AiMessage { Role = "system", Content = preamble });
        try
        {
            var r = await ai.SendAsync(new MatCMS.Shared.AiRequest
            {
                Purpose = AiService.PluginPurpose(_pluginKey), Messages = messages, MaxTokens = Math.Clamp(call!.MaxTokens, 64, 16000), Json = call.Json
            }, ct);
            return new PluginAiAnswer(r.Ok, r.Text ?? "", r.Ok ? null : r.Error ?? "Keine Antwort.", r.PromptTokens, r.CompletionTokens, r.Model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PluginAiAnswer(false, "", ex.GetBaseException().Message);
        }
    }

    public Task<IReadOnlyList<SiteSearchHit>> SearchPagesAsync(string query, int max = 5, PluginMember? visitor = null, string? locale = null, CancellationToken ct = default)
    {
        var db = _services.GetService(typeof(AppDbContext)) as AppDbContext ?? throw new InvalidOperationException("Die Datenbank ist in diesem Zusammenhang nicht verfügbar.");
        var cache = _services.GetService(typeof(Microsoft.Extensions.Caching.Memory.IMemoryCache)) as Microsoft.Extensions.Caching.Memory.IMemoryCache
                    ?? throw new InvalidOperationException("Der Zwischenspeicher ist nicht verfügbar.");
        var loc = string.IsNullOrWhiteSpace(locale) ? Site.ActiveLocales.FirstOrDefault() ?? "de" : locale.Trim().ToLowerInvariant();
        return SiteSearch.SearchAsync(db, cache, query ?? "", max, visitor, loc, ct);
    }

    public string RenderTemplatePart(string part, IReadOnlyDictionary<string, string> tokens)
    {
        var html = TemplateSchema.EffectivePart(Site.ActiveTemplate, part);
        return part == TemplateSchema.PartPost ? TemplateSchema.RenderPost(html, tokens) : TemplateSchema.RenderTokens(html, tokens);
    }
}
