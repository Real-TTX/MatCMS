using System.IO.Compression;
using System.Net;
using System.Text;
using MatCMS.Data;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The shop with the small-business setting: no VAT shown, anywhere.</summary>
public class ShopSmallBusinessTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopSmallBusinessTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app, new Dictionary<string, string> { [Keys.SmallBusiness] = "true" });
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_small_business_shows_no_vat_charges_none_and_says_why_on_the_invoice()
    {
        await _shop.AddProductAsync("Klein Tasse", 2000);
        var visitor = _app.Browser();
        var product = WebUtility.HtmlDecode(await visitor.GetStringAsync("/shop/klein-tasse"));
        Assert.Contains("§ 19 UStG", product);
        Assert.DoesNotContain("inkl. 19 % MwSt.", product);

        await _shop.AddToCartAsync(visitor, "klein-tasse", 1);
        var checkout = WebUtility.HtmlDecode(await visitor.GetStringAsync("/shop/checkout"));
        Assert.Contains("§ 19 UStG", checkout);

        var res = await _shop.CheckoutAsync(visitor);
        var number = ShopApp.NumberOf(res); var token = ShopApp.TokenOf(res);
        var order = (await _shop.OrderAsync(number))!;
        Assert.Equal(0, order.TaxCents);                                                   // a 19 % product, and still no tax in the order
        Assert.True(order.SmallBusiness);
        var items = await _shop.WithContextAsync((_, s) => s.ItemsAsync(order.Id));
        Assert.All(items, i => { Assert.Equal(0, i.TaxBp); Assert.Equal(0, i.TaxCents); });

        await _shop.AdminActionAsync(await _shop.AdminAsync(), order.Id, "paid");
        var invoice = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/shop/invoice/{number}?t={token}"));
        Assert.Contains("Gemäß § 19 UStG", invoice);
        Assert.DoesNotContain("MwSt.", invoice);                                           // no VAT column, no per-rate breakdown
        Assert.Contains("24,90 €", invoice);                                                // 20,00 + 4,90 shipping
    }
}

/// <summary>The shop that takes orders only from customers with an account.</summary>
public class ShopAccountRequiredTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopAccountRequiredTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app, new Dictionary<string, string> { [Keys.GuestCheckout] = "false" });
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Without_guest_orders_the_checkout_makes_the_account_mandatory_and_creates_it_with_the_order()
    {
        await _shop.AddProductAsync("Konto Pflicht", 1000);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "konto-pflicht");
        var page = WebUtility.HtmlDecode(await c.GetStringAsync("/shop/checkout"));
        Assert.Contains("legen wir ein Kundenkonto an", page);
        Assert.DoesNotContain("name=\"create_account\" value=\"1\"> <span>", page);     // no checkbox to untick: it is not optional
        Assert.Contains("type=\"password\"", page);

        // Without a password it does not go through…
        var no = await _shop.CheckoutAsync(c, email: "pflicht@example.org");
        Assert.Equal((HttpStatusCode)422, no.StatusCode);
        Assert.Contains("mindestens 8 Zeichen", WebUtility.HtmlDecode(await no.Content.ReadAsStringAsync()));
        // …with one, the account is made and the order is its.
        var ok = await _shop.CheckoutAsync(c, email: "pflicht@example.org", extra: new Dictionary<string, string> { ["create_account"] = "1", ["password"] = "long enough pw" });
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(ok)))!;
        Assert.True(order.MemberId > 0);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/konto")).StatusCode);
    }
}

/// <summary>Switching the shop off and on, and backing it up.</summary>
public class ShopSwitchAndBackupTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopSwitchAndBackupTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SetEnabledAsync(bool on)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Plugins.FirstAsync(p => p.Key == "shop");
        row.Enabled = on;
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
    }

    [Fact]
    public async Task A_shop_that_is_off_has_no_pages_no_routes_and_no_menu_entry_and_its_data_waits_for_the_day_it_is_on_again()
    {
        var pid = await _shop.AddProductAsync("Schalter Tasse", 1500);
        var c = _app.Browser();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/shop/schalter-tasse")).StatusCode);

        await SetEnabledAsync(false);
        try
        {
            foreach (var url in new[] { "/shop", "/shop/schalter-tasse", "/shop/cart", "/shop/checkout", "/konto", "/shop/webhook/stripe" })
                Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync(url)).StatusCode);
            var admin = await _shop.AdminAsync();
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/admin/p/shop")).StatusCode);
            Assert.DoesNotContain("/admin/p/shop", await admin.GetStringAsync("/admin"));
            Assert.DoesNotContain("/shop/schalter-tasse", await c.GetStringAsync("/sitemap.xml"));
        }
        finally { await SetEnabledAsync(true); }

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/shop/schalter-tasse")).StatusCode);        // everything is where it was
        Assert.Equal("Schalter Tasse", (await _shop.ProductAsync(pid)).Title);
    }

    [Fact]
    public async Task A_site_with_the_store_package_has_the_shop_switched_off_and_running_signed_without_the_native_switch()
    {
        using var f = new AppFactory();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Plugins.AsNoTracking().FirstAsync(p => p.Key == "shop");
        Assert.False(row.Enabled);
        // The shop left the image (Release B): it is the MatCMS Store's signed native package, and its signature — not "Native Plugins erlauben" — lets it run.
        Assert.Equal(MatCMS.Models.PluginKind.Native, MatCMS.Models.PluginKind.Normalize(row.Kind));
        Assert.True(MatCMS.Services.Plugins.NativeTrust.IsSigned(scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(), row));
        Assert.False(await db.SiteSettings.AnyAsync(s => s.Key == SettingKeys.AllowNativePlugins && s.Value == "1"));
        Assert.False(await db.Plugins.AsNoTracking().AnyAsync(p => p.Key == "blog"));                   // the blog left the image: a fresh site has none
        var visitor = f.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/shop")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/konto")).StatusCode);
    }

    [Fact]
    public async Task An_operator_switches_the_shop_on_and_off_in_the_plugin_list()
    {
        using var f = new AppFactory();
        var admin = f.Browser();
        await AppFactory.LoginAsync(admin);
        var list = await admin.GetStringAsync("/admin/plugins");
        Assert.Contains("Shop", list);
        int id;
        using (var scope = f.Services.CreateScope()) id = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Plugins.AsNoTracking().FirstAsync(p => p.Key == "shop")).Id;

        async Task PostAsync(string handler)
        {
            var page = await admin.GetStringAsync("/admin/plugins");
            var res = await admin.PostAsync($"/admin/plugins?handler={handler}&id={id}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)! }));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        }
        var visitor = f.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/shop")).StatusCode);
        await PostAsync("Toggle");
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/shop")).StatusCode);                  // on: the catalogue answers
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/p/shop")).StatusCode);            // and the back office has its section
        await PostAsync("Toggle");
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/shop")).StatusCode);            // off again
        using (var scope = f.Services.CreateScope()) Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Plugins.AnyAsync(p => p.Key == "shop"));
    }

    [Fact]
    public async Task A_site_whose_image_dropped_the_shop_before_the_package_came_keeps_its_data_and_settings_and_says_so_and_the_package_takes_them_over()
    {
        using var f = new AppFactory();
        int id;
        // The state after the update to Release B on a site that ran the built-in shop: its row is still "builtin", on, with its settings and a table of its own.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Plugins.FirstAsync(p => p.Key == "shop");
            row.Kind = MatCMS.Models.PluginKind.BuiltIn; row.Entry = ""; row.Sha256 = ""; row.CodeSignature = ""; row.Version = "1.0";
            row.Enabled = true; row.ConfigJson = "{\"seller.name\":\"Alte Werkstatt\"}";
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS p_shop_legacy_probe (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO p_shop_legacy_probe (v) VALUES ('bleibt');");
            id = row.Id;
            await scope.ServiceProvider.GetRequiredService<MatCMS.Services.PluginRunner>().RunAllAsync();
            var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
            Assert.False(registry.IsLoaded("shop"));                                    // nothing of it runs …
            Assert.False(registry.Errors.ContainsKey(id));                              // … and that is a state, not a fault
        }
        var visitor = f.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/shop")).StatusCode);

        var admin = f.Browser();
        await AppFactory.LoginAsync(admin);
        await admin.PostAsync("/set-language?culture=de", new StringContent(""));
        var list = await admin.GetStringAsync("/admin/plugins");
        Assert.Contains("data-plugin-gone=\"shop\"", list);
        Assert.Contains("Nicht mehr im Image", System.Net.WebUtility.HtmlDecode(list));
        Assert.Contains("aus dem Store installieren oder ein Paket importieren", System.Net.WebUtility.HtmlDecode(list));
        Assert.Contains("data-plugin-gone=\"shop\"", await admin.GetStringAsync($"/admin/plugins/builtin/{id}"));
        await admin.PostAsync("/set-language?culture=en", new StringContent(""));
        Assert.Contains("No longer in the image", System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/plugins")));

        // The store's signed package arrives (a hand import is enough): it takes the row over — on stays on, the settings stay, the data is untouched.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var env = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            var (plugin, updated, error) = await PluginPackager.ImportAsync(SignedShopBundle(), env, db);
            Assert.Null(error);
            Assert.True(updated);
            Assert.Equal(id, plugin!.Id);
            Assert.Equal(MatCMS.Models.PluginKind.Native, plugin.Kind);
            Assert.True(plugin.Enabled);
            Assert.Equal("{\"seller.name\":\"Alte Werkstatt\"}", plugin.ConfigJson);
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(id);
            Assert.True(scope.ServiceProvider.GetRequiredService<PluginRegistry>().IsLoaded("shop"));
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT v FROM p_shop_legacy_probe";
            Assert.Equal("bleibt", (string?)await cmd.ExecuteScalarAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/shop")).StatusCode);
        Assert.DoesNotContain("data-plugin-gone=\"shop\"", await admin.GetStringAsync("/admin/plugins"));
    }

    /// <summary>The shop's package as the store hands it out: the built DLL under src/, the manifest, and the code signature (test key).</summary>
    private static MemoryStream SignedShopBundle()
    {
        var dll = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "shop", "Shop.dll"));
        var tmp = Path.Combine(Path.GetTempPath(), "matcms-shop-sign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string signature;
        try
        {
            File.WriteAllBytes(Path.Combine(tmp, "Shop.dll"), dll);
            signature = TestStoreKey.Sign("shop", "2.0", tmp);
        }
        finally { Directory.Delete(tmp, true); }
        var manifest = new System.Text.Json.Nodes.JsonObject
        {
            ["Format"] = 2, ["Name"] = "Shop", ["Key"] = "shop", ["Version"] = "2.0", ["Description"] = "", ["Code"] = MatCMS.Shared.PluginBundle.NativeGuardCode, ["Mapping"] = "",
            ["Kind"] = "native", ["Api"] = typeof(IPlugin).Assembly.GetName().Version!.Major.ToString(), ["Entry"] = "Shop.dll",
            ["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dll)).ToLowerInvariant(), ["CodeSignature"] = signature
        };
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("plugin.json").Open())) w.Write(manifest.ToJsonString());
            using (var s = zip.CreateEntry("src/Shop.dll").Open()) s.Write(dll);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task A_backup_carries_the_shops_tables_and_private_files_and_a_restore_on_another_site_brings_orders_invoices_and_downloads_back()
    {
        // A shop with a physical product, a digital one with a file, and a paid digital order.
        await _shop.AddProductAsync("Backup Lampe", 3000, trackStock: true, stock: 7);
        var digital = await _shop.AddProductAsync("Backup Heft", 1900, ProductKind.Digital);
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync($"/admin/p/shop/products/{digital}");
        await admin.PostAsync($"/admin/p/shop/products/{digital}", new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(page)!), "__RequestVerificationToken" }, { new StringContent("upload-file"), "action" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("BACKED UP CONTENT")), "file", "heft.pdf" }
        });
        var buyer = _app.Browser();
        await _shop.AddToCartAsync(buyer, "backup-heft");
        var res = await _shop.CheckoutAsync(buyer, waiver: true);
        var number = ShopApp.NumberOf(res); var token = ShopApp.TokenOf(res);
        var order = (await _shop.OrderAsync(number))!;
        await _shop.AdminActionAsync(admin, order.Id, "paid");
        var file = (await _shop.WithContextAsync((_, s) => s.FilesOfProductAsync(digital))).Single();
        var invoiceNumber = (await _shop.OrderAsync(number))!.InvoiceNumber;

        byte[] backup;
        using (var scope = _app.Services.CreateScope())
            backup = await scope.ServiceProvider.GetRequiredService<ContentTransferService>().ExportAsync(new ContentTransferService.BackupOptions());
        using (var zip = new ZipArchive(new MemoryStream(backup), ZipArchiveMode.Read))
        {
            Assert.NotNull(zip.GetEntry("plugin-files/shop/" + file.StorageName));
            using var r = new StreamReader(zip.GetEntry("content.json")!.Open());
            var json = await r.ReadToEndAsync();
            Assert.Contains(number, json);                                                  // the order is in the backup…
            Assert.Contains("Backup Heft", json);
            Assert.DoesNotContain("BACKED UP CONTENT", json);                               // …the file only as a file, not inside the data
        }

        // A second site that has the shop on and nothing in it.
        using var other = new AppFactory();
        var shop2 = await ShopApp.StartAsync(other);
        using (var scope = other.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ContentTransferService>().ImportAsync(backup);

        var restored = (await shop2.OrderAsync(number))!;
        Assert.Equal(OrderStatus.Shipped, restored.Status);
        Assert.Equal(invoiceNumber, restored.InvoiceNumber);
        Assert.Equal(1900, restored.TotalCents);
        Assert.Equal("Backup Lampe", (await shop2.WithContextAsync((_, s) => s.ProductBySlugAsync("backup-lampe")))!.Title);
        var visitor = other.Browser();
        Assert.Contains("Backup Lampe", WebUtility.HtmlDecode(await visitor.GetStringAsync("/shop")));
        // The download works on the new site, with the same link.
        var dl = await visitor.GetAsync($"/shop/download/{number}/{file.Id}?t={token}");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("BACKED UP CONTENT", await dl.Content.ReadAsStringAsync());
        // The invoice too.
        Assert.Contains(invoiceNumber, WebUtility.HtmlDecode(await visitor.GetStringAsync($"/shop/invoice/{number}?t={token}")));
        // Order numbers carry on where they were: the next order does not repeat one.
        await shop2.AddProductAsync("Danach Tasse", 500);
        var v2 = other.Browser();
        await shop2.AddToCartAsync(v2, "danach-tasse");
        var next = long.Parse(ShopApp.NumberOf(await shop2.CheckoutAsync(v2)));
        Assert.True(next > long.Parse(number));
    }
}
