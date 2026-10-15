using System.Text;
using System.Text.RegularExpressions;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The shop's arithmetic, parsing and checks — the parts where a wrong cent or a wrong signature is a real loss.</summary>
public class ShopUnitTests
{
    // ---- money ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "EUR", "de", "0,00 €")]
    [InlineData(5, "EUR", "de", "0,05 €")]
    [InlineData(1990, "EUR", "de", "19,90 €")]
    [InlineData(123456789, "EUR", "de", "1.234.567,89 €")]
    [InlineData(1990, "EUR", "en", "€19.90")]
    [InlineData(123456, "USD", "en", "$1,234.56")]
    [InlineData(250, "CHF", "de", "2,50 CHF")]
    [InlineData(-1990, "EUR", "de", "−19,90 €")]
    public void Money_is_formatted_for_a_person_without_depending_on_the_machines_culture(long cents, string currency, string locale, string expected) =>
        Assert.Equal(expected, Money.Format(cents, currency, locale));

    [Theory]
    [InlineData("19,90", 1990)]
    [InlineData("19.90", 1990)]
    [InlineData("19", 1900)]
    [InlineData("19,9", 1990)]
    [InlineData("1.299,00", 129900)]
    [InlineData("1,299.00", 129900)]
    [InlineData("1.299", 129900)]                 // three digits after the only separator: a thousands mark
    [InlineData("1.299.000", 129900000)]
    [InlineData("0,05", 5)]
    [InlineData(" 12,50 € ", 1250)]
    [InlineData("-3,20", -320)]
    [InlineData("0", 0)]
    public void An_amount_somebody_typed_is_read_as_cents(string text, long expected)
    {
        Assert.True(Money.TryParse(text, out var cents));
        Assert.Equal(expected, cents);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-")]
    [InlineData("1.5.00")]
    [InlineData("12,")]
    [InlineData("1,2,3")]
    [InlineData("99999999999999999999")]
    [InlineData("1--2")]
    public void Garbage_is_not_guessed_at(string text) => Assert.False(Money.TryParse(text, out _));

    [Fact]
    public void Parsing_and_formatting_for_an_input_field_round_trip()
    {
        foreach (var cents in new long[] { 0, 1, 99, 100, 1990, 100000, 999999, -250 })
        {
            Assert.True(Money.TryParse(Money.ForInput(cents), out var back));
            Assert.Equal(cents, back);
        }
    }

    [Theory]
    [InlineData(11900, 1900, 1900)]       // 119,00 gross at 19 % holds 19,00 tax
    [InlineData(10700, 700, 700)]
    [InlineData(1000, 1900, 160)]         // 10,00 gross: 1000 * 19/119 = 159.66 → 160
    [InlineData(1, 1900, 0)]
    [InlineData(5000, 0, 0)]
    [InlineData(100, 1950, 16)]           // 19,5 %: 100*1950/11950 = 16.3
    public void Tax_inside_a_gross_amount_is_rounded_to_the_cent(long gross, int bp, long tax) => Assert.Equal(tax, Money.TaxIncluded(gross, bp));

    [Theory]
    [InlineData("19", 1900)] [InlineData("7", 700)] [InlineData("7,5", 750)] [InlineData("19 %", 1900)] [InlineData("0", 0)] [InlineData("19.5", 1950)]
    public void A_percent_is_read_as_hundredths(string text, int expected)
    {
        Assert.True(Money.TryParsePercent(text, out var bp));
        Assert.Equal(expected, bp);
        Assert.Equal(text.StartsWith("19 ") ? "19" : text.Replace('.', ','), text == "19 %" ? "19" : Money.Percent(bp) == text.Replace('.', ',') ? text.Replace('.', ',') : Money.Percent(bp));
    }

    [Theory] [InlineData("")] [InlineData("abc")] [InlineData("-1")] [InlineData("101")]
    public void A_bad_percent_is_refused(string text) => Assert.False(Money.TryParsePercent(text, out _));

    // ---- order status ------------------------------------------------------------------------------------

    [Fact]
    public void Only_the_listed_status_moves_are_allowed()
    {
        var allowed = new HashSet<(string, string)>
        {
            ("awaiting_payment", "paid"), ("awaiting_payment", "cancelled"), ("cancelled", "paid"), ("paid", "shipped"), ("paid", "completed"), ("paid", "refunded"),
            ("shipped", "completed"), ("shipped", "refunded"), ("completed", "refunded")
        };
        foreach (var from in OrderStatus.All)
            foreach (var to in OrderStatus.All)
                Assert.Equal(allowed.Contains((from, to)), OrderStatus.CanMove(from, to));
        // Nothing comes back from a refund, and a paid order is never "unpaid" again.
        Assert.All(OrderStatus.All, s => Assert.False(OrderStatus.CanMove(OrderStatus.Refunded, s)));
        Assert.All(new[] { "paid", "shipped", "completed" }, s => Assert.False(OrderStatus.CanMove(s, OrderStatus.AwaitingPayment)));
    }

    // ---- variants textarea -----------------------------------------------------------------------------------

    [Fact]
    public void A_variants_list_is_parsed_line_by_line_with_optional_parts()
    {
        var list = ShopAdminCatalog.ParseVariants("Größe M | | 12 | TS-M\nGröße L | +2,00 | 8 | TS-L\nXL | -1,50\nEinheitsgröße\n\n  ", out var error);
        Assert.Null(error);
        Assert.Equal(4, list.Count);
        Assert.Equal(("Größe M", 0L, (long?)12, "TS-M"), list[0]);
        Assert.Equal(("Größe L", 200L, (long?)8, "TS-L"), list[1]);
        Assert.Equal(("XL", -150L, (long?)null, ""), list[2]);
        Assert.Equal(("Einheitsgröße", 0L, (long?)null, ""), list[3]);
    }

    [Theory]
    [InlineData("A | abc", "kein Betrag")]
    [InlineData("A | 1 | -3", "keine Stückzahl")]
    [InlineData("A | 1 | x", "keine Stückzahl")]
    [InlineData(" | 1", "Name fehlt")]
    [InlineData("A\nA", "doppelt")]
    [InlineData("A\na", "doppelt")]
    public void A_bad_variants_line_says_which_line_and_why(string text, string reason)
    {
        ShopAdminCatalog.ParseVariants(text, out var error);
        Assert.NotNull(error);
        Assert.Contains(reason, error);
        Assert.Contains("Zeile", error);
    }

    // ---- file names ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Guide v2.pdf", "Guide_v2.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\boot.ini", "boot.ini")]
    [InlineData(".htaccess", "htaccess")]
    [InlineData("", "file")]
    [InlineData("ä ö ü.zip", "_____.zip")]
    public void An_uploaded_files_name_becomes_safe_for_disk_and_headers(string input, string expected) => Assert.Equal(expected, ShopAdminCatalog.SafeFileName(input));

    [Theory]
    [InlineData("https://example.org/a.png", "https://example.org/a.png")]
    [InlineData("/uploads/a.png", "/uploads/a.png")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("//evil.example/x.png", "")]
    [InlineData("/a.png'); background:url(x", "")]
    [InlineData("/a b.png", "")]
    [InlineData("data:image/png;base64,AAAA", "")]
    public void A_picture_address_that_could_break_out_of_markup_or_css_is_dropped(string input, string expected) => Assert.Equal(expected, ShopPublic.SafeUrl(input));

    // ---- Stripe signatures ------------------------------------------------------------------------------------

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"id\":\"evt_1\",\"type\":\"checkout.session.completed\"}");
    private const string Secret = "whsec_test_secret";

    [Fact]
    public void A_correctly_signed_body_is_accepted()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(StripeClient.VerifySignature(StripeClient.Sign(Body, Secret, now), Body, Secret, now));
    }

    [Fact]
    public void A_changed_body_a_wrong_secret_or_a_missing_signature_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        var header = StripeClient.Sign(Body, Secret, now);
        Assert.False(StripeClient.VerifySignature(header, Encoding.UTF8.GetBytes("{\"id\":\"evt_1\",\"type\":\"x\"}"), Secret, now));   // body changed
        Assert.False(StripeClient.VerifySignature(header, Body, "whsec_other", now));                                                        // another secret
        Assert.False(StripeClient.VerifySignature("", Body, Secret, now));
        Assert.False(StripeClient.VerifySignature("garbage", Body, Secret, now));
        Assert.False(StripeClient.VerifySignature("t=,v1=", Body, Secret, now));
        Assert.False(StripeClient.VerifySignature(header, Body, "", now));                                                                   // no secret configured: nothing verifies
        Assert.False(StripeClient.VerifySignature($"t={now.ToUnixTimeSeconds()},v1=zz-not-hex", Body, Secret, now));
        Assert.False(StripeClient.VerifySignature(header.Replace("v1=", "v0="), Body, Secret, now));                                         // only v1 counts
    }

    [Fact]
    public void An_old_signature_is_refused_so_a_recorded_notification_cannot_be_replayed()
    {
        var then = DateTimeOffset.UtcNow.AddMinutes(-10);
        var header = StripeClient.Sign(Body, Secret, then);
        Assert.False(StripeClient.VerifySignature(header, Body, Secret, DateTimeOffset.UtcNow));
        Assert.True(StripeClient.VerifySignature(header, Body, Secret, then.AddMinutes(2)));
        Assert.False(StripeClient.VerifySignature(StripeClient.Sign(Body, Secret, DateTimeOffset.UtcNow.AddMinutes(10)), Body, Secret, DateTimeOffset.UtcNow));   // from the future, too
    }

    [Fact]
    public void Several_signatures_are_fine_when_one_matches()
    {
        var now = DateTimeOffset.UtcNow;
        var good = StripeClient.Sign(Body, Secret, now);
        var header = good.Replace("v1=", "v1=" + new string('0', 64) + ",v1=");
        Assert.True(StripeClient.VerifySignature(header, Body, Secret, now));
    }

    // ---- the strings ------------------------------------------------------------------------------------------

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatCMS.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [Fact]
    public void Every_text_key_the_shop_code_uses_has_a_german_and_an_english_text()
    {
        var have = ShopStrings.All.Select(r => r.Key).ToHashSet();
        var used = new HashSet<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "src", "MatCMS", "Plugin", "Shop"), "*.cs", SearchOption.AllDirectories).Where(f => !f.EndsWith("ShopStrings.cs")))
        {
            var text = File.ReadAllText(file);
            // (In ShopStore, T(...) is the table accessor, not a text.)
            if (!file.EndsWith("ShopStore.cs")) foreach (Match m in Regex.Matches(text, "\\bT\\(\"([^\"]+)\"")) used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, "\"((?:shop|set)\\.[A-Za-z0-9_.]+)\"")) used.Add(m.Groups[1].Value);
        }
        // Names that look like keys but are events or mail keys, not texts.
        used.ExceptWith(new[] { "shop.payment.methods", "shop.payment.paid", "shop.payment.start", "shop.order.placed", "shop.order.paid", "shop.order.shipped", "shop.order.cancelled", "shop.order.refunded", "shop.order.completed", "shop.admin.order" });
        used.RemoveWhere(k => k.EndsWith('.'));                                                     // "shop.status." + status: the dynamic ones are checked in the next test
        Assert.True(used.Count > 200, "the scan found only " + used.Count);
        Assert.Empty(used.Where(k => !have.Contains(k)).OrderBy(k => k));
        Assert.All(ShopStrings.All, r => { Assert.False(string.IsNullOrWhiteSpace(r.De), r.Key); Assert.False(string.IsNullOrWhiteSpace(r.En), r.Key); });
        Assert.Equal(ShopStrings.All.Length, have.Count);                                           // no key twice
        // A text with a placeholder has it in both languages.
        Assert.All(ShopStrings.All, r => Assert.Equal(Regex.Matches(r.De, @"\{\d\}").Select(m => m.Value).Distinct().OrderBy(x => x), Regex.Matches(r.En, @"\{\d\}").Select(m => m.Value).Distinct().OrderBy(x => x)));
    }

    [Fact]
    public void The_dynamic_text_keys_exist()
    {
        var have = ShopStrings.All.Select(r => r.Key).ToHashSet();
        foreach (var s in OrderStatus.All) Assert.Contains("shop.status." + s, have);
        foreach (var p in new[] { "none", "stripe", "bank", "free", "external" }) Assert.Contains("shop.pay." + p, have);
        foreach (var r in new[] { "expired", "payment_failed", "customer", "admin" }) Assert.Contains("shop.cancel." + r, have);
        foreach (var e in new[] { "placed", "paid", "shipped", "delivered", "cancelled", "refund" }) Assert.Contains("shop.event." + e, have);
    }

    [Fact]
    public void The_mail_defaults_only_use_placeholders_they_declare()
    {
        foreach (var spec in ShopMail.Specs)
        {
            var declared = spec.Placeholders.Select(p => p.Token).Concat(spec.Loops.SelectMany(l => l.Fields.Select(f => f.Token))).Concat(spec.Loops.Select(l => l.Name)).ToHashSet();
            foreach (Match m in Regex.Matches(spec.Subject + spec.Body, @"\{\{[#/]?([a-z_]+)\}\}")) Assert.True(declared.Contains(m.Groups[1].Value), $"{spec.Key}: {m.Value} is not declared");
            Assert.StartsWith("shop.", spec.Key);
        }
    }
}
