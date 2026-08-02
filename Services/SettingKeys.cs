namespace MatCMS.Services;

public static class SettingKeys
{
    public const string SiteName = "SiteName";
    public const string LogoUrl = "LogoUrl";
    public const string FaviconUrl = "FaviconUrl";
    public const string TopBarLink1Text = "TopBarLink1Text";
    public const string TopBarLink1Url = "TopBarLink1Url";
    public const string TopBarLink2Text = "TopBarLink2Text";
    public const string TopBarLink2Url = "TopBarLink2Url";
    public const string FooterText = "FooterText";
    public const string ContactRecipient = "ContactRecipient";

    // Error handling: slug of the page shown for 404 / server errors (empty = built-in default).
    public const string NotFoundPage = "error.notFoundPage";
    public const string ErrorPage = "error.errorPage";

    // i18n: comma-separated list of ACTIVE content languages (besides the always-on default "de"),
    // e.g. "en,fr". Managed under Settings → Sprachen. Only routable codes count (Localizer).
    public const string Languages = "i18n.languages";

    // i18n: the DEFAULT (root) content language served at prefix-less URLs. Empty = "de". Applied at
    // startup (Localizer.SetDefaultCulture) → a change needs an app restart. Managed under Settings → Sprachen.
    public const string DefaultLanguage = "i18n.default";

    // Machine translation (Settings → Sprachen): provider "deepl" | "libretranslate" | "" (off).
    // DeepL free keys end in ":fx" (api-free.deepl.com); LibreTranslate needs a reachable URL
    // (self-hosted container or public instance), key optional.
    public const string TranslateProvider = "translate.provider";
    public const string TranslateApiKey = "translate.apiKey";
    public const string TranslateUrl = "translate.url";

    // SEO: "true" serves /sitemap.xml (+ a /robots.txt that references it); anything else = off.
    public const string SitemapEnabled = "sitemap.enabled";

    // Optional public base URL (e.g. "https://example.com") used for absolute links in the sitemap /
    // robots.txt. Empty = derive from the request (only correct when not behind a scheme-changing proxy).
    public const string CanonicalUrl = "site.canonicalUrl";

    // "1" once the setup wizard has been completed (drives the dashboard prompt).
    public const string SetupComplete = "setup.complete";

    // Maintenance / "coming soon" mode (Settings → Wartung). When on, public visitors get a themed
    // maintenance page (HTTP 503); admins bypass it. Title/message are editable here; the standard page
    // uses the active template's colours and can be overridden via its "maintenance.html" layout part.
    public const string MaintenanceEnabled = "maintenance.enabled"; // "1" = on
    public const string MaintenanceTitle = "maintenance.title";
    public const string MaintenanceMessage = "maintenance.message";

    // Custom code / tracking (own tab under Settings). Raw HTML injected site-wide.
    public const string CodeHead = "code.head";           // before </head>
    public const string CodeBodyStart = "code.bodyStart"; // right after <body>
    public const string CodeBodyEnd = "code.bodyEnd";     // before </body>
    public const string AnalyticsGa4 = "analytics.ga4";   // GA4 Measurement-ID (G-XXXXXXX) → auto-snippet

    // SMTP / e-mail settings (own tab under Settings; kept out of `All` so each form saves only its own keys).
    public const string SmtpHost = "smtp.host";
    public const string SmtpPort = "smtp.port";
    public const string SmtpUser = "smtp.user";
    public const string SmtpPassword = "smtp.password";
    public const string SmtpFromEmail = "smtp.fromEmail";
    public const string SmtpFromName = "smtp.fromName";
    public const string SmtpSsl = "smtp.ssl";

    // Note: TopBarLink1/2 are intentionally NOT here — the top bar moved to the "toolbar" menu.
    // The constants remain for the one-time migration in DbSeeder.
    public static readonly string[] All =
    [
        CanonicalUrl, SiteName, LogoUrl, FaviconUrl,
        FooterText, ContactRecipient
    ];

    /// <summary>SMTP setting keys (managed on the Settings → SMTP tab).</summary>
    public static readonly string[] Smtp =
    [
        SmtpHost, SmtpPort, SmtpUser, SmtpPassword, SmtpFromEmail, SmtpFromName, SmtpSsl
    ];

    /// <summary>Error-handling setting keys (managed on the Settings → Fehlerhandling tab).</summary>
    public static readonly string[] Errors = [NotFoundPage, ErrorPage];

    /// <summary>Custom-code / tracking keys (managed on the Settings → Code tab).</summary>
    public static readonly string[] Code = [AnalyticsGa4, CodeHead, CodeBodyStart, CodeBodyEnd];

    /// <summary>Maintenance-mode keys (managed on the Settings → Wartung tab).</summary>
    public static readonly string[] Maintenance = [MaintenanceEnabled, MaintenanceTitle, MaintenanceMessage];

    /// <summary>Machine-translation keys (managed on the Settings → Sprachen tab).</summary>
    public static readonly string[] Translate = [TranslateProvider, TranslateApiKey, TranslateUrl];
}
