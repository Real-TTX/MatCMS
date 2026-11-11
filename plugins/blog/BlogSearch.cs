using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Blog;

/// <summary>The blog's answer to <c>site.search</c> (the event a site-wide search — the AI bot — asks): the published posts of the asked
/// language that match, with an excerpt.</summary>
internal static class BlogSearch
{
    public static async Task<JsonNode?> SearchAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var terms = BlogText.Terms(payload?["q"]?.GetValue<string>() ?? "");
        if (terms.Count == 0) return null;
        var max = Math.Clamp(payload?["max"] is JsonValue m && m.TryGetValue<int>(out var n) ? n : 5, 1, 20);
        // The posts of the language the visitor reads (the payload says which); the default language when it does not say.
        var locale = payload?["locale"] is JsonValue lv && lv.TryGetValue<string>(out var l) && !string.IsNullOrWhiteSpace(l) ? l.Trim().ToLowerInvariant() : BlogUrls.Default(ctx);
        if (!BlogUrls.Publishes(ctx, locale)) locale = BlogUrls.Default(ctx);
        var rows = await ctx.Data.Table("posts").QueryAsync(BlogStore.Live(locale).OrderBy("PublishedAt", descending: true).Page(0, 1000), ct);
        var items = new JsonArray();
        foreach (var hit in rows.Select(BlogStore.From)
                     .Select(p => (Post: p, Text: BlogText.Plain(p.Excerpt + " " + p.ContentHtml + " " + p.Tags)))
                     .Select(x => (x.Post, x.Text, Score: BlogText.Score(x.Post.Title.ToLowerInvariant(), x.Text.ToLowerInvariant(), terms)))
                     .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(max))
        {
            items.Add(new JsonObject
            {
                ["title"] = hit.Post.Title, ["url"] = BlogUrls.Post(ctx, hit.Post), ["score"] = hit.Score,
                ["text"] = BlogText.ExcerptOf(Regex.Replace(hit.Text, @"\s+", " ").Trim(), terms),
                ["kind"] = ctx.T("blog.search.kind")
            });
        }
        return items.Count == 0 ? null : new JsonObject { ["items"] = items };
    }
}
