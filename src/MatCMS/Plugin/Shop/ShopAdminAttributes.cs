using System.Globalization;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>The shop's back office, part three: attributes (colour, size, material, weight …) — what they are, which products they apply to, whether the shop offers them as a filter. Their values are typed in the product's own form.</summary>
internal static class ShopAdminAttributes
{
    private const string Section = ShopAdmin.Section;
    private static string Intl(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string AttributeUrl(long id) => $"{Section}/attributes/{id}";

    internal static string KindLabel(IPluginContext ctx, string kind) => ctx.T("shop.adm.attrKind." + AttributeKind.Normalise(kind));

    public static async Task<PluginResult> AttributesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        if (ctx.Request.IsPost && ctx.Request.Action == "delete" && long.TryParse(ctx.Request.F("id"), out var del))
        {
            await store.DeleteAttributeAsync(del, ct);
            return PluginResult.Redirect($"{Section}/attributes", ctx.T("shop.adm.attrDeleted"));
        }
        var attrs = await store.AttributesAsync(ct);
        var cats = await store.CategoriesAsync(ct);
        var use = await store.AttrUseAsync(ct);
        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("shop.adm.attrName")), Ui.Column(ctx.T("shop.adm.attrKind")), Ui.Column(ctx.T("shop.adm.attrScope")), Ui.Column(ctx.T("shop.adm.products"), right: true), Ui.Column("", actions: true) },
            attrs.Select(a => Ui.RowFor($"{a.Name} {a.Slug} {a.Options} {a.Category}",
                Ui.Html($"<a href=\"{AttributeUrl(a.Id)}\"><strong>{System.Net.WebUtility.HtmlEncode(a.Name)}</strong></a><div class=\"muted\">{System.Net.WebUtility.HtmlEncode(a.Slug)}"
                    + (a.Kind == AttributeKind.Choice && a.OptionList.Count > 0 ? " · " + System.Net.WebUtility.HtmlEncode(string.Join(", ", a.OptionList.Take(6)) + (a.OptionList.Count > 6 ? " …" : "")) : "") + "</div>"),
                Ui.Inline(Ui.Text(KindLabel(ctx, a.Kind)) with { Inline = true }, a.Filterable ? Ui.Badge(ctx.T("shop.adm.attrFilter"), UiTone.Info) : null),
                a.Category.Length > 0 && cats.FirstOrDefault(c => c.Slug == a.Category) is { } c ? ShopCategories.Label(cats, c) : ctx.T("shop.adm.attrAll"),
                Ui.Text(Intl(use.GetValueOrDefault(a.Id))) with { Inline = true },
                Ui.Inline(
                    Ui.Button(ctx.T("action.edit"), AttributeUrl(a.Id), UiButtonKind.Ghost, small: true),
                    Ui.Action(ctx.T("action.delete"), "delete", Intl(a.Id), UiButtonKind.Danger, confirm: ctx.T("shop.adm.attrDeleteConfirm", a.Name), icon: "ti-trash")))),
            searchPlaceholder: ctx.T("action.search"), emptyText: ctx.T("shop.adm.noAttributes")) with
        {
            Below = new UiNode[] { Ui.Button(ctx.T("shop.adm.newAttribute"), AttributeUrl(0)) }
        };
        return PluginResult.Page(Ui.Stack(Ui.Muted(ctx.T("shop.adm.attrIntro")), table), ctx.T("shop.adm.attributes"));
    }

    public static async Task<PluginResult> AttributeAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        long.TryParse(ctx.Request.R("id"), out var id);
        var current = await store.AttributeAsync(id, ct);
        if (id > 0 && current is null) return PluginResult.Redirect($"{Section}/attributes");
        current ??= new ShopAttribute();
        var cats = await store.CategoriesAsync(ct);
        string? error = null;

        if (ctx.Request.IsPost && ctx.Request.Action == "save")
        {
            var f = ctx.Request;
            var kind = AttributeKind.Normalise(f.F("kind"));
            var options = ShopAttributes.NormaliseOptions(f.F("options"));
            var name = ShopAttributes.CleanValue(f.F("name"));
            var category = f.F("category").Trim();
            current = current with
            {
                Name = name, Kind = kind, Options = options, Unit = ShopAttributes.CleanValue(f.F("unit")), Filterable = f.Form.ContainsKey("filterable"),
                Category = cats.Any(c => c.Slug == category) ? category : "",
                Sort = int.TryParse(f.F("sort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var so) ? Math.Clamp(so, 0, 100000) : 0
            };
            if (name.Length == 0) error = ctx.T("shop.adm.errAttrName");
            else if (kind == AttributeKind.Choice && options.Length == 0) error = ctx.T("shop.adm.errAttrOptions");
            else
            {
                if (id == 0)
                {
                    var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(string.IsNullOrWhiteSpace(f.F("slug")) ? name : f.F("slug"));
                    if (string.IsNullOrWhiteSpace(slug)) slug = "eigenschaft";
                    var baseSlug = slug; var n = 2;
                    while (await store.AttributeBySlugAsync(slug, ct) is not null) slug = baseSlug + "-" + n++;
                    current = current with { Slug = slug };
                }
                await store.SaveAttributeAsync(current, ct);
                return PluginResult.Redirect($"{Section}/attributes", ctx.T("shop.adm.attrSaved"));
            }
        }

        var title = ctx.T(id == 0 ? "shop.adm.newAttribute" : "shop.adm.editAttribute");
        var scopes = new[] { Ui.Option("", ctx.T("shop.adm.attrAll")) }.Concat(ShopCategories.Flatten(cats).Select(t => Ui.Option(t.Cat.Slug, ShopCategories.Label(cats, t.Cat)))).ToArray();
        var main = new List<UiNode?>
        {
            Ui.Card(ctx.T("shop.adm.secAttribute"),
                Ui.Input("name", ctx.T("shop.adm.attrName"), current.Name) with { Required = true },
                Ui.Select("kind", ctx.T("shop.adm.attrKind"), current.Kind,
                    Ui.Option(AttributeKind.Choice, KindLabel(ctx, AttributeKind.Choice)), Ui.Option(AttributeKind.Text, KindLabel(ctx, AttributeKind.Text)),
                    Ui.Option(AttributeKind.Number, KindLabel(ctx, AttributeKind.Number)), Ui.Option(AttributeKind.Bool, KindLabel(ctx, AttributeKind.Bool))),
                Ui.TextArea("options", ctx.T("shop.adm.attrOptions"), current.Options, 6) with { Help = ctx.T("shop.adm.attrOptionsHelp") },
                Ui.Input("unit", ctx.T("shop.adm.attrUnit"), current.Unit) with { Help = ctx.T("shop.adm.attrUnitHelp") })
        };
        var side = new List<UiNode?>
        {
            Ui.Card(ctx.T("shop.adm.secPlace"),
                Ui.Checkbox("filterable", ctx.T("shop.adm.attrFilterable"), current.Filterable || id == 0) with { Help = ctx.T("shop.adm.attrFilterableHelp") },
                Ui.Select("category", ctx.T("shop.adm.attrScope"), current.Category, scopes) with { Help = ctx.T("shop.adm.attrScopeHelp") },
                Ui.Input("sort", ctx.T("shop.adm.sort"), Intl(current.Sort), UiInputType.Number) with { Min = "0" },
                id == 0 ? Ui.Input("slug", ctx.T("shop.adm.slug"), "") with { Help = ctx.T("shop.adm.attrSlugHelp") } : Ui.KeyValue((ctx.T("shop.adm.slug"), current.Slug)))
        };
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, title, Ui.Button(ctx.T("shop.adm.back"), $"{Section}/attributes", UiButtonKind.Ghost, icon: "ti-arrow-left", small: true)),
            error is null ? null : Ui.Alert(error, UiTone.Error),
            Ui.Form(Ui.Hidden("action", "save"), Ui.Columns(main, side), Ui.FormActions(Ui.Submit(ctx.T("action.save")), Ui.Button(ctx.T("action.cancel"), $"{Section}/attributes", UiButtonKind.Ghost)))), title);
    }
}
