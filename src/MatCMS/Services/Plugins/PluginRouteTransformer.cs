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
        var rest = StripCulture(path);
        var match = registry.MatchRoute(rest, http.Request.Method);
        if (match is null) return NotMine();
        // A route that has no other languages answers on the plain address only: "/en/blog/x" stays a 404.
        if (match.Value.Route.Options.DefaultLanguageOnly && rest != path) return NotMine();

        http.Items[ItemKey] = new PluginMatch(match.Value.Route, match.Value.Values, rest);
        return new ValueTask<RouteValueDictionary>(new RouteValueDictionary { ["page"] = "/PluginPage" });
    }

    // The null the framework reads as "this endpoint does not apply, keep looking".
    private static ValueTask<RouteValueDictionary> NotMine() => new((RouteValueDictionary)null!);

    /// <summary>Removes a leading language segment (<c>/en/shop/x</c> gives <c>/shop/x</c>) — only for the
    /// non-default languages, whose pages live under such a prefix.</summary>
    internal static string StripCulture(string path)
    {
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        var first = slash < 0 ? trimmed : trimmed.Substring(0, slash);
        if (first.Length > 0 && Localizer.NonDefaultCultures.Contains(first, StringComparer.OrdinalIgnoreCase))
            return slash < 0 ? "/" : trimmed.Substring(slash);
        return path;
    }
}

/// <summary>A small fixed-window limit on anonymous plugin POSTs per client address. Reading a plugin
/// page is not limited (a shop is browsed); writing is — an open write endpoint must not be hammered
/// into filling the database.</summary>
internal static class PluginPostLimiter
{
    private const int PerMinute = 20;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Window, int Count)> Hits = new();

    public static bool Allow(string client)
    {
        var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        // The dictionary is bounded: old windows are dropped on the way, so it cannot grow with the
        // number of distinct visitors over time.
        if (Hits.Count > 5000)
            foreach (var kv in Hits.Where(kv => kv.Value.Window != window).ToList()) Hits.TryRemove(kv.Key, out _);
        var now = Hits.AddOrUpdate(client, (window, 1), (_, cur) => cur.Window == window ? (window, cur.Count + 1) : (window, 1));
        return now.Count <= PerMinute;
    }
}
