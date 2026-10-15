using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using MatCMS.Services.Plugins;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The shop, as a plugin that ships with the CMS and is OFF until an operator switches it on: products (physical and digital, with variants and stock), a cart and
/// checkout, payment by Stripe or bank transfer, orders with their history, invoices, downloads, customer accounts and everything the back office needs to run it.
/// <para>It is written against the same API as every other plugin — and that API grew for it: customer accounts, a raw-body webhook route, cookies, atomic counters,
/// private files, plugin mail templates. Nothing in the core knows the shop.</para>
/// </summary>
public sealed class ShopPlugin : IPlugin
{
    /// <summary>The plugin's identity in the list of built-in plugins. Off on a new site: a site that does not sell anything carries none of this.</summary>
    public static BuiltInPlugin Definition { get; } = new("shop", "Shop",
        "Produkte (physisch und digital), Warenkorb, Kasse mit Stripe und Vorkasse, Bestellungen, Rechnungen, Downloads und Kundenkonto.", "1.0", () => new ShopPlugin(), EnabledOnNewSite: false);


    /// <inheritdoc />
    public void Configure(IPluginBuilder b)
    {
        ShopStrings.Register(b);
        ShopStore.Declare(b.Data);
        ShopConfig.Declare(b.Settings);
        foreach (var spec in ShopMail.Specs) b.MailTemplate(spec);

        // ---- public pages --------------------------------------------------------------------------------
        // Forms of a visitor carry the antiforgery token (a customer's browser must not be made to order or change anything by another page); a page with a form is never
        // cached for that reason. Reading a catalogue page needs none.
        var get = new PublicRouteOptions { Methods = PluginMethod.Get };
        // Real customers post here, and several may share one address; 120 a minute is generous for a person and still a stop for a flood.
        var form = new PublicRouteOptions { Antiforgery = true, PostsPerMinute = 120 };
        var customer = new PublicRouteOptions { Antiforgery = true, RequireMember = true, PostsPerMinute = 120 };
        b.Route("/shop", ShopPublic.CatalogAsync, get);
        b.Route("/shop/cart", ShopPublic.CartAsync, form);
        b.Route("/shop/checkout", ShopPublic.CheckoutAsync, form);
        b.Route("/shop/order/{number}", ShopPublic.OrderAsync, form);
        b.Route("/shop/invoice/{number}", ShopPublic.InvoiceAsync, get);
        b.Route("/shop/download/{number}/{fileId}", ShopDownloads.ServeAsync, get);
        b.Route("/shop/webhook/stripe", StripePayments.WebhookAsync, new PublicRouteOptions { Webhook = true });
        b.Route("/shop/webhook/paypal", PayPalPayments.WebhookAsync, new PublicRouteOptions { Webhook = true });
        b.Route("/shop/{slug}", ShopPublic.ProductAsync, form);
        b.Route("/konto", ShopAccount.HomeAsync, customer);
        b.Route("/konto/bestellungen", ShopAccount.OrdersAsync, customer);
        b.Route("/konto/adressen", ShopAccount.AddressesAsync, customer);
        b.Route("/konto/profil", ShopAccount.ProfileAsync, customer);
        b.Route("/konto/export", ShopAccount.ExportDownloadAsync, new PublicRouteOptions { Methods = PluginMethod.Get, RequireMember = true });

        // Blocks for pages (a list of products, one product) and the lists their editor draws from.
        ShopBlocks.Register(b);

        // ---- back office ---------------------------------------------------------------------------------
        b.Admin.Section("shop.nav", "ti-shopping-cart", AdminGroup.Content, 25)
            .Page("orders", "shop.tab.orders", ShopAdmin.OrdersAsync)
            .Page("orders/{id}", null, ShopAdmin.OrderAsync)
            .Page("products", "shop.tab.products", ShopAdminCatalog.ProductsAsync)
            .Page("products/{id}", null, ShopAdminCatalog.ProductAsync)
            .Page("categories", "shop.tab.categories", ShopAdminCatalog.CategoriesAsync)
            .Page("coupons", "shop.tab.coupons", ShopAdminCoupons.CouponsAsync)
            .Page("coupons/{id}", null, ShopAdminCoupons.CouponAsync)
            .Page("customers", "shop.tab.customers", ShopAdminCatalog.CustomersAsync)
            .Page("shipping", "shop.tab.shipping", ShopAdminCatalog.ShippingAsync)
            .Badge(ShopAdmin.BadgeAsync);
        b.Admin.Tile(ShopAdmin.TileAsync);
        b.Admin.Attention(ShopAdmin.AttentionAsync);
        b.Admin.RecentlyEdited(async (ctx, ct) =>
            (await new ShopStore(ctx.Data).OrdersAsync(take: 20, ct: ct)).Items.Select(o =>
                new RecentItem("ti-shopping-cart", $"{ctx.T("shop.orderNo", o.Number)} · {Money.Format(o.TotalCents, o.Currency, "de")}", o.Status != OrderStatus.Cancelled, o.UpdatedUtc, $"{ShopAdmin.Section}/orders/{o.Id}")));

        // ---- the rest of the site ------------------------------------------------------------------------
        b.Sitemap(async (ctx, ct) =>
        {
            var list = new List<SitemapEntry> { new("/shop") };
            list.AddRange((await new ShopStore(ctx.Data).ProductsAsync(true, ct: ct)).Items.Select(p => new SitemapEntry("/shop/" + Uri.EscapeDataString(p.Slug), p.UpdatedUtc)));
            return list;
        });
        b.MediaReferences(async (ctx, ct) =>
        {
            var urls = new List<string>();
            foreach (var p in (await new ShopStore(ctx.Data).ProductsAsync(false, ct: ct)).Items)
            {
                if (p.Image.Length > 0) urls.Add(p.Image);
                try { if (JsonNode.Parse(p.GalleryJson) is JsonArray arr) urls.AddRange(arr.OfType<JsonObject>().Select(g => g["url"]?.GetValue<string>() ?? "").Where(u => u.Length > 0)); } catch { /* damaged gallery */ }
            }
            return urls;
        });

        b.Job("maintenance", JobSchedule.EveryInterval(TimeSpan.FromMinutes(15)), ShopEvents.MaintenanceAsync);
        b.On("host.member.erase", (ctx, payload) => ShopEvents.EraseAsync(ctx, payload));
        b.On("host.member.export", ShopEvents.ExportAsync);
        b.On("host.member.confirmed", (ctx, payload) => ShopEvents.ConfirmedAsync(ctx, payload));
        b.On("shop.payment.paid", (ctx, payload) => ShopEvents.ExternalPaidAsync(ctx, payload));
        b.Operation("product.create", ShopEvents.CreateProductAsync);
        b.Operation("coupon.create", ShopEvents.CreateCouponAsync);

        // A first shipping rate, so a shop that sells something physical is not stuck on "no shipping" before anybody has looked at the tab. Editable, deletable.
        b.Lifecycle.OnInstall(async ctx =>
        {
            var store = new ShopStore(ctx.Data);
            if ((await store.RatesAsync()).Count == 0)
                // Named in the SITE's language (this runs in whatever language the operator's browser used, which says nothing about the shop).
                await store.SaveRateAsync(new ShippingRate { Name = MatCMS.Services.Localizer.DefaultCulture == "en" ? "Standard shipping Germany" : "Standardversand Deutschland", Countries = "DE", PriceCents = 490, FreeFromCents = 5000 });
        });
    }
}
