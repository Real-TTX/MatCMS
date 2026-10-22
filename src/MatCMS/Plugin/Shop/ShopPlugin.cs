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
        foreach (var spec in ShopMail.Specs.Append(ShopReminders.Spec)) b.MailTemplate(spec);

        // ---- public pages --------------------------------------------------------------------------------
        // Forms of a visitor carry the antiforgery token (a customer's browser must not be made to order or change anything by another page); a page with a form is never
        // cached for that reason. Reading a catalogue page needs none.
        var get = new PublicRouteOptions { Methods = PluginMethod.Get };
        // Real customers post here, and several may share one address; 120 a minute is generous for a person and still a stop for a flood.
        var form = new PublicRouteOptions { Antiforgery = true, PostsPerMinute = 120 };
        var customer = new PublicRouteOptions { Antiforgery = true, RequireMember = true, PostsPerMinute = 120 };
        b.Route("/shop", ShopPublic.CatalogAsync, get);
        b.Route("/shop/categories", ShopPublic.CategoriesPageAsync, get);
        b.Route("/shop/tags", ShopPublic.TagsPageAsync, get);
        b.Route("/shop/suggest", ShopSuggest.SuggestAsync, get);
        b.Route("/shop/categories/{slug}", ShopPublic.CatalogAsync, get);
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
        ShopMenu.Register(b);
        // How the shop looks is the site's template's business: the product card, the product page and the catalogue are files a template can replace.
        ShopParts.Register(b);
        ShopPageRoles.Register(b);
        // The shop in the site's own menus: the cart and the way to the account in the icon strip, the shop with its categories in the main menu.

        // ---- back office ---------------------------------------------------------------------------------
        b.Admin.Section("shop.nav", "ti-shopping-cart", AdminGroup.Content, 25)
            .Page("dashboard", null, ShopAdmin.DashboardAsync)
            .Page("orders", "shop.tab.orders", ShopAdmin.OrdersAsync)
            .Page("orders/{id}", null, ShopAdmin.OrderAsync)
            .Page("products", "shop.tab.products", ShopAdminCatalog.ProductsAsync)
            .Page("products/{id}", null, ShopAdminCatalog.ProductAsync)
            .Page("categories", "shop.tab.categories", ShopAdminCatalog.CategoriesAsync)
            .Page("categories/{id}", null, ShopAdminCatalog.CategoryAsync)
            .Page("attributes", "shop.tab.attributes", ShopAdminAttributes.AttributesAsync)
            .Page("attributes/{id}", null, ShopAdminAttributes.AttributeAsync)
            .Page("coupons", "shop.tab.coupons", ShopAdminCoupons.CouponsAsync)
            .Page("coupons/{id}", null, ShopAdminCoupons.CouponAsync)
            .Page("customers", "shop.tab.customers", ShopAdminCatalog.CustomersAsync)
            .Page("customers/{id}", null, ShopAdminCustomers.CustomerAsync)
            .Page("shipping", "shop.tab.shipping", ShopAdminCatalog.ShippingAsync)
            .Page("payment", "shop.tab.payment", ShopConditions.PaymentAsync)
            .Page("taxes", "shop.tab.taxes", ShopConditions.TaxesAsync)
            .Page("legal", "shop.tab.legal", ShopConditions.LegalAsync)
            .Page("numbers", "shop.tab.numbers", ShopConditions.NumbersAsync)
            .Page("reports", "shop.tab.reports", ShopReports.ReportAsync)
            .Page("data", "shop.tab.data", ShopAdminData.DataAsync)
            .Badge(ShopAdmin.BadgeAsync)
            // One line in the main menu like every other plugin; opening it shows the shop's own menu (with a way back) on every one of its pages, starting at the dashboard. Conditions and settings are screens with a sub-menu of their own.
            .Link("shop.nav.dashboard", "dashboard", "ti-layout-dashboard")
            .Link("shop.tab.orders", "orders", "ti-receipt", badge: true)
            .Link("shop.tab.products", "products", "ti-package")
            .Link("shop.tab.categories", "categories", "ti-category")
            .Link("shop.tab.attributes", "attributes", "ti-adjustments")
            .Link("shop.tab.customers", "customers", "ti-users")
            .Link("shop.tab.reports", "reports", "ti-chart-bar")
            .Link("shop.tab.data", "data", "ti-arrows-exchange")
            .Link("shop.nav.conditions", "shipping", "ti-adjustments-horizontal", also: new[] { "payment", "coupons", "taxes", "legal", "numbers" })
            .Link("shop.nav.settings", "_settings", "ti-settings");
        b.Admin.Tile(ShopAdmin.TileAsync);
        b.Admin.Attention(ShopAdmin.AttentionAsync);
        // Orders are not "edited": they get their own dashboard card, named after the shop.
        b.Admin.DashboardCard(ShopAdmin.DashboardCardAsync);

        // ---- the rest of the site ------------------------------------------------------------------------
        b.Sitemap(async (ctx, ct) =>
        {
            var store = new ShopStore(ctx.Data);
            var list = new List<SitemapEntry> { new("/shop"), new("/shop/categories"), new("/shop/tags") };
            var (counts, _) = await store.FacetsAsync(ct);
            list.AddRange((await store.CategoriesAsync(ct)).Where(c => counts.ContainsKey(c.Slug)).Select(c => new SitemapEntry(ShopUrls.Category(c.Slug))));
            list.AddRange((await store.ProductsAsync(true, ct: ct)).Items.Select(p => new SitemapEntry(ShopUrls.Product(p.Slug), p.UpdatedUtc)));
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
        // Described (ShopAiOps), so the site's AI assistant can use them too; the order operations carry customers' data and are kept from it.
        b.Operation(ShopAiOps.ProductList, ShopAiOps.ListProductsAsync);
        b.Operation(ShopAiOps.ProductRead, ShopAiOps.ReadProductAsync);
        b.Operation(ShopAiOps.ProductCreate, ShopEvents.CreateProductAsync);
        b.Operation(ShopAiOps.ProductUpdate, ShopAiOps.UpdateProductAsync);
        b.Operation(ShopAiOps.CategoryList, ShopAiOps.ListCategoriesAsync);
        b.Operation(ShopAiOps.CategoryCreate, ShopAiOps.CreateCategoryAsync);
        b.Operation(ShopAiOps.CategoryUpdate, ShopAiOps.UpdateCategoryAsync);
        b.Operation(ShopAiOps.CouponCreate, ShopEvents.CreateCouponAsync);
        b.Operation(ShopAiOps.VariantSet, ShopOps.SetVariantsAsync);
        b.Operation(ShopAiOps.AttributeCreate, ShopOps.CreateAttributeAsync);
        b.Operation(ShopAiOps.ProductAttributes, ShopOps.SetProductAttributesAsync);
        b.Operation(ShopAiOps.OrderList, ShopOps.ListOrdersAsync);
        b.Operation(ShopAiOps.OrderUpdate, ShopOps.UpdateOrderAsync);
        b.Operation(ShopAiOps.ProductRestore, ShopAiOps.RestoreProductAsync);
        b.Operation(ShopAiOps.ProductRemove, ShopAiOps.RemoveProductAsync);
        b.Operation(ShopAiOps.CategoryRestore, ShopAiOps.RestoreCategoryAsync);
        b.Operation(ShopAiOps.CategoryRemove, ShopAiOps.RemoveCategoryAsync);

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
