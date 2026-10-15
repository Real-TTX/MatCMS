using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

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
    private IPluginMembers? _members;
    private IPluginMail? _mail;
    private IPluginFiles? _files;

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
    /// <summary>The signed-in customer, set by <see cref="FromHttpAsync"/>; null in every other kind of context.</summary>
    public PluginMember? Member { get; private set; }
    public IPluginMembers Members => _members ??= new PluginMembersAccess(_services);
    public IPluginMail Mail => _mail ??= new PluginMailAccess(_services, Plugin.Key, Slice);
    public IPluginFiles Files => _files ??= new PluginFilesAccess(
        _services.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment ?? throw new InvalidOperationException("Der Speicher ist in diesem Zusammenhang nicht verfügbar."), Plugin.Key);
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
        string pathWithoutCulture, IReadOnlyDictionary<string, string> routeValues, bool withAntiforgery, bool webhook = false)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<UploadedFile>();
        var body = Array.Empty<byte>();
        if (webhook)
        {
            // A notification from another server: the body is read raw (the signature is computed over exactly these bytes) and never as a form.
            body = await ReadBodyAsync(http, RequestData.MaxWebhookBody);
        }
        else if (http.Request.HasFormContentType)
        {
            var posted = await http.Request.ReadFormAsync(http.RequestAborted);
            foreach (var kv in posted) form[kv.Key] = kv.Value.ToString();
            foreach (var file in posted.Files)
                files.Add(new UploadedFile(file.Name, file.FileName, file.ContentType, file.Length, file.OpenReadStream));
        }
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in http.Request.Query) query[kv.Key] = kv.Value.ToString();

        var csrf = "";
        if (withAntiforgery)
        {
            var t = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
            csrf = $"<input type=\"hidden\" name=\"{t.FormFieldName}\" value=\"{System.Net.WebUtility.HtmlEncode(t.RequestToken)}\" />";
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in http.Request.Headers) headers[kv.Key] = kv.Value.ToString();
        // Only this plugin's own cookies, under the name it set them with: another plugin's — or the site's login — are none of its business.
        var prefix = CookiePrefix(info.Key);
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in http.Request.Cookies)
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) cookies[kv.Key[prefix.Length..]] = kv.Value;

        var request = new RequestData
        {
            Method = http.Request.Method.ToUpperInvariant(), Path = pathWithoutCulture,
            Query = query, Form = form, Route = new Dictionary<string, string>(routeValues, StringComparer.OrdinalIgnoreCase),
            Headers = headers, Cookies = cookies, Body = body, Files = files,
            ClientIp = http.Connection.RemoteIpAddress?.ToString() ?? ""
        };
        var user = new PluginUser
        {
            IsAdmin = http.User?.IsInRole("Admin") == true,
            Name = http.User?.Identity?.Name ?? ""
        };
        var ctx = new PluginRequestContext(info, request, user, CurrentLocale(), null, csrf, http.RequestServices, registry);
        ctx.Member = await CurrentMemberAsync(http);
        return ctx;
    }

    /// <summary>The name a plugin's cookie has in the browser: <c>{key}.</c> in front, so two plugins cannot read or overwrite each other's.</summary>
    internal static string CookiePrefix(string pluginKey) => pluginKey + ".";

    /// <summary>
    /// The customer behind the request, read from the database and not from the cookie: an account that was deleted or switched off stops being
    /// "the member" at once, instead of acting until its cookie expires. Null when nobody is signed in.
    /// </summary>
    internal static async Task<PluginMember?> CurrentMemberAsync(HttpContext http)
    {
        var id = await MemberService.CurrentIdAsync(http);
        if (id is null || http.RequestServices.GetService(typeof(AppDbContext)) is not AppDbContext db) return null;
        var m = await db.SiteMembers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value, http.RequestAborted);
        return m is null || !m.IsActive ? null
            : new PluginMember(m.Id, m.Username, string.IsNullOrWhiteSpace(m.DisplayName) ? m.Username : m.DisplayName, m.Email, m.Roles.ToList());
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext http, int max)
    {
        if (http.Request.ContentLength > max) throw new RequestBodyTooLargeException();
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        var stream = http.Request.Body;
        while ((n = await stream.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > max) throw new RequestBodyTooLargeException();
        }
        return ms.ToArray();
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

/// <summary>A webhook body over the limit (<see cref="RequestData.MaxWebhookBody"/>): answered with 413, the handler never runs.</summary>
internal sealed class RequestBodyTooLargeException : Exception;
