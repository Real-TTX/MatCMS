using Ganss.Xss;

namespace MatCMS.Services;

/// <summary>
/// Sanitises HTML that did NOT come from a trusted admin. The CMS renders admin-entered HTML raw on purpose
/// (the admin is trusted); this is for the one place that is NOT — rich content an AI supplies through the
/// cloud's MCP content ops (e.g. <c>Post.ContentHtml</c>), which is rendered raw on the public site and would
/// otherwise be stored XSS. Uses the battle-tested HtmlSanitizer with its default allow-list (drops
/// <c>&lt;script&gt;</c>, <c>on*</c> handlers, <c>javascript:</c> URIs, etc.) — we do NOT hand-roll HTML
/// sanitisation.
/// </summary>
public static class SafeHtml
{
    // The sanitizer is thread-safe for Sanitize once configured; build one and reuse it.
    private static readonly HtmlSanitizer _sanitizer = new();

    public static string Sanitize(string? html) =>
        string.IsNullOrEmpty(html) ? "" : _sanitizer.Sanitize(html);
}
