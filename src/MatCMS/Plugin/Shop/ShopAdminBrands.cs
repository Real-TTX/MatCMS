using System.Globalization;
using MatCMS.Plugin;
using MatCMS.Services;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The back office's brands: the list (logo, name, products, status) and one brand's form — name, short text (what galleries and lists show), the details (only on the brand's own page), logo,
/// header picture, the manufacturer's website and what search engines read. A shop that kept its brands as an attribute ("Marke") takes them over with one button.
/// </summary>
internal static class ShopAdminBrands
{
    private const string Section = ShopAdmin.Section;
    private static string Intl(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string BrandUrl(long id) => $"{Section}/brands/{id}";
    private static string E(string? t) => System.Net.WebUtility.HtmlEncode(t ?? "");

    /// <summary>Names a shop gives an attribute that really holds the brand.</summary>
    private static readonly string[] AttributeNames = { "marke", "marken", "brand", "brands", "hersteller", "manufacturer" };

    /// <summary>The attribute that holds the brands in a shop that had no brands yet ("Marke", "Hersteller" …) — a choice or a text; null when there is none.</summary>
    internal static ShopAttribute? BrandAttribute(IReadOnlyList<ShopAttribute> attrs) =>
        attrs.FirstOrDefault(a => a.Kind is AttributeKind.Choice or AttributeKind.Text && (AttributeNames.Contains(a.Slug.ToLowerInvariant()) || AttributeNames.Contains(a.Name.Trim().ToLowerInvariant())));

    /// <summary>
    /// Takes the brands over from that attribute: every value it offers or a product has becomes a brand (an existing one of that name is used), every product without a brand gets the one its
    /// attribute names, and the attribute stops being offered as a filter (the brand filter replaces it; its values stay, nothing is deleted). Returns how many brands were made and products given one.
    /// </summary>
    internal static async Task<(int Brands, int Products)> TakeOverAsync(ShopStore store, ShopAttribute attr, CancellationToken ct)
    {
        var rows = await store.AttrRowsAsync(null, new[] { attr.Id }, ct);
        var names = attr.OptionList.Concat(rows.Select(r => r.Value)).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var before = (await store.BrandsAsync(false, ct)).Count;
        var slugOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            if (await store.ResolveBrandAsync(n, true, ct) is { } b) slugOf[n] = b.Slug;
        var made = (await store.BrandsAsync(false, ct)).Count - before;
        var given = 0;
        var byProduct = rows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.First().Value.Trim());
        foreach (var p in (await store.ProductsAsync(false, ct: ct)).Items)
            if (p.Brand.Length == 0 && byProduct.TryGetValue(p.Id, out var v) && slugOf.TryGetValue(v, out var slug))
            {
                await store.SetProductBrandAsync(p.Id, slug, ct);
                given++;
            }
        if (attr.Filterable) await store.SaveAttributeAsync(attr with { Filterable = false }, ct);
        return (made, given);
    }

    // ---- the list ---------------------------------------------------------------------------------------------

    public static async Task<PluginResult> BrandsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        if (ctx.Request.IsPost && ctx.Request.Action == "delete" && long.TryParse(ctx.Request.F("id"), out var del))
        {
            await store.DeleteBrandAsync(del, ct);
            return PluginResult.Redirect($"{Section}/brands", ctx.T("shop.adm.brandDeleted"));
        }
        if (ctx.Request.IsPost && ctx.Request.Action == "takeover")
        {
            if (BrandAttribute(await store.AttributesAsync(ct)) is not { } source) return PluginResult.Redirect($"{Section}/brands");
            var (made, given) = await TakeOverAsync(store, source, ct);
            return PluginResult.Redirect($"{Section}/brands", ctx.T("shop.adm.brandTakeoverDone", made, given));
        }

        var brands = await store.BrandsAsync(false, ct);
        var counts = await store.BrandCountsAsync(false, ct);
        var nodes = new List<UiNode?>();
        // A shop that kept its brands as an attribute: one button takes them over (offered while that attribute is still a filter, or while there are no brands at all).
        if (BrandAttribute(await store.AttributesAsync(ct)) is { } attr && (attr.Filterable || brands.Count == 0))
            nodes.Add(Ui.Card(ctx.T("shop.adm.brandTakeover"),
                Ui.Muted(ctx.T("shop.adm.brandTakeoverHelp", attr.Name)),
                Ui.Inline(Ui.Action(ctx.T("shop.adm.brandTakeoverButton", attr.Name), "takeover", null, UiButtonKind.Primary, confirm: ctx.T("shop.adm.brandTakeoverConfirm", attr.Name), icon: "ti-transfer") with { WithLabel = true })));

        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("shop.brand")), Ui.Column(ctx.T("shop.adm.products"), right: true), Ui.Column(ctx.T("shop.adm.status")), Ui.Column(ctx.T("shop.adm.sort"), right: true), Ui.Column("", actions: true) },
            brands.Select(x =>
            {
                var logo = ShopPublic.SafeUrl(x.Logo);
                var thumb = logo.Length > 0 ? $"<span class=\"ui-thumb\" style=\"background:#fff url('{E(logo)}') center/contain no-repeat\"></span>" : "<span class=\"ui-thumb empty\"><i class=\"ti ti-award\"></i></span>";
                var summary = x.Summary.Length > 90 ? x.Summary[..90] + "…" : x.Summary;
                var cell = $"<div class=\"ui-media-cell\">{thumb}<div><a href=\"{BrandUrl(x.Id)}\"><strong>{E(x.Name)}</strong></a><div class=\"muted\">{E(x.Slug)}{(summary.Length > 0 ? " · " + E(summary) : "")}</div></div></div>";
                return Ui.RowFor($"{x.Name} {x.Slug} {x.Summary}",
                    Ui.Html(cell),
                    Ui.Text(Intl(counts.GetValueOrDefault(x.Slug))) with { Inline = true },
                    Ui.Inline(x.Active ? Ui.Badge(ctx.T("shop.adm.active"), UiTone.Success) : Ui.Badge(ctx.T("shop.adm.inactive"), UiTone.Muted), x.Featured ? Ui.Badge(ctx.T("shop.adm.featured"), UiTone.Info) : null),
                    Ui.Text(Intl(x.Sort)) with { Inline = true, Muted = true },
                    Ui.Inline(
                        Ui.Button(ctx.T("action.edit"), BrandUrl(x.Id), UiButtonKind.Ghost, small: true),
                        Ui.Button(ctx.T("shop.adm.view"), ShopUrls.Brand(x.Slug), UiButtonKind.Ghost, icon: "ti-external-link", small: true) with { IconOnly = true, NewTab = true },
                        Ui.Action(ctx.T("action.delete"), "delete", Intl(x.Id), UiButtonKind.Danger, confirm: ctx.T("shop.adm.deleteBrandConfirm", x.Name), icon: "ti-trash"))) with
                { Tags = (x.Active ? "active" : "inactive") + (x.Featured ? " featured" : "") };
            }),
            searchPlaceholder: ctx.T("action.search"), pageSize: 50, emptyText: ctx.T("shop.adm.noBrands")) with
        {
            Filter = Ui.ListFilter(ctx.T("shop.adm.filter"),
                ("", $"{ctx.T("shop.adm.all")} ({brands.Count})"), ("active", $"{ctx.T("shop.adm.active")} ({brands.Count(x => x.Active)})"),
                ("inactive", $"{ctx.T("shop.adm.inactive")} ({brands.Count(x => !x.Active)})"), ("featured", $"{ctx.T("shop.adm.featured")} ({brands.Count(x => x.Featured)})")),
            Below = new UiNode[] { Ui.Button(ctx.T("shop.adm.newBrand"), BrandUrl(0)), Ui.Button(ctx.T("shop.adm.brandsPublic"), ShopUrls.BrandsPage, UiButtonKind.Ghost, icon: "ti-external-link") with { NewTab = true } }
        };
        nodes.Add(table);
        return PluginResult.Page(Ui.Stack(nodes.ToArray()), ctx.T("shop.adm.brands"));
    }

    // ---- one brand ----------------------------------------------------------------------------------------------

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    public static async Task<PluginResult> BrandAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        long.TryParse(ctx.Request.R("id"), out var id);
        var current = await store.BrandAsync(id, ct);
        if (id > 0 && current is null) return PluginResult.Redirect($"{Section}/brands");
        current ??= new Brand { Active = true, Sort = (await store.BrandsAsync(false, ct)).Count };
        string? error = null;

        if (ctx.Request.IsPost && ctx.Request.Action == "save")
        {
            var f = ctx.Request;
            var name = Clip(f.F("name").Trim(), 120);
            var website = f.F("website").Trim();
            current = current with
            {
                Name = name, Summary = Clip(f.F("summary").Trim(), 600), DetailsHtml = SafeHtml.Sanitize(f.F("details")), Logo = ShopPublic.SafeUrl(f.F("logo")), Image = ShopPublic.SafeUrl(f.F("image")),
                Website = ShopBrands.SafeWebsite(website), SeoTitle = Clip(f.F("seo_title").Trim(), 120), SeoDescription = Clip(f.F("seo_description").Trim(), 320),
                Active = f.Form.ContainsKey("active"), Featured = f.Form.ContainsKey("featured"),
                Sort = int.TryParse(f.F("sort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var so) ? Math.Clamp(so, 0, 100000) : 0
            };
            if (name.Length == 0) error = ctx.T("shop.adm.errBrandName");
            else if (website.Length > 0 && current.Website.Length == 0) error = ctx.T("shop.adm.errBrandWebsite");
            else if (id == 0)
            {
                var slug = await store.FreeBrandSlugAsync(string.IsNullOrWhiteSpace(f.F("slug")) ? name : f.F("slug"), ct);
                if (slug.Length == 0) slug = await store.FreeBrandSlugAsync("marke", ct);
                var created = await store.SaveBrandAsync(current with { Slug = slug }, ct);
                return PluginResult.Redirect(BrandUrl(created), ctx.T("shop.adm.brandSaved"));
            }
            else
            {
                await store.SaveBrandAsync(current, ct);
                return PluginResult.Redirect(BrandUrl(id), ctx.T("shop.adm.brandSaved"));
            }
        }

        var count = id > 0 ? (await store.BrandCountsAsync(false, ct)).GetValueOrDefault(current.Slug) : 0;
        var main = new List<UiNode?>
        {
            Ui.Card(ctx.T("shop.adm.secBrand"),
                Ui.Input("name", ctx.T("shop.adm.brandName"), current.Name) with { Required = true, MaxLength = 120 },
                Ui.TextArea("summary", ctx.T("shop.adm.brandSummary"), current.Summary, 3) with { Help = ctx.T("shop.adm.brandSummaryHelp") },
                Ui.RichText("details", ctx.T("shop.adm.brandDetails"), current.DetailsHtml) with { Help = ctx.T("shop.adm.brandDetailsHelp") },
                Ui.Input("website", ctx.T("shop.adm.brandWebsite"), current.Website) with { Placeholder = "https://www.hersteller.de", Help = ctx.T("shop.adm.brandWebsiteHelp") }),
            Ui.Card(ctx.T("shop.adm.secPictures"),
                Ui.Image("logo", ctx.T("shop.adm.brandLogo"), current.Logo) with { Help = ctx.T("shop.adm.brandLogoHelp") },
                Ui.Image("image", ctx.T("shop.adm.brandImage"), current.Image) with { Help = ctx.T("shop.adm.brandImageHelp") }),
            Ui.Card(ctx.T("shop.adm.seo"),
                Ui.Muted(ctx.T("shop.adm.brandSeoHelp")),
                Ui.Input("seo_title", ctx.T("shop.adm.seoTitle"), current.SeoTitle) with { MaxLength = 120, Placeholder = current.Name.Length > 0 ? ctx.T("shop.brand.seoTitle", current.Name, ctx.Site.Name) : "" },
                Ui.TextArea("seo_description", ctx.T("shop.adm.seoDescription"), current.SeoDescription, 3) with { Placeholder = current.Summary })
        };
        var side = new List<UiNode?>
        {
            Ui.Card(ctx.T("shop.adm.secStatus"),
                Ui.Checkbox("active", ctx.T("shop.adm.brandActive"), current.Active) with { Help = ctx.T("shop.adm.brandActiveHelp") },
                Ui.Checkbox("featured", ctx.T("shop.adm.featured"), current.Featured) with { Help = ctx.T("shop.adm.brandFeaturedHelp") },
                Ui.Input("sort", ctx.T("shop.adm.sort"), Intl(current.Sort), UiInputType.Number) with { Min = "0" }),
            Ui.Card(ctx.T("shop.adm.secPlace"),
                id == 0 ? Ui.Input("slug", ctx.T("shop.adm.slug"), "") with { Help = ctx.T("shop.adm.brandSlugHelp") } : Ui.KeyValue((ctx.T("shop.adm.slug"), current.Slug), (ctx.T("shop.adm.products"), Intl(count))))
        };
        var title = ctx.T(id == 0 ? "shop.adm.newBrand" : "shop.adm.editBrand");
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, title, Ui.Button(ctx.T("shop.adm.back"), $"{Section}/brands", UiButtonKind.Ghost, icon: "ti-arrow-left", small: true),
                id > 0 ? Ui.Button(ctx.T("shop.adm.view"), ShopUrls.Brand(current.Slug), UiButtonKind.Ghost, icon: "ti-external-link", small: true) with { NewTab = true } : null),
            error is null ? null : Ui.Alert(error, UiTone.Error),
            Ui.Form(Ui.Hidden("action", "save"), Ui.Columns(main, side), Ui.FormActions(Ui.Submit(ctx.T("action.save")), Ui.Button(ctx.T("action.cancel"), $"{Section}/brands", UiButtonKind.Ghost)))), title);
    }
}
