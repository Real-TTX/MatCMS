using System.Globalization;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The sales report: what was sold in a period, in the way a shop owner and their bookkeeper read it — the sums (gross, VAT, shipping, discounts, refunds), the course over days or months, the best
/// sellers and the split by country, payment method and VAT rate. <see cref="Compute"/> is a pure function over orders and their lines (so it is tested without a database); the page only draws it.
/// A sale counts from the day it was PAID, an order that was later refunded counts with what is left of it, and what was never paid (awaiting payment, cancelled) is not revenue.
/// </summary>
internal static class ShopReports
{
    public sealed record Period(DateOnly From, DateOnly To, string Key)
    {
        public int Days => To.DayNumber - From.DayNumber + 1;
    }

    public static readonly string[] PeriodKeys = { "30d", "month", "lastmonth", "year", "lastyear", "all", "custom" };

    /// <summary>The dates a period key stands for, in local time. An unknown key is the last 30 days; a custom range with a bad or reversed date is corrected, never refused.</summary>
    public static Period Resolve(string? key, string? from, string? to, DateOnly today)
    {
        DateOnly Start(int y, int m) => new(y, m, 1);
        switch ((key ?? "").ToLowerInvariant())
        {
            case "month": return new Period(Start(today.Year, today.Month), today, "month");
            case "lastmonth": { var first = Start(today.Year, today.Month).AddMonths(-1); return new Period(first, first.AddMonths(1).AddDays(-1), "lastmonth"); }
            case "year": return new Period(Start(today.Year, 1), today, "year");
            case "lastyear": return new Period(Start(today.Year - 1, 1), new DateOnly(today.Year - 1, 12, 31), "lastyear");
            case "all": return new Period(new DateOnly(2000, 1, 1), today, "all");
            case "custom":
                {
                    var f = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) ? a : today.AddDays(-29);
                    var t = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b) ? b : today;
                    return f <= t ? new Period(f, t, "custom") : new Period(t, f, "custom");
                }
            default: return new Period(today.AddDays(-29), today, "30d");
        }
    }

    public sealed record Bucket(string Label, int Orders, long GrossCents, long TaxCents);
    public sealed record Row(string Label, long Qty, long Cents, int Orders);
    public sealed record Sales(int Orders, long GrossCents, long ShippingCents, long DiscountCents, long TaxCents, long RefundedCents, long GoodsCents, int Units,
        IReadOnlyList<Bucket> Course, IReadOnlyList<Row> Products, IReadOnlyList<Row> Countries, IReadOnlyList<Row> Payments, IReadOnlyList<Row> TaxRates)
    {
        public long NetCents => GrossCents - TaxCents;
        public long AverageCents => Orders == 0 ? 0 : GrossCents / Orders;
    }

    /// <summary>The day an order counts on: when it was paid, else when it was placed (an old order without a paid date).</summary>
    public static DateTime CountedOn(Order o) => o.PaidUtc ?? o.CreatedUtc;

    public static bool Counts(Order o) => OrderStatus.IsPaid(o.Status) || o.Status == OrderStatus.Refunded && o.PaidUtc is not null;

    public static Sales Compute(IReadOnlyList<Order> orders, IReadOnlyList<OrderItem> items, Period period, int top = 10)
    {
        bool InPeriod(Order o) { var d = DateOnly.FromDateTime(CountedOn(o).ToLocalTime()); return d >= period.From && d <= period.To; }
        var mine = orders.Where(o => Counts(o) && InPeriod(o)).ToList();
        var ids = mine.Select(o => o.Id).ToHashSet();
        var lines = items.Where(i => ids.Contains(i.OrderId)).ToList();

        // What is left of an order after a refund, and the share of its tax that goes with it (a refund takes tax out in proportion).
        long Left(Order o) => o.TotalCents - o.RefundedCents;
        long TaxLeft(Order o) => o.TotalCents <= 0 ? 0 : (long)Math.Round((decimal)o.TaxCents * Left(o) / o.TotalCents, MidpointRounding.AwayFromZero);

        var byMonth = period.Days > 62;
        var course = mine.GroupBy(o => { var d = CountedOn(o).ToLocalTime(); return byMonth ? new DateOnly(d.Year, d.Month, 1) : DateOnly.FromDateTime(d); })
            .OrderBy(g => g.Key).Select(g => new Bucket(g.Key.ToString(byMonth ? "yyyy-MM" : "yyyy-MM-dd", CultureInfo.InvariantCulture), g.Count(), g.Sum(Left), g.Sum(TaxLeft))).ToList();

        var products = lines.GroupBy(i => i.ProductId > 0 ? "p" + i.ProductId : "t" + i.Title)
            .Select(g => new Row(g.First().Title, g.Sum(i => (long)i.Qty), g.Sum(i => i.NetOfDiscountCents), g.Select(i => i.OrderId).Distinct().Count()))
            .OrderByDescending(r => r.Cents).ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase).Take(top).ToList();

        var countries = mine.GroupBy(o => string.IsNullOrWhiteSpace(o.Shipping.Country) ? o.Billing.Country : o.Shipping.Country)
            .Select(g => new Row(g.Key, 0, g.Sum(Left), g.Count())).OrderByDescending(r => r.Cents).ToList();
        var payments = mine.GroupBy(o => o.PaymentProvider).Select(g => new Row(g.Key, 0, g.Sum(Left), g.Count())).OrderByDescending(r => r.Cents).ToList();
        var rates = lines.GroupBy(i => i.TaxBp).OrderByDescending(g => g.Key)
            .Select(g => new Row(g.Key.ToString(CultureInfo.InvariantCulture), 0, g.Sum(i => i.TaxCents), g.Select(i => i.OrderId).Distinct().Count())).ToList();

        return new Sales(mine.Count, mine.Sum(Left), mine.Sum(o => o.ShippingCents), mine.Sum(o => o.DiscountCents), mine.Sum(TaxLeft), mine.Sum(o => o.RefundedCents),
            lines.Sum(i => i.NetOfDiscountCents), (int)lines.Sum(i => (long)i.Qty), course, products, countries, payments, rates);
    }

    // ---- the page ------------------------------------------------------------------------------------------

    private const string Section = ShopAdmin.Section;

    /// <summary>The orders the report may need: everything paid or refunded (a period is cut afterwards, in local time, which a database filter on UTC cannot do exactly).</summary>
    internal static async Task<(IReadOnlyList<Order> Orders, IReadOnlyList<OrderItem> Items)> LoadAsync(ShopStore store, Period period, CancellationToken ct)
    {
        var margin = new DateTime(period.From.Year, period.From.Month, period.From.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-2);
        var q = DataQuery.Everything.Where("status", DataOp.In, new List<string> { OrderStatus.Paid, OrderStatus.Shipped, OrderStatus.Completed, OrderStatus.Refunded }).Where("paid", DataOp.Ge, margin).Page(0, 50000);
        var orders = await store.OrdersWhereAsync(q, ct);
        var items = new List<OrderItem>();
        foreach (var o in orders.Where(Counts)) items.AddRange(await store.ItemsAsync(o.Id, ct));
        return (orders, items);
    }

    public static async Task<PluginResult> ReportAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        string Pick(string k) => ctx.Request.IsPost ? ctx.Request.F(k) : ctx.Request.Q(k);          // the date form posts, the tabs link
        var period = Resolve(Pick("period"), Pick("from"), Pick("to"), DateOnly.FromDateTime(DateTime.Now));
        var (orders, items) = await LoadAsync(store, period, ct);
        var s = Compute(orders, items, period);
        var cur = cfg.Currency;
        string M(long c) => Money.Format(c, cur, "de");

        var tabs = Ui.Tabs(new[] { "30d", "month", "lastmonth", "year", "lastyear", "all" }.Select(k => Ui.Tab(ctx.T("shop.rep.p." + k), $"{Section}/reports?period={k}", period.Key == k)).ToArray());
        var range = Ui.Form(
            Ui.Hidden("period", "custom"),
            Ui.Inline(Ui.Input("from", ctx.T("shop.rep.from"), period.From.ToString("yyyy-MM-dd"), UiInputType.Date), Ui.Input("to", ctx.T("shop.rep.to"), period.To.ToString("yyyy-MM-dd"), UiInputType.Date),
                Ui.Submit(ctx.T("shop.rep.show"), UiButtonKind.Ghost)));

        var tiles = Ui.Stats(
            Ui.Stat(ctx.T("shop.rep.revenue"), M(s.GrossCents), ctx.T("shop.rep.ordersN", s.Orders), UiTone.Success),
            Ui.Stat(ctx.T("shop.rep.net"), M(s.NetCents), ctx.T("shop.rep.taxN", M(s.TaxCents))),
            Ui.Stat(ctx.T("shop.rep.avg"), M(s.AverageCents), ctx.T("shop.rep.unitsN", s.Units)),
            Ui.Stat(ctx.T("shop.rep.refunds"), M(s.RefundedCents), s.DiscountCents > 0 ? ctx.T("shop.rep.discountN", M(s.DiscountCents)) : null, s.RefundedCents > 0 ? UiTone.Warning : UiTone.Default));

        var max = Math.Max(1, s.Course.Count == 0 ? 1 : s.Course.Max(b => b.GrossCents));
        var course = Ui.Table(new[] { Ui.Column(ctx.T(period.Days > 62 ? "shop.rep.month" : "shop.rep.day")), Ui.Column(ctx.T("shop.rep.orders"), right: true), Ui.Column(ctx.T("shop.rep.revenue"), right: true), Ui.Column(ctx.T("shop.rep.tax"), right: true), Ui.Column("") },
            s.Course.Select(b => Ui.Row(b.Label, b.Orders.ToString(CultureInfo.InvariantCulture), M(b.GrossCents), M(b.TaxCents), Ui.Html(Bar(b.GrossCents, max)))), pageSize: 31, emptyText: ctx.T("shop.rep.none"));

        UiNode Split(string title, string firstHeader, IEnumerable<Row> rows, Func<Row, string> label, bool qty = false) =>
            Ui.Card(title, Ui.Table(new[] { Ui.Column(firstHeader), Ui.Column(ctx.T(qty ? "shop.rep.qty" : "shop.rep.orders"), right: true), Ui.Column(ctx.T("shop.rep.revenue"), right: true) },
                rows.Select(r => Ui.Row(label(r), (qty ? r.Qty : r.Orders).ToString(CultureInfo.InvariantCulture), M(r.Cents))), emptyText: ctx.T("shop.rep.none")));

        var exportLink = $"{Section}/data?export=orders&period={period.Key}&from={period.From:yyyy-MM-dd}&to={period.To:yyyy-MM-dd}";
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("shop.rep.title"), Ui.Button(ctx.T("shop.rep.export"), exportLink, UiButtonKind.Ghost, icon: "ti-download", small: true)),
            tabs, Ui.Card(range, Ui.Muted(ctx.T("shop.rep.basis"))), tiles,
            Ui.Card(ctx.T("shop.rep.course"), course),
            Ui.Columns(new UiNode?[]
            {
                Split(ctx.T("shop.rep.top"), ctx.T("shop.item"), s.Products, r => r.Label, qty: true),
                Split(ctx.T("shop.rep.byTax"), ctx.T("shop.rep.rate"), s.TaxRates, r => Money.Percent(int.Parse(r.Label, CultureInfo.InvariantCulture)) + " %")
            }, new UiNode?[]
            {
                Split(ctx.T("shop.rep.byCountry"), ctx.T("shop.country"), s.Countries, r => r.Label.Length == 0 ? "—" : Countries.Name(r.Label, ctx.Locale)),
                Split(ctx.T("shop.rep.byPayment"), ctx.T("shop.cond.method"), s.Payments, r => r.Label.Length == 0 ? "—" : ctx.T("shop.pay." + r.Label))
            })), ctx.T("shop.rep.title"));
    }

    /// <summary>A bar for a table cell, as long as its share of the largest. The colours are the admin's variables (so dark mode follows); the track has a width of its own because an empty table column would squeeze a percentage to nothing.</summary>
    private static string Bar(long cents, long max) =>
        $"<span style=\"display:block;width:10em;max-width:100%;height:.6em;border-radius:3px;background:var(--border-strong)\"><span style=\"display:block;height:100%;width:{Math.Clamp(cents * 100 / max, cents > 0 ? 2 : 0, 100)}%;border-radius:3px;background:var(--ok-fg)\"></span></span>";
}
