using System.Globalization;
using System.Text;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// "Import &amp; Export" in the back office: the CSV downloads (orders, order lines, customers, products, variants — for a period and status where that makes sense, German or international
/// notation) and the product import with its dry run. The files themselves are made by <see cref="ShopExport"/>; this page only asks what the owner wants and shows what happened.
/// </summary>
internal static class ShopAdminData
{
    private const string Section = ShopAdmin.Section;
    private static readonly string[] Kinds = { "orders", "items", "customers", "products", "variants" };

    public static async Task<PluginResult> DataAsync(IPluginContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        string Pick(string k) => req.IsPost ? req.F(k) : req.Q(k);
        var store = new ShopStore(ctx.Data);
        var cfg = ShopConfig.From(ctx.Settings);
        ShopExport.ImportReport? report = null;
        UiNode? problem = null;

        var kind = req.IsPost && req.Action == "export" ? req.F("kind") : req.Q("export");
        if (kind.Length > 0)
        {
            if (kind == "template") return File(ShopExport.TemplateBytes(ShopCsv.Of(Pick("format"))), "shop-products-template.csv");
            if (Kinds.Contains(kind)) return await ExportAsync(ctx, store, cfg, kind, Pick, ct);
        }

        if (req.IsPost && req.Action == "import")
        {
            var products = req.File("products"); var variants = req.File("variants");
            if (products is null && variants is null) problem = Ui.Alert(ctx.T("shop.data.noFile"), UiTone.Warning);
            else if ((products?.Length ?? 0) > ShopExport.MaxImportBytes || (variants?.Length ?? 0) > ShopExport.MaxImportBytes) problem = Ui.Alert(ctx.T("shop.data.tooBig"), UiTone.Error);
            else
            {
                var dry = req.F("dry") == "1";
                report = await ShopExport.ImportProductsAsync(ctx, store, cfg, Read(products), Read(variants), dry, ct);
            }
        }

        var period = new[] { "30d", "month", "lastmonth", "year", "lastyear", "all", "custom" };
        var export = Ui.Card(ctx.T("shop.data.export"), Ui.Muted(ctx.T("shop.data.exportHelp")),
            Ui.Form(
                Ui.Hidden("action", "export"),
                Ui.Select("kind", ctx.T("shop.data.what"), "orders", Kinds.Select(k => Ui.Option(k, ctx.T("shop.data.k." + k))).ToArray()),
                Ui.Select("period", ctx.T("shop.data.period"), "month", period.Select(k => Ui.Option(k, ctx.T("shop.rep.p." + k))).ToArray()),
                Ui.Inline(Ui.Input("from", ctx.T("shop.rep.from"), "", UiInputType.Date), Ui.Input("to", ctx.T("shop.rep.to"), "", UiInputType.Date)),
                Ui.Select("status", ctx.T("shop.data.status"), "", new[] { Ui.Option("", ctx.T("shop.data.allStatus")) }.Concat(OrderStatus.All.Select(s => Ui.Option(s, ctx.T("shop.status." + s)))).ToArray()),
                Ui.Select("format", ctx.T("shop.data.format"), "de", Ui.Option("de", ctx.T("shop.data.fmtDe")), Ui.Option("intl", ctx.T("shop.data.fmtIntl"))),
                Ui.Muted(ctx.T("shop.data.periodNote")),
                Ui.FormActions(Ui.Submit(ctx.T("shop.data.download"), UiButtonKind.Primary))) with { Narrow = true });

        var import = Ui.Card(ctx.T("shop.data.import"), Ui.Muted(ctx.T("shop.data.importHelp")),
            Ui.Form(
                Ui.Hidden("action", "import"),
                Ui.File("products", ctx.T("shop.data.fileProducts"), ".csv,text/csv"),
                Ui.File("variants", ctx.T("shop.data.fileVariants"), ".csv,text/csv"),
                Ui.Checkbox("dry", ctx.T("shop.data.dry"), report is null || report.DryRun),
                Ui.FormActions(Ui.Submit(ctx.T("shop.data.run"), UiButtonKind.Primary), Ui.Button(ctx.T("shop.data.template"), $"{Section}/data?export=template", UiButtonKind.Ghost, icon: "ti-file-download")))
                with { Upload = true, Narrow = true },
            Ui.Muted(ctx.T("shop.data.importRules")));

        var body = new List<UiNode?> { Ui.PageHead(null, ctx.T("shop.tab.data")), problem };
        if (report is not null) body.Add(ReportCard(ctx, report));
        body.Add(Ui.Columns(new UiNode?[] { export }, new UiNode?[] { import }));
        return PluginResult.Page(Ui.Stack(body.ToArray()), ctx.T("shop.tab.data"));
    }

    private static UiNode ReportCard(IPluginContext ctx, ShopExport.ImportReport r)
    {
        if (r.Fatal is not null) return Ui.Alert(r.Fatal, UiTone.Error);
        var kids = new List<UiNode?>
        {
            Ui.Alert(ctx.T(r.DryRun ? "shop.data.drySummary" : "shop.data.summary", r.Created, r.Updated, r.Skipped, r.CategoriesMade), r.Skipped > 0 ? UiTone.Warning : UiTone.Success)
        };
        if (r.Ignored.Count > 0) kids.Add(Ui.Muted(ctx.T("shop.data.ignored") + " " + string.Join(", ", r.Ignored)));
        kids.Add(Ui.Table(new[] { Ui.Column(ctx.T("shop.data.row")), Ui.Column(ctx.T("shop.item")), Ui.Column(ctx.T("shop.adm.status")) },
            r.Lines.Select(l => Ui.Row(l.Row.ToString(CultureInfo.InvariantCulture), l.Subject, Ui.Badge(l.Outcome, l.Ok ? UiTone.Success : UiTone.Warning))), pageSize: 50, emptyText: "—"));
        return Ui.Card(ctx.T("shop.data.result"), kids.ToArray());
    }

    private static string? Read(UploadedFile? f)
    {
        if (f is null) return null;
        using var ms = new MemoryStream();
        using (var s = f.OpenRead()) s.CopyTo(ms);
        var bytes = ms.ToArray();
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }          // a spreadsheet's "CSV" in the local code page
    }

    private static PluginResult File(byte[] bytes, string name) => PluginResult.File(bytes, "text/csv; charset=utf-8", name);

    private static async Task<PluginResult> ExportAsync(IPluginContext ctx, ShopStore store, ShopConfig cfg, string kind, Func<string, string> pick, CancellationToken ct)
    {
        var f = ShopCsv.Of(pick("format"));
        var today = DateOnly.FromDateTime(DateTime.Now);
        var period = ShopReports.Resolve(string.IsNullOrEmpty(pick("period")) ? "all" : pick("period"), pick("from"), pick("to"), today);
        var status = pick("status");
        var stamp = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var span = period.Key == "all" ? "" : $"-{period.From:yyyyMMdd}-{period.To:yyyyMMdd}";
        return kind switch
        {
            "orders" => File(await ShopExport.OrdersAsync(store, f, period.From, period.To, status, ct), $"shop-orders{span}.csv"),
            "items" => File(await ShopExport.ItemsAsync(store, f, period.From, period.To, status, ct), $"shop-order-lines{span}.csv"),
            "customers" => File(await ShopExport.CustomersAsync(store, cfg, f, ct), $"shop-customers-{stamp}.csv"),
            "products" => File(await ShopExport.ProductsAsync(store, f, ct), $"shop-products-{stamp}.csv"),
            _ => File(await ShopExport.VariantsAsync(store, f, ct), $"shop-variants-{stamp}.csv")
        };
    }
}
