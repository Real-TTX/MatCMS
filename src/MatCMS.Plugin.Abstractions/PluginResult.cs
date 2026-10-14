using System.Text.Json.Nodes;

namespace MatCMS.Plugin;

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
    public int StatusCode { get; private init; } = 200;

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
