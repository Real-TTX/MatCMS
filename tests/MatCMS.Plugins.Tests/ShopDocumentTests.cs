using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MatCMS.Plugin.Shop;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The PDF writer: a file a reader opens, text in the right encoding, as many pages as the content needs.</summary>
public class ShopPdfTests
{
    private static InvoiceDoc Doc(int lines = 2, string seller = "Müller & Söhne (GmbH)", string title = "Kaffee Röstung „Hausmischung“ 250 g", bool small = false) => new()
    {
        Number = "RE-00042", OrderNumber = "1007", IssuedUtc = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc), PaidUtc = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc),
        SellerName = seller, SellerAddress = "Rösterweg 4\n10115 Berlin", SellerEmail = "hallo@example.org", SellerVatId = "DE123456789", Billing = "Erika Muster\nMusterweg 1\n12345 Musterstadt\nDeutschland",
        Shipping = "Erika Muster\nLieferweg 2\n54321 Lieferstadt\nDeutschland",
        Lines = Enumerable.Range(1, lines).Select(i => new DocLine(title, "ART-" + i, 2, 1250, small ? null : 1900, 2500)).ToList(),
        Groups = small ? Array.Empty<TaxGroup>() : new[] { new TaxGroup(1900, 2500L * lines, Money.TaxIncluded(2500L * lines, 1900)) },
        TotalCents = 2500L * lines, SmallBusiness = small, BankInfo = "Bitte überweisen Sie auf\nIBAN DE00 0000 0000 0000 0000 00", Footer = "Geschäftsführer: Max Müller · Amtsgericht Berlin HRB 1"
    };

    private static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    [Fact]
    public void The_file_is_a_well_formed_pdf_whose_cross_reference_table_points_at_every_object()
    {
        var bytes = ShopPdf.Render(Doc());
        var s = Text(bytes);
        Assert.StartsWith("%PDF-1.4\n", s);
        Assert.EndsWith("%%EOF\n", s);
        Assert.All(bytes.Skip(20), b => Assert.True(b < 128));                              // only the header's binary marker is outside ASCII: text is escaped

        // startxref names the xref table; every entry names the object it belongs to.
        var start = long.Parse(Regex.Match(s, @"startxref\n(\d+)\n").Groups[1].Value);
        Assert.StartsWith("xref\n", s[(int)start..]);
        var entries = Regex.Matches(s[(int)start..], @"(\d{10}) 00000 n \n").Select(m => long.Parse(m.Groups[1].Value)).ToList();
        Assert.True(entries.Count >= 7);
        for (var i = 0; i < entries.Count; i++) Assert.StartsWith($"{i + 1} 0 obj", s[(int)entries[i]..]);
        Assert.Contains($"/Size {entries.Count + 1}", s);

        // A stream says how long it is, truthfully.
        foreach (Match m in Regex.Matches(s, @"/Length (\d+) >>\nstream\n"))
        {
            var from = m.Index + m.Length; var len = int.Parse(m.Groups[1].Value);
            Assert.Equal("\nendstream", s.Substring(from + len, 10));
        }
        Assert.Contains("/BaseFont /Helvetica ", s); Assert.Contains("/BaseFont /Helvetica-Bold ", s);
    }

    [Fact]
    public void Text_is_winansi_with_umlauts_the_euro_sign_and_special_characters_escaped()
    {
        var s = Text(ShopPdf.Render(Doc()));
        Assert.Contains("(RE-00042)", s);
        Assert.Contains("M\\374ller & S\\366hne \\(GmbH\\)", s);                            // ü = 374, ö = 366 octal, parentheses escaped
        Assert.Contains("R\\366sterweg", s);
        Assert.Contains("\\204Hausmischung\\223", s);                                       // „ = 204 and “ = 223 octal (0x84 / 0x93)
        Assert.Contains("12,50 \\200", s);                                                  // € is 0x80 = 200 octal
        Assert.Equal((byte)0x80, PdfWriter.WinAnsi('€'));
        Assert.Equal((byte)'-', PdfWriter.WinAnsi('−'));                                    // the minus Money.Format writes
        Assert.Equal((byte)'?', PdfWriter.WinAnsi('Ω'));
        Assert.Equal("(a\\(b\\)\\\\c)", PdfWriter.Literal("a(b)\\c"));
    }

    [Fact]
    public void Widths_follow_the_helvetica_metrics_and_text_wraps_inside_its_width()
    {
        Assert.Equal(6.67, PdfWriter.Width("A", 10, false), 2);                              // 667 / 1000
        Assert.Equal(5.56, PdfWriter.Width("0", 10, false), 2);
        Assert.Equal(6.116, PdfWriter.Width("0", 11, false), 3);
        Assert.Equal(PdfWriter.Width("a", 10, false), PdfWriter.Width("ä", 10, false));     // an umlaut is as wide as its letter
        Assert.True(PdfWriter.Width("Hallo", 10, true) > PdfWriter.Width("Hallo", 10, false));
        var lines = PdfWriter.Wrap("Ein ziemlich langer Satz, der nicht in eine einzige Zeile passt, weil er zu breit ist.", 120, 10, false);
        Assert.True(lines.Count > 3);
        Assert.All(lines, l => Assert.True(PdfWriter.Width(l, 10, false) <= 120.01));
        Assert.Equal("Ein ziemlich langer Satz, der nicht in eine einzige Zeile passt, weil er zu breit ist.", string.Join(" ", lines));
        var cut = PdfWriter.Wrap("Donaudampfschifffahrtsgesellschaftskapitän", 60, 10, false);   // one word wider than the line is cut, not overflowed
        Assert.True(cut.Count > 1);
        Assert.All(cut, l => Assert.True(PdfWriter.Width(l, 10, false) <= 60.01));
        Assert.Equal(new[] { "a", "", "b" }, PdfWriter.Wrap("a\n\nb", 100, 10, false));
    }

    [Fact]
    public void A_long_invoice_runs_over_to_further_pages_each_with_its_header_and_footer_and_the_sums_stay_together()
    {
        var one = Text(ShopPdf.Render(Doc(2)));
        Assert.Contains("/Count 1 ", one);
        Assert.Contains("(Seite 1 / 1)", one);

        var many = Text(ShopPdf.Render(Doc(70)));
        var pages = int.Parse(Regex.Match(many, @"/Count (\d+) ").Groups[1].Value);
        Assert.True(pages >= 3);
        for (var i = 1; i <= pages; i++) Assert.Contains($"(Seite {i} / {pages})", many);
        Assert.Equal(pages, Regex.Matches(many, @"\(Bezeichnung\)").Count);                  // the table header is drawn again on every page
        Assert.Equal(1, Regex.Matches(many, @"\(Gesamtbetrag\)").Count);
    }

    [Fact]
    public void A_small_business_document_has_no_vat_column_and_a_credit_note_has_its_own_title()
    {
        var small = Text(ShopPdf.Render(Doc(small: true)));
        Assert.Contains("(Gem\\344\\337 \\247 19 UStG wird keine Umsatzsteuer berechnet.)", small);
        Assert.DoesNotContain("(MwSt.)", small);

        var credit = Text(ShopPdf.Render(Doc() with { Kind = InvoiceKinds.Credit, Number = "GS-00003", InvoiceNumber = "RE-00042", TotalCents = -1000 }));
        Assert.Contains("(Gutschrift)", credit);
        Assert.Contains("(GS-00003)", credit);
        Assert.Contains("(RE-00042)", credit);                                              // the invoice it corrects
        Assert.Contains("(Erstattungsbetrag)", credit);
        Assert.Contains("(-10,00 \\200)", credit);
        Assert.DoesNotContain("(Leistungs-/Zahldatum:)", credit);

        var english = Text(ShopPdf.Render(Doc() with { En = true }));
        Assert.Contains("(Invoice)", english); Assert.Contains("(Page 1 / 1)", english);
    }

    [Fact]
    public void A_document_is_the_same_in_html_and_pdf_because_both_come_from_one_value()
    {
        var doc = Doc(3);
        var html = Html(doc);
        foreach (var expected in new[] { "RE-00042", "Müller & Söhne (GmbH)", "Rösterweg 4", "DE123456789", "Kaffee Röstung „Hausmischung“ 250 g", "ART-3", "Netto 19 %", "MwSt. 19 %", "75,00 €" })
            Assert.Contains(expected, html);
        Assert.Contains(ShopInvoice.PdfMarker, html);                                       // the route puts the real link in
        Assert.Contains(doc.M(doc.TotalCents), html);
    }

    private static string Html(InvoiceDoc d) => System.Net.WebUtility.HtmlDecode(ShopInvoice.Html(d));
}

/// <summary>A refund after an invoice makes a credit note: its own gapless number, the VAT paid back in proportion, a page and a PDF, and only for who may see the order.</summary>
public class ShopCreditNoteTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopCreditNoteTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    /// <summary>A paid order of one 19 % and one 7 % product (10,00 each) with shipping: total 24,90.</summary>
    private async Task<(Order Order, string Token)> PaidOrderAsync(string tag)
    {
        await _shop.AddProductAsync("Gutschrift Neunzehn " + tag, 1000, taxBp: 1900);
        await _shop.AddProductAsync("Gutschrift Sieben " + tag, 1000, taxBp: 700);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, MatCMS.Pages.Admin.Pages.IndexModel.Slugify("Gutschrift Neunzehn " + tag));
        await _shop.AddToCartAsync(c, MatCMS.Pages.Admin.Pages.IndexModel.Slugify("Gutschrift Sieben " + tag));
        var res = await _shop.CheckoutAsync(c);
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(res)))!;
        await _shop.AdminActionAsync(await _shop.AdminAsync(), order.Id, "paid");
        return ((await _shop.OrderAsync(order.Number))!, ShopApp.TokenOf(res));
    }

    private async Task RefundAsync(long orderId, string amount) =>
        Assert.Equal(HttpStatusCode.Redirect, (await _shop.AdminActionAsync(await _shop.AdminAsync(), orderId, "refund", new Dictionary<string, string> { ["amount"] = amount })).StatusCode);

    private Task<IReadOnlyList<ShopStore.InvoiceRow>> CreditsAsync(long orderId) => _shop.WithContextAsync((_, s) => s.CreditNotesOfOrderAsync(orderId));

    [Fact]
    public async Task The_invoice_comes_as_a_pdf_for_the_key_holder_and_for_nobody_else()
    {
        var (order, token) = await PaidOrderAsync("pdf");
        var visitor = _app.Browser();
        var pdf = await visitor.GetAsync($"/shop/invoice/{order.Number}?t={token}&pdf=1");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Contains($"Rechnung-{order.InvoiceNumber}.pdf", pdf.Content.Headers.ContentDisposition!.ToString());
        Assert.True(pdf.Headers.CacheControl!.NoStore); Assert.True(pdf.Headers.CacheControl.Private);
        var text = Encoding.Latin1.GetString(await pdf.Content.ReadAsByteArrayAsync());
        Assert.Contains($"({order.InvoiceNumber})", text);
        Assert.Contains("(Test Shop GmbH)", text);

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/shop/invoice/{order.Number}?pdf=1")).StatusCode);          // no key
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/shop/invoice/{order.Number}?t=falsch&pdf=1")).StatusCode);

        // The page links to its PDF — with the key, because only the answering route knows it.
        var html = await visitor.GetStringAsync($"/shop/invoice/{order.Number}?t={token}");
        Assert.DoesNotContain(ShopInvoice.PdfMarker, html);
        Assert.Contains($"href=\"/shop/invoice/{order.Number}?pdf=1&amp;t={token}\"", html);
        // …and the customer's order page offers both.
        var page = Decode(await visitor.GetStringAsync($"/shop/order/{order.Number}?t={token}"));
        Assert.Contains($"pdf=1", page);
        Assert.Contains(order.InvoiceNumber, page);
    }

    [Fact]
    public async Task A_full_refund_writes_a_credit_note_for_the_whole_invoice_with_the_vat_of_each_rate_paid_back()
    {
        var (order, token) = await PaidOrderAsync("voll");
        await RefundAsync(order.Id, Money.ForInput(order.TotalCents));
        var credits = await CreditsAsync(order.Id);
        var credit = Assert.Single(credits);
        Assert.Matches(@"^GS-\d{5}$", credit.Number);
        Assert.Equal(order.TotalCents, credit.AmountCents);
        Assert.Equal(InvoiceKinds.Credit, credit.Kind);
        Assert.Contains(credit.Number, (await _shop.EventsAsync(order.Id)).Single(e => e.Kind == "credit").Text);

        var visitor = _app.Browser();
        var html = Decode(await visitor.GetStringAsync($"/shop/invoice/{order.Number}?t={token}&doc={credit.Number}"));
        Assert.Contains("Gutschrift", html);
        Assert.Contains(credit.Number, html);
        Assert.Contains($"zu Rechnung: {order.InvoiceNumber}", html);
        Assert.Contains("−24,90 €", html);
        Assert.Contains("Erstattungsbetrag", html);
        Assert.Contains("Erstattung zu Rechnung " + order.InvoiceNumber + " (19 %)", html);       // one row per rate
        Assert.Contains("Erstattung zu Rechnung " + order.InvoiceNumber + " (7 %)", html);
        // The VAT paid back is the VAT paid in.
        var items = await _shop.ItemsAsync(order.Id);
        var doc = ShopInvoice.BuildCredit(order, items, ShopConfig.From(new EmptySettings()), credit.Number, DateTime.UtcNow, order.InvoiceNumber, order.TotalCents);
        Assert.Equal(-order.TaxCents, doc.Groups.Sum(g => g.TaxCents));
        Assert.Equal(-order.TotalCents, doc.Groups.Sum(g => g.GrossCents));

        var pdf = await visitor.GetAsync($"/shop/invoice/{order.Number}?t={token}&doc={credit.Number}&pdf=1");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Contains($"Gutschrift-{credit.Number}.pdf", pdf.Content.Headers.ContentDisposition!.ToString());
        Assert.Contains("(Gutschrift)", Encoding.Latin1.GetString(await pdf.Content.ReadAsByteArrayAsync()));

        var orderPage = Decode(await visitor.GetStringAsync($"/shop/order/{order.Number}?t={token}"));
        Assert.Contains($"Gutschrift {credit.Number}", orderPage);
        Assert.Equal(OrderStatus.Refunded, (await _shop.OrderAsync(order.Number))!.Status);
    }

    [Fact]
    public async Task Partial_refunds_each_get_their_own_gapless_credit_note_that_together_equal_what_was_refunded()
    {
        var (order, token) = await PaidOrderAsync("teil");
        await RefundAsync(order.Id, "10,00");
        await RefundAsync(order.Id, "5,00");
        var credits = await CreditsAsync(order.Id);
        Assert.Equal(2, credits.Count);
        Assert.Equal(new long[] { 1000, 500 }, credits.Select(c => c.AmountCents).ToArray());
        var n = credits.Select(c => int.Parse(c.Number[3..])).ToList();
        Assert.Equal(n[0] + 1, n[1]);                                                         // no gap
        Assert.Equal(1500, (await _shop.OrderAsync(order.Number))!.RefundedCents);
        Assert.Equal(OrderStatus.Paid, (await _shop.OrderAsync(order.Number))!.Status);        // a part is not a full refund

        // 10,00 spread over the rates in proportion to what each contributed: 19 % had 11,90 (goods + shipping), 7 % had 10,00 → 5,43 + 4,57.
        var html = Decode(await _app.Browser().GetStringAsync($"/shop/invoice/{order.Number}?t={token}&doc={credits[0].Number}"));
        Assert.Contains("−10,00 €", html);
        var doc = ShopInvoice.BuildCredit(order, await _shop.ItemsAsync(order.Id), ShopConfig.From(new EmptySettings()), "GS-X", DateTime.UtcNow, order.InvoiceNumber, 1000);
        Assert.Equal(-1000, doc.Groups.Sum(g => g.GrossCents));
        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Groups, g => Assert.Equal(-Money.TaxIncluded(-g.GrossCents, g.Bp), g.TaxCents));
    }

    [Fact]
    public async Task A_credit_note_is_for_the_orders_key_holder_only_and_never_shows_another_orders_document()
    {
        var (order, token) = await PaidOrderAsync("zugriff");
        var (other, otherToken) = await PaidOrderAsync("fremd");
        await RefundAsync(other.Id, "5,00");
        var otherCredit = (await CreditsAsync(other.Id)).Single();
        var visitor = _app.Browser();

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/shop/invoice/{other.Number}?doc={otherCredit.Number}")).StatusCode);                       // no key
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/shop/invoice/{order.Number}?t={token}&doc={otherCredit.Number}")).StatusCode);              // somebody else's number under my key
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/shop/invoice/{order.Number}?t={token}&doc=GS-99999")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/shop/invoice/{other.Number}?t={otherToken}&doc={otherCredit.Number}")).StatusCode);
        // The invoice's own number works as a document too; asking for the PDF of an order without a stored one is a plain 404, not an error.
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/shop/invoice/{order.Number}?t={token}&doc={order.InvoiceNumber}&pdf=1")).StatusCode);
    }

    [Fact]
    public async Task The_customers_data_export_names_the_coupon_the_discount_and_the_credit_notes()
    {
        var (order, _) = await PaidOrderAsync("export");
        await RefundAsync(order.Id, "4,00");
        var export = await _shop.WithContextAsync((_, s) => ShopAccount.ExportAsync(s, 0, order.Email));
        var mine = export["orders"]!.AsArray().Single(o => o!["number"]!.GetValue<string>() == order.Number)!;
        Assert.Equal(0, mine["discount_cents"]!.GetValue<long>());
        Assert.Equal("", mine["coupon"]!.GetValue<string>());
        var credit = Assert.Single(mine["credit_notes"]!.AsArray())!;
        Assert.Equal(400, credit["amount_cents"]!.GetValue<long>());
        Assert.Matches(@"^GS-\d{5}$", credit["number"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unpaid_order_has_no_invoice_so_a_manual_refund_cannot_write_a_credit_note()
    {
        // Nothing to correct: an order that was never invoiced is not refundable at all (only paid orders are), so no document can come from it.
        await _shop.AddProductAsync("Gutschrift Unbezahlt", 1000);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "gutschrift-unbezahlt");
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!;
        Assert.Empty(await CreditsAsync(order.Id));
        Assert.False(await _shop.WithContextAsync((ctx, _) => ShopOrders.ApplyRefundAsync(ctx, order.Id, 500, "test", "x")));
        Assert.Empty(await CreditsAsync(order.Id));
    }
}

/// <summary>The seller's logo and accent colour on the invoice, the credit note and the PDF.</summary>
public class ShopInvoiceBrandingTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopInvoiceBrandingTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static InvoiceDoc Doc(string accent = "#ff0000", string logo = "/uploads/logo.png") => new()
    {
        Number = "RE-1", OrderNumber = "1", SellerName = "Rostkunst", Billing = "Erika\nMusterweg 1", Accent = accent, Logo = logo,
        Lines = new[] { new DocLine("Kaffee", "K1", 1, 1000, 1900, 1000) }, Groups = new[] { new TaxGroup(1900, 1000, Money.TaxIncluded(1000, 1900)) }, TotalCents = 1000
    };

    private static byte[] Jpeg(int w, int h)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(w, h, SixLabors.ImageSharp.Color.CornflowerBlue);
        using var ms = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsJpeg(image, ms);
        return ms.ToArray();
    }

    [Fact]
    public void The_html_invoice_shows_the_logo_and_takes_its_colour_from_the_accent_and_never_from_junk()
    {
        var html = ShopInvoice.Html(Doc());
        Assert.Contains("<img class=\"logo\" src=\"/uploads/logo.png\" alt=\"Rostkunst\">", html);
        Assert.Contains(":root{--accent:#ff0000}", html);
        Assert.Contains("border-top:2px solid var(--accent)", html);

        var junk = ShopInvoice.Html(Doc(accent: "red;}</style><script>x</script>", logo: "javascript:alert(1)"));
        Assert.Contains(":root{--accent:#111111}", junk);                              // the black the invoice always had
        Assert.DoesNotContain("<script>", junk);
        Assert.DoesNotContain("class=\"logo\"", junk);
        Assert.DoesNotContain("<img", ShopInvoice.Html(Doc(logo: "")));
    }

    [Fact]
    public void The_pdf_carries_the_logo_as_an_image_and_the_accent_as_colour_and_stays_well_formed()
    {
        var plain = Encoding.Latin1.GetString(ShopPdf.Render(Doc()));
        Assert.DoesNotContain("/XObject", plain);
        Assert.Contains("1 0 0 rg", plain);                                            // the title in the accent colour (ff0000)
        Assert.Contains("1 0 0 RG", plain);                                            // and the rules

        var jpeg = Jpeg(120, 60);
        var bytes = ShopPdf.Render(Doc(), new PluginImage(jpeg, 120, 60));
        var s = Encoding.Latin1.GetString(bytes);
        Assert.Contains("/XObject << /Im1 ", s);
        Assert.Contains("/Subtype /Image /Width 120 /Height 60 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", s);
        Assert.Contains("/Im1 Do", s);
        var start = long.Parse(Regex.Match(s, @"startxref\n(\d+)\n").Groups[1].Value);
        var entries = Regex.Matches(s[(int)start..], @"(\d{10}) 00000 n \n").Select(m => long.Parse(m.Groups[1].Value)).ToList();
        for (var i = 0; i < entries.Count; i++) Assert.StartsWith($"{i + 1} 0 obj", s[(int)entries[i]..]);       // the image object is where the table says
        foreach (Match m in Regex.Matches(s, @"/Length (\d+) >>\nstream\n"))
            Assert.Equal("\nendstream", s.Substring(m.Index + m.Length + int.Parse(m.Groups[1].Value), 10));       // and as long as it says (the JPEG bytes survive the round trip)
        var imageObject = Regex.Match(s, @"/DCTDecode /Length (\d+) >>\nstream\n");
        Assert.Equal(jpeg.Length, int.Parse(imageObject.Groups[1].Value));
        Assert.Equal(jpeg, bytes.Skip(imageObject.Index + imageObject.Length).Take(jpeg.Length).ToArray());
    }

    [Fact]
    public async Task A_logo_is_read_from_the_media_library_only_resized_on_white_and_never_as_svg_or_from_elsewhere()
    {
        using var scope = _app.Services.CreateScope();
        var env = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        var uploads = MatCMS.Services.StoragePaths.Uploads(env);
        Directory.CreateDirectory(uploads);
        var png = Path.Combine(uploads, "branding-logo.png");
        using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(800, 400))   // fully transparent
            await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(image, png);
        await File.WriteAllTextAsync(Path.Combine(uploads, "branding-logo.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        try
        {
            var site = new MatCMS.Services.Plugins.PluginSiteFacade(scope.ServiceProvider);
            var logo = await site.LoadImageAsync("/uploads/branding-logo.png", 200);
            Assert.NotNull(logo);
            Assert.Equal((200, 100), (logo!.Width, logo.Height));
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, logo.Jpeg.Take(2).ToArray());
            using var back = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(logo.Jpeg);
            Assert.True(back[100, 50].R > 240 && back[100, 50].G > 240 && back[100, 50].B > 240);                 // transparency became white

            Assert.Null(await site.LoadImageAsync("/uploads/branding-logo.svg"));                                  // SVG is never read
            Assert.Null(await site.LoadImageAsync("/uploads/gibt-es-nicht.png"));
            Assert.Null(await site.LoadImageAsync("/uploads/../appsettings.json"));
            Assert.Null(await site.LoadImageAsync("https://example.org/logo.png"));                                // only the library
            Assert.Null(await site.LoadImageAsync(""));
        }
        finally { File.Delete(png); File.Delete(Path.Combine(uploads, "branding-logo.svg")); }
    }

    [Fact]
    public async Task The_settings_take_a_colour_and_a_picture_address_and_refuse_anything_else()
    {
        var admin = await _shop.AdminAsync();
        async Task<HttpResponseMessage> Save(string accent, string logo)
        {
            var page = await admin.GetStringAsync("/admin/p/shop/_settings?group=set.groupSeller");
            return await admin.PostAsync("/admin/p/shop/_settings?group=set.groupSeller", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "save-settings", ["s_seller.accent"] = accent, ["s_seller.logo"] = logo
            }));
        }
        try
        {
            Assert.Equal(HttpStatusCode.Redirect, (await Save("#FF8800", "/uploads/logo-x.png")).StatusCode);
            var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/p/shop/_settings?group=set.groupSeller"));
            Assert.Contains("type=\"color\"", page);
            Assert.Contains("value=\"#ff8800\"", page);                                   // stored in one spelling
            Assert.Contains("/uploads/logo-x.png", page);

            var bad = await Save("rot", "/uploads/logo-x.png");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
            Assert.Contains("Keine Farbe", WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));
            var badLogo = await Save("#112233", "javascript:alert(1)");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, badLogo.StatusCode);
            Assert.Contains("Keine gültige Bildadresse", WebUtility.HtmlDecode(await badLogo.Content.ReadAsStringAsync()));
        }
        finally { await Save("#111111", ""); }
    }
}
