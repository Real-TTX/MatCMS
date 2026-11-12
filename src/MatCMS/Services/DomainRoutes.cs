using System.Text;
using System.Text.RegularExpressions;

namespace MatCMS.Services;

/// <summary>One mapping of Settings → Domains: <paramref name="Host"/> shows the part of the site under <paramref name="Prefix"/>
/// (<c>docs.matmon.eu → /docs</c>); with <paramref name="Redirect"/> the site's own addresses under the prefix lead there.</summary>
public sealed record DomainRoute(string Host, string Prefix, bool Redirect);

/// <summary>
/// Domain routing: one site, several domains, each further domain showing ONE part of the site — a plugin's area (<c>/docs</c>), a page or a
/// path. The request on such a domain is rewritten before anything else sees it (<c>docs.matmon.eu/x</c> is served as <c>/docs/x</c>, with the
/// language prefix in front where there is one: <c>/en/x</c> → <c>/en/docs/x</c>), the page that comes back has its links written for that domain
/// (<c>/docs/x</c> → <c>/x</c>; a link to the rest of the site → the site's main address), and a visitor who opens the part at the main address is
/// sent to its domain (302 — a cached 301 would outlive taking the mapping back) when the mapping asks for it. Pure functions here; the middleware in <c>Program.cs</c> applies them.
/// <para>The host's own areas (admin, sign-in, uploads, static files, sitemap, robots) are never rewritten: they work on every domain as they are.</para>
/// <para>A domain must also reach the instance (DNS, the edge's route and certificate) — that is the cloud's or the operator's part, not this one.</para>
/// </summary>
public static partial class DomainRoutes
{
    /// <summary>The item a rewritten request carries its mapping in.</summary>
    public const string ItemKey = "matcms.domainRoute";

    /// <summary>First path segments the host owns: never rewritten, links to them never moved to another address.</summary>
    public static readonly string[] SystemSegments =
    [
        "admin", "api", "login", "logout", "anmelden", "registrieren", "uploads", "thumb", "thumbs", "plugin", "plugin-assets", "template-assets",
        "_content", "_framework", "_status", "_preview.webp", "error", "set-language", "sso", "oauth", "fonts", "pwa", "css", "js", "lib", "img",
        "favicon.ico", "sitemap.xml", "robots.txt", ".well-known"
    ];

    /// <summary>
    /// Reads the setting: one mapping per line, <c>host | /path</c> with an optional third cell (<c>umleiten</c>/<c>redirect</c>/<c>ja</c>) — the form a
    /// list setting stores; <c>host = /path</c> and <c>host → /path</c> are read too. A host is lower-cased without scheme, port or path; a line whose
    /// host or path is not usable is skipped, and a host is taken once (the first line wins).
    /// </summary>
    public static IReadOnlyList<DomainRoute> Parse(string? text)
    {
        var list = new List<DomainRoute>();
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var cells = SepRx().Split(line).Select(c => c.Trim()).ToArray();
            if (cells.Length < 2) continue;
            var host = CleanHost(cells[0]);
            var prefix = CleanPrefix(cells[1]);
            if (host is null || prefix is null || list.Any(r => r.Host == host)) continue;
            var redirect = cells.Length > 2 && cells[2].ToLowerInvariant() is "umleiten" or "redirect" or "ja" or "yes" or "1" or "true" or "on";
            list.Add(new DomainRoute(host, prefix, redirect));
        }
        return list;
    }

    /// <summary>The host as it is compared: lower case, no scheme, no port, no path; null when it is not a host name.</summary>
    public static string? CleanHost(string? s)
    {
        var h = (s ?? "").Trim().ToLowerInvariant();
        var scheme = h.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) h = h[(scheme + 3)..];
        var cut = h.IndexOfAny(['/', '?', '#']);
        if (cut >= 0) h = h[..cut];
        var port = h.LastIndexOf(':');
        if (port > 0) h = h[..port];
        h = h.TrimEnd('.');
        return h.Length is > 0 and <= 253 && h.Contains('.') && h.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') && !h.StartsWith('.') ? h : null;
    }

    /// <summary>The path a domain shows: starts with "/", no trailing "/", no query, not the root and not one of the host's own areas; null otherwise.</summary>
    public static string? CleanPrefix(string? s)
    {
        var p = (s ?? "").Trim();
        var cut = p.IndexOfAny(['?', '#']);
        if (cut >= 0) p = p[..cut];
        p = "/" + p.Trim('/');
        if (p == "/" || p.Contains("//") || p.Contains("..") || p.Any(c => char.IsWhiteSpace(c) || c is '"' or '<' or '>' or '\\')) return null;
        var first = p[1..].Split('/')[0].ToLowerInvariant();
        if (SystemSegments.Contains(first) || Localizer.SupportedCultures.Contains(first)) return null;
        return p.ToLowerInvariant();
    }

    public static DomainRoute? ForHost(IReadOnlyList<DomainRoute> routes, string? host)
    {
        var h = CleanHost(host);
        return h is null ? null : routes.FirstOrDefault(r => r.Host == h);
    }

    /// <summary>True for a path of the host's own areas (and anything with a file extension at the top level, like <c>/favicon.svg</c>).</summary>
    public static bool IsSystemPath(string path)
    {
        var first = path.TrimStart('/').Split('/')[0].ToLowerInvariant();
        return SystemSegments.Contains(first);
    }

    /// <summary>Splits a language prefix off a path: <c>/en/docs/x</c> → ("/en", "/docs/x"); a path without one → ("", path).</summary>
    public static (string Culture, string Path) SplitCulture(string path)
    {
        var p = string.IsNullOrEmpty(path) ? "/" : path;
        var seg = p.TrimStart('/').Split('/')[0].ToLowerInvariant();
        if (seg.Length > 0 && Localizer.SupportedCultures.Contains(seg))
        {
            var rest = p[(seg.Length + 1)..];
            return ("/" + seg, rest.Length == 0 ? "/" : rest);
        }
        return ("", p);
    }

    /// <summary>The part of <paramref name="path"/> below the prefix (<c>/docs/x</c> → <c>/x</c>, <c>/docs</c> → <c>/</c>), or null when it is not below it.</summary>
    public static string? Below(string prefix, string path)
    {
        if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.Equals(prefix + "/", StringComparison.OrdinalIgnoreCase)) return "/";
        return path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : null;
    }

    /// <summary>
    /// The path a request on the mapped domain is served as: <c>/x</c> → <c>/docs/x</c>, <c>/en/x</c> → <c>/en/docs/x</c>, <c>/</c> → <c>/docs</c>.
    /// Null for the host's own areas (served as they are).
    /// </summary>
    public static string? Inbound(DomainRoute r, string path)
    {
        if (IsSystemPath(path)) return null;
        var (culture, rest) = SplitCulture(path);
        return culture + r.Prefix + (rest == "/" ? "" : rest);
    }

    /// <summary>On the mapped domain: an address that names the prefix itself (<c>/docs/x</c>, <c>/en/docs</c>) as it is written there (<c>/x</c>, <c>/en</c>), or null.</summary>
    public static string? Strip(DomainRoute r, string path)
    {
        var (culture, rest) = SplitCulture(path);
        var below = Below(r.Prefix, rest);
        if (below is null) return null;
        return culture.Length > 0 && below == "/" ? culture : culture + below;
    }

    /// <summary>The route whose part of the site a path of the MAIN address lies in (for the redirect and the sitemap), or null.</summary>
    public static DomainRoute? Owning(IReadOnlyList<DomainRoute> routes, string path)
    {
        var (_, rest) = SplitCulture(path);
        return routes.Where(r => Below(r.Prefix, rest) is not null).OrderByDescending(r => r.Prefix.Length).FirstOrDefault();
    }

    /// <summary>
    /// A link or address in a page served on the mapped domain, written for that domain: below the prefix → stripped; the host's own areas and
    /// anything that is not a local path → unchanged; any other local path (the rest of the site) → the main address in front, when it is known.
    /// </summary>
    public static string Outbound(DomainRoute r, string url, string? mainOrigin)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/' || url.StartsWith("//", StringComparison.Ordinal)) return url;
        var cut = url.IndexOfAny(['?', '#']);
        var path = cut >= 0 ? url[..cut] : url;
        var tail = cut >= 0 ? url[cut..] : "";
        if (IsSystemPath(path)) return url;
        if (Strip(r, path) is { } own) return own + tail;
        return string.IsNullOrEmpty(mainOrigin) ? url : mainOrigin + url;
    }

    /// <summary>
    /// The page's links for the mapped domain: every <c>href</c>/<c>action</c> that is a local path goes through <see cref="Outbound"/>, and an absolute
    /// address of the main site below the prefix (a share tag, a canonical link) becomes the domain's. Only attribute values in double quotes are touched
    /// — that is how the CMS and its plugins write them; text and scripts stay as they are.
    /// </summary>
    public static string RewriteHtml(string html, DomainRoute r, string scheme, string? mainOrigin)
    {
        if (string.IsNullOrEmpty(html)) return html;
        var own = scheme + "://" + r.Host;
        var result = AttrRx().Replace(html, m => m.Groups[1].Value + Outbound(r, System.Net.WebUtility.HtmlDecode(m.Groups[2].Value), mainOrigin).Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;") + "\"");
        if (!string.IsNullOrEmpty(mainOrigin))
        {
            result = AbsRx().Replace(result, m =>
            {
                var url = m.Groups[2].Value;
                if (!url.StartsWith(mainOrigin + "/", StringComparison.OrdinalIgnoreCase)) return m.Value;
                var local = url[mainOrigin.Length..];
                var cut = local.IndexOfAny(['?', '#']);
                var stripped = Strip(r, cut >= 0 ? local[..cut] : local);
                return stripped is null ? m.Value : m.Groups[1].Value + own + stripped + (cut >= 0 ? local[cut..] : "") + "\"";
            });
        }
        return result;
    }

    /// <summary>A Location header for the mapped domain (a redirect a handler wrote with the site's own path).</summary>
    public static string RewriteLocation(DomainRoute r, string location, string? mainOrigin) => Outbound(r, location, mainOrigin);

    /// <summary>The site's main address (scheme and host) when it is configured and is not the mapped domain itself — where links to the rest of the site go.</summary>
    public static string? MainOrigin(string? canonicalUrl, DomainRoute r)
    {
        if (!Uri.TryCreate((canonicalUrl ?? "").Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        return CleanHost(u.Host) == r.Host ? null : u.GetLeftPart(UriPartial.Authority);
    }

    [GeneratedRegex(@"\s*(?:\||=|→|->)\s*")]
    private static partial Regex SepRx();
    [GeneratedRegex(@"(\s(?:href|action)="")(/[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex AttrRx();
    [GeneratedRegex(@"(\s(?:href|action|content|src)="")(https?://[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AbsRx();
}
