using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MatCMS.Shared;

namespace MatCMS.Services;

/// <summary>
/// Counts the site's traffic in memory; <see cref="StatsFlushService"/> writes the counts to the database once a
/// minute. A request never touches the database for statistics — the site stays as fast with it as without.
/// <para><b>Visitors without cookies and without storing an address:</b> a visitor is a hash of IP + user agent
/// with a random salt that exists only in memory and is replaced every day. The hash can neither be reversed nor
/// linked across days, and nothing of it reaches the database — only the number of distinct hashes. The price: a
/// restart during the day starts a new salt, so a visitor seen before and after it is counted twice that day.</para>
/// </summary>
public sealed class StatsCollector
{
    private readonly ConcurrentDictionary<(string Day, string Kind, string Key), long> _pending = new();
    private readonly object _visitorLock = new();
    private string _visitorDay = "";
    private byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private readonly HashSet<ulong> _seen = new();

    // Per flush window and kind, how many distinct keys are kept before the rest is folded into "…". Bounds memory
    // against a scanner requesting ten thousand invented paths in a minute; the flush service bounds the table.
    private const int MaxKeysPerKind = 1000;
    private readonly ConcurrentDictionary<(string Day, string Kind), int> _keysPerKind = new();

    public void Add(string day, string kind, string key, long by = 1)
    {
        if (kind != StatKinds.Total && !_pending.ContainsKey((day, kind, key)))
        {
            var n = _keysPerKind.AddOrUpdate((day, kind), 1, (_, c) => c + 1);
            if (n > MaxKeysPerKind) key = StatKinds.Other;
        }
        _pending.AddOrUpdate((day, kind, key), by, (_, c) => c + by);
    }

    /// <summary>True the first time this visitor is seen today — the one moment it adds to "Besucher".</summary>
    public bool FirstVisitToday(string day, string? ip, string? userAgent)
    {
        lock (_visitorLock)
        {
            if (day != _visitorDay)
            {
                _visitorDay = day;
                _seen.Clear();
                _salt = RandomNumberGenerator.GetBytes(32);
            }
            var bytes = Encoding.UTF8.GetBytes((ip ?? "") + "\n" + (userAgent ?? ""));
            var h = HMACSHA256.HashData(_salt, bytes);
            return _seen.Add(BitConverter.ToUInt64(h, 0));
        }
    }

    /// <summary>Takes everything counted since the last call (the flush), leaving the collector empty.</summary>
    public List<StatRow> Drain()
    {
        var rows = new List<StatRow>();
        foreach (var k in _pending.Keys)
            if (_pending.TryRemove(k, out var c) && c != 0) rows.Add(new StatRow(k.Day, k.Kind, k.Key, c));
        _keysPerKind.Clear();
        return rows;
    }

    /// <summary>What is counted but not flushed yet — added to the database figures so the page shows today live.</summary>
    public List<StatRow> Snapshot() => _pending.Select(p => new StatRow(p.Key.Day, p.Key.Kind, p.Key.Key, p.Value)).ToList();

    /// <summary>Puts rows back after a failed flush, so a locked database loses nothing.</summary>
    public void Return(IEnumerable<StatRow> rows)
    {
        foreach (var r in rows) _pending.AddOrUpdate((r.Day, r.Kind, r.Key), r.Count, (_, c) => c + r.Count);
    }
}

/// <summary>
/// Decides what a request counts as and hands it to <see cref="StatsCollector"/>. Runs after the response, so the
/// status is known; fail-safe like the request log — statistics can never break a request.
/// </summary>
public sealed class StatsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly StatsCollector _stats;
    public StatsMiddleware(RequestDelegate next, StatsCollector stats) { _next = next; _stats = stats; }

    // Not the public site: the back office, the machine interfaces, static files, and the internal re-execution of
    // the status page (which would count every 404 a second time as /_status).
    private static readonly string[] Skip =
    {
        "/admin", "/api/", "/_content/", "/uploads/", "/plugin-assets/", "/lib/", "/css/", "/js/", "/_status",
        "/error", "/login", "/logout", "/sso/", "/set-language", "/mcp", "/_framework/", "/healthz"
    };

    private static readonly string[] BotMarks =
    {
        "bot", "crawl", "spider", "slurp", "facebookexternalhit", "preview", "curl/", "wget/", "python", "go-http",
        "headless", "monitor", "uptime", "scan", "java/", "okhttp", "httpclient", "lighthouse", "pingdom"
    };

    public async Task InvokeAsync(HttpContext ctx)
    {
        await _next(ctx);
        try { Count(ctx); } catch { /* statistics must never break a request */ }
    }

    private void Count(HttpContext ctx)
    {
        var req = ctx.Request;
        if (!HttpMethods.IsGet(req.Method)) return;
        var path = req.Path.Value ?? "/";
        foreach (var p in Skip) if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return;
        // The site's own people browsing it are not visitors.
        if (ctx.User?.Identity?.IsAuthenticated == true) return;
        // A browser prefetching a link has not visited it.
        if (req.Headers["Sec-Purpose"].ToString().Contains("prefetch") || req.Headers["Purpose"] == "prefetch") return;

        var site = ctx.RequestServices.GetRequiredService<SiteContext>();
        if (site.Get(SettingKeys.StatsEnabled) == "0") return;

        var status = ctx.Response.StatusCode;
        var day = StatKinds.DayOf(DateTime.UtcNow);
        var ua = req.Headers.UserAgent.ToString();

        if (IsBot(ua)) { _stats.Add(day, StatKinds.Total, StatKinds.Bots); return; }

        // The cloud's thumbnails and previews load the site in a frame — an operator looking, not a visitor.
        var referrer = Referrer(req);
        if (referrer is not null && IsCloud(site, referrer)) return;

        if (status == 404)
        {
            _stats.Add(day, StatKinds.Total, StatKinds.StatusKey(404));
            _stats.Add(day, StatKinds.NotFound, Trim(path));
            return;
        }

        // A page view is an HTML page that was delivered. A file under a page's address, an API answer or a
        // redirect is not one — and neither is a 5xx, which is counted but not viewed.
        var html = ctx.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
        if (status >= 500) { _stats.Add(day, StatKinds.Total, StatKinds.StatusKey(status)); return; }
        if (!html) return;
        _stats.Add(day, StatKinds.Total, StatKinds.StatusKey(status));
        if (status < 200 || status >= 300) return;

        _stats.Add(day, StatKinds.Total, StatKinds.Views);
        _stats.Add(day, StatKinds.Path, Trim(path));
        _stats.Add(day, StatKinds.Device, DeviceOf(ua));
        if (referrer is not null && !SameSite(referrer, req.Host.Host)) _stats.Add(day, StatKinds.Referrer, referrer);
        if (_stats.FirstVisitToday(day, ctx.Connection.RemoteIpAddress?.ToString(), ua))
            _stats.Add(day, StatKinds.Total, StatKinds.Visitors);
    }

    internal static bool IsBot(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return true;
        var l = ua.ToLowerInvariant();
        foreach (var m in BotMarks) if (l.Contains(m)) return true;
        return false;
    }

    internal static string DeviceOf(string ua)
    {
        if (ua.Contains("iPad", StringComparison.Ordinal) || ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase)
            || (ua.Contains("Android", StringComparison.Ordinal) && !ua.Contains("Mobile", StringComparison.Ordinal)))
            return "tablet";
        if (ua.Contains("Mobi", StringComparison.Ordinal) || ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("Android", StringComparison.Ordinal))
            return "mobile";
        return "desktop";
    }

    /// <summary>The referring host without "www.", or null when there is none (typed in, bookmark, app).</summary>
    private static string? Referrer(HttpRequest req)
    {
        var r = req.Headers.Referer.ToString();
        if (!Uri.TryCreate(r, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return null;
        var host = u.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    private static bool SameSite(string referrer, string host)
    {
        host = host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        return referrer == host;
    }

    private static bool IsCloud(SiteContext site, string referrer)
    {
        foreach (var key in new[] { SettingKeys.CloudUrl, SettingKeys.CloudPublicUrl })
            if (Uri.TryCreate(site.Get(key), UriKind.Absolute, out var u)
                && string.Equals(u.Host.StartsWith("www.") ? u.Host[4..] : u.Host, referrer, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Paths are keys, not content: trailing slash off (except the root), bounded length.</summary>
    private static string Trim(string path)
    {
        if (path.Length > 1) path = path.TrimEnd('/');
        return path.Length <= 200 ? path : path[..200];
    }
}
