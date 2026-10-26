using MatCMS.Plugin;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;

namespace MatCMS.Services.Plugins;

/// <summary>What the transformer found: the route, what its pattern captured and the path with the
/// language prefix taken off. Handed to the page that runs the handler.</summary>
internal sealed record PluginMatch(PluginRegistry.RouteEntry Route, Dictionary<string, string> Values, string Path);

/// <summary>
/// Sends a request to the plugin page when — and only when — a loaded plugin has a route for it.
/// <para>This is the whole reason plugin routes can be unloaded: ASP.NET cannot remove an endpoint once
/// it is registered, so there is ONE dynamic endpoint, and the route table behind it is the registry.
/// It is registered with a very low order so it is asked first, and it answers "not mine" (null) for
/// everything that is not a plugin route, which is the normal case and costs a dictionary walk.</para>
/// </summary>
internal sealed class PluginRouteTransformer(PluginRegistry registry) : DynamicRouteValueTransformer
{
    public const string ItemKey = "matcms.plugin.match";

    public override ValueTask<RouteValueDictionary> TransformAsync(HttpContext http, RouteValueDictionary values)
    {
        if (registry.Routes.Count == 0) return NotMine();

        var path = http.Request.Path.Value ?? "/";
        var rest = StripCulture(path, out var culture);
        // The default language's prefix is an address only while the Standardroute is off; with it on, "/de/shop" stays what it always was.
        if (culture == Localizer.DefaultCulture && Localizer.DefaultAtRoot) return NotMine();
        var match = registry.MatchRoute(rest, http.Request.Method);
        if (match is null) return NotMine();
        // A route that has no other languages answers on the plain address and under the default language's own prefix only
        // ("/de/blog/x" when the Standardroute is off): "/en/blog/x" stays a 404.
        if (match.Value.Route.Options.DefaultLanguageOnly && culture is not null && culture != Localizer.DefaultCulture) return NotMine();

        http.Items[ItemKey] = new PluginMatch(match.Value.Route, match.Value.Values, rest);
        return new ValueTask<RouteValueDictionary>(new RouteValueDictionary { ["page"] = "/PluginPage" });
    }

    // The null the framework reads as "this endpoint does not apply, keep looking".
    private static ValueTask<RouteValueDictionary> NotMine() => new((RouteValueDictionary)null!);

    /// <summary>Removes a leading language segment (<c>/en/shop/x</c> gives <c>/shop/x</c>) — any content language, the default one
    /// included (its pages live under "/de" when the Standardroute is off). <paramref name="culture"/> is the segment taken, or null.</summary>
    internal static string StripCulture(string path, out string? culture)
    {
        culture = null;
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        var first = slash < 0 ? trimmed : trimmed.Substring(0, slash);
        if (first.Length > 0 && Localizer.SupportedCultures.Contains(first, StringComparer.OrdinalIgnoreCase))
        {
            culture = first.ToLowerInvariant();
            return slash < 0 ? "/" : trimmed.Substring(slash);
        }
        return path;
    }

    internal static string StripCulture(string path) => StripCulture(path, out _);
}

/// <summary>A small fixed-window limit on anonymous plugin POSTs per client address. Reading a plugin
/// page is not limited (a shop is browsed); writing is — an open write endpoint must not be hammered
/// into filling the database.</summary>
internal static class PluginPostLimiter
{
    private const int PerMinute = 20;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Window, int Count)> Hits = new();

    /// <summary>A webhook is called by a provider from a few addresses, in bursts: its limit is far looser (and still there — an open endpoint is open).</summary>
    public static bool AllowWebhook(string client)
    {
        var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        if (WebhookHits.Count > 2000)
            foreach (var kv in WebhookHits.Where(kv => kv.Value.Window != window).ToList()) WebhookHits.TryRemove(kv.Key, out _);
        var now = WebhookHits.AddOrUpdate(client, (window, 1), (_, cur) => cur.Window == window ? (window, cur.Count + 1) : (window, 1));
        return now.Count <= 600;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Window, int Count)> WebhookHits = new();

    /// <summary>Forgets every count (tests start from a clean minute).</summary>
    internal static void Reset() { Hits.Clear(); WebhookHits.Clear(); }

    /// <param name="client">The visitor's address, joined with the plugin's key so one plugin's traffic does not use up another's allowance.</param>
    /// <param name="perMinute">The route's allowance (<see cref="PublicRouteOptions.PostsPerMinute"/>).</param>
    public static bool Allow(string client, int perMinute = PerMinute)
    {
        var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        // The dictionary is bounded: old windows are dropped on the way, so it cannot grow with the
        // number of distinct visitors over time.
        if (Hits.Count > 5000)
            foreach (var kv in Hits.Where(kv => kv.Value.Window != window).ToList()) Hits.TryRemove(kv.Key, out _);
        var now = Hits.AddOrUpdate(client, (window, 1), (_, cur) => cur.Window == window ? (window, cur.Count + 1) : (window, 1));
        return now.Count <= Math.Clamp(perMinute, 1, 10_000);
    }
}
