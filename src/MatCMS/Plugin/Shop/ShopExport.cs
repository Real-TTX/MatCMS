using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopCsv;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// Getting data out of the shop and into it: CSV of orders (one row per order, and one per order line), customers, products and variants — for the bookkeeper, for a spreadsheet, for moving
/// shops — and an import of products (and their variants) from the same files. Everything that is written is written by <see cref="ShopCsv"/> (formula-safe); everything that is read is
/// checked the way the product form checks it.
/// </summary>
internal static class ShopExport
{
    public const int MaxImportRows = 2000;
    public const long MaxImportBytes = 2 * 1024 * 1024;

    // ---- the files --------------------------------------------------------------------------------------

    /// <summary>The orders of a period (local dates, both ends included) and status as a table: one row per order, with the sums, the invoice number and the payment — what bookkeeping asks for.</summary>
    public static async Task<byte[]> OrdersAsync(ShopStore store, Format f, DateOnly? from, DateOnly? to, string? status, CancellationToken ct)
    {
        var rows = new List<List<string>> { new() { "Bestellnummer", "Datum", "Status", "Name", "E-Mail", "Land", "Zahlart", "Zwischensumme", "Rabatt", "Gutschein", "Versand", "Steuer", "Gesamt", "Erstattet", "Währung", "Rechnungsnummer", "Bezahlt am", "Paketdienst", "Sendungsnummer" } };
        foreach (var o in await OrdersOfAsync(store, from, to, status, ct))
            rows.Add(new List<string>
            {
                Text(o.Number, f), Date(o.CreatedUtc), Text(o.Status, f), Text(string.IsNullOrWhiteSpace(o.Name) ? o.Billing.Name : o.Name, f), Text(o.Email, f), Text(o.Billing.Country, f), Text(o.PaymentProvider, f),
                Money(o.SubtotalCents, f), Money(o.DiscountCents, f), Text(o.CouponCode, f), Money(o.ShippingCents, f), Money(o.TaxCents, f), Money(o.TotalCents, f), Money(o.RefundedCents, f), Text(o.Currency, f),
                Text(o.InvoiceNumber, f), o.PaidUtc is { } p ? Date(p) : "", Text(ShopTracking.NameOf(o.Carrier), f), Text(o.Tracking, f)
            });
        return Document(rows, f);
    }

    /// <summary>The same orders line by line: what was sold, how many, at what price and tax — for accounting by product or tax rate.</summary>
    public static async Task<byte[]> ItemsAsync(ShopStore store, Format f, DateOnly? from, DateOnly? to, string? status, CancellationToken ct)
    {
        var rows = new List<List<string>> { new() { "Bestellnummer", "Datum", "Status", "Artikel", "Variante", "Art.-Nr.", "Menge", "Einzelpreis", "Steuersatz %", "Rabatt", "Steuer", "Zeilensumme", "Währung" } };
        foreach (var o in await OrdersOfAsync(store, from, to, status, ct))
            foreach (var i in await store.ItemsAsync(o.Id, ct))
                rows.Add(new List<string>
                {
                    Text(o.Number, f), Date(o.CreatedUtc), Text(o.Status, f), Text(i.Title, f), Text(i.VariantTitle, f), Text(i.Sku, f), Number(i.Qty), Money(i.UnitCents, f), Percent(i.TaxBp, f), Money(i.DiscountCents, f), Money(i.TaxCents, f), Money(i.NetOfDiscountCents, f), Text(o.Currency, f)
                });
        return Document(rows, f);
    }

    private static async Task<IReadOnlyList<Order>> OrdersOfAsync(ShopStore store, DateOnly? from, DateOnly? to, string? status, CancellationToken ct)
    {
        var all = (await store.OrdersAsync(OrderStatus.All.Contains(status ?? "") ? status : null, take: 0, ct: ct)).Items;
        return all.Where(o => { var d = DateOnly.FromDateTime(o.CreatedUtc.ToLocalTime()); return (from is null || d >= from) && (to is null || d <= to); }).OrderBy(o => o.CreatedUtc).ToList();
    }

    public static async Task<byte[]> CustomersAsync(ShopStore store, ShopConfig cfg, Format f, CancellationToken ct)
    {
        var rows = new List<List<string>> { new() { "E-Mail", "Name", "Konto", "Bestellungen", "Umsatz", "Letzte Bestellung", "Währung" } };
        foreach (var c in await store.CustomersAsync(ct))
            rows.Add(new List<string> { Text(c.Email, f), Text(c.Name, f), c.MemberId > 0 ? "ja" : "nein", Number(c.Orders), Money(c.SpentCents, f), Date(c.LastOrderUtc), Text(cfg.Currency, f) });
        return Document(rows, f);
    }

    public static readonly string[] ProductColumns =
        { "slug", "title", "summary", "description", "price", "compare", "tax", "sku", "category", "extra_categories", "tags", "stock", "weight", "active", "featured", "kind", "image", "gallery", "seo_title", "seo_description" };

    public static async Task<byte[]> ProductsAsync(ShopStore store, Format f, CancellationToken ct)
    {
        var rows = new List<List<string>> { ProductColumns.Select(c => c).ToList() };
        foreach (var p in (await store.ProductsAsync(false, ct: ct)).Items)
        {
            var gallery = string.Join("|", ShopPublic.ParseGalleryUrls(p.GalleryJson));
            rows.Add(new List<string>
            {
                Text(p.Slug, f), Text(p.Title, f), Text(p.Summary, f), Text(p.DescriptionHtml, f), Money(p.PriceCents, f), p.CompareCents > 0 ? Money(p.CompareCents, f) : "", Percent(p.TaxBp, f), Text(p.Sku, f),
                Text(p.Category, f), Text(p.ExtraCategories.Replace(",", "|"), f), Text(string.Join("|", ShopTags.Split(p.Tags)), f), p.TrackStock ? Number(p.Stock) : "", Number(p.WeightGrams), Yes(p.Active), Yes(p.Featured), Text(p.Kind, f),
                Text(p.Image, f), Text(gallery, f), Text(p.SeoTitle, f), Text(p.SeoDescription, f)
            });
        }
        return Document(rows, f);
    }

    /// <summary>The empty form for an import: the header the importer knows and one example row, so nobody has to guess the columns.</summary>
    public static byte[] TemplateBytes(Format f)
    {
        var example = new[] { "tasse-blau", "Tasse „Blau“", "Handgemacht, spülmaschinenfest", "<p>Steinzeug, 300 ml.</p>", Money(1990, f), Money(2490, f), Percent(1900, f), "TASSE-B", "Geschirr", "Küche|Geschenke", "neu|handgemacht", "25", "420", "1", "0", "physical", "", "", "Blaue Tasse aus Steinzeug", "Handgemachte Tasse, 300 ml, spülmaschinenfest." };
        return Document(new[] { ProductColumns.Select(c => c), example.Select((c, i) => i is 5 or 4 or 6 or 11 or 12 or 13 or 14 ? c : Text(c, f)) }, f);
    }

    public static readonly string[] VariantColumns = { "product", "name", "delta", "stock", "sku", "image" };

    public static async Task<byte[]> VariantsAsync(ShopStore store, Format f, CancellationToken ct)
    {
        var rows = new List<List<string>> { VariantColumns.Select(c => c).ToList() };
        var products = (await store.ProductsAsync(false, ct: ct)).Items;
        foreach (var v in await store.VariantsOfAsync(products.Select(p => p.Id), ct))
            if (v.Active && products.FirstOrDefault(p => p.Id == v.ProductId) is { } p)
                rows.Add(new List<string> { Text(p.Slug, f), Text(v.Name, f), v.PriceDeltaCents == 0 ? "" : Money(v.PriceDeltaCents, f), v.TrackStock ? Number(v.Stock) : "", Text(v.Sku, f), Text(v.Image, f) });
        return Document(rows, f);
    }

    // ---- the import -------------------------------------------------------------------------------------

    public sealed record ImportLine(int Row, string Subject, string Outcome, bool Ok);
    public sealed record ImportReport(bool DryRun, int Created, int Updated, int Skipped, int CategoriesMade, IReadOnlyList<ImportLine> Lines, IReadOnlyList<string> Ignored, string? Fatal = null);

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["slug"] = "slug", ["adresse"] = "slug", ["url"] = "slug",
        ["title"] = "title", ["titel"] = "title", ["name"] = "title", ["produkt"] = "title", ["produktname"] = "title",
        ["summary"] = "summary", ["kurztext"] = "summary", ["kurzbeschreibung"] = "summary",
        ["description"] = "description", ["beschreibung"] = "description",
        ["price"] = "price", ["preis"] = "price", ["compare"] = "compare", ["statt-preis"] = "compare", ["streichpreis"] = "compare", ["tax"] = "tax", ["steuer"] = "tax", ["mwst"] = "tax", ["steuersatz"] = "tax",
        ["sku"] = "sku", ["art.-nr."] = "sku", ["artikelnummer"] = "sku", ["art-nr"] = "sku",
        ["category"] = "category", ["kategorie"] = "category", ["extra_categories"] = "extra_categories", ["weitere kategorien"] = "extra_categories",
        ["tags"] = "tags", ["schlagwörter"] = "tags", ["schlagworte"] = "tags", ["stock"] = "stock", ["bestand"] = "stock", ["weight"] = "weight", ["gewicht"] = "weight",
        ["active"] = "active", ["aktiv"] = "active", ["featured"] = "featured", ["hervorgehoben"] = "featured", ["kind"] = "kind", ["art"] = "kind", ["image"] = "image", ["bild"] = "image",
        ["gallery"] = "gallery", ["galerie"] = "gallery", ["seo_title"] = "seo_title", ["meta-titel"] = "seo_title", ["seo_description"] = "seo_description", ["meta-beschreibung"] = "seo_description"
    };

    private static readonly Dictionary<string, string> VariantAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["product"] = "product", ["produkt"] = "product", ["slug"] = "product", ["name"] = "name", ["variante"] = "name", ["delta"] = "delta", ["aufpreis"] = "delta",
        ["stock"] = "stock", ["bestand"] = "stock", ["sku"] = "sku", ["art.-nr."] = "sku", ["image"] = "image", ["bild"] = "image"
    };

    private static bool Truth(string s) => s.Trim().ToLowerInvariant() is "1" or "ja" or "yes" or "true" or "j" or "x" or "wahr";

    /// <summary>
    /// Reads a product file (and optionally a variants file). A product is found by its address (slug; without one it is made from the title): an existing one is updated — only the columns the file HAS,
    /// an empty cell clears an optional field — and an unknown one is created. A category that does not exist is created at the top level. With <paramref name="dryRun"/> nothing is saved: the report says
    /// what would happen, row by row.
    /// </summary>
    public static async Task<ImportReport> ImportProductsAsync(IPluginContext ctx, ShopStore store, ShopConfig cfg, string? productsCsv, string? variantsCsv, bool dryRun, CancellationToken ct)
    {
        var lines = new List<ImportLine>(); var ignored = new List<string>();
        int created = 0, updated = 0, skipped = 0, catsMade = 0;
        var cats = (await store.CategoriesAsync(ct)).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(productsCsv))
        {
            var table = ShopCsv.Parse(productsCsv, out _);
            if (table.Count < 2) return new ImportReport(dryRun, 0, 0, 0, 0, lines, ignored, "Die Datei enthält keine Zeilen unter der Kopfzeile.");
            if (table.Count - 1 > MaxImportRows) return new ImportReport(dryRun, 0, 0, 0, 0, lines, ignored, $"Mehr als {MaxImportRows} Zeilen sind nicht möglich.");
            var header = table[0].Select(h => Aliases.TryGetValue(h.Trim(), out var c) ? c : "").ToArray();
            foreach (var (h, i) in table[0].Select((h, i) => (h, i))) if (header[i].Length == 0 && h.Trim().Length > 0) ignored.Add(h.Trim());
            if (!header.Contains("title") && !header.Contains("slug")) return new ImportReport(dryRun, 0, 0, 0, 0, lines, ignored, "Die Kopfzeile braucht eine Spalte „title“ (Titel) oder „slug“ (Adresse).");
            if (header.Count(h => h == "price") == 0) ignored.Add("(keine Preis-Spalte: neue Produkte können so nicht angelegt werden)");

            for (var r = 1; r < table.Count; r++)
            {
                var row = table[r];
                string Cell(string col) { var i = Array.IndexOf(header, col); return i >= 0 && i < row.Length ? row[i].Trim() : ""; }
                bool Has(string col) => Array.IndexOf(header, col) >= 0;
                var title = Cell("title");
                var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(Cell("slug").Length > 0 ? Cell("slug") : title);
                var subject = title.Length > 0 ? title : slug;
                if (slug.Length == 0) { lines.Add(new(r + 1, subject, "übersprungen: weder Titel noch Adresse", false)); skipped++; continue; }
                if (ShopPublic.ReservedSlugs.Contains(slug)) slug += "-1";
                if (!seen.Add(slug)) { lines.Add(new(r + 1, subject, $"übersprungen: die Adresse „{slug}“ steht schon weiter oben in der Datei", false)); skipped++; continue; }

                var existing = await store.ProductBySlugAsync(slug, ct);
                var p = existing ?? new Product { Slug = slug, Active = true, TaxBp = cfg.DefaultTaxBp };
                string? problem = null;
                if (Has("title") && title.Length > 0) p = p with { Title = title };
                if (existing is null && p.Title.Length == 0) problem = "es fehlt der Titel";
                if (Has("summary")) p = p with { Summary = Cell("summary") };
                if (Has("description")) p = p with { DescriptionHtml = MatCMS.Services.SafeHtml.Sanitize(row.Length > Array.IndexOf(header, "description") ? row[Array.IndexOf(header, "description")] : "") };
                if (Has("price") && Cell("price").Length > 0)
                {
                    if (Money.TryParse(Cell("price"), out var price) && price >= 0) p = p with { PriceCents = price }; else problem ??= $"„{Cell("price")}“ ist kein Preis";
                }
                else if (existing is null) problem ??= "es fehlt der Preis";
                if (Has("compare")) { if (Cell("compare").Length == 0) p = p with { CompareCents = 0 }; else if (Money.TryParse(Cell("compare"), out var cmp) && cmp >= 0) p = p with { CompareCents = cmp }; else problem ??= $"„{Cell("compare")}“ ist kein Statt-Preis"; }
                if (Has("tax") && Cell("tax").Length > 0) { if (Money.TryParsePercent(Cell("tax"), out var bp)) p = p with { TaxBp = bp }; else problem ??= $"„{Cell("tax")}“ ist kein Steuersatz"; }
                if (Has("sku")) p = p with { Sku = Cell("sku") };
                if (Has("tags")) p = p with { Tags = ShopTags.Normalise(Cell("tags").Replace('|', ',')) };
                if (Has("stock"))
                {
                    if (Cell("stock").Length == 0) p = p with { TrackStock = false, Stock = 0 };
                    else if (long.TryParse(Cell("stock"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var st) && st >= 0) p = p with { TrackStock = true, Stock = st };
                    else problem ??= $"„{Cell("stock")}“ ist kein Bestand";
                }
                if (Has("weight") && Cell("weight").Length > 0) { if (int.TryParse(Cell("weight"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w >= 0) p = p with { WeightGrams = w }; else problem ??= $"„{Cell("weight")}“ ist kein Gewicht"; }
                if (Has("active") && Cell("active").Length > 0) p = p with { Active = Truth(Cell("active")) };
                if (Has("featured") && Cell("featured").Length > 0) p = p with { Featured = Truth(Cell("featured")) };
                if (Has("kind") && Cell("kind").Length > 0) p = p with { Kind = Cell("kind").ToLowerInvariant() is "digital" or "download" ? ProductKind.Digital : ProductKind.Physical };
                if (Has("image")) p = p with { Image = ShopPublic.SafeUrl(Cell("image")) };
                if (Has("gallery")) p = p with { GalleryJson = new JsonArray(Cell("gallery").Split('|', StringSplitOptions.RemoveEmptyEntries).Select(u => ShopPublic.SafeUrl(u.Trim())).Where(u => u.Length > 0).Select(u => (JsonNode)new JsonObject { ["url"] = u, ["alt"] = "" }).ToArray()).ToJsonString() };
                if (Has("seo_title")) p = p with { SeoTitle = Cell("seo_title").Length > 120 ? Cell("seo_title")[..120] : Cell("seo_title") };
                if (Has("seo_description")) p = p with { SeoDescription = Cell("seo_description").Length > 320 ? Cell("seo_description")[..320] : Cell("seo_description") };
                if (Has("category") && Cell("category").Length > 0) p = p with { Category = await CategoryFor(Cell("category"), r + 1) };
                if (Has("extra_categories")) p = p with { ExtraCategories = string.Join(",", (await Task.WhenAll(Cell("extra_categories").Split('|', StringSplitOptions.RemoveEmptyEntries).Select(x => CategoryFor(x.Trim(), r + 1)))).Where(x => x.Length > 0 && x != p.Category).Distinct()) };

                if (problem is not null) { lines.Add(new(r + 1, subject, "übersprungen: " + problem, false)); skipped++; continue; }
                if (!dryRun) await store.SaveProductAsync(p, ct);
                if (existing is null) { created++; lines.Add(new(r + 1, p.Title, dryRun ? "würde angelegt" : "angelegt", true)); }
                else { updated++; lines.Add(new(r + 1, p.Title, dryRun ? "würde aktualisiert" : "aktualisiert", true)); }
            }
        }

        if (!string.IsNullOrWhiteSpace(variantsCsv))
        {
            var vt = ShopCsv.Parse(variantsCsv, out _);
            if (vt.Count >= 2 && vt.Count - 1 <= MaxImportRows * 10)
            {
                var vh = vt[0].Select(h => VariantAliases.TryGetValue(h.Trim(), out var c) ? c : "").ToArray();
                if (vh.Contains("product") && vh.Contains("name"))
                    for (var r = 1; r < vt.Count; r++)
                    {
                        var row = vt[r];
                        string Cell(string col) { var i = Array.IndexOf(vh, col); return i >= 0 && i < row.Length ? row[i].Trim() : ""; }
                        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(Cell("product")); var name = Cell("name");
                        var product = await store.ProductBySlugAsync(slug, ct);
                        var inFile = seen.Contains(slug);
                        if (name.Length == 0 || name.Length > ShopVariants.MaxName) { lines.Add(new(r + 1, $"{slug} / {name}", "Variante übersprungen: Name fehlt oder ist zu lang", false)); skipped++; continue; }
                        if (product is null && !(dryRun && inFile)) { lines.Add(new(r + 1, $"{slug} / {name}", "Variante übersprungen: kein Produkt mit dieser Adresse", false)); skipped++; continue; }
                        long delta = 0; if (Cell("delta").Length > 0 && !Money.TryParse(Cell("delta").TrimStart('+'), out delta)) { lines.Add(new(r + 1, $"{slug} / {name}", $"Variante übersprungen: „{Cell("delta")}“ ist kein Betrag", false)); skipped++; continue; }
                        long? stock = null; if (Cell("stock").Length > 0) { if (long.TryParse(Cell("stock"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s >= 0) stock = s; else { lines.Add(new(r + 1, $"{slug} / {name}", $"Variante übersprungen: „{Cell("stock")}“ ist kein Bestand", false)); skipped++; continue; } }
                        if (!dryRun && product is not null)
                        {
                            var have = (await store.VariantsAsync(product.Id, false, ct)).FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                            var all = await store.VariantsAsync(product.Id, false, ct);
                            await store.SaveVariantAsync((have ?? new Variant { ProductId = product.Id, Sort = all.Count }) with
                            {
                                Name = name, PriceDeltaCents = delta, TrackStock = stock is not null, Stock = stock ?? 0, Sku = Cell("sku"), Image = ShopPublic.SafeUrl(Cell("image")), Active = true
                            }, ct);
                        }
                        lines.Add(new(r + 1, $"{slug} / {name}", dryRun ? "Variante würde gespeichert" : "Variante gespeichert", true));
                    }
                else ignored.Add("(Varianten-Datei: es fehlen die Spalten „product“ und „name“)");
            }
        }
        return new ImportReport(dryRun, created, updated, skipped, catsMade, lines, ignored);

        // A category by slug, else by name; a new one at the top level (or, in a dry run, only counted).
        async Task<string> CategoryFor(string text, int row)
        {
            var hit = cats.FirstOrDefault(c => string.Equals(c.Slug, text, StringComparison.OrdinalIgnoreCase)) ?? cats.FirstOrDefault(c => string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Slug;
            var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(text);
            if (slug.Length == 0 || ShopPublic.ReservedSlugs.Contains(slug)) return "";
            if (!dryRun) await store.AddCategoryAsync(slug, text, cats.Count, ct);
            cats.Add(new Category { Id = -cats.Count - 1, Slug = slug, Name = text });
            catsMade++;
            return slug;
        }
    }
}
