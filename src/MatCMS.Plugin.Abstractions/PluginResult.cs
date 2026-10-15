using System.Text.Json.Nodes;

namespace MatCMS.Plugin;

/// <summary>A cookie a handler sets (or, with <paramref name="Delete"/>, removes).</summary>
/// <param name="Name">The name inside the plugin.</param>
/// <param name="Value">The value.</param>
/// <param name="MaxAge">How long it lives; null = until the browser closes.</param>
/// <param name="Delete">Remove the cookie instead of setting it.</param>
public sealed record PluginCookie(string Name, string Value, TimeSpan? MaxAge, bool Delete);

/// <summary>What a handler answers with. One type for public pages, admin pages and block renders; the
/// host decides where it is drawn (site layout, admin layout, inside a block).</summary>
public sealed class PluginResult
{
    /// <summary>How the host has to treat the result.</summary>
    public enum ResultKind
    {
        /// <summary>HTML drawn inside the surrounding layout (site or admin).</summary>
        Page,
        /// <summary>HTML without a layout — a block's output, or an Ajax answer.</summary>
        Fragment,
        /// <summary>A redirect to a local path.</summary>
        Redirect,
        /// <summary>JSON.</summary>
        Json,
        /// <summary>Plain text.</summary>
        Text,
        /// <summary>A file download or inline file.</summary>
        File,
        /// <summary>Only a status code.</summary>
        Status
    }

    private PluginResult(ResultKind kind) => Kind = kind;

    /// <summary>The kind of result.</summary>
    public ResultKind Kind { get; }
    /// <summary>The component tree of a <see cref="ResultKind.Page"/> or <see cref="ResultKind.Fragment"/> built from components
    /// (null when the handler answered with raw HTML). The host draws it with the library's own renderer.</summary>
    public UiNode? Tree { get; private init; }
    /// <summary>The HTML (<see cref="ResultKind.Page"/>, <see cref="ResultKind.Fragment"/>) or the text. Empty when <see cref="Tree"/> is set.</summary>
    public string Content { get; private init; } = "";
    /// <summary>Page title: the title of a public page, the topbar title of an admin page. Empty = the plugin's own.</summary>
    public string Title { get; private init; } = "";
    /// <summary>Meta description of a public page.</summary>
    public string Description { get; private init; } = "";
    /// <summary>Extra meta tags (<c>og:image</c> etc.) of a public page.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Meta { get; private init; } = Array.Empty<KeyValuePair<string, string>>();
    /// <summary>A one-line message to show after a redirect (back office only): "Saved." Null for none.</summary>
    public string? Notice { get; private init; }
    /// <summary>The <see cref="Notice"/> is an error.</summary>
    public bool NoticeIsError { get; private init; }
    /// <summary>Redirect target, or the file name of a download.</summary>
    public string Target { get; private init; } = "";
    /// <summary>The JSON body.</summary>
    public JsonNode? Body { get; private init; }
    /// <summary>The bytes of a file.</summary>
    public byte[] Bytes { get; private init; } = Array.Empty<byte>();
    /// <summary>The content type of a file, JSON or text answer.</summary>
    public string ContentType { get; private init; } = "";
    /// <summary>HTTP status code (default 200; 302 for a redirect).</summary>
    public int StatusCode { get; private set; } = 200;
    /// <summary>The content of a <see cref="ResultKind.File"/> answered with <see cref="FromStream"/>; null for bytes. The host disposes it after sending.</summary>
    public Stream? Stream { get; private init; }
    /// <summary>Cookies to set or delete on the answer (<see cref="WithCookie"/>, <see cref="WithoutCookie"/>).</summary>
    public IReadOnlyList<PluginCookie> Cookies { get; private set; } = Array.Empty<PluginCookie>();
    /// <summary>Extra response headers (<see cref="WithHeader"/>).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; private set; } = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>The same answer with another HTTP status — a page inside the layout that is a 404 ("no such product"), or a form that came back with a 422.</summary>
    public PluginResult WithStatus(int statusCode)
    {
        var copy = (PluginResult)MemberwiseClone();
        copy.StatusCode = statusCode;
        return copy;
    }

    /// <summary>The same answer with a cookie added. The host sets it <c>HttpOnly</c>, <c>SameSite=Lax</c> and <c>Secure</c> on https, under
    /// the name <c>plugin-key.</c> + <paramref name="name"/> so two plugins cannot read or overwrite each other's.</summary>
    /// <param name="name">The cookie's name inside the plugin.</param>
    /// <param name="value">The value (keep it opaque: a token, never a record).</param>
    /// <param name="maxAge">How long the browser keeps it; null makes it a session cookie.</param>
    public PluginResult WithCookie(string name, string value, TimeSpan? maxAge = null)
    {
        var copy = (PluginResult)MemberwiseClone();
        copy.Cookies = Cookies.Append(new PluginCookie(name, value, maxAge, Delete: false)).ToList();
        return copy;
    }

    /// <summary>The same answer telling the browser to forget a cookie of the plugin.</summary>
    public PluginResult WithoutCookie(string name)
    {
        var copy = (PluginResult)MemberwiseClone();
        copy.Cookies = Cookies.Append(new PluginCookie(name, "", null, Delete: true)).ToList();
        return copy;
    }

    /// <summary>The same answer with a response header (not <c>Set-Cookie</c> — use <see cref="WithCookie"/> — and not a header that
    /// changes how the body is framed; the host drops those).</summary>
    public PluginResult WithHeader(string name, string value)
    {
        var copy = (PluginResult)MemberwiseClone();
        copy.Headers = Headers.Append(new KeyValuePair<string, string>(name, value)).ToList();
        return copy;
    }

    /// <summary>A large file sent from a stream, with range support (resuming a download). The host disposes the stream. A non-empty
    /// <paramref name="fileName"/> makes it a download.</summary>
    public static PluginResult FromStream(Stream stream, string contentType, string? fileName = null) =>
        new(ResultKind.File) { Stream = stream ?? throw new ArgumentNullException(nameof(stream)), ContentType = contentType, Target = fileName ?? "" };

    /// <summary>HTML inside the surrounding layout.</summary>
    public static PluginResult Page(string html, string? title = null, string? description = null,
        IEnumerable<KeyValuePair<string, string>>? meta = null) =>
        new(ResultKind.Page)
        {
            Content = html ?? "", Title = title ?? "", Description = description ?? "",
            Meta = meta?.ToList() ?? new List<KeyValuePair<string, string>>()
        };

    /// <summary>A screen built from components, inside the surrounding layout. The preferred way: it conforms to the
    /// admin (or the site) by construction.</summary>
    public static PluginResult Page(UiNode tree, string? title = null, string? description = null,
        IEnumerable<KeyValuePair<string, string>>? meta = null) =>
        new(ResultKind.Page)
        {
            Tree = tree ?? throw new ArgumentNullException(nameof(tree)), Title = title ?? "", Description = description ?? "",
            Meta = meta?.ToList() ?? new List<KeyValuePair<string, string>>()
        };

    /// <summary>Components without a layout — a block's output, or an Ajax answer.</summary>
    public static PluginResult Fragment(UiNode tree) =>
        new(ResultKind.Fragment) { Tree = tree ?? throw new ArgumentNullException(nameof(tree)) };

    /// <summary>HTML without a layout.</summary>
    public static PluginResult Fragment(string html) => new(ResultKind.Fragment) { Content = html ?? "" };

    /// <summary>Redirect to a LOCAL path. A path starting with two slashes or an absolute URL is refused by the host.</summary>
    public static PluginResult Redirect(string localPath) =>
        new(ResultKind.Redirect) { Target = localPath ?? "/", StatusCode = 302 };

    /// <summary>
    /// Redirect to ANOTHER site — the page of a payment provider. Only an absolute https address is accepted (the host turns anything else into a redirect to the
    /// home page), because this is a door out of the site and a handler that builds the target from a request value must not be able to make an open redirect of it.
    /// </summary>
    public static PluginResult RedirectExternal(string httpsUrl) =>
        new(ResultKind.Redirect) { Target = httpsUrl ?? "/", StatusCode = 302, External = true };

    /// <summary>The redirect leaves the site (<see cref="RedirectExternal"/>).</summary>
    public bool External { get; private init; }

    /// <summary>Redirect to a local path and show <paramref name="notice"/> on the page it lands on (the back office's green or red
    /// banner). On a public route the notice is ignored.</summary>
    public static PluginResult Redirect(string localPath, string notice, bool isError = false) =>
        new(ResultKind.Redirect) { Target = localPath ?? "/", StatusCode = 302, Notice = notice, NoticeIsError = isError };

    /// <summary>A JSON answer.</summary>
    public static PluginResult Json(JsonNode? body, int status = 200) =>
        new(ResultKind.Json) { Body = body, StatusCode = status, ContentType = "application/json; charset=utf-8" };

    /// <summary>A plain-text answer.</summary>
    public static PluginResult Text(string text, string contentType = "text/plain; charset=utf-8", int status = 200) =>
        new(ResultKind.Text) { Content = text ?? "", ContentType = contentType, StatusCode = status };

    /// <summary>A file. A non-empty <paramref name="fileName"/> makes it a download.</summary>
    public static PluginResult File(byte[] bytes, string contentType, string? fileName = null) =>
        new(ResultKind.File) { Bytes = bytes ?? Array.Empty<byte>(), ContentType = contentType, Target = fileName ?? "" };

    /// <summary>Only a status code (403, 404, ...).</summary>
    public static PluginResult Status(int statusCode) => new(ResultKind.Status) { StatusCode = statusCode };

    /// <summary>404 — the route matched but the thing behind it does not exist.</summary>
    public static PluginResult NotFound() => Status(404);
}
