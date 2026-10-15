using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Customer accounts around the shop (checkout with an account, the account pages, addresses, data export and deletion) and the downloads of digital goods.</summary>
public class ShopAccountTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopAccountTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);
    private static string Unique(string stem) => $"{stem}-{Guid.NewGuid():N}"[..(stem.Length + 9)];

    private async Task<(string Email, string Password)> NewMemberAsync(string name = "Anna Konto")
    {
        var email = Unique("anna") + "@example.org";
        using var scope = _app.Services.CreateScope();
        var (status, _) = await scope.ServiceProvider.GetRequiredService<MemberService>().CreateAsync(email, "correct horse", name);
        Assert.Equal(MemberService.CreateStatus.Created, status);
        return (email, "correct horse");
    }

    private static async Task SignInAsync(HttpClient c, string email, string password)
    {
        var page = await c.GetStringAsync("/anmelden");
        var res = await c.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = email, ["password"] = password, ["__RequestVerificationToken"] = AppFactory.Token(page)! }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private async Task<long> MemberIdAsync(string email)
    {
        using var scope = _app.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteMembers.AsNoTracking().FirstAsync(m => m.Email == email)).Id;
    }

    // ---- account at checkout ------------------------------------------------------------------------------

    [Fact]
    public async Task A_guest_can_create_an_account_while_ordering_and_is_signed_in_with_the_order_in_it()
    {
        await _shop.AddProductAsync("Konto Tasse", 1500);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "konto-tasse");
        var email = Unique("neu") + "@example.org";

        var weak = await _shop.CheckoutAsync(c, email: email, extra: new Dictionary<string, string> { ["create_account"] = "1", ["password"] = "short" });
        Assert.Equal((HttpStatusCode)422, weak.StatusCode);
        Assert.Contains("mindestens 8 Zeichen", Decode(await weak.Content.ReadAsStringAsync()));

        var res = await _shop.CheckoutAsync(c, email: email, extra: new Dictionary<string, string> { ["create_account"] = "1", ["password"] = "a long password" });
        var number = ShopApp.NumberOf(res);
        var order = (await _shop.OrderAsync(number))!;
        Assert.Equal(await MemberIdAsync(email), order.MemberId);                       // the order belongs to the new account
        using (var scope = _app.Services.CreateScope())
            Assert.Null((await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteMembers.AsNoTracking().FirstAsync(m => m.Email == email)).EmailConfirmedAt);   // unconfirmed until the mailed link is followed

        // Signed in at once: the account pages work, and the order is listed.
        var home = Decode(await c.GetStringAsync("/konto"));
        Assert.Contains("Hallo Erika Muster", home);
        Assert.Contains(number, Decode(await c.GetStringAsync("/konto/bestellungen")));
        // The owner opens the order without the link key.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/shop/order/{number}")).StatusCode);
        // The same address cannot be registered again; the customer is told to sign in.
        var c2 = _app.Browser();
        await _shop.AddToCartAsync(c2, "konto-tasse");
        var dup = await _shop.CheckoutAsync(c2, email: email, extra: new Dictionary<string, string> { ["create_account"] = "1", ["password"] = "a long password" });
        Assert.Equal((HttpStatusCode)422, dup.StatusCode);
        Assert.Contains("schon ein Konto", Decode(await dup.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task The_customer_pages_are_for_signed_in_customers_only_and_an_order_belongs_to_its_owner()
    {
        await _shop.AddProductAsync("Besitz Tasse", 1500);
        var (emailA, pwA) = await NewMemberAsync("Anna A");
        var (emailB, pwB) = await NewMemberAsync("Bert B");

        var anon = _app.Browser();
        foreach (var url in new[] { "/konto", "/konto/bestellungen", "/konto/adressen", "/konto/profil", "/konto/export" })
        {
            var res = await anon.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            Assert.StartsWith("/anmelden?returnUrl=", res.Headers.Location!.OriginalString);
        }

        var a = _app.Browser(); await SignInAsync(a, emailA, pwA);
        await _shop.AddToCartAsync(a, "besitz-tasse");
        var res2 = await _shop.CheckoutAsync(a, email: emailA);
        var number = ShopApp.NumberOf(res2); var token = ShopApp.TokenOf(res2);

        var b = _app.Browser(); await SignInAsync(b, emailB, pwB);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/shop/order/{number}")).StatusCode);                 // not B's order
        Assert.DoesNotContain(number, Decode(await b.GetStringAsync("/konto/bestellungen")));
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/shop/order/{number}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/shop/order/{number}?t={token}")).StatusCode);          // the secret link still works for whoever holds it
    }

    [Fact]
    public async Task A_signed_in_customers_checkout_starts_with_their_address_and_can_save_the_new_one()
    {
        await _shop.AddProductAsync("Adressbuch Tasse", 1500);
        var (email, pw) = await NewMemberAsync("Cora Adresse");
        var c = _app.Browser(); await SignInAsync(c, email, pw);
        await _shop.AddToCartAsync(c, "adressbuch-tasse");

        var form = Decode(await c.GetStringAsync("/shop/checkout"));
        Assert.Contains($"value=\"{email}\"", form);
        Assert.Contains("readonly", form);                                                  // the account's own address is not edited here
        Assert.Contains("Cora Adresse", form);
        Assert.Contains("Diese Adresse in meinem Konto speichern", form);

        var res = await _shop.CheckoutAsync(c, email: email, extra: new Dictionary<string, string> { ["save_address"] = "1", ["b_line1"] = "Neue Straße 7" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var list = Decode(await c.GetStringAsync("/konto/adressen"));
        Assert.Contains("Neue Straße 7", list);
        Assert.Contains("Standard", list);                                                  // the first saved address is the default
        // The next checkout starts with it.
        await _shop.AddToCartAsync(c, "adressbuch-tasse");
        Assert.Contains("Neue Straße 7", Decode(await c.GetStringAsync("/shop/checkout")));
    }

    [Fact]
    public async Task Addresses_are_added_edited_made_default_and_deleted_and_only_by_their_owner()
    {
        var (email, pw) = await NewMemberAsync();
        var (emailB, pwB) = await NewMemberAsync();
        var c = _app.Browser(); await SignInAsync(c, email, pw);
        string Token(string html) => AppFactory.Token(html)!;
        Dictionary<string, string> Addr(string html, string label, string street, string id = "0") => new()
        { ["__RequestVerificationToken"] = Token(html), ["action"] = "save", ["id"] = id, ["label"] = label, ["name"] = "Anna", ["line1"] = street, ["line2"] = "", ["zip"] = "10115", ["city"] = "Berlin", ["country"] = "DE", ["phone"] = "" };

        var page = await c.GetStringAsync("/konto/adressen");
        var bad = await c.PostAsync("/konto/adressen", new FormUrlEncodedContent(Addr(page, "x", "")));
        Assert.Contains("Rechnungsadresse", Decode(await bad.Content.ReadAsStringAsync()));                 // the street is missing
        await c.PostAsync("/konto/adressen", new FormUrlEncodedContent(Addr(page, "Zuhause", "Erste 1")));
        await c.PostAsync("/konto/adressen", new FormUrlEncodedContent(Addr(page, "Büro", "Zweite 2")));
        var list = await c.GetStringAsync("/konto/adressen");
        Assert.Contains("Erste 1", Decode(list)); Assert.Contains("Zweite 2", Decode(list));
        var ids = Regex.Matches(list, "name=\"action\" value=\"delete\"><input type=\"hidden\" name=\"id\" value=\"(\\d+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, ids.Count);

        // Another account cannot touch these ids.
        var other = _app.Browser(); await SignInAsync(other, emailB, pwB);
        var otherPage = await other.GetStringAsync("/konto/adressen");
        await other.PostAsync("/konto/adressen", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(otherPage), ["action"] = "delete", ["id"] = ids[0] }));
        Assert.Contains("Erste 1", Decode(await c.GetStringAsync("/konto/adressen")));
        await other.PostAsync("/konto/adressen", new FormUrlEncodedContent(Addr(otherPage, "Gekapert", "Fremd 9", ids[0])));    // an edit of somebody else's id
        Assert.DoesNotContain("Fremd 9", Decode(await c.GetStringAsync("/konto/adressen")));

        // Edit one, make the other the default, delete one.
        await c.PostAsync("/konto/adressen", new FormUrlEncodedContent(Addr(list, "Büro", "Zweite 22", ids[1])));
        Assert.Contains("Zweite 22", Decode(await c.GetStringAsync("/konto/adressen")));
        await c.PostAsync("/konto/adressen", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(list), ["action"] = "default", ["id"] = ids[1] }));
        var after = Decode(await c.GetStringAsync("/konto/adressen"));
        Assert.Matches(@"Büro</strong> <span class=""shop-badge ok"">Standard", after);
        await c.PostAsync("/konto/adressen", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(list), ["action"] = "delete", ["id"] = ids[0] }));
        Assert.DoesNotContain("Erste 1", Decode(await c.GetStringAsync("/konto/adressen")));
    }

    [Fact]
    public async Task A_guests_earlier_orders_join_the_account_when_its_address_is_confirmed_and_not_before()
    {
        await _shop.AddProductAsync("Gast Tasse", 1500);
        var email = Unique("gast") + "@example.org";
        var guest = _app.Browser();
        await _shop.AddToCartAsync(guest, "gast-tasse");
        var number = ShopApp.NumberOf(await _shop.CheckoutAsync(guest, email: email));
        Assert.Equal(0, (await _shop.OrderAsync(number))!.MemberId);

        // The same person registers later (unconfirmed): their account does not show the old order — an unconfirmed address proves nothing.
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MemberService>().CreateAsync(email, "correct horse", "Gast Spät");
        var c = _app.Browser(); await SignInAsync(c, email, "correct horse");
        Assert.DoesNotContain(number, Decode(await c.GetStringAsync("/konto/bestellungen")));

        // They follow the link in the confirmation mail.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SiteMembers.FirstAsync(m => m.Email == email);
            row.ConfirmTokenHash = MemberService.HashToken("confirm-me"); row.ConfirmTokenExpiresAt = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
        }
        await c.GetStringAsync("/anmelden/bestaetigen?token=confirm-me");
        Assert.Contains(number, Decode(await c.GetStringAsync("/konto/bestellungen")));
        Assert.Equal(await MemberIdAsync(email), (await _shop.OrderAsync(number))!.MemberId);
    }

    // ---- profile, export, deletion --------------------------------------------------------------------------------

    [Fact]
    public async Task The_password_is_changed_only_with_the_current_one()
    {
        var (email, pw) = await NewMemberAsync();
        var c = _app.Browser(); await SignInAsync(c, email, pw);
        var page = await c.GetStringAsync("/konto/profil");
        Dictionary<string, string> Post(string current, string next) => new() { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "password", ["current"] = current, ["new"] = next };
        Assert.Contains("stimmt nicht", Decode(await (await c.PostAsync("/konto/profil", new FormUrlEncodedContent(Post("wrong", "a new password")))).Content.ReadAsStringAsync()));
        Assert.Contains("stimmt nicht", Decode(await (await c.PostAsync("/konto/profil", new FormUrlEncodedContent(Post(pw, "short")))).Content.ReadAsStringAsync()));
        Assert.Contains("Passwort wurde geändert", Decode(await (await c.PostAsync("/konto/profil", new FormUrlEncodedContent(Post(pw, "a new password")))).Content.ReadAsStringAsync()));
        var fresh = _app.Browser();
        await SignInAsync(fresh, email, "a new password");
    }

    [Fact]
    public async Task The_customer_downloads_their_data_and_it_holds_their_orders_and_addresses_and_nobody_elses()
    {
        await _shop.AddProductAsync("Export Tasse", 1500);
        var (email, pw) = await NewMemberAsync("Erika Export");
        var (emailB, pwB) = await NewMemberAsync("Bert Anders");
        var c = _app.Browser(); await SignInAsync(c, email, pw);
        await _shop.AddToCartAsync(c, "export-tasse");
        var number = ShopApp.NumberOf(await _shop.CheckoutAsync(c, email: email, extra: new Dictionary<string, string> { ["save_address"] = "1" }));
        var b = _app.Browser(); await SignInAsync(b, emailB, pwB);
        await _shop.AddToCartAsync(b, "export-tasse");
        var numberB = ShopApp.NumberOf(await _shop.CheckoutAsync(b, email: emailB));

        var res = await c.GetAsync("/konto/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("meine-daten.json", res.Content.Headers.ContentDisposition!.FileName);
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal(number, json["orders"]![0]!["number"]!.GetValue<string>());
        Assert.Single(json["orders"]!.AsArray());
        Assert.DoesNotContain(numberB, json.ToJsonString());
        Assert.Equal("Musterweg 1", json["addresses"]![0]!["line1"]!.GetValue<string>());
    }

    [Fact]
    public async Task Deleting_the_account_removes_addresses_and_carts_unlinks_the_orders_but_keeps_them_and_signs_out()
    {
        await _shop.AddProductAsync("Löschen Tasse", 1500);
        var (email, pw) = await NewMemberAsync("Lena Löschen");
        var id = await MemberIdAsync(email);
        var c = _app.Browser(); await SignInAsync(c, email, pw);
        await _shop.AddToCartAsync(c, "loeschen-tasse");
        var number = ShopApp.NumberOf(await _shop.CheckoutAsync(c, email: email, extra: new Dictionary<string, string> { ["save_address"] = "1" }));
        await _shop.AddToCartAsync(c, "loeschen-tasse");                                // an open cart of this account
        Assert.Equal(id, (await _shop.OrderAsync(number))!.MemberId);

        var page = await c.GetStringAsync("/konto/profil");
        Dictionary<string, string> Post(string confirm) => new() { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "delete", ["confirm"] = confirm };
        var wrong = await c.PostAsync("/konto/profil", new FormUrlEncodedContent(Post("someone@else.org")));
        Assert.Contains("stimmt nicht", Decode(await wrong.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/konto")).StatusCode);              // still there

        var done = await c.PostAsync("/konto/profil", new FormUrlEncodedContent(Post(email.ToUpperInvariant())));
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        using (var scope = _app.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteMembers.AnyAsync(m => m.Id == id));
        var order = (await _shop.OrderAsync(number))!;                                  // the order and its invoice data stay: they must be kept by law
        Assert.Equal(0, order.MemberId);
        Assert.Equal("Erika Muster", order.Billing.Name);
        Assert.Empty(await _shop.WithContextAsync((_, s) => s.AddressesAsync(id)));
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/konto")).StatusCode);        // no longer a customer, cookie or not
    }

    [Fact]
    public async Task The_hosts_data_protection_export_collects_what_the_shop_holds()
    {
        await _shop.AddProductAsync("Host Export", 1500);
        var (email, pw) = await NewMemberAsync("Hanna Host");
        var c = _app.Browser(); await SignInAsync(c, email, pw);
        await _shop.AddToCartAsync(c, "host-export");
        var number = ShopApp.NumberOf(await _shop.CheckoutAsync(c, email: email));
        using var scope = _app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MemberService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var export = await svc.ExportAsync(await db.SiteMembers.FirstAsync(m => m.Email == email));
        var shop = export.OfType<JsonObject>().Single(o => o["plugin"]!.GetValue<string>() == "shop");
        Assert.Equal(number, shop["data"]!["orders"]![0]!["number"]!.GetValue<string>());
    }

    // ---- downloads --------------------------------------------------------------------------------------------

    private async Task<(long ProductId, string Slug)> DigitalProductAsync(string title, string fileName = "handbuch.pdf", string content = "%PDF-1.4 secret contents")
    {
        var id = await _shop.AddProductAsync(title, 1900, ProductKind.Digital);
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync($"/admin/p/shop/products/{id}");
        Assert.Contains("type=\"file\"", page);
        var upload = new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(page)!), "__RequestVerificationToken" }, { new StringContent("upload-file"), "action" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", fileName }
        };
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostAsync($"/admin/p/shop/products/{id}", upload)).StatusCode);
        return (id, MatCMS.Pages.Admin.Pages.IndexModel.Slugify(title));
    }

    private async Task<(string Number, string Token, long OrderId, ProductFile File)> PaidDigitalOrderAsync(string title, string content = "%PDF-1.4 secret contents")
    {
        var (pid, slug) = await DigitalProductAsync(title, content: content);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, slug);
        var res = await _shop.CheckoutAsync(c, waiver: true);
        var number = ShopApp.NumberOf(res); var token = ShopApp.TokenOf(res);
        var order = (await _shop.OrderAsync(number))!;
        await _shop.AdminActionAsync(await _shop.AdminAsync(), order.Id, "paid");
        var file = (await _shop.WithContextAsync((_, s) => s.FilesOfProductAsync(pid))).Single();
        return (number, token, order.Id, file);
    }

    [Fact]
    public async Task A_paid_digital_order_delivers_its_file_by_the_secret_link_and_the_file_is_never_publicly_reachable()
    {
        var (number, token, orderId, file) = await PaidDigitalOrderAsync("Download Handbuch");
        Assert.StartsWith("products/", file.StorageName);
        var visitor = _app.Browser();

        // The order page links it; the mail text does too.
        var page = Decode(await visitor.GetStringAsync($"/shop/order/{number}?t={token}"));
        Assert.Contains($"/shop/download/{number}/{file.Id}?t={token}", page);
        Assert.Contains("handbuch.pdf", page);

        var res = await visitor.GetAsync($"/shop/download/{number}/{file.Id}?t={token}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("%PDF-1.4 secret contents", await res.Content.ReadAsStringAsync());
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("handbuch.pdf", res.Content.Headers.ContentDisposition.FileName);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", res.Headers.GetValues("Cache-Control").Single());

        // Not reachable by any address under the web root, whatever the guess.
        foreach (var url in new[] { "/uploads/" + file.StorageName, "/plugin-files/shop/" + file.StorageName, "/plugin-assets/shop/" + Path.GetFileName(file.StorageName), "/appdata/plugin-files/shop/" + file.StorageName })
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(url)).StatusCode);
        Assert.True(File.Exists(Path.Combine(_app.Root, "appdata", "plugin-files", "shop", file.StorageName.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Contains("download", string.Join("|", (await _shop.EventsAsync(orderId)).Select(e => e.Kind)));
    }

    [Fact]
    public async Task A_download_needs_the_right_order_the_right_key_a_file_of_that_order_and_a_paid_order()
    {
        var (number, token, orderId, file) = await PaidDigitalOrderAsync("Schutz Handbuch");
        var (number2, token2, _, file2) = await PaidDigitalOrderAsync("Anderes Handbuch", "other file");
        var v = _app.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync($"/shop/download/{number}/{file.Id}")).StatusCode);                  // no key
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync($"/shop/download/{number}/{file.Id}?t=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync($"/shop/download/{number}/{file2.Id}?t={token}")).StatusCode);       // a file of another order
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync($"/shop/download/{number2}/{file.Id}?t={token}")).StatusCode);       // another order's number with this key
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync($"/shop/download/{number}/abc?t={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync($"/shop/download/{number}/99999?t={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync($"/shop/download/nope/{file.Id}?t={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync($"/shop/download/{number2}/{file2.Id}?t={token2}")).StatusCode);

        // An order that is not paid gives nothing, even with its key: here an order made but not paid.
        var (pid, slug) = await DigitalProductAsync("Unbezahlt Handbuch");
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, slug);
        var res = await _shop.CheckoutAsync(c, waiver: true);
        var unpaidNumber = ShopApp.NumberOf(res);
        var unpaidFile = (await _shop.WithContextAsync((_, s) => s.FilesOfProductAsync(pid))).Single();
        Assert.Equal(HttpStatusCode.Forbidden, (await v.GetAsync($"/shop/download/{unpaidNumber}/{unpaidFile.Id}?t={ShopApp.TokenOf(res)}")).StatusCode);   // told why: not paid
    }

    [Fact]
    public async Task A_download_stops_at_its_limit_its_expiry_and_a_refund_and_the_admin_does_not_use_it_up()
    {
        var (number, token, orderId, file) = await PaidDigitalOrderAsync("Limit Handbuch");
        var v = _app.Browser();
        string Url() => $"/shop/download/{number}/{file.Id}?t={token}";
        await _shop.WithContextAsync(async (ctx, s) =>
        {
            // The limit is two.
            var table = ctx.Data.Table("downloads");
            var row = (await table.QueryAsync(DataQuery.Everything.Eq("order_id", orderId))).Single();
            await table.UpdateAsync(row.Id, new Dictionary<string, object?> { ["max_downloads"] = 2L });
        });
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync(Url())).StatusCode);
        // The administrator's own look does not count.
        var admin = await _shop.AdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/shop/download/{number}/{file.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync(Url())).StatusCode);
        var third = await v.GetAsync(Url());
        Assert.Equal(HttpStatusCode.Gone, third.StatusCode);
        Assert.Contains("Limit", Decode(await third.Content.ReadAsStringAsync()));
        Assert.Equal(2, (await _shop.WithContextAsync((_, s) => s.DownloadsOfOrderAsync(orderId))).Single().Count);   // the refused try took its count back

        // Expiry.
        await _shop.WithContextAsync(async (ctx, s) =>
        {
            var table = ctx.Data.Table("downloads");
            var row = (await table.QueryAsync(DataQuery.Everything.Eq("order_id", orderId))).Single();
            await table.UpdateAsync(row.Id, new Dictionary<string, object?> { ["max_downloads"] = 0L, ["expires"] = DateTime.UtcNow.AddMinutes(-1) });
        });
        Assert.Equal(HttpStatusCode.Gone, (await v.GetAsync(Url())).StatusCode);
        await _shop.WithContextAsync(async (ctx, s) =>
        {
            var table = ctx.Data.Table("downloads");
            var row = (await table.QueryAsync(DataQuery.Everything.Eq("order_id", orderId))).Single();
            await table.UpdateAsync(row.Id, new Dictionary<string, object?> { ["expires"] = DateTime.UtcNow.AddDays(3) });
        });
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync(Url())).StatusCode);

        // A full refund blocks it for good (manual record: the order was bank-paid).
        await _shop.AdminActionAsync(admin, orderId, "refund", new Dictionary<string, string> { ["amount"] = "19,00" });
        Assert.Equal(OrderStatus.Refunded, (await _shop.OrderAsync(number))!.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await v.GetAsync(Url())).StatusCode);          // a refunded order is no longer a paid one
    }

    [Fact]
    public async Task A_download_can_be_resumed_with_a_range_request_and_a_missing_file_is_a_not_found_not_a_crash()
    {
        var (number, token, orderId, file) = await PaidDigitalOrderAsync("Range Handbuch", "0123456789");
        var v = _app.Browser();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/shop/download/{number}/{file.Id}?t={token}");
        req.Headers.Range = new RangeHeaderValue(3, 6);
        var part = await v.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("3456", await part.Content.ReadAsStringAsync());

        File.Delete(Path.Combine(_app.Root, "appdata", "plugin-files", "shop", file.StorageName.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync($"/shop/download/{number}/{file.Id}?t={token}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_product_file_or_the_product_removes_the_file_from_the_private_store()
    {
        var (pid, slug) = await DigitalProductAsync("Aufräumen Handbuch", content: "to be deleted");
        var file = (await _shop.WithContextAsync((_, s) => s.FilesOfProductAsync(pid))).Single();
        var path = Path.Combine(_app.Root, "appdata", "plugin-files", "shop", file.StorageName.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path));
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync($"/admin/p/shop/products/{pid}");
        await admin.PostAsync($"/admin/p/shop/products/{pid}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "delete-file", ["id"] = file.Id.ToString() }));
        Assert.False(File.Exists(path));
        Assert.Empty(await _shop.WithContextAsync((_, s) => s.FilesOfProductAsync(pid)));
    }
}
