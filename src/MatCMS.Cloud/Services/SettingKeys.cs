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

    /// <summary>
    /// Ob diese Cloud selbst Instanzen betreiben darf — Container auf dem erreichbaren Docker-Daemon
    /// anlegen und später mehr.
    /// <para>Aus, bis es jemand einschaltet. Der Socket-Zugriff ist schon freiwillig; dies ist die
    /// zweite, ausdrückliche Zusage, dass diese Cloud nicht nur zuschauen, sondern erzeugen darf.
    /// Ohne sie erscheinen die entsprechenden Aktionen gar nicht erst.</para>
    /// </summary>
    public const string HostingEnabled = "hosting.enabled";

    /// <summary>Welcher Reverse-Proxy die Routen der Instanzen führt: "none" (kein Proxy — die Instanz ist
    /// über ihren Host-Port erreichbar, eine Domain wird nur vermerkt), "matcad" (über das Matcad-REST-API)
    /// oder "caddy" (direkt über die Admin-API eines Caddy). Früher "docker"/"matcad"; "docker" wird als
    /// "none" gelesen. Ein String und kein Schalter, genau weil es inzwischen die dritte Antwort gibt.</summary>
    public const string HostingMode = "hosting.mode";

    /// <summary>Admin-API eines Caddy für den Provider "caddy", z. B. http://caddy:2019.</summary>
    public const string HostingCaddyAdminUrl = "hosting.caddyAdminUrl";

    /// <summary>Name des HTTP-Servers in der Caddy-Konfiguration, in den die Routen kommen (ein per
    /// Caddyfile erzeugter heißt "srv0"). Er sollte auf :443 lauschen — nur dann holt Caddy selbst das
    /// Zertifikat. Fremde Routen darin bleiben unangetastet; die eigenen tragen eine @id.</summary>
    public const string HostingCaddyServer = "hosting.caddyServer";

    /// <summary>Wie der Proxy den Instanz-Container erreicht: "network" = über ein gemeinsames
    /// Docker-Netz (<see cref="HostingProxyNetwork"/>) per Containername und Port 8080 — die Cloud hängt
    /// den Container dafür live in dieses Netz; "hostport" = über den veröffentlichten Host-Port auf
    /// <see cref="HostingProxyUpstreamHost"/>.</summary>
    public const string HostingProxyUpstream = "hosting.proxyUpstream";

    /// <summary>Das Docker-Netz, in dem der Proxy (Caddy/Matcads Caddy) läuft — für den Modus "network".</summary>
    public const string HostingProxyNetwork = "hosting.proxyNetwork";

    /// <summary>Host, unter dem der Proxy die Host-Ports erreicht — für den Modus "hostport",
    /// z. B. host.docker.internal oder die IP des Docker-Hosts.</summary>
    public const string HostingProxyUpstreamHost = "hosting.proxyUpstreamHost";

    /// <summary>Optionale E-Mail für ACME (Let's Encrypt), die Matcad pro Route mitbekommt.</summary>

    /// <summary>Präfix für eine bei der Provisionierung schon angelegte Route, deren Instanz sich noch
    /// nicht gemeldet hat: <c>hosting.pendingRoute:&lt;containername&gt;</c> → JSON. Die Instanz-Zeile
    /// entsteht erst beim Join; die Route braucht sie nicht und wird sofort angelegt, danach übernommen.</summary>
    public const string HostingPendingRoutePrefix = "hosting.pendingRoute:";

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

    // Log system (Admin → Protokoll). Applies to the CLOUD's OWN log (instance logs are a mirror pruned
    // on the heartbeat). Request logging is opt-in; retention is per category, in days (0 = keep).
    public const string LogRequests = "log.requests";                        // "on" = log EVERY HTTP request (category "webrequest")
    public const string LogRetentionErrorsDays = "log.retentionErrorsDays";  // errors/5xx (category "request"); default 90
    public const string LogRetentionRequestsDays = "log.retentionRequestsDays"; // full request log (category "webrequest"); default 14

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
