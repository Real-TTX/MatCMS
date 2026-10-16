using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The shop's pages the site's editors lay out in the block editor: the catalogue, the page of all categories, the keywords, a product, the cart, the checkout, an order, the customer area.
/// Each is a role (<see cref="PageRoleSpec"/>) with its variables — the values the page's handler already computes for the template parts, now also offered to blocks: <c>{{product.title}}</c> in a
/// heading, <c>{{product.price}}</c> in a text — and a starting layout that draws what the page showed before. The pieces the shop builds itself (the picture gallery, the cart's lines, the whole
/// checkout form) are variables that hold HTML; the block <c>shop_slot</c> places one of them. The shop's forms stay whole on purpose — layout and words around them are free, the form is not.
/// </summary>
internal static class ShopPageRoles
{
    /// <summary>One variable: its role, name, whether it holds HTML, and its two texts.</summary>
    private sealed record Var(string Role, string Name, bool Html, string De, string En);

    private static Var V(string role, string name, string de, string en) => new(role, name, false, de, en);
    private static Var H(string role, string name, string de, string en) => new(role, name, true, de, en);

    private static readonly Var[] Vars =
    {
        // ---- the catalogue (/shop and /shop/categories/{slug}) -------------------------------------------------
        V("catalog", "title", "Titel der Seite (Name der Kategorie, „Ergebnisse für …“ oder „Shop“)", "Title of the page (category name, “Results for …” or “Shop”)"),
        V("catalog", "category", "Name der Kategorie (leer im ganzen Shop)", "Name of the category (empty in the whole shop)"),
        V("catalog", "category_description", "Text der Kategorie", "The category's text"), V("catalog", "category_image_url", "Bild der Kategorie (Adresse)", "The category's picture (address)"),
        V("catalog", "query", "Suchbegriff", "Search words"), V("catalog", "total", "Anzahl der gefundenen Produkte", "Number of products found"),
        H("catalog", "crumbs_html", "Brotkrumen-Navigation", "Breadcrumbs"), H("catalog", "category_image_html", "Bild der Kategorie", "The category's picture"),
        H("catalog", "subcategories_html", "Unterkategorien als Kacheln", "Subcategories as tiles"), H("catalog", "search_html", "Suchfeld", "Search field"),
        H("catalog", "sort_html", "Sortierung (Auswahl)", "Sorting (drop-down)"), H("catalog", "count_html", "Anzahl der Produkte", "Number of products"),
        H("catalog", "toolbar_html", "Anzahl und Sortierung", "Count and sorting"), H("catalog", "active_html", "Aktive Filter als Chips", "Active filters as chips"),
        H("catalog", "categories_html", "Kategorien als Liste", "Categories as a list"), H("catalog", "filters_html", "Filter (Preis, Eigenschaften)", "Filters (price, attributes)"),
        H("catalog", "tags_html", "Schlagwörter", "Keywords"), H("catalog", "panel_html", "Filterbereich: Kategorien, Filter und Schlagwörter (auf dem Handy eingeklappt)", "Filter area: categories, filters and keywords (folded on a phone)"),
        H("catalog", "grid_html", "Die Produktkarten", "The product cards"), H("catalog", "pager_html", "Seitenzahlen", "Page numbers"), H("catalog", "nav_html", "Kleine Shop-Navigation (Shop, Warenkorb, Konto)", "The small shop navigation (shop, cart, account)"),
        // ---- all categories (/shop/categories) ------------------------------------------------------------------
        V("categories", "title", "Titel der Seite", "Title of the page"), V("categories", "total", "Anzahl der Kategorien", "Number of categories"), V("categories", "tag", "Das gewählte Schlagwort", "The keyword chosen"),
        H("categories", "crumbs_html", "Brotkrumen-Navigation", "Breadcrumbs"), H("categories", "tags_html", "Schlagwörter der Kategorien", "The categories' keywords"),
        H("categories", "tiles_html", "Kategorien als Kacheln", "Categories as tiles"), H("categories", "list_html", "Kategorien als verschachtelte Liste", "Categories as a nested list"), H("categories", "nav_html", "Kleine Shop-Navigation", "The small shop navigation"),
        // ---- keywords (/shop/tags) -----------------------------------------------------------------------------
        V("tags", "title", "Titel der Seite", "Title of the page"), V("tags", "total", "Anzahl der Schlagwörter", "Number of keywords"),
        H("tags", "crumbs_html", "Brotkrumen-Navigation", "Breadcrumbs"), H("tags", "tags_html", "Alle Schlagwörter", "All keywords"), H("tags", "nav_html", "Kleine Shop-Navigation", "The small shop navigation"),
        // ---- a product (/shop/{slug}) --------------------------------------------------------------------------
        V("product", "title", "Name des Produkts", "The product's name"), V("product", "summary", "Kurzbeschreibung", "Short description"), V("product", "sku", "Artikelnummer", "Article number"),
        V("product", "category", "Name der Kategorie", "Name of the category"), V("product", "category_url", "Adresse der Kategorie", "Address of the category"), V("product", "url", "Adresse des Produkts", "Address of the product"),
        V("product", "image_url", "Hauptbild (Adresse)", "Main picture (address)"), V("product", "price", "Preis (z. B. „24,90 €“, bei Varianten „ab …“)", "Price (e.g. “€24.90”, “from …” with variants)"),
        V("product", "price_old", "Früherer Preis (leer ohne Angebot)", "Earlier price (empty without an offer)"), V("product", "save_percent", "Ersparnis in Prozent (leer ohne Angebot)", "Saving in percent (empty without an offer)"),
        V("product", "on_sale", "Gesetzt, wenn das Produkt im Angebot ist", "Set when the product is on offer"), V("product", "sold_out", "Gesetzt, wenn das Produkt ausverkauft ist", "Set when the product is sold out"),
        V("product", "digital", "Gesetzt bei digitalen Produkten", "Set for digital products"), V("product", "digital_note", "Hinweis zu digitalen Produkten", "Note on digital products"),
        V("product", "tax_note", "Hinweis zu Steuer und Versand", "Note on tax and shipping"), V("product", "description_label", "Das Wort „Beschreibung“", "The word “Description”"),
        H("product", "breadcrumbs_html", "Brotkrumen-Navigation", "Breadcrumbs"), H("product", "image_html", "Bilder: Hauptbild mit Galerie", "Pictures: main picture with gallery"),
        H("product", "price_html", "Preis, Statt-Preis und Ersparnis", "Price, earlier price and saving"), H("product", "notice_html", "Hinweise (ausverkauft, nur noch wenige …)", "Notices (sold out, only a few left …)"),
        H("product", "form_html", "Kaufen: Variante, Menge und „In den Warenkorb“", "Buy: variant, quantity and “Add to cart”"), H("product", "tags_html", "Schlagwörter des Produkts", "The product's keywords"),
        H("product", "attributes_html", "Eigenschaften als Tabelle", "Attributes as a table"), H("product", "description_html", "Ausführliche Beschreibung", "The full description"),
        H("product", "related_html", "Ähnliche Produkte", "Related products"), H("product", "nav_html", "Kleine Shop-Navigation", "The small shop navigation"),
        // ---- the cart (/shop/cart) -----------------------------------------------------------------------------
        V("cart", "title", "Das Wort „Warenkorb“", "The word “Cart”"), V("cart", "pieces", "Anzahl der Artikel im Warenkorb", "Number of items in the cart"), V("cart", "pieces_text", "„3 Artikel“", "“3 items”"),
        V("cart", "has_items", "Gesetzt, wenn etwas im Warenkorb liegt", "Set when the cart holds something"), V("cart", "empty", "Gesetzt, wenn der Warenkorb leer ist", "Set when the cart is empty"),
        H("cart", "notices_html", "Hinweise (Gutschein, Preisänderungen)", "Notices (coupon, price changes)"), H("cart", "lines_html", "Die Positionen mit Menge und „Entfernen“", "The lines with quantity and “Remove”"),
        H("cart", "summary_html", "Zusammenfassung mit Kasse-Knopf und Gutschein", "Summary with the checkout button and coupon box"), H("cart", "empty_html", "Der leere Warenkorb mit Wegen zurück in den Shop", "The empty cart with ways back into the shop"),
        H("cart", "nav_html", "Kleine Shop-Navigation", "The small shop navigation"),
        // ---- the checkout (/shop/checkout) ---------------------------------------------------------------------
        V("checkout", "title", "Das Wort „Kasse“", "The word “Checkout”"), V("checkout", "has_errors", "Gesetzt, wenn etwas zu korrigieren ist", "Set when something has to be corrected"),
        H("checkout", "steps_html", "Fortschritt: Warenkorb › Kasse › Bestätigung", "Progress: cart › checkout › confirmation"), H("checkout", "errors_html", "Hinweise zu Eingaben", "Notices about the input"),
        H("checkout", "form_html", "Das Kassenformular mit Adresse, Versand, Zahlung und Bestellübersicht", "The checkout form with address, shipping, payment and the order summary"), H("checkout", "nav_html", "Kleine Shop-Navigation", "The small shop navigation"),
        // ---- an order (/shop/order/{number}) -------------------------------------------------------------------
        V("order", "title", "„Bestellung R-1000“", "“Order R-1000”"), V("order", "number", "Bestellnummer", "Order number"), V("order", "status", "Status (Schlüssel: paid, shipped …)", "Status (key: paid, shipped …)"),
        V("order", "status_label", "Status als Wort", "Status as a word"), V("order", "date", "Bestelldatum", "Order date"), V("order", "total", "Gesamtsumme", "Total"),
        H("order", "head_html", "Überschrift mit Status und Datum", "Heading with status and date"), H("order", "message_html", "Hinweise (Danke, Zahlung abgebrochen …)", "Notices (thank you, payment cancelled …)"),
        H("order", "steps_html", "Fortschritt: bestellt, bezahlt, versendet, abgeschlossen", "Progress: ordered, paid, shipped, completed"), H("order", "items_html", "Die bestellten Artikel", "The items ordered"),
        H("order", "pay_html", "Zahlung einer offenen Bestellung (Überweisung, erneut zahlen, stornieren)", "Payment of an open order (transfer, pay again, cancel)"),
        H("order", "history_html", "Verlauf der Bestellung", "The order's history"), H("order", "side_html", "Seitenspalte: Summe, Sendungsnummer, Adressen, Downloads, Rechnung", "Side column: total, tracking, addresses, downloads, invoice"),
        H("order", "back_html", "Link zurück zu „Meine Bestellungen“", "Link back to “My orders”"), H("order", "nav_html", "Kleine Shop-Navigation", "The small shop navigation"),
        // ---- the customer area (/konto …) ----------------------------------------------------------------------
        V("account", "title", "Überschrift der Seite („Hallo Erika“, „Meine Bestellungen“ …)", "Heading of the page (“Hello Erika”, “My orders” …)"), V("account", "section", "Der Bereich: home, orders, addresses, profile", "The section: home, orders, addresses, profile"),
        V("account", "name", "Name des Kunden", "The customer's name"), V("account", "email", "E-Mail-Adresse des Kunden", "The customer's e-mail address"), V("account", "initials", "Initialen des Kunden", "The customer's initials"),
        H("account", "profile_html", "Wer angemeldet ist (Bild mit Initialen, Name, E-Mail)", "Who is signed in (picture with initials, name, e-mail)"), H("account", "nav_html", "Navigation des Kontos", "The account's navigation"),
        H("account", "signout_html", "„Abmelden“", "“Sign out”"), H("account", "content_html", "Der Inhalt der Seite (Übersicht, Bestellungen, Adressen, Profil)", "The page's content (overview, orders, addresses, profile)"),
    };

    private static readonly (string Role, string De, string En, string DescDe, string DescEn)[] Roles =
    {
        ("catalog", "Shop – Katalog", "Shop – catalogue", "Die Produktliste unter /shop, einer Kategorie und der Suche.", "The product list at /shop, a category and the search."),
        ("categories", "Shop – Kategorien", "Shop – categories", "Die Seite /shop/categories mit allen Kategorien.", "The /shop/categories page with all categories."),
        ("tags", "Shop – Schlagwörter", "Shop – keywords", "Die Seite /shop/tags mit allen Schlagwörtern.", "The /shop/tags page with all keywords."),
        ("product", "Shop – Produkt", "Shop – product", "Die Seite eines Produkts: Bilder, Preis, Kaufen, Beschreibung.", "A product's page: pictures, price, buy, description."),
        ("cart", "Shop – Warenkorb", "Shop – cart", "Der Warenkorb mit den Positionen und der Zusammenfassung.", "The cart with its lines and the summary."),
        ("checkout", "Shop – Kasse", "Shop – checkout", "Das Kassenformular.", "The checkout form."),
        ("order", "Shop – Bestellung", "Shop – order", "Die Seite einer Bestellung für den Kunden.", "An order's page for the customer."),
        ("account", "Shop – Kundenkonto", "Shop – customer account", "Der Rahmen des Kundenbereichs /konto mit seinen Seiten.", "The frame of the customer area /konto with its pages."),
    };

    public static IEnumerable<string> RoleNames => Roles.Select(r => r.Role);

    /// <summary>The names (without the role) of a page's variables that hold HTML.</summary>
    public static IReadOnlyCollection<string> HtmlNames(string role) => Vars.Where(v => v.Role == role && v.Html).Select(v => v.Name).ToList();

    /// <summary>True when the complete name ("product.image_html") is a variable that holds HTML the shop built — the only kind the slot block draws as it is.</summary>
    public static bool IsHtml(string complete) => Vars.Any(v => v.Html && v.Role + "." + v.Name == complete);

    /// <summary>The texts of every role and variable (titles, descriptions, labels), as rows for the shop's string table.</summary>
    public static IEnumerable<(string Key, string De, string En)> Strings()
    {
        foreach (var r in Roles)
        {
            yield return ("shop.page." + r.Role, r.De, r.En);
            yield return ("shop.page." + r.Role + ".desc", r.DescDe, r.DescEn);
        }
        foreach (var v in Vars) yield return ($"shop.var.{v.Role}.{v.Name}", v.De, v.En);
        foreach (var v in Vars.Where(x => x.Html)) yield return ($"shop.slot.{v.Role}.{v.Name}", Roles.First(r => r.Role == v.Role).De.Replace("Shop – ", "") + " › " + v.De, Roles.First(r => r.Role == v.Role).En.Replace("Shop – ", "") + " › " + v.En);
    }

    /// <summary>The block that places one of the shop's HTML variables (the gallery, the cart's lines, the checkout form …) on a page: only the HTML variables, and only on a shop page.</summary>
    public static IReadOnlyList<FieldOption> SlotOptions() => Vars.Where(v => v.Html).Select(v => new FieldOption($"{v.Role}.{v.Name}", $"shop.slot.{v.Role}.{v.Name}")).ToList();

    // ---- registering ------------------------------------------------------------------------------------------

    public static void Register(IPluginBuilder b)
    {
        foreach (var r in Roles)
            b.PageRole(new PageRoleSpec(r.Role, "shop.page." + r.Role)
            {
                Description = "shop.page." + r.Role + ".desc",
                Variables = Vars.Where(v => v.Role == r.Role).Select(v => new PageVariable(v.Name, $"shop.var.{v.Role}.{v.Name}") { Html = v.Html }).ToList(),
                Layout = ShopPageLayouts.For(r.Role),
                Preview = (ctx, ct) => ShopPageSamples.PreviewUrlAsync(ctx, r.Role, ct),
                Scope = (ctx, ct) => ShopPageSamples.ScopeAsync(ctx, r.Role, ct)
            });
        b.Block(new BlockSpec("shop_slot", "shop.slot.name", "shop.slot.desc")
        {
            Category = "design", IconSvg = "<rect x=\"3\" y=\"4\" width=\"18\" height=\"16\" rx=\"2\"/><path d=\"M7 9h10M7 13h6\"/>",
            Fields = new[] { new FieldSpec("slot", "shop.slot.field", FieldKind.Select) { Options = SlotOptions(), Help = "shop.slot.fieldHelp" } }
        }, SlotAsync);
    }

    /// <summary>Draws the shop's piece the block names, from the page's variables — nothing when the variable is empty or is not one of the shop's HTML pieces (a name typed into the stored data is never trusted).</summary>
    private static Task<PluginResult> SlotAsync(IPluginContext ctx, CancellationToken ct)
    {
        var name = (ctx.Block?.Get("slot") ?? "").Trim();
        if (!IsHtml(name) || !ctx.PageScope.TryGetValue(name, out var html) || string.IsNullOrWhiteSpace(html)) return Task.FromResult(PluginResult.Fragment(""));
        return Task.FromResult(PluginResult.Fragment($"<div class=\"shop shop-block shop-slot\">{html}</div>"));
    }
}
