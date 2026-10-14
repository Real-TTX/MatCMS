using System.Text.Json.Nodes;

namespace MatCMS.Plugin;

/// <summary>The values of one request, already read. Plain data: no framework type behind it.</summary>
public sealed class RequestData
{
    /// <summary>HTTP method, upper case.</summary>
    public string Method { get; init; } = "GET";
    /// <summary>Request path as the visitor typed it (e.g. <c>/shop/tasse</c>), without the culture prefix.</summary>
    public string Path { get; init; } = "";
    /// <summary>The query string.</summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();
    /// <summary>The posted form fields (empty for a GET).</summary>
    public IReadOnlyDictionary<string, string> Form { get; init; } = new Dictionary<string, string>();
    /// <summary>The values captured by the route pattern (<c>{slug}</c> gives <c>Route["slug"]</c>).</summary>
    public IReadOnlyDictionary<string, string> Route { get; init; } = new Dictionary<string, string>();

    /// <summary>True for a POST.</summary>
    public bool IsPost => string.Equals(Method, "POST", StringComparison.OrdinalIgnoreCase);
    /// <summary>A query value, or "".</summary>
    public string Q(string key) => Query.TryGetValue(key, out var v) ? v : "";
    /// <summary>A form value, or "".</summary>
    public string F(string key) => Form.TryGetValue(key, out var v) ? v : "";
    /// <summary>A route value, or "".</summary>
    public string R(string key) => Route.TryGetValue(key, out var v) ? v : "";
    /// <summary>The posted <c>action</c> field, which tells several buttons of one page apart.</summary>
    public string Action => F("action");
}

/// <summary>Who is asking.</summary>
public sealed class PluginUser
{
    /// <summary>True when the visitor is signed in to the back office as an administrator.</summary>
    public bool IsAdmin { get; init; }
    /// <summary>The back-office user name, or "".</summary>
    public string Name { get; init; } = "";
}

/// <summary>Data of the block being rendered (block renders only).</summary>
public sealed class BlockData
{
    /// <summary>The block type as registered.</summary>
    public string Type { get; init; } = "";
    /// <summary>The values the editor stored for the block's fields.</summary>
    public JsonObject Values { get; init; } = new();

    /// <summary>A field value as text, or "".</summary>
    public string Get(string field)
    {
        if (!Values.TryGetPropertyValue(field, out var n) || n is null) return "";
        return n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();
    }
}

/// <summary>
/// Everything a handler may use, scoped to ONE request. Deliberately narrow: no raw service provider
/// and no HTTP types. What a plugin needs beyond this is added here as a typed member, so an internal
/// change of the CMS cannot break plugins; the one raw door is <see cref="Unsafe"/>, named so.
/// </summary>
public interface IPluginContext
{
    /// <summary>The plugin this request belongs to.</summary>
    PluginInfo Plugin { get; }
    /// <summary>The request.</summary>
    RequestData Request { get; }
    /// <summary>Who is asking.</summary>
    PluginUser User { get; }
    /// <summary>The language of the page being served (<c>de</c>, <c>en</c>, ...).</summary>
    string Locale { get; }
    /// <summary>The block being rendered; null outside a block render.</summary>
    BlockData? Block { get; }
    /// <summary>The hidden antiforgery input for a POST form of an admin page. Empty on public requests, which are not antiforgery-protected.</summary>
    string Antiforgery { get; }
    /// <summary>Public URL of a file in THIS plugin's asset folder.</summary>
    string AssetUrl(string file);
    /// <summary>Writes a line to this plugin's log (visible under Plugins in the admin).</summary>
    void Log(string message);
    /// <summary>The plugin's saved settings (<see cref="IPluginBuilder.Settings"/>).</summary>
    IPluginSettings Settings { get; }
    /// <summary>The plugin's tables (<see cref="IPluginBuilder.Data"/>).</summary>
    IPluginData Data { get; }
    /// <summary>Events across plugins and from the host.</summary>
    IPluginEvents Events { get; }
    /// <summary>The site: its name, address, languages, and the means to draw the CMS's own content.</summary>
    IPluginSite Site { get; }
    /// <summary>A text of the plugin in the page's language (<see cref="IPluginBuilder.Strings"/>); <c>{0}</c>… are replaced with <paramref name="args"/>.</summary>
    string T(string key, params object[] args);
    /// <summary>The raw door to the host's services. No stability promise: it exists so nothing is impossible, not so everything is easy.</summary>
    IPluginUnsafe Unsafe { get; }
}

/// <summary>Raw host access for NATIVE plugins that need something the typed API does not offer yet.</summary>
public interface IPluginUnsafe
{
    /// <summary>The host's request-scoped service provider.</summary>
    IServiceProvider Services { get; }
}
