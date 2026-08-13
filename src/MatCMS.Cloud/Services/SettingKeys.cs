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

    /// <summary>
    /// "My instances are reachable over https, whatever they report."
    /// <para>An instance behind a TLS-terminating proxy sees only the unencrypted hop and reports
    /// http. An https cloud may then neither frame nor link it. The proper fix is forwarded headers
    /// ON THE INSTANCE — but that is one env var per site, and the operator of a fleet already knows
    /// the answer for all of them at once. This is them saying it here.</para>
    /// </summary>
    public const string ForceHttpsUrls = "instances.forceHttps";

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
