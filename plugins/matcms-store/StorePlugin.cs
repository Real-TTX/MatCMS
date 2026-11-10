using System.Text.Json.Nodes;

namespace MatCMS.Plugin.MatcmsStore;

/// <summary>
/// The MatCMS Store — the store SERVER (docs/official-store.md). Runs on matcms.de only: the catalogue at <c>/store</c> (a "Store" entry in the
/// header menu), publishers register and upload at <c>/store/konto</c>, the operator reviews every version in the admin and approves it with a
/// signature made on his own machine (<c>tools/StoreSign</c>) — the server holds no private key. Clouds and sites read <c>/store/api/…</c>.
/// <para>A native plugin, written against <c>MatCMS.Plugin.Abstractions</c> only: no host type, no reflection-based JSON, nothing that keeps it
/// from being unloaded.</para>
/// </summary>
public sealed class StorePlugin : IPlugin
{
    /// <inheritdoc />
    public void Configure(IPluginBuilder b)
    {
        StoreText.Register(b);
        StoreDb.Declare(b.Data);
        StoreSettings.Declare(b.Settings);
        foreach (var spec in StoreMail.Specs) b.MailTemplate(spec);

        // ---- public pages --------------------------------------------------------------------------------------------
        // One address per page (the store does not publish per language). Pages with a form carry the antiforgery token.
        var read = new PublicRouteOptions { Methods = PluginMethod.Get, DefaultLanguageOnly = true };
        var form = new PublicRouteOptions { Antiforgery = true, DefaultLanguageOnly = true, PostsPerMinute = 30 };
        var member = form with { RequireMember = true };
        var api = new PublicRouteOptions { Methods = PluginMethod.Get, DefaultLanguageOnly = true, DuringMaintenance = true };

        b.Route(StoreUrls.Catalog, StoreApiRoutes.CatalogAsync, api);
        b.Route("/store/api/plugins/{key}", StoreApiRoutes.ItemAsync, api);
        b.Route("/store/api/plugins/{key}/{version}/bundle", StoreApiRoutes.BundleAsync, api);
        b.Route("/store/api/plugins/{key}/images/{id}", StoreApiRoutes.ImageAsync, api);
        b.Route(StoreUrls.Register, StoreAccount.RegisterAsync, form);
        b.Route(StoreUrls.Account, StoreAccount.AccountAsync, member);
        // A package may be big; the bigger limit applies only to a signed-in member's form (checked by the host before it reads the body).
        b.Route(StoreUrls.Upload, StoreAccount.UploadAsync, member with { MaxBodyMegabytes = StoreSettings.MaxPackageLimit + 5 });
        b.Route("/store/konto/eintrag/{key}", StoreAccount.ItemAsync, member with { MaxBodyMegabytes = 30 });
        b.Route("/store/herausgeber/{slug}", StorePages.PublisherAsync, read);
        b.Route(StoreUrls.Home, StorePages.IndexAsync, read);
        b.Route("/store/{key}", StorePages.ItemAsync, read);

        // ---- back office ---------------------------------------------------------------------------------------------
        b.Admin.Section("store.nav", "ti-building-store", AdminGroup.Content, 40)
            .Page("review", null, StoreAdmin.ReviewAsync)
            .Page("review/{id}", null, StoreAdmin.ReviewItemAsync)
            .Page("package/{id}", null, StoreAdmin.PackageAsync)
            .Page("items", "store.admin.items", StoreAdmin.ItemsAsync)
            .Page("items/{id}", null, StoreAdmin.ItemAsync)
            .Page("publishers", "store.admin.publishers", StoreAdmin.PublishersAsync)
            .Badge(StoreAdmin.BadgeAsync)
            .Link("store.admin.review", "review", "ti-checklist", badge: true)
            .Link("store.admin.items", "items", "ti-package")
            .Link("store.admin.publishers", "publishers", "ti-users")
            .Link("store.admin.settings", "_settings", "ti-settings");

        // ---- the rest of the site ------------------------------------------------------------------------------------
        b.MenuElement(new MenuElementSpec("store", "store.menu") { Help = "store.menuHelp", Icon = "building-store", DefaultMenu = "header" },
            (ctx, ct) => Task.FromResult<MenuEntry?>(new MenuEntry(ctx.T("store.menu"), StoreUrls.Home) { Icon = "building-store" }));
        b.Sitemap(SitemapAsync);
        b.LinkTargets("store.links.group", LinksAsync);
        b.On("site.search", SiteSearchAsync);
    }

    private static async Task<IEnumerable<SitemapEntry>> SitemapAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var list = new List<SitemapEntry> { new(StoreUrls.Home) };
        foreach (var (item, pub, _) in await StorePages.PublicItemsAsync(db, "", "", "", ct)) list.Add(new SitemapEntry(StoreUrls.Item(item.Key), item.Updated));
        foreach (var p in (await db.PublishersAsync(ct)).Where(p => !p.Blocked)) list.Add(new SitemapEntry(StoreUrls.Publisher(p.Slug)));
        return list;
    }

    private static async Task<IEnumerable<LinkTarget>> LinksAsync(IPluginContext ctx, CancellationToken ct)
    {
        var list = new List<LinkTarget> { new(StoreSettings.StoreTitle(ctx), StoreUrls.Home), new(ctx.T("store.account"), StoreUrls.Account) };
        list.AddRange((await StorePages.PublicItemsAsync(new StoreDb(ctx.Data), "", "", "", ct)).Select(x => new LinkTarget(ctx.T("store.links.item", x.Item.Name), StoreUrls.Item(x.Item.Key))));
        return list;
    }

    /// <summary>The site's search and the visitors' chat bot: published entries by name, key, text and keywords.</summary>
    private static async Task<JsonNode?> SiteSearchAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var q = payload?["q"]?.GetValue<string>()?.Trim() ?? "";
        var max = payload?["max"] is JsonValue m && m.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 20) : 5;
        if (q.Length < 2) return null;
        var hits = (await StorePages.PublicItemsAsync(new StoreDb(ctx.Data), StoreText.Cut(q, 80), "", "", ct)).Take(max).ToList();
        if (hits.Count == 0) return null;
        var items = new JsonArray();
        foreach (var (item, _, _) in hits)
            items.Add(new JsonObject
            {
                ["title"] = item.Name, ["url"] = StoreUrls.Item(item.Key), ["kind"] = ctx.T("store.search.kind"),
                ["text"] = StoreText.Cut(item.Summary.Length > 0 ? item.Summary : item.Description, 300), ["score"] = 1.0
            });
        return new JsonObject { ["items"] = items };
    }
}
