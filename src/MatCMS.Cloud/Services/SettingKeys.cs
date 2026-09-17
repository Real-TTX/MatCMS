namespace MatCMS.Cloud.Services;

public static class SettingKeys
{
    /// <summary>Display name of this cloud (browser title, mail sender name fallback).</summary>
    public const string CloudName = "cloud.name";

    /// <summary>Public base URL of the cloud, e.g. "https://cloud.example.com". Used for the links in
    /// notification mails and for the pairing instructions. Empty = derived from the request.</summary>
    public const string CanonicalUrl = "cloud.canonicalUrl";

    /// <summary>How many GB of backups ONE instance may occupy. Beyond it the oldest is dropped, so
    /// this is the number that decides how far back a site can be restored — not a technical limit
    /// but a policy, which is why it belongs in the settings and not in a constant.</summary>
    public const string BackupQuotaGb = "backup.quotaGb";
    // Cloud-wide retention defaults (used when a profile leaves the field empty). All optional; 0 = off.
    public const string BackupKeepDaily = "backup.keepDaily";
    public const string BackupKeepWeekly = "backup.keepWeekly";
    public const string BackupKeepMonthly = "backup.keepMonthly";
    public const string BackupMaxCount = "backup.maxCount";

    /// <summary>
    /// "My instances are reachable over https, whatever they report."
    /// <para>An instance behind a TLS-terminating proxy sees only the unencrypted hop and reports
    /// http. An https cloud may then neither frame nor link it. The proper fix is forwarded headers
    /// ON THE INSTANCE — but that is one env var per site, and the operator of a fleet already knows
    /// the answer for all of them at once. This is them saying it here.</para>
    /// </summary>
    public const string ForceHttpsUrls = "instances.forceHttps";

    /// <summary>"1" = every cloud account must use two-factor auth; one who has not set it up is
    /// funnelled to the enrolment page on the next admin request (forced setup, not a lock-out).
    /// Optional per account otherwise. A cloud-wide policy stored in CloudSettings; separate from the
    /// per-instance <c>security.require2fa</c> a profile can roll out to sites.</summary>
    public const string Require2fa = "security.require2fa";

    /// <summary>"1" = serve the cloud's OWN admin auth + antiforgery cookies as <c>SameSite=None; Secure;
    /// Partitioned</c> so the cloud login and the SSO consent can run INSIDE the instance-preview iframe
    /// instead of breaking out to top level. Off by default (the frame-buster then forces top level, the
    /// robust path). Requires the cloud to be served over HTTPS — a <c>None</c> cookie without
    /// <c>Secure</c> is rejected. This is the cloud-side mirror of the instance's <c>site.embedAuth</c>;
    /// both must be on for a fully in-iframe SSO. <c>Partitioned</c> (CHIPS) is what lets it survive a
    /// browser that blocks third-party cookies (e.g. Brave strict); browsers without CHIPS ignore the
    /// unknown attribute and fall back to plain <c>SameSite=None</c>.</summary>
    public const string EmbedAuth = "security.embedAuth";

    /// <summary>
    /// Ob diese Cloud selbst Instanzen betreiben darf — Container auf dem erreichbaren Docker-Daemon
    /// anlegen und später mehr.
    /// <para>Aus, bis es jemand einschaltet. Der Socket-Zugriff ist schon freiwillig; dies ist die
    /// zweite, ausdrückliche Zusage, dass diese Cloud nicht nur zuschauen, sondern erzeugen darf.
    /// Ohne sie erscheinen die entsprechenden Aktionen gar nicht erst.</para>
    /// </summary>
    public const string HostingEnabled = "hosting.enabled";

    /// <summary>"matcad" oder "docker": ob eine neue Instanz ihre Route von Matcad bekommt oder als
    /// reiner Container startet, dem der Betreiber selbst eine Domain zuweist. Ein String und kein
    /// Schalter, weil es später eine dritte Antwort geben kann.</summary>
    public const string HostingMode = "hosting.mode";

    /// <summary>Adresse des Matcad-API, das die Route einrichtet.</summary>
    public const string HostingMatcadUrl = "hosting.matcadUrl";

    /// <summary>Zugangsschlüssel für dieses API — verschlüsselt abgelegt wie jedes andere Geheimnis
    /// hier, und im Formular nie im Klartext zurückgegeben.</summary>
    public const string HostingMatcadToken = "hosting.matcadToken";

    /// <summary>Der lokale Portbereich, aus dem eine neue Instanz ihren Port bekommt. Ein BEREICH und
    /// keine Liste: der nächste freie Port lässt sich daraus ableiten, und niemand pflegt von Hand
    /// nach, welcher gerade belegt ist.</summary>
    public const string HostingPortFrom = "hosting.portFrom";
    public const string HostingPortTo = "hosting.portTo";

    /// <summary>Muster für den Namen des Stacks, z. B. "MatCMS-$NAME". $NAME wird durch den
    /// normalisierten Seitennamen ersetzt.</summary>
    public const string HostingNamePattern = "hosting.namePattern";

    // --- Notifications ------------------------------------------------------
    /// <summary>Where notification mails go (comma-separated). Empty = every cloud user's e-mail.</summary>
    public const string NotifyRecipients = "notify.recipients";

    /// <summary>"1" = mail when an instance stops sending heartbeats (dead-man switch).</summary>
    public const string NotifyOffline = "notify.offline";

    /// <summary>"1" = mail when a newer MatCMS release appears for a connected instance.</summary>
    public const string NotifyUpdate = "notify.update";

    /// <summary>"1" = update LOCAL instances automatically as soon as a new release is found.
    /// Off by default: recreating a container is destructive enough to want a human click.</summary>
    public const string AutoUpdateLocal = "update.autoLocal";

    // --- AI (Admin → Einstellungen → KI) ------------------------------------
    /// <summary>Provider id: "openai" (default) or "" (off). Room for "anthropic" etc. later; the
    /// AiService abstracts over it.</summary>
    public const string AiProvider = "ai.provider";

    /// <summary>Model id the cloud uses for EVERY relayed call, e.g. "gpt-4o-mini". Central on purpose,
    /// so a connected instance can never escalate to a costlier model through a request field.</summary>
    public const string AiModel = "ai.model";

    /// <summary>Optional base URL for an OpenAI-compatible endpoint; empty = the provider's default.</summary>
    public const string AiBaseUrl = "ai.baseUrl";

    /// <summary>The provider API key. SecretProtector-encrypted at rest, <b>never</b> rolled out to an
    /// instance (the entire reason for the relay is that the key stays central) and never echoed back
    /// to the settings form. The OAuth "Mit ChatGPT anmelden" token (later) lands here the same way.</summary>
    public const string AiApiKey = "ai.apiKey";

    // --- SMTP (own tab; kept out of `All` so each form saves only its own keys) ---
    public const string SmtpHost = "smtp.host";
    public const string SmtpPort = "smtp.port";
    public const string SmtpUser = "smtp.user";
    public const string SmtpPassword = "smtp.password";
    public const string SmtpFromEmail = "smtp.fromEmail";
    public const string SmtpFromName = "smtp.fromName";
    public const string SmtpSsl = "smtp.ssl";

    public static readonly string[] Smtp =
    [
        SmtpHost, SmtpPort, SmtpUser, SmtpPassword, SmtpFromEmail, SmtpFromName, SmtpSsl
    ];

    public static readonly string[] General =
    [
        CloudName, CanonicalUrl
    ];

    public static readonly string[] Notifications =
    [
        NotifyRecipients, NotifyOffline, NotifyUpdate, AutoUpdateLocal
    ];
}
