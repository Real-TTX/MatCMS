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

    /// <summary>The request headers (names compare case-insensitively). Only what a plugin has a use for is listed here: the
    /// framework's own and the proxy's are there too, so a webhook can read <c>Stripe-Signature</c>.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>The cookies this plugin set with <see cref="PluginResult.WithCookie"/>, under the name it gave them (the host keeps a prefix on the wire so
    /// plugins cannot read each other's — nor the site's login cookie). Names compare case-sensitively, as browsers do.</summary>
    public IReadOnlyDictionary<string, string> Cookies { get; init; } = new Dictionary<string, string>();
    /// <summary>
    /// The raw request body, exactly as it arrived — the one thing a signature is computed over. Filled only for a route declared with
    /// <see cref="PublicRouteOptions.Webhook"/> (and then limited to <see cref="MaxWebhookBody"/> bytes); empty otherwise, because a form
    /// post has already been read as fields.
    /// </summary>
    public byte[] Body { get; init; } = Array.Empty<byte>();
    /// <summary>The address the request came from, as the host sees it (behind a proxy: the visitor's, when the proxy is trusted). "" outside a request.</summary>
    public string ClientIp { get; init; } = "";
    /// <summary>The files posted with a multipart form (an admin page's upload field). Empty for everything else.</summary>
    public IReadOnlyList<UploadedFile> Files { get; init; } = Array.Empty<UploadedFile>();

    /// <summary>The largest body a webhook route reads (1 MB — a payment notification is a few kilobytes).</summary>
    public const int MaxWebhookBody = 1024 * 1024;

    /// <summary>A header value, or "".</summary>
    public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";
    /// <summary>A cookie value, or "".</summary>
    public string Cookie(string name) => Cookies.TryGetValue(name, out var v) ? v : "";
    /// <summary>The body as UTF-8 text.</summary>
    public string BodyText => System.Text.Encoding.UTF8.GetString(Body);
    /// <summary>The uploaded file posted under a field name, or null.</summary>
    public UploadedFile? File(string field) => Files.FirstOrDefault(f => string.Equals(f.Field, field, StringComparison.Ordinal));
}

/// <summary>A file posted with a multipart form. The bytes are read when <see cref="OpenRead"/> is called, not before.</summary>
public sealed class UploadedFile
{
    private readonly Func<Stream> _open;

    /// <summary>Creates the description of an upload. The host builds these; a test can too.</summary>
    public UploadedFile(string field, string fileName, string contentType, long length, Func<Stream> open)
    {
        Field = field; FileName = fileName; ContentType = contentType; Length = length; _open = open;
    }

    /// <summary>The form field it was posted under.</summary>
    public string Field { get; }
    /// <summary>The name the browser sent (never trust it as a path: use <see cref="System.IO.Path.GetFileName(string)"/>).</summary>
    public string FileName { get; }
    /// <summary>The content type the browser claimed (also not to be trusted).</summary>
    public string ContentType { get; }
    /// <summary>The size in bytes.</summary>
    public long Length { get; }
    /// <summary>Opens the content for reading. The caller disposes the stream.</summary>
    public Stream OpenRead() => _open();
}

/// <summary>Who is asking: the back-office administrator, if any. A signed-in customer of the site is <see cref="IPluginContext.Member"/>.</summary>
public sealed class PluginUser
{
    /// <summary>True when the visitor is signed in to the back office as an administrator.</summary>
    public bool IsAdmin { get; init; }
    /// <summary>The back-office user name, or "".</summary>
    public string Name { get; init; } = "";
}

/// <summary>
/// A visitor who is signed in to the site's guest area (<c>/anmelden</c>) — the same account the members-only pages use. Entirely separate
/// from <see cref="PluginUser"/>: a member is never an administrator and an administrator is never a member.
/// </summary>
/// <param name="Id">The account's key.</param>
/// <param name="Username">The login name.</param>
/// <param name="DisplayName">The name to show; falls back to the user name.</param>
/// <param name="Email">The e-mail address, or "" for an account that was created by an administrator without one.</param>
/// <param name="Roles">The roles the account holds (a members-only page can require one).</param>
public sealed record PluginMember(long Id, string Username, string DisplayName, string Email, IReadOnlyList<string> Roles)
{
    /// <summary>True when the account holds the role.</summary>
    public bool InRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
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
    /// <summary>Who is asking, when it is the back-office administrator.</summary>
    PluginUser User { get; }
    /// <summary>The signed-in customer of the site (the guest-area account), or null for an anonymous visitor and outside a request.</summary>
    PluginMember? Member { get; }
    /// <summary>The site's accounts: look one up, create one, sign in and out.</summary>
    IPluginMembers Members { get; }
    /// <summary>Sends e-mail through the site's own mail settings, using templates the plugin declared (<see cref="IPluginBuilder.MailTemplate"/>).</summary>
    IPluginMail Mail { get; }
    /// <summary>The plugin's private files: stored outside the public folders, in the backup, never served by address.</summary>
    IPluginFiles Files { get; }
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

    /// <summary>
    /// Renders one of the plugin's own template parts (<see cref="IPluginBuilder.TemplatePart"/>) with <paramref name="values"/>: the ACTIVE template's version of it if it has one, else
    /// the plugin's default. A value is encoded unless its placeholder is declared <see cref="TemplatePlaceholder.Html"/>; a placeholder without a value is empty.
    /// </summary>
    string RenderPart(string name, IReadOnlyDictionary<string, string> values);
    /// <summary>
    /// The variables of the plugin page a block is being drawn on ("product.title" → the name), empty on any other page. A plugin block reads the data of THIS request here instead of asking the address again.
    /// </summary>
    IReadOnlyDictionary<string, string> PageScope { get; }
    /// <summary>True while an administrator looks at the page in the block editor's preview (<c>?editor=1</c>): a handler may then show sample data where the real request has none (a cart, a customer).</summary>
    bool IsEditorPreview { get; }
    /// <summary>
    /// Draws the page the editors made for <paramref name="role"/> (<see cref="IPluginBuilder.PageRole"/>) with <paramref name="scope"/> and answers its HTML, or null when there is no such page (never created, switched off) —
    /// then the plugin draws its own default. The page's blocks are drawn in the language of the request; the variables are the scope's, prefixed with the role.
    /// </summary>
    Task<string?> RenderPageAsync(string role, PageScope scope, CancellationToken ct = default);
    /// <summary>The raw door to the host's services. No stability promise: it exists so nothing is impossible, not so everything is easy.</summary>
    IPluginUnsafe Unsafe { get; }
}

/// <summary>Raw host access for NATIVE plugins that need something the typed API does not offer yet.</summary>
public interface IPluginUnsafe
{
    /// <summary>The host's request-scoped service provider.</summary>
    IServiceProvider Services { get; }
}
