using System.Net;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The links to follow a parcel — built only from a fixed https address with the number escaped into it.</summary>
public class ShopTrackingUnitTests
{
    [Fact]
    public void A_known_carrier_and_a_number_make_the_carriers_own_tracking_address()
    {
        Assert.Equal("https://www.dhl.de/de/privatkunden/pakete-empfangen/verfolgen.html?piececode=003404345", ShopTracking.Url("dhl", "003404345"));
        Assert.Equal("https://tracking.dpd.de/parcelstatus?query=012345&locale=de_DE", ShopTracking.Url("dpd", "0123 45"));   // blanks inside a number are dropped
        Assert.Equal("", ShopTracking.Url("dhl", ""));                                                         // no number, nothing to follow
        Assert.Equal("", ShopTracking.Url("", "12345"));                                                       // no carrier, nothing known to link to
        Assert.Equal("", ShopTracking.Url("gibtsnicht", "12345"));
    }

    [Fact]
    public void A_number_cannot_break_out_of_the_address_and_a_full_https_link_is_used_as_it_is()
    {
        var url = ShopTracking.Url("ups", "1Z/&?#<script>");
        Assert.StartsWith("https://www.ups.com/track?loc=de_DE&tracknum=", url);
        Assert.DoesNotContain("<", url); Assert.DoesNotContain("#", url); Assert.DoesNotContain("1Z/&", url);
        Assert.Equal("https://dhl.example/track/42", ShopTracking.Url("dhl", "https://dhl.example/track/42"));
        // Anything that is not an https link is only a number: it is escaped into the carrier's own address, never used as one.
        foreach (var odd in new[] { "javascript:alert(1)//https://x", "http://insecure.example/1" })
        {
            var u = ShopTracking.Url("dhl", odd);
            Assert.StartsWith("https://www.dhl.de/de/privatkunden/pakete-empfangen/verfolgen.html?piececode=", u);
            Assert.DoesNotContain("javascript:", u); Assert.DoesNotContain("http://", u);
        }
    }

    [Fact]
    public void The_operators_own_template_must_be_https_and_say_where_the_number_goes()
    {
        Assert.Equal("https://t.example/?nr=77", ShopTracking.Url(ShopTracking.Custom, "77", "https://t.example/?nr={n}"));
        Assert.Equal("", ShopTracking.Url(ShopTracking.Custom, "77", "http://t.example/?nr={n}"));
        Assert.Equal("", ShopTracking.Url(ShopTracking.Custom, "77", "https://t.example/no-number"));
        Assert.Equal("", ShopTracking.Url(ShopTracking.Custom, "77", ""));
        Assert.Equal("dhl", ShopTracking.Clean(" DHL ")); Assert.Equal("", ShopTracking.Clean("evil")); Assert.Equal("custom", ShopTracking.Clean("custom"));
    }

    [Fact]
    public void The_price_range_is_read_in_euros_with_comma_or_point_and_a_reversed_pair_is_turned_around()
    {
        Assert.Equal(1250L, ShopCatalogUi.Cents("12,5")); Assert.Equal(1250L, ShopCatalogUi.Cents("12.50")); Assert.Null(ShopCatalogUi.Cents("-3")); Assert.Null(ShopCatalogUi.Cents("abc")); Assert.Null(ShopCatalogUi.Cents(""));
        var (lo, hi) = ShopCatalogUi.ReadPrice(k => k == "price.min" ? "20" : k == "price.max" ? "5" : "");
        Assert.Equal((500L, 2000L), (lo, hi));
        Assert.Equal("12.5", ShopCatalogUi.Euros(1250));
    }
}

/// <summary>The catalogue's controls (search field, sort, categories, filters, chips), the tracking link on the customer's pages and the customer area.</summary>
public class ShopCatalogBarAndAccountTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopCatalogBarAndAccountTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);
    private static string Unique(string stem) => $"{stem}-{Guid.NewGuid():N}"[..(stem.Length + 9)];

    [Fact]
    public async Task The_catalogue_has_a_side_list_a_toolbar_with_count_and_sort_and_chips_that_take_one_filter_off_each()
    {
        await _shop.AddProductAsync("Cb Billig", 500); await _shop.AddProductAsync("Cb Mittel", 1500); await _shop.AddProductAsync("Cb Teuer", 5000);
        var v = _app.Browser();

        var page = Decode(await v.GetStringAsync("/shop?q=Cb"));
        Assert.Contains("<h1>Ergebnisse für „Cb“</h1>", page);                                                  // a search says what it is a search for
        Assert.Contains("class=\"shop-find\"", page);                                                           // the search field
        Assert.Contains("class=\"shop-fpanel\"", page);                                                         // the filter area (a page laid out in blocks draws it as one piece)
        Assert.Contains("class=\"shop-toolbar\"", page);
        Assert.Contains("<p class=\"shop-count\">3 Produkte</p>", page);                                        // the count is always there
        Assert.Contains("class=\"shop-sort\"", page);
        Assert.Contains("name=\"price.min\"", page);                                                            // the products differ in price: a price range is offered

        // A price range narrows, and shows up as a chip that removes just it.
        var ranged = Decode(await v.GetStringAsync("/shop?q=Cb&price.min=10&price.max=20"));
        Assert.Contains("Cb Mittel", ranged); Assert.DoesNotContain("Cb Billig", ranged); Assert.DoesNotContain("Cb Teuer", ranged);
        Assert.Contains("<p class=\"shop-count\">1 Produkt</p>", ranged);
        Assert.Contains("class=\"shop-active\"", ranged);
        Assert.Contains("Preis: ", ranged);
        Assert.Contains("href=\"/shop?q=Cb\" rel=\"nofollow\" aria-label=\"Preis:", ranged);                    // the price chip drops the range and keeps the search
        Assert.Contains("href=\"/shop?price.min=10&price.max=20\" rel=\"nofollow\" aria-label=\"Suche: Cb entfernen\"", ranged);   // the search chip drops the words and keeps the range
        Assert.Contains("Filter zurücksetzen", ranged);

        // Nothing matches: a box that says so, with the way back.
        var none = Decode(await v.GetStringAsync("/shop?q=Cb&price.min=100000"));
        Assert.Contains("class=\"shop-nomatch\"", none);
        Assert.DoesNotContain("Cb Billig", none);
        // A bad range is ignored, not an error.
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/shop?price.min=abc&price.max=%27")).StatusCode);
    }

    [Fact]
    public async Task The_default_catalogue_part_has_the_phones_way_to_open_the_filters()
    {
        await _shop.AddProductAsync("Cp Ding", 700);
        await _shop.BlockPagesAsync(false);                                                                      // the part draws the page while the block page is off
        var page = Decode(await _app.Browser().GetStringAsync("/shop?q=Cp"));
        await _shop.BlockPagesAsync(true);
        Assert.Contains("class=\"shop-layout ", page);
        Assert.Contains("for=\"shop-ft\"", page);                                                               // the phone's way to open the filters
        Assert.Contains("class=\"shop-ftoggle\"", page);
    }

    [Fact]
    public async Task The_shops_colours_follow_the_sites_own_variables()
    {
        var css = Decode(await _app.Browser().GetStringAsync("/shop"));
        Assert.Contains("var(--shop-accent,var(--accent,#1a1a1a))", css);                                      // accent: the shop's own variable, else the site's, else a neutral
        Assert.Contains("var(--shop-line,var(--line,", css);
        Assert.Contains("var(--font-head", css);
    }

    [Fact]
    public async Task A_shipped_order_with_a_carrier_gets_a_tracking_link_on_the_customers_page_in_the_account_and_in_the_mail_values()
    {
        await _shop.AddProductAsync("Tr Paket", 2500);
        var email = Unique("trk") + "@example.org";
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MemberService>().CreateAsync(email, "correct horse", "Tina Track");
        var c = _app.Browser();
        var login = await c.GetStringAsync("/anmelden");
        Assert.Equal(HttpStatusCode.Redirect, (await c.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = email, ["password"] = "correct horse", ["__RequestVerificationToken"] = AppFactory.Token(login)! }))).StatusCode);
        await _shop.AddToCartAsync(c, "tr-paket");
        var number = ShopApp.NumberOf(await _shop.CheckoutAsync(c, email: email));
        var order = (await _shop.OrderAsync(number))!;
        var admin = await _shop.AdminAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await _shop.AdminActionAsync(admin, order.Id, "paid")).StatusCode);

        // Before it is shipped there is nothing to follow.
        Assert.DoesNotContain("Sendung verfolgen", Decode(await c.GetStringAsync("/konto/bestellungen")));

        Assert.Equal(HttpStatusCode.Redirect, (await _shop.AdminActionAsync(admin, order.Id, "ship", new Dictionary<string, string> { ["tracking"] = "JD014600003SE", ["carrier"] = "dhl" })).StatusCode);
        var shipped = (await _shop.OrderAsync(number))!;
        Assert.Equal("dhl", shipped.Carrier); Assert.Equal("JD014600003SE", shipped.Tracking);
        var link = "https://www.dhl.de/de/privatkunden/pakete-empfangen/verfolgen.html?piececode=JD014600003SE";

        Assert.Contains($"href=\"{link}\"", Decode(await c.GetStringAsync($"/shop/order/{number}")));            // the order page: carrier, number and the button
        Assert.Contains("DHL", Decode(await c.GetStringAsync($"/shop/order/{number}")));
        var list = Decode(await c.GetStringAsync("/konto/bestellungen"));
        Assert.Contains("class=\"shop-ocard\"", list);                                                          // the account lists orders as cards
        Assert.Contains($"href=\"{link}\"", list);
        Assert.Contains("rel=\"noopener noreferrer\"", list);

        // The mail's values: the number with the link under it, and the link and carrier by themselves.
        var values = await _shop.WithContextAsync((ctx, store) => Task.FromResult(ShopMail.Values(ctx, shipped, Array.Empty<OrderItem>(), "Test")));
        Assert.Equal("JD014600003SE\n" + link, values["tracking"]); Assert.Equal(link, values["tracking_url"]); Assert.Equal("DHL", values["carrier"]);

        // The back office shows it too, and can change it without shipping again.
        Assert.Contains(link.Replace("&", "&amp;"), await admin.GetStringAsync($"/admin/p/shop/orders/{order.Id}"));
        Assert.Equal(HttpStatusCode.Redirect, (await _shop.AdminActionAsync(admin, order.Id, "tracking", new Dictionary<string, string> { ["tracking"] = "99887766", ["carrier"] = "evil" })).StatusCode);
        var changed = (await _shop.OrderAsync(number))!;
        Assert.Equal("99887766", changed.Tracking); Assert.Equal("", changed.Carrier);                           // an unknown carrier is not stored
    }

    [Fact]
    public async Task The_customer_area_is_a_frame_with_the_person_the_navigation_and_stat_tiles_and_the_pages_keep_working()
    {
        var email = Unique("acc") + "@example.org";
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MemberService>().CreateAsync(email, "correct horse", "Max Mustermann");
        var c = _app.Browser();
        var login = await c.GetStringAsync("/anmelden");
        await c.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = email, ["password"] = "correct horse", ["__RequestVerificationToken"] = AppFactory.Token(login)! }));

        var home = Decode(await c.GetStringAsync("/konto"));
        Assert.Contains("class=\"shop-accwho\"", home);
        Assert.Contains("<span class=\"avatar\" aria-hidden=\"true\">MM</span>", home);                         // first letters of first and last name
        Assert.Contains("class=\"shop-accnav\"", home);
        Assert.Contains("aria-current=\"page\"", home);
        Assert.Contains("class=\"shop-stats\"", home);
        Assert.Contains("Noch keine Bestellungen", home);                                                       // the empty state of an account without orders
        Assert.Contains("action=\"/anmelden?handler=Logout\"", home);                                           // the way out is in the frame
        foreach (var url in new[] { "/konto/bestellungen", "/konto/adressen", "/konto/profil" })
        {
            var page = Decode(await c.GetStringAsync(url));
            Assert.Contains("class=\"shop-accnav\"", page);
            Assert.Contains("href=\"" + url + "\" class=\"on\" aria-current=\"page\"", page);                   // the page one is on is marked
        }
        Assert.Contains("shop-danger", Decode(await c.GetStringAsync("/konto/profil")));                        // deleting the account is set apart
        Assert.Equal("MM", ShopAccount.Initials("Max Mustermann", ""));
        Assert.Equal("AN", ShopAccount.Initials("anna", "")); Assert.Equal("J", ShopAccount.Initials("", "jo@example.org"));
    }

    [Fact]
    public async Task A_customer_has_a_page_with_contact_addresses_and_orders_reached_from_the_lists_and_only_by_a_real_address()
    {
        Assert.Equal("anna@example.org", ShopAdminCustomers.FromKey(ShopAdminCustomers.Key(" Anna@Example.org ")));            // the key is the address, lower case
        Assert.Equal("", ShopAdminCustomers.FromKey("%%%")); Assert.Equal("", ShopAdminCustomers.FromKey(Convert.ToBase64String("kein-at"u8.ToArray())));   // anything else is nothing

        await _shop.AddProductAsync("Kd Ding", 1900);
        var email = Unique("kd") + "@example.org";
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "kd-ding");
        var number = ShopApp.NumberOf(await _shop.CheckoutAsync(c, email: email));
        var admin = await _shop.AdminAsync();

        var page = Decode(await admin.GetStringAsync(ShopAdminCustomers.Url(email)));
        Assert.Contains(email, page);
        Assert.Contains(number, page);                                                                         // the order is listed
        Assert.Contains("Rechnungsadresse", page);                                                             // the addresses card
        Assert.Contains("Gast", page);                                                                         // no account
        Assert.Contains("mailto:" + email, page);

        // From the lists and from the order page one gets there.
        var order = (await _shop.OrderAsync(number))!;
        Assert.Contains(ShopAdminCustomers.Url(email), await admin.GetStringAsync("/admin/p/shop/customers"));
        Assert.Contains(ShopAdminCustomers.Url(email), await admin.GetStringAsync("/admin/p/shop/orders"));
        Assert.Contains(ShopAdminCustomers.Url(email), await admin.GetStringAsync($"/admin/p/shop/orders/{order.Id}"));

        // An address nobody ordered with, or a made-up key, goes back to the list instead of showing an empty person.
        Assert.Equal(HttpStatusCode.Redirect, (await admin.GetAsync(ShopAdminCustomers.Url("niemand-" + Guid.NewGuid().ToString("N") + "@example.org"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.GetAsync("/admin/p/shop/customers/%%%")).StatusCode);
        // And it is the back office only.
        Assert.NotEqual(HttpStatusCode.OK, (await _app.Browser().GetAsync(ShopAdminCustomers.Url(email))).StatusCode);
    }

    [Fact]
    public async Task The_variants_editor_makes_combinations_keeps_a_variant_through_a_rename_and_the_tax_is_a_drop_down()
    {
        var admin = await _shop.AdminAsync();
        var form = await admin.GetStringAsync("/admin/p/shop/products/0");
        var page = Decode(form);
        Assert.Contains("data-ui-rows", page);                                                                  // the variants are rows to edit
        Assert.Contains("name=\"opt_values_1\"", page);                                                         // and options to make them from
        Assert.Contains("<option value=\"19\" selected>19 % – Normalsatz</option>", page);                      // the tax is a choice, the home country's rate first
        Assert.Contains("<option value=\"7\">7 % – ermäßigt</option>", page);
        Assert.DoesNotContain("name=\"tax\" value=", page);

        Dictionary<string, string> Post(string token, params (string, string)[] more)
        {
            var d = new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token, ["action"] = "save", ["title"] = "Vk Hemd", ["slug"] = "", ["kind"] = "physical", ["price"] = "20,00", ["tax"] = "19", ["sku"] = "", ["category"] = "", ["summary"] = "",
                ["description"] = "", ["image"] = "", ["gallery"] = "[]", ["stock"] = "0", ["weight"] = "0", ["max"] = "0", ["sort"] = "0", ["active"] = "true", ["variants.count"] = "0"
            };
            foreach (var (k, v) in more) d[k] = v;
            return d;
        }

        // "Kombinationen erzeugen" saves and adds every combination: 2 sizes × 2 colours.
        var made = await admin.PostAsync("/admin/p/shop/products/0", new FormUrlEncodedContent(Post(AppFactory.Token(form)!,
            ("opt_name_1", "Größe"), ("opt_values_1", "S, M"), ("opt_name_2", "Farbe"), ("opt_values_2", "Rot, Blau"), ("generate", "1"))));
        Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
        var location = made.Headers.Location!.OriginalString;
        var id = long.Parse(location[(location.LastIndexOf('/') + 1)..]);
        var variants = await _shop.WithContextAsync((_, s) => s.VariantsAsync(id, true));
        Assert.Equal(new[] { "S / Rot", "S / Blau", "M / Rot", "M / Blau" }, variants.Select(v => v.Name));
        Assert.Equal("Größe", ShopVariants.OptionsFromJson((await _shop.ProductAsync(id)).VariantOptions)[0].Name);                               // the options are remembered

        // The editor shows them again: options as typed, variants as rows with their ids.
        var edit = Decode(await admin.GetStringAsync(location));
        Assert.Contains("value=\"Größe\"", edit); Assert.Contains("value=\"S / Rot\"", edit);
        Assert.Contains($"name=\"variants.0.id\" value=\"{variants[0].Id}\"", edit);
        Assert.Contains("<option value=\"19\" selected>", edit);

        // Generating again adds nothing that is there; a rename by id keeps the variant (and so its stock and its orders), a row left out is switched off.
        var token = AppFactory.Token(await admin.GetStringAsync(location))!;
        var renamed = await admin.PostAsync(location, new FormUrlEncodedContent(Post(token,
            ("variants.count", "2"), ("variants.0.id", variants[0].Id.ToString()), ("variants.0.name", "Klein rot"), ("variants.0.delta", "+1,50"), ("variants.0.stock", "5"), ("variants.0.sku", "KR"),
            ("variants.1.id", variants[1].Id.ToString()), ("variants.1.name", "S / Blau"))));
        Assert.Equal(HttpStatusCode.Redirect, renamed.StatusCode);
        var after = await _shop.WithContextAsync((_, s) => s.VariantsAsync(id, true));
        Assert.Equal(new[] { "Klein rot", "S / Blau" }, after.Select(v => v.Name));
        Assert.Equal(variants[0].Id, after[0].Id);                                                              // the same variant under a new name
        Assert.Equal((150L, true, 5L, "KR"), (after[0].PriceDeltaCents, after[0].TrackStock, after[0].Stock, after[0].Sku));
        Assert.Equal(4, (await _shop.WithContextAsync((_, s) => s.VariantsAsync(id, false))).Count);            // the two left out are switched off, not deleted

        // Options without a value cannot make anything; the page says so and keeps what was typed.
        var none = await admin.PostAsync(location, new FormUrlEncodedContent(Post(token, ("variants.count", "0"), ("opt_name_1", "Nichts"), ("opt_values_1", ""), ("generate", "1"))));
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Contains("mindestens einen Wert", Decode(await none.Content.ReadAsStringAsync()));
    }
}
