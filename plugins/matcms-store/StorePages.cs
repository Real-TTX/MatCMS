using System.Text;
using static MatCMS.Plugin.MatcmsStore.StoreHtml;

namespace MatCMS.Plugin.MatcmsStore;

/// <summary>The public store: the catalogue at <c>/store</c> (search, kind, keyword), one entry at <c>/store/{key}</c>, a publisher at <c>/store/herausgeber/{slug}</c>.</summary>
internal static class StorePages
{
    private static PluginResult NotFound(IPluginContext ctx) =>
        Page(ctx, ctx.T("store.notFound"), $"<h1>{E(ctx.T("store.notFound"))}</h1><p><a href=\"{StoreUrls.Home}\">{E(ctx.T("store.backHome"))}</a></p>", status: 404, noIndex: true);

    /// <summary>The published entries a visitor may see, with their publishers and covers, filtered by search, kind and keyword.</summary>
    internal static async Task<List<(Item Item, Publisher? Pub, Picture? Cover)>> PublicItemsAsync(StoreDb db, string q, string kind, string tag, CancellationToken ct)
    {
        var pubs = (await db.PublishersAsync(ct)).ToDictionary(p => p.Id);
        var list = new List<(Item, Publisher?, Picture?)>();
        foreach (var item in await db.ItemsAsync(ItemStatus.Published, ct))
        {
            pubs.TryGetValue(item.PublisherId, out var pub);
            if (pub?.Blocked == true) continue;
            if (kind.Length > 0 && item.LatestKind != kind) continue;
            if (tag.Length > 0 && !StoreText.Tags(item.Tags).Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            if (q.Length > 0)
            {
                var hay = string.Join(' ', item.Key, item.Name, item.Summary, item.Description, item.Tags, pub?.Name ?? "");
                if (!q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            }
            list.Add((item, pub, (await db.PicturesOfAsync(item.Id, ct)).FirstOrDefault()));
        }
        return list.OrderByDescending(x => x.Item1.Updated ?? DateTime.MinValue).ToList();
    }

    public static async Task<PluginResult> IndexAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var q = ctx.Request.Q("q").Trim();
        var kind = ctx.Request.Q("art") is "script" or "native" ? ctx.Request.Q("art") : "";
        var tag = ctx.Request.Q("tag").Trim();
        var items = await PublicItemsAsync(db, StoreText.Cut(q, 80), kind, StoreText.Cut(tag, 40), ct);

        string Tab(string value, string label) =>
            $"<a class=\"mst-tab{(kind == value ? " is-on" : "")}\" href=\"{E(StoreUrls.Home + Query(q, value, tag))}\">{E(label)}</a>";
        var sb = new StringBuilder();
        sb.Append("<div class=\"mst-head\"><div><h1>").Append(E(StoreSettings.StoreTitle(ctx))).Append("</h1>");
        if (ctx.Settings.Get(StoreSettings.Intro) is { Length: > 0 } intro) sb.Append("<p class=\"mst-intro\">").Append(E(intro)).Append("</p>");
        sb.Append("</div><a class=\"btn btn-outline\" href=\"").Append(StoreUrls.Account).Append("\">").Append(E(ctx.T("store.submit"))).Append("</a></div>");
        sb.Append("<div class=\"mst-bar\"><div class=\"mst-tabs\">").Append(Tab("", ctx.T("store.all"))).Append(Tab("script", ctx.T("store.kind.script")))
          .Append(Tab("native", ctx.T("store.kind.native")));
        if (tag.Length > 0)
            sb.Append($"<a class=\"mst-tab is-on\" href=\"{E(StoreUrls.Home + Query(q, kind, ""))}\" title=\"{E(ctx.T("store.removeTag"))}\">#{E(tag)} ×</a>");
        sb.Append("</div><form class=\"mst-search\" method=\"get\" action=\"").Append(StoreUrls.Home).Append("\">");
        if (kind.Length > 0) sb.Append("<input type=\"hidden\" name=\"art\" value=\"").Append(E(kind)).Append("\">");
        sb.Append("<input type=\"search\" name=\"q\" value=\"").Append(E(q)).Append("\" placeholder=\"").Append(E(ctx.T("store.searchPlaceholder")))
          .Append("\" aria-label=\"").Append(E(ctx.T("store.searchPlaceholder"))).Append("\"></form></div>");
        if (items.Count == 0) sb.Append("<p class=\"mst-empty\">").Append(E(ctx.T(q.Length > 0 || kind.Length > 0 || tag.Length > 0 ? "store.nothingFound" : "store.empty"))).Append("</p>");
        else
        {
            sb.Append("<div class=\"mst-grid\">");
            foreach (var (item, pub, cover) in items) sb.Append(Card(ctx, item, pub, cover));
            sb.Append("</div>");
        }
        return Page(ctx, StoreSettings.StoreTitle(ctx), sb.ToString(), ctx.Settings.Get(StoreSettings.Intro), noIndex: q.Length > 0);
    }

    private static string Query(string q, string kind, string tag)
    {
        var parts = new List<string>();
        if (q.Length > 0) parts.Add("q=" + Uri.EscapeDataString(q));
        if (kind.Length > 0) parts.Add("art=" + kind);
        if (tag.Length > 0) parts.Add("tag=" + Uri.EscapeDataString(tag));
        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    public static async Task<PluginResult> ItemAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var key = ctx.Request.R("key").ToLowerInvariant();
        if (await db.ItemByKeyAsync(key, ct) is not { Status: ItemStatus.Published } item) return NotFound(ctx);
        var pub = await db.PublisherAsync(item.PublisherId, ct);
        if (pub?.Blocked == true) return NotFound(ctx);
        var versions = (await db.VersionsOfAsync(item.Id, ct)).Where(v => v.Status is VersionStatus.Published or VersionStatus.Withdrawn).ToList();
        var latest = versions.FirstOrDefault(v => v.Status == VersionStatus.Published);
        var pics = await db.PicturesOfAsync(item.Id, ct);

        var sb = new StringBuilder();
        sb.Append("<p class=\"mst-crumbs\"><a href=\"").Append(StoreUrls.Home).Append("\">").Append(E(StoreSettings.StoreTitle(ctx))).Append("</a> › ").Append(E(item.Name)).Append("</p>");
        sb.Append("<div class=\"mst-dhead\"><span class=\"mst-mono").Append(item.LatestKind == "native" ? " mst-native" : "").Append("\" aria-hidden=\"true\">")
          .Append(E(Initials(item.Name))).Append("</span><div><h1>").Append(E(item.Name)).Append("</h1>");
        if (pub is not null)
            sb.Append("<div class=\"mst-by\">").Append(E(ctx.T("store.by"))).Append(" <a href=\"").Append(E(StoreUrls.Publisher(pub.Slug))).Append("\">").Append(E(pub.Name))
              .Append("</a>").Append(pub.Verified ? "<span class=\"mst-check\" title=\"" + E(ctx.T("store.verified")) + "\">✓</span>" : "").Append("</div>");
        sb.Append("</div>").Append(KindBadge(ctx, item.LatestKind)).Append("</div>");
        if (item.Summary.Length > 0) sb.Append("<p class=\"mst-intro\">").Append(E(item.Summary)).Append("</p>");

        if (pics.Count > 0)
        {
            // The site's own lightbox (gallery.js) shows them large; one group, so "next" stays in this entry.
            sb.Append("<div class=\"mst-strip").Append(pics.Count == 1 ? " mst-one" : "").Append("\" data-lightbox-group=\"store-").Append(E(item.Key)).Append("\">");
            foreach (var p in pics)
            {
                var src = StoreUrls.Image(item.Key, p.Id);
                sb.Append("<a href=\"").Append(E(src)).Append("\" data-lightbox data-caption=\"").Append(E(p.Caption)).Append("\"><span class=\"mst-shot\"><img src=\"").Append(E(src))
                  .Append("\" alt=\"").Append(E(p.Caption.Length > 0 ? p.Caption : item.Name)).Append("\" loading=\"lazy\"></span>");
                if (p.Caption.Length > 0) sb.Append("<span class=\"mst-cap\">").Append(E(p.Caption)).Append("</span>");
                sb.Append("</a>");
            }
            sb.Append("</div>");
        }

        sb.Append("<div class=\"mst-cols\"><div class=\"mst-text\">");
        foreach (var para in item.Description.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) sb.Append("<p>").Append(E(para)).Append("</p>");
        var tags = StoreText.Tags(item.Tags);
        if (tags.Count > 0)
        {
            sb.Append("<div class=\"mst-tabs\">");
            foreach (var t in tags) sb.Append("<a class=\"mst-tab\" href=\"").Append(E(StoreUrls.Home + "?tag=" + Uri.EscapeDataString(t))).Append("\">#").Append(E(t)).Append("</a>");
            sb.Append("</div>");
        }
        sb.Append("<p class=\"mst-notice mst-info\">").Append(E(ctx.T("store.install"))).Append(item.LatestKind == "native" ? " " + E(ctx.T("store.installNative")) : "").Append("</p>");
        if (latest is not null)
            sb.Append("<div class=\"mst-actions\"><a class=\"btn btn-outline\" href=\"").Append(E(StoreUrls.Bundle(item.Key, latest.Version))).Append("\">")
              .Append(E(ctx.T("store.downloadPackage", latest.Version))).Append("</a></div>");
        sb.Append("</div><dl class=\"mst-facts\">");
        void Fact(string label, string value) { if (value.Length > 0) sb.Append("<div><dt>").Append(E(label)).Append("</dt><dd>").Append(E(value)).Append("</dd></div>"); }
        Fact(ctx.T("store.fact.version"), latest?.Version ?? "");
        Fact(ctx.T("store.fact.updated"), Day(latest?.PublishedAt));
        Fact(ctx.T("store.fact.kind"), ctx.T(item.LatestKind == "native" ? "store.kind.native" : "store.kind.script") + (latest?.Api is { Length: > 0 } api ? " · " + ctx.T("store.fact.api", api) : ""));
        Fact(ctx.T("store.fact.publisher"), pub?.Name ?? "");
        Fact(ctx.T("store.fact.license"), item.License);
        Fact(ctx.T("store.fact.downloads"), N(item.Downloads));
        Fact(ctx.T("store.fact.signed"), latest is null ? "" : ctx.T("store.fact.signedBy", latest.KeyId));
        Fact(ctx.T("store.fact.sha"), latest is null ? "" : latest.Sha256[..16] + "…");
        sb.Append("</dl></div>");
        if (item.Homepage.Length > 0 && StoreText.SafeUrl(item.Homepage) is { Length: > 0 } home)
            sb.Append("<p><a href=\"").Append(E(home)).Append("\" rel=\"noopener nofollow\" target=\"_blank\">").Append(E(ctx.T("store.homepage"))).Append("</a></p>");

        if (versions.Count > 0)
        {
            sb.Append("<div class=\"mst-versions\"><h2>").Append(E(ctx.T("store.versions"))).Append("</h2>");
            foreach (var v in versions)
            {
                sb.Append("<div class=\"mst-version\"><strong>").Append(E(v.Version)).Append("</strong><span class=\"mst-by\">").Append(E(Day(v.PublishedAt))).Append("</span>");
                if (v.Status == VersionStatus.Withdrawn) sb.Append(" <span class=\"mst-status\">").Append(E(ctx.T("store.withdrawn"))).Append("</span>");
                if (v.Status == VersionStatus.Withdrawn && v.Reason.Length > 0) sb.Append("<p>").Append(E(v.Reason)).Append("</p>");
                else if (v.Notes.Length > 0) sb.Append("<p>").Append(E(v.Notes)).Append("</p>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }
        return Page(ctx, item.Name + " – " + StoreSettings.StoreTitle(ctx), sb.ToString(), item.Summary.Length > 0 ? item.Summary : StoreText.Cut(item.Description, 160));
    }

    public static async Task<PluginResult> PublisherAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        if (await db.PublisherBySlugAsync(ctx.Request.R("slug").ToLowerInvariant(), ct) is not { Blocked: false } pub) return NotFound(ctx);
        var sb = new StringBuilder();
        sb.Append("<p class=\"mst-crumbs\"><a href=\"").Append(StoreUrls.Home).Append("\">").Append(E(StoreSettings.StoreTitle(ctx))).Append("</a> › ").Append(E(pub.Name)).Append("</p>");
        sb.Append("<div class=\"mst-dhead\"><span class=\"mst-mono\" aria-hidden=\"true\">").Append(E(Initials(pub.Name))).Append("</span><div><h1>").Append(E(pub.Name))
          .Append(pub.Verified ? " <span class=\"mst-check\" title=\"" + E(ctx.T("store.verified")) + "\">✓</span>" : "").Append("</h1>");
        if (StoreText.SafeUrl(pub.Website) is { Length: > 0 } web)
            sb.Append("<div class=\"mst-by\"><a href=\"").Append(E(web)).Append("\" rel=\"noopener\" target=\"_blank\">").Append(E(web)).Append("</a></div>");
        sb.Append("</div></div>");
        if (pub.About.Length > 0) sb.Append("<p class=\"mst-intro\">").Append(E(pub.About)).Append("</p>");
        var items = (await PublicItemsAsync(db, "", "", "", ct)).Where(x => x.Item.PublisherId == pub.Id).ToList();
        sb.Append("<div class=\"mst-bar\"></div>");
        if (items.Count == 0) sb.Append("<p class=\"mst-empty\">").Append(E(ctx.T("store.empty"))).Append("</p>");
        else
        {
            sb.Append("<div class=\"mst-grid\">");
            foreach (var (item, p, cover) in items) sb.Append(Card(ctx, item, p, cover));
            sb.Append("</div>");
        }
        return Page(ctx, pub.Name + " – " + StoreSettings.StoreTitle(ctx), sb.ToString());
    }
}
