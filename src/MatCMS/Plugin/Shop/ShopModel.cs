using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Shop;

/// <summary>The states an order goes through. One list, one place: a status the code does not know here cannot be stored.</summary>
internal static class OrderStatus
{
    public const string AwaitingPayment = "awaiting_payment";
    public const string Paid = "paid";
    public const string Shipped = "shipped";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Refunded = "refunded";

    public static readonly string[] All = { AwaitingPayment, Paid, Shipped, Completed, Cancelled, Refunded };

    /// <summary>Which state may follow which. A transition that is not listed is refused — nothing writes <c>status</c> directly.</summary>
    public static bool CanMove(string from, string to) => (from, to) switch
    {
        (AwaitingPayment, Paid) or (AwaitingPayment, Cancelled) => true,
        // Money arrived for an order that had run out of time: the provider's word counts, the order comes back (MarkPaidAsync takes the stock again).
        (Cancelled, Paid) => true,
        (Paid, Shipped) or (Paid, Completed) or (Paid, Refunded) => true,
        (Shipped, Completed) or (Shipped, Refunded) => true,
        (Completed, Refunded) => true,
        _ => false
    };

    /// <summary>The goods are paid for and not given back.</summary>
    public static bool IsPaid(string status) => status is Paid or Shipped or Completed;
}

/// <summary>What a product is.</summary>
internal static class ProductKind
{
    public const string Physical = "physical";
    public const string Digital = "digital";
    public static string Normalise(string? kind) => kind == Digital ? Digital : Physical;
}

/// <summary>A product as the shop works with it.</summary>
internal sealed record Product
{
    public long Id { get; init; }
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string DescriptionHtml { get; init; } = "";
    public string Image { get; init; } = "";
    public string GalleryJson { get; init; } = "[]";
    /// <summary>The options variants are made from ("Größe": S, M, L) as JSON — only a memory for the editor, what is sold are the variants.</summary>
    public string VariantOptions { get; init; } = "[]";
    public string Kind { get; init; } = ProductKind.Physical;
    /// <summary>The price a customer pays, tax included, in the smallest unit.</summary>
    public long PriceCents { get; init; }
    /// <summary>The earlier price, struck through next to the real one (0 = none). Only shown when it is higher than <see cref="PriceCents"/>; what is charged is always the price.</summary>
    public long CompareCents { get; init; }
    /// <summary>The tax rate in hundredths of a percent (1900 = 19 %).</summary>
    public int TaxBp { get; init; } = 1900;
    public string Sku { get; init; } = "";
    /// <summary>False = unlimited (a download, a service).</summary>
    public bool TrackStock { get; init; }
    public long Stock { get; init; }
    public int WeightGrams { get; init; }
    public string Category { get; init; } = "";
    /// <summary>Further categories the product also stands in (slugs, comma-separated); <see cref="Category"/> stays the main one (breadcrumbs, the address of its page).</summary>
    public string ExtraCategories { get; init; } = "";
    /// <summary>What a search engine shows for the page: the title and the description (empty = the product's title and short text).</summary>
    public string SeoTitle { get; init; } = "";
    public string SeoDescription { get; init; } = "";
    /// <summary>Free keywords, stored as typed and joined with ", " (see <see cref="ShopTags"/>).</summary>
    public string Tags { get; init; } = "";
    /// <summary>Shown first where a block or a page asks for the highlighted products.</summary>
    public bool Featured { get; init; }
    public int MaxPerOrder { get; init; }
    public bool Active { get; init; } = true;
    public int Sort { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;

    public bool IsDigital => Kind == ProductKind.Digital;
    /// <summary>The product is offered cheaper than before (a struck-through earlier price is set and higher).</summary>
    public bool OnSale => CompareCents > PriceCents && PriceCents >= 0;
    /// <summary>Percent off the earlier price, rounded down (never promises more than it is).</summary>
    public int SavePercent => OnSale ? (int)((CompareCents - PriceCents) * 100 / CompareCents) : 0;
}

/// <summary>Keywords of a product: typed freely, kept as typed, compared without regard to case. A keyword never contains a comma (the block editor stores a choice as one ", "-joined string).</summary>
internal static class ShopTags
{
    public const int MaxLength = 40;
    public const int MaxPerProduct = 20;

    /// <summary>The keywords of a text typed or stored: split at commas, semicolons and line breaks, trimmed, no empty ones, no duplicates (first spelling wins).</summary>
    public static List<string> Split(string? text)
    {
        var list = new List<string>();
        foreach (var raw in (text ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (t.Length == 0) continue;
            if (t.Length > MaxLength) t = t[..MaxLength].TrimEnd();
            if (list.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(t);
            if (list.Count >= MaxPerProduct) break;
        }
        return list;
    }

    /// <summary>The form a product stores.</summary>
    public static string Normalise(string? text) => string.Join(", ", Split(text));

    public static bool Has(Product p, string tag) => Split(p.Tags).Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
}

/// <summary>How a list of products is asked for: what to show, in which order, and which part of it.</summary>
internal sealed record ProductQuery
{
    public bool ActiveOnly { get; init; } = true;
    public string? Category { get; init; }
    public string? Tag { get; init; }
    public string? Search { get; init; }
    /// <summary>What a search looks at: "title" (name, article number), "text" (+ summary, keywords) or "all" (+ description).</summary>
    public string SearchIn { get; init; } = "text";
    public bool FeaturedOnly { get; init; }
    /// <summary>Only products with a lower price than their earlier one.</summary>
    public bool OnSaleOnly { get; init; }
    /// <summary>A price range in cents (the product's own price, gross), either end open.</summary>
    public long? MinCents { get; init; }
    public long? MaxCents { get; init; }
    /// <summary>When set, only these products (a hand-picked list).</summary>
    public IReadOnlyCollection<long>? Ids { get; init; }
    public long ExceptId { get; init; }
    /// <summary>"" = the shop's own order, "new", "price_asc", "price_desc" or "name".</summary>
    public string Sort { get; init; } = "";
    public int Skip { get; init; }
    public int Take { get; init; }

    public static readonly string[] Sorts = { "", "new", "price_asc", "price_desc", "name" };
    public static string NormaliseSort(string? s) => Sorts.Contains(s ?? "") ? s ?? "" : "";
}

/// <summary>A variant of a product (a size, a colour): its own name, price difference and stock.</summary>
internal sealed record Variant
{
    /// <summary>A picture of this variant (a local path or https address); empty = the product's.</summary>
    public string Image { get; init; } = "";
    public long Id { get; init; }
    public long ProductId { get; init; }
    public string Name { get; init; } = "";
    public string Sku { get; init; } = "";
    public long PriceDeltaCents { get; init; }
    public bool TrackStock { get; init; }
    public long Stock { get; init; }
    public bool Active { get; init; } = true;
    public int Sort { get; init; }
}

/// <summary>A file a digital product delivers. <see cref="StorageName"/> is the name inside the plugin's private files.</summary>
internal sealed record ProductFile
{
    public long Id { get; init; }
    public long ProductId { get; init; }
    public string FileName { get; init; } = "";
    public string StorageName { get; init; } = "";
    public long Size { get; init; }
    public int Sort { get; init; }
}

internal sealed record Category
{
    public long Id { get; init; }
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public int Sort { get; init; }
    /// <summary>The category above this one; 0 = top level.</summary>
    public long ParentId { get; init; }
    /// <summary>A picture for the category's page, tiles and blocks (a local path or https address); empty = none.</summary>
    public string Image { get; init; } = "";
    /// <summary>A short plain text shown on the category's page.</summary>
    public string Description { get; init; } = "";
    /// <summary>Free keywords on the category itself ("Neuware", "Gebrauchtware"), written like a product's: a menu or link can lead to all categories with one of them.</summary>
    public string Tags { get; init; } = "";
    /// <summary>An automatic collection: besides the products filed in the category, every product a rule picks — <c>tag:Neuware</c>, <c>sale</c>, <c>featured</c>, <c>new:30</c> (added in the last 30 days),
    /// <c>price&lt;20</c>, <c>price&gt;100</c>, <c>instock</c> (several rules separated by a semicolon must all hold). Empty = a plain category (see <see cref="ShopCollectionRule"/>).</summary>
    public string Rule { get; init; } = "";
}

/// <summary>One line of a cart: what, which variant (0 = none), how many.</summary>
internal sealed record CartLine(long ProductId, long VariantId, int Qty);

/// <summary>A postal address as it is typed and frozen into an order.</summary>
internal sealed record Address
{
    public string Name { get; init; } = "";
    public string Line1 { get; init; } = "";
    public string Line2 { get; init; } = "";
    public string Zip { get; init; } = "";
    public string City { get; init; } = "";
    /// <summary>Two-letter country code.</summary>
    public string Country { get; init; } = "DE";
    public string Phone { get; init; } = "";

    public bool IsComplete => Name.Trim().Length > 0 && Line1.Trim().Length > 0 && Zip.Trim().Length > 0 && City.Trim().Length > 0 && Country.Trim().Length == 2;

    /// <summary>The address as lines of text, for a mail or an invoice.</summary>
    public string Lines()
    {
        var sb = new StringBuilder();
        void Add(string s) { if (!string.IsNullOrWhiteSpace(s)) sb.AppendLine(s.Trim()); }
        Add(Name); Add(Line1); Add(Line2); Add((Zip.Trim() + " " + City.Trim()).Trim()); Add(Countries.Name(Country));
        return sb.ToString().TrimEnd();
    }
}

/// <summary>An order: the customer, the frozen addresses and totals, the payment. Items are in their own table.</summary>
internal sealed record Order
{
    public long Id { get; init; }
    public string Number { get; init; } = "";
    public string Status { get; init; } = OrderStatus.AwaitingPayment;
    public string CancelReason { get; init; } = "";
    public long MemberId { get; init; }
    public string Email { get; init; } = "";
    public string Name { get; init; } = "";
    public string Phone { get; init; } = "";
    public Address Billing { get; init; } = new();
    public Address Shipping { get; init; } = new();
    public string ShippingMethod { get; init; } = "";
    public long SubtotalCents { get; init; }
    public long ShippingCents { get; init; }
    public long TaxCents { get; init; }
    public long TotalCents { get; init; }
    public long RefundedCents { get; init; }
    public string Currency { get; init; } = "EUR";
    public string PaymentProvider { get; init; } = "";
    public string PaymentRef { get; init; } = "";
    public string PaymentIntent { get; init; } = "";
    public DateTime? PaidUtc { get; init; }
    public string AccessToken { get; init; } = "";
    public string TermsVersion { get; init; } = "";
    public DateTime? TermsAcceptedUtc { get; init; }
    public bool WithdrawalWaiver { get; init; }
    public string Note { get; init; } = "";
    public string Tracking { get; init; } = "";
    /// <summary>The carrier the parcel went with (a key of <see cref="ShopTracking.All"/>, "custom", or "" when none was chosen).</summary>
    public string Carrier { get; init; } = "";
    public string InvoiceNumber { get; init; } = "";
    public string Locale { get; init; } = "de";
    public bool SmallBusiness { get; init; }
    /// <summary>What the coupon took off the goods (gross). <see cref="SubtotalCents"/> is the goods BEFORE it; <see cref="TotalCents"/> = subtotal − discount + shipping.</summary>
    public long DiscountCents { get; init; }
    public string CouponCode { get; init; } = "";
    /// <summary>"" = taxed at the seller's rate, "oss" = at the rate of the country the goods go to, "export" = no VAT (the buyer is outside the EU).</summary>
    public string TaxNote { get; init; } = "";
    /// <summary>The VAT rate the shipping line was charged with, frozen with the order (-1 = an order from before it was stored: the setting of the day).</summary>
    public int ShippingTaxBp { get; init; } = -1;
    public DateTime? ReservedUntilUtc { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>What can still be given back.</summary>
    public long RefundableCents => Math.Max(0, TotalCents - RefundedCents);
}

/// <summary>One line of an order, frozen: later changes to the product never touch it.</summary>
internal sealed record OrderItem
{
    public long Id { get; init; }
    public long OrderId { get; init; }
    public long ProductId { get; init; }
    public long VariantId { get; init; }
    public string Title { get; init; } = "";
    public string VariantTitle { get; init; } = "";
    public string Sku { get; init; } = "";
    public string Kind { get; init; } = ProductKind.Physical;
    public int Qty { get; init; }
    public long UnitCents { get; init; }
    public int TaxBp { get; init; }
    /// <summary>The tax inside <see cref="LineCents"/>.</summary>
    public long TaxCents { get; init; }
    /// <summary>The line BEFORE the coupon (<c>qty × unit</c>).</summary>
    public long LineCents { get; init; }
    /// <summary>The share of the order's coupon that falls on this line; <see cref="TaxCents"/> is the tax inside <c>LineCents − DiscountCents</c>.</summary>
    public long DiscountCents { get; init; }

    /// <summary>What the customer pays for this line.</summary>
    public long NetOfDiscountCents => LineCents - DiscountCents;

    public string DisplayTitle => VariantTitle.Length == 0 ? Title : Title + " – " + VariantTitle;
}

/// <summary>The kinds of coupon.</summary>
internal static class CouponKind
{
    public const string Percent = "percent";
    public const string Fixed = "fixed";
    public const string Shipping = "shipping";
    public static readonly string[] All = { Percent, Fixed, Shipping };
    public static string Normalise(string? k) => All.Contains(k ?? "") ? k! : Percent;
}

/// <summary>A coupon code. <see cref="Value"/> is hundredths of a percent for a percentage and cents for a fixed amount (and unused for free shipping).</summary>
internal sealed record Coupon
{
    public long Id { get; init; }
    public string Code { get; init; } = "";
    public string Kind { get; init; } = CouponKind.Percent;
    public long Value { get; init; }
    /// <summary>Not valid for a cart of goods worth less than this (0 = no minimum).</summary>
    public long MinOrderCents { get; init; }
    /// <summary>How often it may be used in all (0 = without limit).</summary>
    public int MaxUses { get; init; }
    public int Used { get; init; }
    /// <summary>Once per e-mail address.</summary>
    public bool OncePerCustomer { get; init; }
    public DateTime? StartsUtc { get; init; }
    public DateTime? ExpiresUtc { get; init; }
    /// <summary>Only the goods of this category count (empty = everything).</summary>
    public string Category { get; init; } = "";
    public bool Active { get; init; } = true;
    public string Note { get; init; } = "";
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>The form a code is stored and compared in: upper case, letters, digits, dash and underscore, at most 32.</summary>
    public static string NormaliseCode(string? code) => new((code ?? "").Trim().ToUpperInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(32).ToArray());
}

/// <summary>A way of shipping to some countries for some price.</summary>
internal sealed record ShippingRate
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Comma-separated country codes, or <c>*</c> for every country.</summary>
    public string Countries { get; init; } = "*";
    public long PriceCents { get; init; }
    /// <summary>From this order value on, shipping is free (0 = never).</summary>
    public long FreeFromCents { get; init; }
    /// <summary>Not for parcels heavier than this (0 = no limit).</summary>
    public int MaxWeightGrams { get; init; }
    public bool Active { get; init; } = true;
    public int Sort { get; init; }

    public bool Covers(string country) =>
        Countries.Trim() == "*" || Countries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(country, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Countries the shop knows by name. Small on purpose: the list an operator ships to is theirs to extend via shipping rates, and a code not
/// listed is shown as the code.</summary>
internal static class Countries
{
    public static readonly (string Code, string De, string En)[] All =
    {
        ("DE", "Deutschland", "Germany"), ("AT", "Österreich", "Austria"), ("CH", "Schweiz", "Switzerland"), ("LI", "Liechtenstein", "Liechtenstein"),
        ("BE", "Belgien", "Belgium"), ("BG", "Bulgarien", "Bulgaria"), ("HR", "Kroatien", "Croatia"), ("CY", "Zypern", "Cyprus"), ("CZ", "Tschechien", "Czechia"),
        ("DK", "Dänemark", "Denmark"), ("EE", "Estland", "Estonia"), ("FI", "Finnland", "Finland"), ("FR", "Frankreich", "France"), ("GR", "Griechenland", "Greece"),
        ("HU", "Ungarn", "Hungary"), ("IE", "Irland", "Ireland"), ("IT", "Italien", "Italy"), ("LV", "Lettland", "Latvia"), ("LT", "Litauen", "Lithuania"),
        ("LU", "Luxemburg", "Luxembourg"), ("MT", "Malta", "Malta"), ("NL", "Niederlande", "Netherlands"), ("PL", "Polen", "Poland"), ("PT", "Portugal", "Portugal"),
        ("RO", "Rumänien", "Romania"), ("SK", "Slowakei", "Slovakia"), ("SI", "Slowenien", "Slovenia"), ("ES", "Spanien", "Spain"), ("SE", "Schweden", "Sweden"),
        ("GB", "Vereinigtes Königreich", "United Kingdom"), ("NO", "Norwegen", "Norway"), ("US", "USA", "United States")
    };

    public static string Name(string code, string locale = "de")
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        var hit = All.FirstOrDefault(x => x.Code == c);
        return hit.Code is null ? c : locale == "de" ? hit.De : hit.En;
    }

    public static bool Known(string code) => All.Any(x => x.Code == (code ?? "").Trim().ToUpperInvariant());
}

/// <summary>Money is whole cents everywhere; these are the only places that turn it into text or back, or compute tax.</summary>
internal static class Money
{
    /// <summary>The amount for a person: <c>1.234,50 €</c> in German, <c>€1,234.50</c> in English. (No ICU in the image — see InvariantGlobalization — so no culture formatting.)</summary>
    public static string Format(long cents, string currency = "EUR", string locale = "de")
    {
        var neg = cents < 0;
        var abs = Math.Abs(cents);
        var whole = abs / 100; var frac = abs % 100;
        var german = locale != "en";
        var digits = whole.ToString("N0", CultureInfo.InvariantCulture);           // 1,234 with invariant grouping
        digits = german ? digits.Replace(",", ".") : digits;
        var number = digits + (german ? "," : ".") + frac.ToString("00", CultureInfo.InvariantCulture);
        var symbol = Symbol(currency);
        var text = german ? number + " " + symbol : symbol + number;
        return neg ? "−" + text : text;
    }

    private static string Symbol(string currency) => (currency ?? "EUR").ToUpperInvariant() switch
    {
        "EUR" => "€", "USD" => "$", "GBP" => "£", "CHF" => "CHF", var c => c
    };

    /// <summary>The amount as a plain number for an input field: <c>19,90</c> (German) — what the operator types and reads.</summary>
    public static string ForInput(long cents) => (cents < 0 ? "-" : "") + (Math.Abs(cents) / 100).ToString(CultureInfo.InvariantCulture) + "," + (Math.Abs(cents) % 100).ToString("00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads an amount somebody typed: <c>19,90</c>, <c>19.90</c>, <c>1.299,00</c>, <c>1,299.00</c>, <c>19</c>, <c>19,9 €</c>. The LAST separator is the decimal one when it is
    /// followed by one or two digits; otherwise a separator is a thousands mark. False for anything else — nothing is guessed from garbage.
    /// </summary>
    public static bool TryParse(string? text, out long cents)
    {
        cents = 0;
        var s = new string((text ?? "").Where(ch => char.IsDigit(ch) || ch is ',' or '.' or '-').ToArray());
        if (s.Length == 0 || s == "-") return false;
        var neg = s.StartsWith('-');
        s = s.TrimStart('-');
        if (s.Contains('-') || s.Length == 0) return false;
        var lastSep = s.LastIndexOfAny(new[] { ',', '.' });
        string wholePart, frac = "";
        if (lastSep < 0) wholePart = s;
        else
        {
            var after = s[(lastSep + 1)..];
            // The last separator is the decimal one only when one or two digits follow it AND it is the only one of its kind
            // (1.299,00 and 1,299.00 qualify; 1.299 and 1.299.000 are thousands marks).
            if (after.Length is 1 or 2 && s.Count(ch => ch == s[lastSep]) == 1) { wholePart = s[..lastSep]; frac = after; }
            else wholePart = s;
        }
        if (!TryGroups(wholePart, out var digits)) return false;
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var w) || w > 100_000_000_000L) return false;
        var f = frac.Length switch { 0 => 0, 1 => int.Parse(frac, CultureInfo.InvariantCulture) * 10, _ => int.Parse(frac, CultureInfo.InvariantCulture) };
        cents = w * 100 + f;
        if (neg) cents = -cents;
        return true;
    }

    /// <summary>A whole number with optional thousands marks: <c>1299</c>, <c>1.299</c>, <c>1,299,000</c> — the first group has one to three digits, every later group exactly three.</summary>
    private static bool TryGroups(string text, out string digits)
    {
        digits = "";
        if (text.Length == 0) { digits = "0"; return true; }
        var groups = text.Split(new[] { ',', '.' });
        if (groups.Length == 1) { digits = groups[0]; return groups[0].All(char.IsDigit); }
        if (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(g => g.Length != 3) || groups.Any(g => !g.All(char.IsDigit))) return false;
        digits = string.Concat(groups);
        return true;
    }

    /// <summary>Splits <paramref name="total"/> into parts proportional to <paramref name="weights"/> so the parts add up to EXACTLY the total (largest remainder: the cents that do not divide go to the biggest remainders).</summary>
    public static long[] Allocate(long total, IReadOnlyList<long> weights)
    {
        var parts = new long[weights.Count];
        var sum = weights.Sum();
        if (total <= 0 || sum <= 0) return parts;
        var rest = new (int Index, decimal Fraction)[weights.Count];
        long given = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            var exact = (decimal)total * weights[i] / sum;
            parts[i] = (long)Math.Floor(exact);
            given += parts[i];
            rest[i] = (i, exact - parts[i]);
        }
        foreach (var r in rest.OrderByDescending(r => r.Fraction).ThenBy(r => r.Index).Take((int)(total - given))) parts[r.Index]++;
        return parts;
    }

    /// <summary>The tax INSIDE a gross amount: <c>gross × rate / (100 % + rate)</c>, rounded to the cent (half away from zero).</summary>
    public static long TaxIncluded(long grossCents, int taxBp) =>
        taxBp <= 0 ? 0 : (long)Math.Round(grossCents * (decimal)taxBp / (10000m + taxBp), MidpointRounding.AwayFromZero);

    /// <summary>A rate as the person says it: <c>19</c> or <c>7,5</c> (a percent sign is added by the caller).</summary>
    public static string Percent(int taxBp) => taxBp % 100 == 0 ? (taxBp / 100).ToString(CultureInfo.InvariantCulture)
        : (taxBp / 100m).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>Reads a percent a person typed (<c>19</c>, <c>7,5</c>) as hundredths of a percent; false when it is not a number between 0 and 100.</summary>
    public static bool TryParsePercent(string? text, out int bp)
    {
        bp = 0;
        var s = (text ?? "").Trim().Replace("%", "").Replace(',', '.').Trim();
        if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) || d < 0 || d > 100) return false;
        bp = (int)Math.Round(d * 100, MidpointRounding.AwayFromZero);
        return true;
    }
}
