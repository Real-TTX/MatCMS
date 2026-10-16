using System.Net;
using System.Text;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>CSV writing/reading, the sales report's arithmetic and the period dates — all pure.</summary>
public class ShopDataUnitTests
{
    [Fact]
    public void German_csv_uses_semicolon_and_decimal_comma_and_international_uses_comma_and_point()
    {
        Assert.Equal("12,50", ShopCsv.Money(1250, ShopCsv.German)); Assert.Equal("12.50", ShopCsv.Money(1250, ShopCsv.International));
        Assert.Equal("-0,05", ShopCsv.Money(-5, ShopCsv.German)); Assert.Equal("0,00", ShopCsv.Money(0, ShopCsv.German));
        Assert.Equal("19", ShopCsv.Percent(1900, ShopCsv.German)); Assert.Equal("7,5", ShopCsv.Percent(750, ShopCsv.German)); Assert.Equal("7.5", ShopCsv.Percent(750, ShopCsv.International));
        Assert.Same(ShopCsv.International, ShopCsv.Of("intl")); Assert.Same(ShopCsv.German, ShopCsv.Of(null));
    }

    [Fact]
    public void Text_that_a_spreadsheet_would_run_as_a_formula_gets_a_quote_in_front_and_separators_are_quoted()
    {
        var f = ShopCsv.German;
        Assert.Equal("'=HYPERLINK(\"x\")", ShopCsv.Text("=HYPERLINK(\"x\")", f).Replace("\"\"", "\"").Trim('"'));
        Assert.StartsWith("'+", ShopCsv.Text("+49 123", f)); Assert.StartsWith("'@", ShopCsv.Text("@sum", f)); Assert.StartsWith("'-", ShopCsv.Text("-1+1", f));
        Assert.Equal("normal", ShopCsv.Text("normal", f));
        Assert.Equal("\"a;b\"", ShopCsv.Text("a;b", f)); Assert.Equal("\"say \"\"hi\"\"\"", ShopCsv.Text("say \"hi\"", f)); Assert.Equal("\"l1\nl2\"", ShopCsv.Text("l1\nl2", f));
        Assert.Equal("a,b", ShopCsv.Text("a,b", f));                               // a comma is no separator here
        Assert.Equal("\"a,b\"", ShopCsv.Text("a,b", ShopCsv.International));
    }

    [Fact]
    public void A_document_starts_with_a_byte_order_mark_and_every_row_ends_in_crlf()
    {
        var bytes = ShopCsv.Document(new[] { new[] { "a", "b" }, new[] { "1", "2" } }, ShopCsv.German);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal("a;b\r\n1;2\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void Parsing_reads_quotes_line_breaks_inside_cells_a_bom_and_finds_the_separator()
    {
        var rows = ShopCsv.Parse("﻿name;note\r\n\"Tasse; blau\";\"zeile1\nzeile2 \"\"x\"\"\"\r\n\r\nzweite;\r\n", out var sep);
        Assert.Equal(';', sep); Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "name", "note" }, rows[0]);
        Assert.Equal("Tasse; blau", rows[1][0]); Assert.Equal("zeile1\nzeile2 \"x\"", rows[1][1]);
        Assert.Equal(new[] { "zweite", "" }, rows[2]);
        var intl = ShopCsv.Parse("a,b\n1,\"2,5\"", out var s2); Assert.Equal(',', s2); Assert.Equal("2,5", intl[1][1]);
        Assert.Equal('\t', ShopCsv.DetectSeparator("a\tb\tc"));
        Assert.Empty(ShopCsv.Parse("", out _)); Assert.Empty(ShopCsv.Parse(";;\r\n  ;\r\n", out _));
    }

    [Fact]
    public void What_is_written_can_be_read_back_unchanged()
    {
        foreach (var f in new[] { ShopCsv.German, ShopCsv.International })
        {
            var cells = new[] { "plain", "mit;Semikolon", "mit,Komma", "mit \"Anführung\"", "zwei\r\nZeilen", "Ünïcödé €" };
            var bytes = ShopCsv.Document(new[] { cells.Select(c => ShopCsv.Text(c, f)) }, f);
            var back = ShopCsv.Parse(Encoding.UTF8.GetString(bytes), out _);
            Assert.Equal(cells.Select(c => c.Replace("\r\n", "\r\n")).ToArray(), back[0]);
        }
    }

    [Fact]
    public void Period_keys_turn_into_dates_and_a_bad_custom_range_is_corrected()
    {
        var today = new DateOnly(2026, 10, 16);
        Assert.Equal((new DateOnly(2026, 10, 1), today), Dates(ShopReports.Resolve("month", null, null, today)));
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), Dates(ShopReports.Resolve("lastmonth", null, null, today)));
        Assert.Equal((new DateOnly(2026, 1, 1), today), Dates(ShopReports.Resolve("year", null, null, today)));
        Assert.Equal((new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)), Dates(ShopReports.Resolve("lastyear", null, null, today)));
        Assert.Equal((new DateOnly(2026, 9, 17), today), Dates(ShopReports.Resolve("unbekannt", null, null, today)));
        Assert.Equal((new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)), Dates(ShopReports.Resolve("custom", "2026-03-31", "2026-03-01", today)));   // reversed
        Assert.Equal((new DateOnly(2026, 9, 17), today), Dates(ShopReports.Resolve("custom", "kaputt", "", today)));
        Assert.Equal(new DateOnly(2026, 1, 1), ShopReports.Resolve("lastmonth", null, null, new DateOnly(2026, 2, 5)).From.AddMonths(0));                      // January
    }

    private static (DateOnly, DateOnly) Dates(ShopReports.Period p) => (p.From, p.To);

    private static Order O(long id, string status, long total, long tax, DateTime paid, string country = "DE", string pay = "stripe", long refunded = 0, long shipping = 0, long discount = 0) =>
        new() { Id = id, Number = "N" + id, Status = status, TotalCents = total, TaxCents = tax, PaidUtc = paid, CreatedUtc = paid, RefundedCents = refunded, ShippingCents = shipping, DiscountCents = discount, PaymentProvider = pay, Shipping = new Address { Country = country } };

    [Fact]
    public void The_report_counts_paid_orders_in_the_period_deducts_refunds_and_ignores_the_unpaid()
    {
        var day = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var orders = new[]
        {
            O(1, OrderStatus.Paid, 11900, 1900, day), O(2, OrderStatus.Completed, 23800, 3800, day.AddDays(1), "AT", "paypal"),
            O(3, OrderStatus.AwaitingPayment, 5000, 800, day), O(4, OrderStatus.Cancelled, 5000, 800, day),
            O(5, OrderStatus.Refunded, 11900, 1900, day, refunded: 5950),                         // half refunded: half of the tax goes with it
            O(6, OrderStatus.Paid, 9999, 1597, day.AddDays(-40))                                  // outside the period
        };
        var items = new[]
        {
            new OrderItem { OrderId = 1, ProductId = 1, Title = "Tasse", Qty = 1, UnitCents = 11900, LineCents = 11900, TaxBp = 1900, TaxCents = 1900 },
            new OrderItem { OrderId = 2, ProductId = 1, Title = "Tasse", Qty = 2, UnitCents = 11900, LineCents = 23800, TaxBp = 1900, TaxCents = 3800 },
            new OrderItem { OrderId = 3, ProductId = 9, Title = "Unbezahlt", Qty = 5, UnitCents = 1000, LineCents = 5000, TaxBp = 1900, TaxCents = 800 }
        };
        var period = new ShopReports.Period(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), "month");
        var s = ShopReports.Compute(orders, items, period);
        Assert.Equal(3, s.Orders);
        Assert.Equal(11900 + 23800 + (11900 - 5950), s.GrossCents);
        Assert.Equal(1900 + 3800 + 950, s.TaxCents);
        Assert.Equal(5950, s.RefundedCents);
        Assert.Equal(s.GrossCents - s.TaxCents, s.NetCents);
        Assert.Equal(s.GrossCents / 3, s.AverageCents);
        var top = Assert.Single(s.Products); Assert.Equal("Tasse", top.Label); Assert.Equal(3, top.Qty); Assert.Equal(2, top.Orders);
        Assert.Equal(2, s.Course.Count); Assert.Equal("2026-10-10", s.Course[0].Label);
        Assert.Equal(new[] { "AT", "DE" }, s.Countries.Select(c => c.Label));
        Assert.Contains(s.Payments, p => p.Label == "paypal" && p.Cents == 23800);
        var rate = Assert.Single(s.TaxRates); Assert.Equal("1900", rate.Label); Assert.Equal(1900 + 3800, rate.Cents);
    }

    [Fact]
    public void A_long_period_is_shown_by_month()
    {
        var d = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var s = ShopReports.Compute(new[] { O(1, OrderStatus.Paid, 1000, 160, d), O(2, OrderStatus.Paid, 2000, 320, d.AddDays(40)) }, Array.Empty<OrderItem>(),
            new ShopReports.Period(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "year"));
        Assert.Equal(new[] { "2026-03", "2026-04" }, s.Course.Select(b => b.Label));
    }
}

/// <summary>Export and import of the shop's data, through the back office and the importer.</summary>
public class ShopDataTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopDataTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Csv(HttpResponseMessage r) => Encoding.UTF8.GetString(r.Content.ReadAsByteArrayAsync().Result).TrimStart('﻿');

    private Task<ShopExport.ImportReport> Import(string? products, string? variants = null, bool dry = false) =>
        _shop.WithContextAsync((ctx, store) => ShopExport.ImportProductsAsync(ctx, store, ShopConfig.From(ctx.Settings), products, variants, dry, default));

    [Fact]
    public async Task The_import_creates_updates_and_creates_missing_categories_and_a_dry_run_changes_nothing()
    {
        var file = "slug;titel;preis;steuer;kategorie;bestand;aktiv;tags\r\nimp-tasse;Import Tasse;12,50;7;Neue Kat;5;ja;Neuware|Bio\r\nimp-tasse2;Zweite;3,00;;;;nein;\r\n";
        var dry = await Import(file, dry: true);
        Assert.True(dry.DryRun); Assert.Equal(2, dry.Created); Assert.Equal(1, dry.CategoriesMade);
        Assert.Null(await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("imp-tasse")));
        Assert.Empty(await _shop.WithContextAsync(async (_, s) => (await s.CategoriesAsync()).Where(c => c.Slug == "neue-kat").ToList()));

        var real = await Import(file);
        Assert.Equal((2, 0, 0, 1), (real.Created, real.Updated, real.Skipped, real.CategoriesMade));
        var p = (await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("imp-tasse")))!;
        Assert.Equal((1250L, 700, "neue-kat", true, 5L), (p.PriceCents, p.TaxBp, p.Category, p.TrackStock, p.Stock));
        Assert.False((await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("imp-tasse2")))!.Active);

        // A second run updates what the file has columns for and leaves the rest alone.
        var again = await Import("slug;preis\r\nimp-tasse;14,00\r\n");
        Assert.Equal((0, 1), (again.Created, again.Updated));
        var p2 = (await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("imp-tasse")))!;
        Assert.Equal(1400, p2.PriceCents); Assert.Equal("Import Tasse", p2.Title); Assert.Equal(5, p2.Stock); Assert.Equal("neue-kat", p2.Category);
    }

    [Fact]
    public async Task Bad_rows_are_skipped_with_a_reason_and_the_good_ones_still_go_in()
    {
        var r = await Import("slug;title;price\r\nok-eins;Eins;1,00\r\nok-eins;Doppelt;2,00\r\nbad-preis;Böse;abc\r\n;;5,00\r\nohne-preis;Ohne;\r\n");
        Assert.Equal(1, r.Created); Assert.Equal(4, r.Skipped);
        Assert.Contains(r.Lines, l => !l.Ok && l.Outcome.Contains("steht schon weiter oben")); Assert.Contains(r.Lines, l => !l.Ok && l.Outcome.Contains("kein Preis"));
        Assert.Contains(r.Lines, l => !l.Ok && l.Outcome.Contains("fehlt der Preis"));
        Assert.Null(await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("bad-preis")));
        Assert.NotNull((await Import("nur eine Kopfzeile")).Fatal);
        Assert.NotNull((await Import("foo;bar\r\n1;2\r\n")).Fatal);                                      // no title or slug column
    }

    [Fact]
    public async Task Imported_text_is_cleaned_and_addresses_cannot_take_a_reserved_word_and_variants_attach_to_their_product()
    {
        var csv = "slug;title;price;description;image\r\ncart;Warenkorb;1,00;<p>ok</p><script>alert(1)</script>;javascript:alert(1)\r\n";
        var r = await Import(csv);
        Assert.Equal(1, r.Created);
        var p = (await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("cart-1")))!;
        Assert.DoesNotContain("<script", p.DescriptionHtml, StringComparison.OrdinalIgnoreCase); Assert.Contains("ok", p.DescriptionHtml); Assert.Equal("", p.Image);

        var v = await Import(null, "product;name;delta;stock;sku\r\ncart-1;Groß;+2,50;4;G1\r\ncart-1;Klein;;;K1\r\nnirgends;X;1;1;\r\n");
        Assert.Equal(2, v.Lines.Count(l => l.Ok)); Assert.Equal(1, v.Skipped);
        var variants = await _shop.WithContextAsync((_, s) => s.VariantsAsync(p.Id));
        Assert.Equal(2, variants.Count); Assert.Equal(250, variants.First(x => x.Name == "Groß").PriceDeltaCents); Assert.True(variants.First(x => x.Name == "Groß").TrackStock);
        // The same file again updates the variant instead of making a second one.
        await Import(null, "product;name;delta\r\ncart-1;Groß;3,00\r\n");
        Assert.Equal(2, (await _shop.WithContextAsync((_, s) => s.VariantsAsync(p.Id))).Count);
    }

    [Fact]
    public async Task Exporting_products_and_importing_the_file_again_changes_nothing_but_the_numbers_stay_exact()
    {
        await Import("slug;title;price;compare;tax;category;extra_categories;tags;stock;active\r\nrt-a;Rundlauf, A;19,90;24,90;19;Roh;Extra1|Extra2;eins|zwei;7;1\r\n");
        var admin = await _shop.AdminAsync();
        var res = await admin.GetAsync("/admin/p/shop/data?export=products");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("attachment", res.Content.Headers.ContentDisposition?.ToString() ?? "");
        var csv = Csv(res);
        Assert.Contains("rt-a;Rundlauf, A;", csv); Assert.Contains("19,90;24,90;19;", csv);
        var before = await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("rt-a"));
        Assert.Equal("eins, zwei", before!.Tags); Assert.Contains("eins|zwei", csv);                          // keywords: "|" in the file, the shop's own list inside
        var report = await Import(csv);
        Assert.Equal(0, report.Skipped);
        var after = (await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("rt-a")))!;
        Assert.Equal((before!.PriceCents, before.CompareCents, before.TaxBp, before.Category, before.ExtraCategories, before.Tags, before.Stock), (after.PriceCents, after.CompareCents, after.TaxBp, after.Category, after.ExtraCategories, after.Tags, after.Stock));
    }

    [Fact]
    public async Task The_order_and_customer_exports_have_the_columns_a_bookkeeper_needs_and_the_international_format_uses_points()
    {
        var id = await _shop.AddProductAsync("Export-Ware", 1999);
        var customer = _app.Browser();
        await _shop.AddToCartAsync(customer, (await _shop.ProductAsync(id)).Slug);
        var order = await _shop.CheckoutAsync(customer, email: "export@example.org");
        Assert.True(order.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK);
        var admin = await _shop.AdminAsync();
        var orders = Csv(await admin.GetAsync("/admin/p/shop/data?export=orders&period=all"));
        var head = orders.Split("\r\n")[0];
        foreach (var col in new[] { "Bestellnummer", "Datum", "Gesamt", "Steuer", "Rechnungsnummer", "Sendungsnummer" }) Assert.Contains(col, head);
        Assert.Contains("export@example.org", orders); Assert.Contains("19,99", orders);
        var intl = Csv(await admin.GetAsync("/admin/p/shop/data?export=orders&period=all&format=intl"));
        Assert.Contains("19.99", intl); Assert.Contains("export@example.org", intl);
        var lines = Csv(await admin.GetAsync("/admin/p/shop/data?export=items&period=all"));
        Assert.Contains("Export-Ware", lines);
        var customers = Csv(await admin.GetAsync("/admin/p/shop/data?export=customers"));
        Assert.Contains("export@example.org", customers);
        // The status filter narrows the file.
        Assert.DoesNotContain("export@example.org", Csv(await admin.GetAsync("/admin/p/shop/data?export=orders&period=all&status=completed")));
    }

    [Fact]
    public async Task The_packing_slip_lists_what_to_pack_for_whom_without_any_price()
    {
        await _shop.AddProductAsync("Slip-Ding", 4242);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "slip-ding", qty: 3);
        var number = ShopApp.NumberOf(await _shop.CheckoutAsync(c, email: "slip@example.org"));
        var order = (await _shop.OrderAsync(number))!;
        var admin = await _shop.AdminAsync();
        var html = await admin.GetStringAsync($"/admin/p/shop/orders/{order.Id}?slip=1");
        Assert.Contains("Lieferschein", html); Assert.Contains(number, html); Assert.Contains("Slip-Ding", html);
        Assert.Contains("<strong>3</strong>", html);
        Assert.DoesNotContain("42,42", html); Assert.DoesNotContain("126,26", html);
        Assert.Contains("Lieferschein", await admin.GetStringAsync($"/admin/p/shop/orders/{order.Id}"));      // the button is on the order page
    }

    [Fact]
    public async Task A_formula_as_a_product_name_is_exported_defanged()
    {
        await Import("slug;title;price\r\nformel;=1+1;1,00\r\n");
        var csv = Csv(await (await _shop.AdminAsync()).GetAsync("/admin/p/shop/data?export=products"));
        Assert.Contains("'=1+1", csv); Assert.DoesNotContain(";=1+1;", csv);
    }

    [Fact]
    public async Task The_data_and_report_pages_render_for_an_admin_and_not_for_a_visitor()
    {
        var admin = await _shop.AdminAsync();
        foreach (var path in new[] { "/admin/p/shop/data", "/admin/p/shop/reports", "/admin/p/shop/reports?period=lastyear", "/admin/p/shop/reports?period=custom&from=2026-01-01&to=2026-12-31" })
        {
            var res = await admin.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
        Assert.Contains("Umsatzbericht", await admin.GetStringAsync("/admin/p/shop/reports"));
        var anon = _app.Browser();
        var no = await anon.GetAsync("/admin/p/shop/data?export=customers");
        Assert.NotEqual(HttpStatusCode.OK, no.StatusCode);
    }
}
