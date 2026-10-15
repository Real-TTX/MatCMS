using System.Globalization;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>The setting keys of the shop and what they default to. The operator edits them under Shop → Einstellungen.</summary>
internal static class Keys
{
    public const string SellerName = "seller.name";
    public const string SellerAddress = "seller.address";
    public const string SellerEmail = "seller.email";
    public const string SellerPhone = "seller.phone";
    public const string SellerVatId = "seller.vatId";
    public const string SellerTaxNumber = "seller.taxNumber";
    public const string InvoiceFooter = "seller.invoiceFooter";
    public const string Currency = "currency";
    public const string SmallBusiness = "tax.smallBusiness";
    public const string ShippingTaxPercent = "tax.shippingPercent";
    public const string DefaultTaxPercent = "tax.defaultPercent";
    public const string OrderStart = "order.start";
    public const string OrderPrefix = "order.prefix";
    public const string InvoicePrefix = "invoice.prefix";
    public const string CreditPrefix = "credit.prefix";
    public const string TaxMode = "tax.mode";
    public const string HomeCountry = "tax.homeCountry";
    public const string OssKeepGross = "tax.ossKeepGross";
    public const string TaxRates = "tax.rates";
    public const string TermsUrl = "legal.termsUrl";
    public const string WithdrawalUrl = "legal.withdrawalUrl";
    public const string PrivacyUrl = "legal.privacyUrl";
    public const string TermsVersion = "legal.version";
    public const string BankEnabled = "bank.enabled";
    public const string BankDetails = "bank.details";
    public const string BankHoldDays = "bank.holdDays";
    public const string PayPalEnabled = "paypal.enabled";
    public const string PayPalClientId = "paypal.clientId";
    public const string PayPalSecret = "paypal.secret";
    public const string PayPalWebhookId = "paypal.webhookId";
    public const string PayPalSandbox = "paypal.sandbox";
    public const string StripeEnabled = "stripe.enabled";
    public const string StripeSecret = "stripe.secretKey";
    public const string StripeWebhookSecret = "stripe.webhookSecret";
    public const string NotifyEmail = "notify.email";
    public const string AccountsAtCheckout = "checkout.accounts";
    public const string GuestCheckout = "checkout.guest";
    public const string PerPage = "catalog.perPage";
    public const string DownloadDays = "downloads.days";
    public const string DownloadLimit = "downloads.limit";
    public const string CustomCss = "design.customCss";
    public const string LowStock = "stock.low";
}

/// <summary>The shop's settings, read once per request into plain values.</summary>
internal sealed record ShopConfig
{
    public string SellerName { get; init; } = "";
    public string SellerAddress { get; init; } = "";
    public string SellerEmail { get; init; } = "";
    public string SellerPhone { get; init; } = "";
    public string SellerVatId { get; init; } = "";
    public string SellerTaxNumber { get; init; } = "";
    public string InvoiceFooter { get; init; } = "";
    public string Currency { get; init; } = "EUR";
    public bool SmallBusiness { get; init; }
    public int ShippingTaxBp { get; init; } = 1900;
    public int DefaultTaxBp { get; init; } = 1900;
    public long OrderStart { get; init; } = 1000;
    public string OrderPrefix { get; init; } = "";
    public string InvoicePrefix { get; init; } = "RE-";
    public string CreditPrefix { get; init; } = "GS-";
    /// <summary>"domestic" = the product's rate everywhere; "oss" = the rate of the country the goods go to (see <see cref="ShopTax"/>).</summary>
    public string TaxMode { get; init; } = "domestic";
    public string HomeCountry { get; init; } = "DE";
    /// <summary>With OSS: the price shown stays what the customer pays (the VAT inside it changes), instead of the net price staying.</summary>
    public bool OssKeepGross { get; init; } = true;
    public string TaxRateOverrides { get; init; } = "";
    public IReadOnlyDictionary<string, VatRate> TaxRates { get; init; } = new Dictionary<string, VatRate>();
    /// <summary>Tax by destination is on (and meaningful: a small business charges no VAT at all).</summary>
    public bool Oss => TaxMode == "oss" && !SmallBusiness;
    public string TermsUrl { get; init; } = "";
    public string WithdrawalUrl { get; init; } = "";
    public string PrivacyUrl { get; init; } = "";
    public string TermsVersion { get; init; } = "1";
    public bool BankEnabled { get; init; }
    public string BankDetails { get; init; } = "";
    public int BankHoldDays { get; init; } = 14;
    public bool PayPalEnabled { get; init; }
    public string PayPalClientId { get; init; } = "";
    public string PayPalSecret { get; init; } = "";
    public string PayPalWebhookId { get; init; } = "";
    /// <summary>PayPal's test environment: no real money moves.</summary>
    public bool PayPalSandbox { get; init; }
    public bool StripeEnabled { get; init; }
    public string StripeSecret { get; init; } = "";
    public string StripeWebhookSecret { get; init; } = "";
    public string NotifyEmail { get; init; } = "";
    public bool AccountsAtCheckout { get; init; } = true;
    public bool GuestCheckout { get; init; } = true;
    public int PerPage { get; init; } = 12;
    public int DownloadDays { get; init; } = 30;
    public int DownloadLimit { get; init; } = 5;
    public string CustomCss { get; init; } = "";
    public int LowStock { get; init; } = 3;

    /// <summary>PayPal is on, and has what it needs to take a payment.</summary>
    public bool PayPalReady => PayPalEnabled && PayPalClientId.Length > 5 && PayPalSecret.Length > 5;
    /// <summary>Stripe is on, and has what it needs to take a payment.</summary>
    public bool StripeReady => StripeEnabled && StripeSecret.StartsWith("sk_", StringComparison.Ordinal) && StripeSecret.Length > 12;
    /// <summary>The secret key is a test key: no real money moves.</summary>
    public bool StripeTestMode => StripeSecret.StartsWith("sk_test_", StringComparison.Ordinal) || StripeSecret.StartsWith("rk_test_", StringComparison.Ordinal);

    public static ShopConfig From(IPluginSettings s)
    {
        int Pct(string key, int def) => Money.TryParsePercent(s.Get(key), out var bp) ? bp : def;
        return new ShopConfig
        {
            SellerName = s.Get(Keys.SellerName).Trim(), SellerAddress = s.Get(Keys.SellerAddress).Trim(), SellerEmail = s.Get(Keys.SellerEmail).Trim(),
            SellerPhone = s.Get(Keys.SellerPhone).Trim(), SellerVatId = s.Get(Keys.SellerVatId).Trim(), SellerTaxNumber = s.Get(Keys.SellerTaxNumber).Trim(),
            InvoiceFooter = s.Get(Keys.InvoiceFooter).Trim(),
            Currency = Currencies.Normalise(s.Get(Keys.Currency)),
            SmallBusiness = s.GetBool(Keys.SmallBusiness),
            ShippingTaxBp = Pct(Keys.ShippingTaxPercent, 1900), DefaultTaxBp = Pct(Keys.DefaultTaxPercent, 1900),
            OrderStart = Math.Max(1, s.GetInt(Keys.OrderStart, 1000)), OrderPrefix = Safe(s.Get(Keys.OrderPrefix)),
            InvoicePrefix = Safe(s.Get(Keys.InvoicePrefix)), CreditPrefix = s.Get(Keys.CreditPrefix).Trim().Length == 0 ? "GS-" : Safe(s.Get(Keys.CreditPrefix)),
            TaxMode = s.Get(Keys.TaxMode) == "oss" ? "oss" : "domestic",
            HomeCountry = Countries.Known(s.Get(Keys.HomeCountry)) ? s.Get(Keys.HomeCountry).Trim().ToUpperInvariant() : "DE",
            OssKeepGross = s.GetBool(Keys.OssKeepGross),
            TaxRateOverrides = s.Get(Keys.TaxRates), TaxRates = ShopTax.ParseOverrides(s.Get(Keys.TaxRates)),
            TermsUrl = LocalOrHttp(s.Get(Keys.TermsUrl)), WithdrawalUrl = LocalOrHttp(s.Get(Keys.WithdrawalUrl)), PrivacyUrl = LocalOrHttp(s.Get(Keys.PrivacyUrl)),
            TermsVersion = string.IsNullOrWhiteSpace(s.Get(Keys.TermsVersion)) ? "1" : s.Get(Keys.TermsVersion).Trim(),
            BankEnabled = s.GetBool(Keys.BankEnabled), BankDetails = s.Get(Keys.BankDetails).Trim(), BankHoldDays = (int)Math.Clamp(s.GetInt(Keys.BankHoldDays, 14), 1, 90),
            PayPalEnabled = s.GetBool(Keys.PayPalEnabled), PayPalClientId = s.Get(Keys.PayPalClientId).Trim(), PayPalSecret = s.Get(Keys.PayPalSecret).Trim(),
            PayPalWebhookId = s.Get(Keys.PayPalWebhookId).Trim(), PayPalSandbox = s.GetBool(Keys.PayPalSandbox),
            StripeEnabled = s.GetBool(Keys.StripeEnabled), StripeSecret = s.Get(Keys.StripeSecret).Trim(), StripeWebhookSecret = s.Get(Keys.StripeWebhookSecret).Trim(),
            NotifyEmail = s.Get(Keys.NotifyEmail).Trim(),
            AccountsAtCheckout = s.GetBool(Keys.AccountsAtCheckout), GuestCheckout = s.GetBool(Keys.GuestCheckout),
            PerPage = (int)Math.Clamp(s.GetInt(Keys.PerPage, 12), 3, 60),
            DownloadDays = (int)Math.Clamp(s.GetInt(Keys.DownloadDays, 30), 1, 3650), DownloadLimit = (int)Math.Clamp(s.GetInt(Keys.DownloadLimit, 5), 0, 1000),
            CustomCss = s.Get(Keys.CustomCss),
            LowStock = (int)Math.Clamp(s.GetInt(Keys.LowStock, 3), 0, 1000)
        };
    }

    /// <summary>A prefix for a number: letters, digits, dash, underscore. Anything else is dropped — it ends up in file names and URLs.</summary>
    private static string Safe(string text) => new((text ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(12).ToArray());

    /// <summary>A link an operator configured: a local path or an http(s) address; anything else (<c>javascript:</c>) is dropped.</summary>
    internal static string LocalOrHttp(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.StartsWith('/') && !u.StartsWith("//") && !u.StartsWith("/\\")) return u;
        return u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? u : "";
    }

    /// <summary>The settings the shop declares, in the order and groups the form shows them.</summary>
    public static void Declare(ISettingsBuilder s)
    {
        s.Add(new SettingSpec(Keys.StripeEnabled, "set.stripeEnabled", SettingKind.Bool) { Group = "set.groupStripe", Help = "set.stripeEnabledHelp" });
        s.Add(new SettingSpec(Keys.StripeSecret, "set.stripeSecret", SettingKind.Secret) { Help = "set.stripeSecretHelp" });
        s.Add(new SettingSpec(Keys.StripeWebhookSecret, "set.stripeWebhook", SettingKind.Secret) { Help = "set.stripeWebhookHelp" });
        s.Add(new SettingSpec(Keys.PayPalEnabled, "set.paypalEnabled", SettingKind.Bool) { Group = "set.groupPayPal", Help = "set.paypalEnabledHelp" });
        s.Add(new SettingSpec(Keys.PayPalClientId, "set.paypalClientId") { Help = "set.paypalClientIdHelp" });
        s.Add(new SettingSpec(Keys.PayPalSecret, "set.paypalSecret", SettingKind.Secret));
        s.Add(new SettingSpec(Keys.PayPalWebhookId, "set.paypalWebhookId") { Help = "set.paypalWebhookIdHelp" });
        s.Add(new SettingSpec(Keys.PayPalSandbox, "set.paypalSandbox", SettingKind.Bool) { Help = "set.paypalSandboxHelp" });
        s.Add(new SettingSpec(Keys.BankEnabled, "set.bankEnabled", SettingKind.Bool) { Group = "set.groupBank", Help = "set.bankEnabledHelp" });
        s.Add(new SettingSpec(Keys.BankDetails, "set.bankDetails", SettingKind.TextArea) { Help = "set.bankDetailsHelp" });
        s.Add(new SettingSpec(Keys.BankHoldDays, "set.bankHold", SettingKind.Number) { Default = "14", Min = 1, Max = 90, Help = "set.bankHoldHelp" });
        s.Add(new SettingSpec(Keys.SellerName, "set.sellerName") { Group = "set.groupSeller", Help = "set.sellerNameHelp" });
        s.Add(new SettingSpec(Keys.SellerAddress, "set.sellerAddress", SettingKind.TextArea));
        s.Add(new SettingSpec(Keys.SellerEmail, "set.sellerEmail"));
        s.Add(new SettingSpec(Keys.SellerPhone, "set.sellerPhone"));
        s.Add(new SettingSpec(Keys.SellerVatId, "set.sellerVatId"));
        s.Add(new SettingSpec(Keys.SellerTaxNumber, "set.sellerTaxNumber"));
        s.Add(new SettingSpec(Keys.InvoiceFooter, "set.invoiceFooter", SettingKind.TextArea) { Help = "set.invoiceFooterHelp" });
        s.Add(new SettingSpec(Keys.Currency, "set.currency", SettingKind.Choice)
        {
            Group = "set.groupMoney", Default = "EUR", Options = Currencies.All.Select(c => new FieldOption(c, c)).ToList()
        });
        s.Add(new SettingSpec(Keys.SmallBusiness, "set.smallBusiness", SettingKind.Bool) { Help = "set.smallBusinessHelp" });
        s.Add(new SettingSpec(Keys.DefaultTaxPercent, "set.defaultTax") { Default = "19", Help = "set.defaultTaxHelp" });
        s.Add(new SettingSpec(Keys.ShippingTaxPercent, "set.shippingTax") { Default = "19", Help = "set.shippingTaxHelp" });
        s.Add(new SettingSpec(Keys.TaxMode, "set.taxMode", SettingKind.Choice)
        {
            Default = "domestic", Help = "set.taxModeHelp",
            Options = new List<FieldOption> { new("domestic", "set.taxMode.domestic"), new("oss", "set.taxMode.oss") }
        });
        s.Add(new SettingSpec(Keys.HomeCountry, "set.homeCountry") { Default = "DE", Help = "set.homeCountryHelp" });
        s.Add(new SettingSpec(Keys.OssKeepGross, "set.ossKeepGross", SettingKind.Bool) { Default = "true", Help = "set.ossKeepGrossHelp" });
        s.Add(new SettingSpec(Keys.TaxRates, "set.taxRates", SettingKind.TextArea) { Help = "set.taxRatesHelp" });
        s.Add(new SettingSpec(Keys.TermsUrl, "set.termsUrl") { Group = "set.groupLegal", Help = "set.termsUrlHelp" });
        s.Add(new SettingSpec(Keys.WithdrawalUrl, "set.withdrawalUrl"));
        s.Add(new SettingSpec(Keys.PrivacyUrl, "set.privacyUrl"));
        s.Add(new SettingSpec(Keys.TermsVersion, "set.termsVersion") { Default = "1", Help = "set.termsVersionHelp" });
        s.Add(new SettingSpec(Keys.OrderStart, "set.orderStart", SettingKind.Number) { Group = "set.groupNumbers", Default = "1000", Min = 1, Help = "set.orderStartHelp" });
        s.Add(new SettingSpec(Keys.OrderPrefix, "set.orderPrefix") { Help = "set.orderPrefixHelp" });
        s.Add(new SettingSpec(Keys.InvoicePrefix, "set.invoicePrefix") { Default = "RE-", Help = "set.invoicePrefixHelp" });
        s.Add(new SettingSpec(Keys.CreditPrefix, "set.creditPrefix") { Default = "GS-", Help = "set.creditPrefixHelp" });
        s.Add(new SettingSpec(Keys.GuestCheckout, "set.guestCheckout", SettingKind.Bool) { Group = "set.groupCheckout", Default = "true", Help = "set.guestCheckoutHelp" });
        s.Add(new SettingSpec(Keys.AccountsAtCheckout, "set.accounts", SettingKind.Bool) { Default = "true", Help = "set.accountsHelp" });
        s.Add(new SettingSpec(Keys.NotifyEmail, "set.notifyEmail") { Help = "set.notifyEmailHelp" });
        s.Add(new SettingSpec(Keys.DownloadDays, "set.downloadDays", SettingKind.Number) { Group = "set.groupDownloads", Default = "30", Min = 1, Max = 3650 });
        s.Add(new SettingSpec(Keys.DownloadLimit, "set.downloadLimit", SettingKind.Number) { Default = "5", Min = 0, Max = 1000, Help = "set.downloadLimitHelp" });
        s.Add(new SettingSpec(Keys.PerPage, "set.perPage", SettingKind.Number) { Group = "set.groupShop", Default = "12", Min = 3, Max = 60 });
        s.Add(new SettingSpec(Keys.LowStock, "set.lowStock", SettingKind.Number) { Default = "3", Min = 0, Max = 1000, Help = "set.lowStockHelp" });
        s.Add(new SettingSpec(Keys.CustomCss, "set.customCss", SettingKind.TextArea) { Help = "set.customCssHelp" });
    }
}

/// <summary>The currencies the shop offers.</summary>
internal static class Currencies
{
    public static readonly string[] All = { "EUR", "CHF", "GBP", "USD" };
    public static string Normalise(string? c) => All.Contains((c ?? "").Trim().ToUpperInvariant()) ? c!.Trim().ToUpperInvariant() : "EUR";
}
