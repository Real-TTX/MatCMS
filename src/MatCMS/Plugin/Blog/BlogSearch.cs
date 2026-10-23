using System.Text.Json.Nodes;
using MatCMS.Plugin;
using MatCMS.Services;

namespace MatCMS.Plugin.Blog;

/// <summary>The blog's answer to <c>site.search</c> (the event a site-wide search — the AI bot — asks): the published posts that match, with an excerpt.</summary>
internal static class BlogSearch
{
    public static async Task<JsonNode?> SearchAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var terms = SiteSearch.Terms(payload?["q"]?.GetValue<string>() ?? "");
        if (terms.Count == 0) return null;
        var max = Math.Clamp(payload?["max"] is JsonValue m && m.TryGetValue<int>(out var n) ? n : 5, 1, 20);
        // The blog publishes in the default language only (its routes say so), whatever language the visitor reads.
        var rows = await ctx.Data.Table("posts").QueryAsync(DataQuery.Everything.Eq("IsPublished", true).Eq("Locale", "de")
            .Where("PublishedAt", DataOp.Le, DateTime.UtcNow).OrderBy("PublishedAt", descending: true).Page(0, 1000), ct);
        var items = new JsonArray();
        foreach (var hit in rows.Select(BlogStore.From)
                     .Select(p => (Post: p, Text: SiteSearch.Plain(p.Excerpt + " " + p.ContentHtml + " " + p.Tags)))
                     .Select(x => (x.Post, x.Text, Score: SiteSearch.Score(x.Post.Title.ToLowerInvariant(), x.Text.ToLowerInvariant(), terms)))
                     .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(max))
        {
            items.Add(new JsonObject
            {
                ["title"] = hit.Post.Title, ["url"] = "/blog/" + hit.Post.Slug, ["score"] = hit.Score,
                ["text"] = SiteSearch.ExcerptOf(System.Text.RegularExpressions.Regex.Replace(hit.Text, @"\s+", " ").Trim(), terms),
                ["kind"] = ctx.T("blog.search.kind")
            });
        }
        return items.Count == 0 ? null : new JsonObject { ["items"] = items };
    }
}
