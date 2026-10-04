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

    /// <summary>
    /// "This site sits behind a proxy that terminates TLS."
    /// <para>Without it the app only sees the unencrypted hop to the proxy and builds every absolute
    /// address it emits — redirects above all — with http://. A browser on an https page then refuses
    /// them; the cloud's embedded view is where that shows up first, because a redirect to the login
    /// page turns into blocked mixed content.</para>
    /// <para>A SETTING and not only the environment variable, because it can then be rolled out from
    /// a cloud profile to a whole fleet at once — which is how a fleet gets this wrong: everywhere,
    /// for the same reason, on the same day.</para>
    /// </summary>
    public const string BehindHttpsProxy = "site.behindHttpsProxy";

    /// <summary>"1" = this instance is meant to be logged into inside the cloud admin's iframe, so its
    /// auth and antiforgery cookies switch to <c>SameSite=None; Secure</c>. A SETTING, not only the old
    /// <c>MatCms:EmbedAuth</c> env var, so the cloud can roll it out per profile — no container env
    /// editing. Requires HTTPS (a None cookie without Secure is rejected); a change takes effect on the
    /// next instance start, which the cloud can trigger for the instances it manages.</summary>
    public const string EmbedAuth = "site.embedAuth";

    /// <summary>"1" = show a "Mit Cloud anmelden" button on the login page and enable the SSO flow
    /// (/sso/start + /sso/callback): a user signs in with their MatCMS.Cloud account. Only works when
    /// this instance is connected to a cloud. A rollable DB setting (not env), like <see cref="EmbedAuth"/>
    /// — the cloud can switch it on per profile. Local login always stays available.</summary>
    public const string SsoEnabled = "sso.enabled";

    /// <summary>"1" = every back-office admin must set up two-factor auth; one who has not is funnelled
    /// to the enrolment page on their next admin request (forced setup, not a lock-out). Optional per
    /// account otherwise. A rollable DB setting (not env), like <see cref="SsoEnabled"/> — the cloud can
    /// switch it on for a whole fleet from a profile. The prefix is deliberately NOT one of the
    /// group-key prefixes (smtp./translate./backup./mail.transport) so it rides the free-settings
    /// rollout.</summary>
    public const string Require2fa = "security.require2fa";

    // "1" once the setup wizard has been completed (drives the dashboard prompt).
    public const string SetupComplete = "setup.complete";

    /// <summary>Site-wide DEFAULT anti-spam level for forms ("0".."3"; empty = "1"). A form may override
    /// it with its own <c>Form.SpamLevel</c>. A SETTING (not env) so the cloud can roll one policy out to
    /// a whole fleet — see <see cref="FormGuard"/>. 0 off, 1 invisible, 2 +proof-of-work, 3 +captcha.</summary>
    public const string AntiSpamLevel = "antispam.level";

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
    /// <summary>How this site sends mail: "smtp" (itself) or "cloud" (hand each message to the
    /// connected cloud, which spools and delivers it). Written by the cloud sync when a profile
    /// decides it; absent means smtp, which is what every site did before the relay existed.</summary>
    public const string MailTransport = "mail.transport";

    /// <summary>How this site uses AI: "cloud" (relay each model call through the connected cloud, which
    /// holds the provider key) or "off"/empty (no AI features). Rolled out from a profile's AI group —
    /// never a free key and never the key itself — same shape as <see cref="MailTransport"/>. Read via
    /// SiteContext.Get; the whole AI feature is gated on it.</summary>
    public const string AiTransport = "ai.transport";

    /// <summary>An always-on instruction/context the cloud rolled out for this site's AI (brand, tone,
    /// language, facts). Prepended to every relayed AI system prompt. Empty = none. Set only via the
    /// cloud profile — never edited on the instance.</summary>
    public const string AiInstruction = "ai.instruction";

    /// <summary>Per-action AI guidance rolled out from the cloud profile: a JSON map purpose→text
    /// ("pagegen"/"sitegen"/"theme"/"seo"), each augmenting that action's built-in prompt. Empty = none.
    /// Set only via the cloud profile — never edited on the instance.</summary>
    public const string AiActionGuides = "ai.actionGuides";

    /// <summary>Whether finished backups are handed to the connected cloud. Off unless somebody says
    /// otherwise — uploading a customer's whole site somewhere is a decision, not a default.</summary>
    public const string BackupToCloud = "backup.toCloud";

    public const string SmtpHost = "smtp.host";
    public const string SmtpPort = "smtp.port";
    public const string SmtpUser = "smtp.user";
    public const string SmtpPassword = "smtp.password";
    public const string SmtpFromEmail = "smtp.fromEmail";
    public const string SmtpFromName = "smtp.fromName";
    public const string SmtpSsl = "smtp.ssl";

    // Log system (Admin → Protokoll). Request logging is opt-in because it can grow fast; retention is
    // per category, in days (0 = keep, bounded only by the hard count cap). Read cached in the
    // middleware and by the retention sweeper.
    public const string LogRequests = "log.requests";                        // "on" = log EVERY HTTP request (category "webrequest")
    public const string LogRetentionErrorsDays = "log.retentionErrorsDays";  // errors/5xx (category "request"); default 90
    public const string LogRetentionRequestsDays = "log.retentionRequestsDays"; // full request log (category "webrequest"); default 14

    // Visitor statistics (Admin → Statistik). On unless switched off: cookie-less, only daily counts, no address
    // stored. Retention in days (default 400 — a year plus the same weeks of the year before, for comparison).
    public const string StatsEnabled = "stats.enabled";              // "0" = off; anything else = on
    public const string StatsRetentionDays = "stats.retentionDays";

    // MatCMS.Cloud link (Settings → Cloud). The cloud watches versions, notifies, and — when this
    // instance runs on ITS Docker host — can perform updates. Empty URL/id/token = fully offline.
    // The token is stored DataProtection-ENCRYPTED (see CloudService), never in the clear.
    public const string CloudUrl = "cloud.url";

    /// <summary>Die Adresse, unter der ein BETREIBER die Cloud aufruft — von ihr gemeldet, weil nur
    /// sie sie kennt. Kann sich von <see cref="CloudUrl"/> unterscheiden, wenn die Instanz die Cloud
    /// über einen internen Namen erreicht. Wird gebraucht, um die Einbettung zu erlauben.</summary>
    public const string CloudPublicUrl = "cloud.publicUrl";
    public const string CloudInstanceId = "cloud.instanceId";
    public const string CloudToken = "cloud.token";

    /// <summary>Profile revision this instance last applied successfully. Persisted (not in-memory)
    /// so a restart does not re-apply the whole configuration.</summary>
    public const string CloudAppliedRevision = "cloud.appliedRevision";

    /// <summary>Why the last apply failed; empty when it succeeded. Reported back on every heartbeat.</summary>
    public const string CloudSyncError = "cloud.syncError";

    /// <summary>The per-item outcome of the last apply, as JSON. Sent to the cloud on the next
    /// heartbeat so it can show what actually happened without deriving anything.</summary>
    public const string CloudSyncReport = "cloud.syncReport";

    /// <summary>Which payloads a "once" profile has already seeded here, as
    /// <c>&lt;profileId&gt;|settings,users,…</c>. The profile id is part of the value on purpose:
    /// moving this site to another profile must let that profile seed once as well, and comparing
    /// the id is cheaper — and harder to get wrong — than clearing the mark on every link change.</summary>
    public const string CloudSeeded = "cloud.seeded";

    /// <summary>When the last apply finished (round-trip UTC). Sent on the heartbeat so the cloud can
    /// tell a NEW run from the same report being repeated every minute.</summary>
    public const string CloudSyncRunAt = "cloud.syncRunAt";

    // Note: TopBarLink1/2 are intentionally NOT here — the top bar moved to the "toolbar" menu.
    // The constants remain for the one-time migration in DbSeeder.
    public static readonly string[] All =
    [
        CanonicalUrl, BehindHttpsProxy, SiteName, LogoUrl, FaviconUrl,
        FooterText, ContactRecipient, AntiSpamLevel
    ];

    /// <summary>SMTP setting keys (managed on the Settings → SMTP tab).</summary>
    public static readonly string[] Smtp =
    [
        SmtpHost, SmtpPort, SmtpUser, SmtpPassword, SmtpFromEmail, SmtpFromName, SmtpSsl
    ];

    /// <summary>Error-handling setting keys (managed on the Settings → Fehlerhandling tab).</summary>
    public static readonly string[] Errors = [NotFoundPage, ErrorPage];

    /// <summary>Security-policy keys (managed on the Settings → Sicherheit tab).</summary>
    public static readonly string[] Security = [Require2fa];

    // Settings → Protokoll & Statistik.
    public static readonly string[] Logs = [LogRequests, LogRetentionErrorsDays, LogRetentionRequestsDays, StatsEnabled, StatsRetentionDays];

    /// <summary>Custom-code / tracking keys (managed on the Settings → Code tab).</summary>
    public static readonly string[] Code = [AnalyticsGa4, CodeHead, CodeBodyStart, CodeBodyEnd];

    /// <summary>Maintenance-mode keys (managed on the Settings → Wartung tab).</summary>
    public static readonly string[] Maintenance = [MaintenanceEnabled, MaintenanceTitle, MaintenanceMessage];

    /// <summary>Machine-translation keys (managed on the Settings → Sprachen tab).</summary>
    public static readonly string[] Translate = [TranslateProvider, TranslateApiKey, TranslateUrl];

    // "Beitrag aus Link": access to the site's OWN Instagram / Facebook accounts through Meta's API, so
    // a post can be taken over completely (full caption, every carousel image). Without them the import
    // falls back to the public preview data, which Meta mostly withholds. Per site, never rolled out —
    // a token names one account. The Instagram token is long-lived (60 days) and is refreshed by the
    // import itself; RefreshedAt records when, so it is not refreshed on every call.
    public const string SocialInstagramToken = "social.instagram.token";
    public const string SocialInstagramRefreshedAt = "social.instagram.refreshedAt";
    public const string SocialFacebookToken = "social.facebook.token";

    /// <summary>Social access keys edited on Settings → Social Media (RefreshedAt is written by the import).</summary>
    public static readonly string[] Social = [SocialInstagramToken, SocialFacebookToken];

    /// <summary>MatCMS.Cloud link keys (managed on the Settings → Cloud tab). Deliberately NOT part
    /// of any generic save path — the token needs encrypting, so CloudService owns these. Also the
    /// deny-list for pushed settings: a profile must never be able to rewrite the cloud link.</summary>
    public static readonly string[] Cloud =
        [CloudUrl, CloudPublicUrl, CloudInstanceId, CloudToken, CloudAppliedRevision, CloudSyncError, CloudSyncReport, CloudSeeded, CloudSyncRunAt];
}
