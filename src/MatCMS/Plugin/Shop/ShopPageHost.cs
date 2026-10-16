using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>What one of the shop's public pages has to say: the values it computed (also the variables of the page the editors laid out), the default markup it would draw without such a page (the
/// template part, or the markup the handler builds itself), and the answer's frame — title, status. A handler that has an answer of its own (a redirect, a 404) puts it in <see cref="Early"/>.</summary>
internal sealed record PageView(PluginResult? Early, string Title, string? Description, Dictionary<string, string> Values, string Fallback)
{
    /// <summary>Markup that follows the page in either case (the product's structured data).</summary>
    public string Tail { get; init; } = "";
    public bool NoIndex { get; init; }
    public int Status { get; init; } = 200;
    /// <summary>Extra head tags (the share tags of a product).</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Meta { get; init; }
    /// <summary>The page is not drawn: the handler already has its answer.</summary>
    public static PageView Done(PluginResult answer) => new(answer, "", null, new Dictionary<string, string>(), "");
}

/// <summary>
/// Draws a page of the shop: the page the editors laid out in the block editor for its role — its blocks, with the values as variables — and, where there is none (never created, switched off), the
/// shop's own default. Everything the handlers compute is the same either way; only the layout around it differs.
/// </summary>
internal static class ShopPageHost
{
    public static async Task<PluginResult> RunAsync(IPluginContext ctx, string role, PageView view, CancellationToken ct = default)
    {
        if (view.Early is not null) return view.Early;
        var cfg = ShopConfig.From(ctx.Settings);
        var values = new Dictionary<string, string>(view.Values);
        if (!values.ContainsKey("nav_html")) values["nav_html"] = await ShopHtml.NavAsync(ctx);
        var blocks = await ctx.RenderPageAsync(role, new PageScope(values, ShopPageRoles.HtmlNames(role)), ct);
        return await ShopHtml.PageAsync(ctx, cfg, view.Title, (blocks ?? view.Fallback) + view.Tail, view.Description, view.Status, view.NoIndex, blocks: blocks is not null, meta: view.Meta);
    }
}
