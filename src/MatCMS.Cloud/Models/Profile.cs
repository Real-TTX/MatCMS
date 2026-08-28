namespace MatCMS.Cloud.Models;

/// <summary>How a profile's instances send mail. Strings on the wire and in the database, so a
/// value nobody knows yet degrades to something safe instead of to a wrong number.</summary>
public static class MailSources
{
    /// <summary>The cloud's own SMTP settings are rolled out to the instances.</summary>
    public const string Global = "global";

    /// <summary>The profile carries its own SMTP values.</summary>
    public const string Own = "own";

    /// <summary>The instances send nothing themselves: each message goes to the cloud, which spools
    /// and delivers it with its OWN sender address.</summary>
    public const string Cloud = "cloud";

    public static string Normalise(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        Global => Global,
        Cloud => Cloud,
        _ => Own,
    };
}

/// <summary>
/// How far a profile keeps reaching into an instance for one payload. The instance decides what this
/// means in practice — the cloud only states the intent — but the contract is:
/// <list type="bullet">
/// <item><see cref="Keep"/> — the instance is made to match the profile on every revision. The
/// profile owns these items.</item>
/// <item><see cref="Add"/> — only what is missing is added; anything the site already has is left
/// exactly as it is, on every revision.</item>
/// <item><see cref="Once"/> — rolled out on the FIRST apply and never touched again, no matter how
/// often the profile changes afterwards. For handing a site a starting set it then owns itself.</item>
/// </list>
/// <para>The numeric values matter: <c>Keep = 0</c> so profiles migrated from the old
/// <c>Overwrite* = true</c> flags keep behaving exactly as before.</para>
/// </summary>
public enum SyncMode
{
    Keep = 0,
    Add = 1,
    Once = 2
}

/// <summary>
/// A configuration bundle plus the policy that applies to every instance assigned to it.
/// <para>The <see cref="JoinCode"/> hangs off the PROFILE, not off the cloud: an instance that
/// enrolls with a profile's code lands in that profile automatically, so rolling out N sites needs
/// no per-instance assignment step.</para>
/// <para><see cref="Revision"/> is the whole sync mechanism: it is bumped on every change, rides on
/// the heartbeat response, and an instance whose <c>AppliedRevision</c> differs pulls the config and
/// applies it. Never bump it by hand — use <c>ProfileService.TouchAsync</c>.</para>
/// </summary>
public class Profile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Enrollment code for this profile. Rotatable; rotating invalidates pending rollouts
    /// but never affects instances that already hold a token.</summary>
    public string JoinCode { get; set; } = "";

    /// <summary>When true an enrolling instance is active immediately; otherwise it waits in
    /// <see cref="InstanceStatus.Pending"/> until an operator approves it.</summary>
    public bool AutoApprove { get; set; } = true;

    /// <summary>Profile new instances fall back to when none can be resolved. Exactly one at a time
    /// (enforced by <c>ProfileService</c>).</summary>
    public bool IsDefault { get; set; }

    /// <summary>Bumped on every content/policy change. The instance compares it against its own
    /// applied revision — that is the entire "is this instance in sync?" question.</summary>
    public int Revision { get; set; } = 1;

    // --- Policy (was global before profiles existed; global settings are now only the fallback) ---
    public bool AutoUpdateLocal { get; set; }
    public bool NotifyOffline { get; set; } = true;
    public bool NotifyUpdate { get; set; } = true;

    /// <summary>Comma-separated. Empty = fall back to the global recipients.</summary>
    public string? NotifyRecipients { get; set; }

    /// <summary>
    /// How much backup storage the cloud grants each instance in this profile, in GB.
    /// Null (or empty in the form) falls back to the cloud-wide default.
    ///
    /// <para>Deliberately a column on the profile and NOT a rolled-out setting: this is what the
    /// CLOUD grants, not something a site configures about itself. An instance that could read — let
    /// alone be told — its own quota would be the wrong shape entirely; the number decides which of
    /// its uploads get pushed out again, and that decision belongs to the side holding the disk.</para>
    ///
    /// <para>It sits with the policy fields rather than on the backup group's page on purpose. That
    /// page is about what gets rolled out, and opening it switches the rollout ON — an operator who
    /// only wanted to grant a customer more space would have started backing up their sites.</para>
    /// </summary>
    /// <summary>Disk ceiling in GB, FRACTIONAL (e.g. 0.1 = 100 MB). Null = the cloud-wide default.
    /// The safety net that never keeps the very last backup from fitting.</summary>
    public double? BackupQuotaGb { get; set; }

    // --- Cloud-side retention (GFS) for AUTOMATIC backups --------------------
    // How many of the instance's OWN scheduled ("auto") backups the cloud keeps, in the classic
    // grandfather-father-son shape. Manual/API uploads are exempt — never auto-pruned. Null on the
    // profile = fall back to the cloud-wide default; 0 = that tier is off. Retention is entirely a
    // CLOUD decision (it holds the disk), like the quota above, and is not rolled out to the instance.
    public int? BackupKeepDaily { get; set; }
    public int? BackupKeepWeekly { get; set; }
    public int? BackupKeepMonthly { get; set; }

    /// <summary>Absolute cap on the number of AUTO backups kept, applied on top of the GFS tiers.
    /// Null = cloud-wide default, 0 = no count cap.</summary>
    public int? BackupMaxCount { get; set; }

    // --- Which payloads this profile pushes ---------------------------------
    public bool SyncSettings { get; set; }

    /// <summary>
    /// WHERE the mail configuration comes from — <see cref="MailSources"/>: the cloud's own SMTP
    /// settings, this profile's own values, or the cloud's relay (the instance hands each message
    /// over and the cloud spools and delivers it).
    /// <para>A string rather than the old <c>UseGlobalSmtp</c> boolean, for the same reason the sync
    /// modes are strings: there turned out to be a third answer, and a boolean can only ever give
    /// two. An unknown value is read as "own", the answer that changes nothing about how a site
    /// already sends.</para>
    /// </summary>
    public string MailSource { get; set; } = MailSources.Own;

    /// <summary>
    /// Whether this profile rolls out SMTP at all. Off = the instance's own mail configuration is
    /// left alone, whatever is stored here — a settings group has to be ticked ON before it may
    /// overwrite anything on a live site, rather than silently shipping because a field was filled in
    /// once. The stored values survive an untick; only the rollout stops.
    /// </summary>
    public bool SyncSmtp { get; set; }

    /// <summary>
    /// Whether this profile rolls out the MACHINE TRANSLATION credentials (provider, API key, URL).
    /// <para>Its own group rather than free key/value rows, for the same reason SMTP has one: these
    /// are credentials, they belong together, and a group has to be switched ON before it may
    /// overwrite what a site already has.</para>
    /// <para>Which LANGUAGES a site offers is deliberately not part of it. That is a decision about
    /// the site's content, not about an account — rolling it out would silently switch languages on
    /// and off underneath pages that are written in them.</para>
    /// </summary>
    public bool SyncTranslation { get; set; }

    /// <summary>
    /// Whether this profile decides the BACKUP policy of its sites: automatic backups on or off, how
    /// often, how many to keep, what goes in — and whether the finished file is handed to the cloud.
    /// <para>Its own group for the usual reason, and one more: "back this site up every night and
    /// upload it" is the single most consequential thing a profile can switch on remotely. It has to
    /// be a deliberate act.</para>
    /// <para>What is deliberately NOT rolled out is the granular selection inside a backup (which
    /// pages, which forms). Those name items that exist on one site and nowhere else — a profile
    /// distributing them would leave every other instance backing up nothing.</para>
    /// </summary>
    public bool SyncBackup { get; set; }
    public bool SyncUsers { get; set; }

    /// <summary>Let the instance drop the built-in default <c>admin</c>/<c>admin</c> login once a real
    /// admin exists. Guarded on the instance side (default password still set + another Admin present),
    /// so it can never lock anyone out. Opt-in, and independent of the add-only user rollout — the point
    /// is to shed the well-known default after provisioning your own account.</summary>
    public bool RemoveDefaultAdmin { get; set; }

    public bool SyncPlugins { get; set; }
    public bool SyncComponents { get; set; }
    public bool SyncTemplates { get; set; }
    public bool SyncMailTemplates { get; set; }

    /// <summary>
    /// Name of the template that should be the ACTIVE design on every assigned instance. Empty means
    /// "roll the templates out but leave the choice to the site" — switching the live design of a
    /// customer's website is a decision that deserves its own switch, not a side effect of syncing.
    /// </summary>
    public string? ActivateTemplateName { get; set; }

    /// <summary>
    /// How each payload is rolled out. Defaults to <see cref="SyncMode.Keep"/> — that is what
    /// "keep in sync" means. Users are the exception: they are add-only whatever the mode says,
    /// so their mode only chooses between "keep adding" and "seed once", never overwriting.
    /// Silently rewriting local accounts, or worse removing them, is how an operator gets locked
    /// out of their own site.
    /// </summary>
    public SyncMode SettingsMode { get; set; } = SyncMode.Keep;
    public SyncMode PluginsMode { get; set; } = SyncMode.Keep;
    public SyncMode ComponentsMode { get; set; } = SyncMode.Keep;
    public SyncMode TemplatesMode { get; set; } = SyncMode.Keep;
    public SyncMode MailTemplatesMode { get; set; } = SyncMode.Keep;
    public SyncMode UsersMode { get; set; } = SyncMode.Add;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One key/value setting pushed to the instance's own settings table (SMTP and anything
/// else addressable by a MatCMS setting key).</summary>
public class ProfileSetting
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }

    public string Key { get; set; } = "";
    public string? Value { get; set; }

    /// <summary>Marks values that must not be rendered back into the admin UI (SMTP password).</summary>
    public bool IsSecret { get; set; }
}

/// <summary>An admin account rolled out to every instance of the profile. The password hash is
/// produced once here and copied verbatim — the plaintext never leaves this form.</summary>
public class ProfileUser
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }

    public string Username { get; set; } = "";
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Admin";
}

/// <summary>A plugin bundle (the exact ZIP produced by MatCMS's <c>PluginPackager.Export</c>) held
/// for rollout. <see cref="Key"/> is the identity — a same-key import updates in place on the
/// instance and runs the plugin's own <c>Migrate</c>.</summary>
public class ProfilePlugin
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }

    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>The bundle itself. Served through its own endpoint rather than inlined in the config
    /// JSON, so a profile with many plugins does not turn every sync into a multi-megabyte payload.</summary>
    public byte[] Bundle { get; set; } = [];

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A visual theme rolled out to the profile's instances. Mirrors MatCMS's <c>Template</c> field for
/// field — <see cref="Name"/> is the identity, matching how MatCMS's own backup/restore identifies
/// templates. <c>IsActive</c> is deliberately NOT carried here: which design a site runs is decided
/// by <see cref="Profile.ActivateTemplateName"/>, once, rather than by every template row claiming it.
/// </summary>
public class ProfileTemplate
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }

    public string Name { get; set; } = "";

    // ---- Designer values ----
    public string AccentColor { get; set; } = "#de7e11";
    public string SecondaryColor { get; set; } = "";
    public string HeadingFont { get; set; } = "Geologica";
    public string BodyFont { get; set; } = "Inter";
    public string ButtonStyle { get; set; } = "solid";
    public string HeadingColor { get; set; } = "#010101";
    public string TextColor { get; set; } = "#1a1a1a";
    public string BackgroundColor { get; set; } = "#ffffff";
    public string AltBackground { get; set; } = "#f6f7f9";
    public string ContainerWidth { get; set; } = "1180";
    public string ButtonRadius { get; set; } = "0";
    public string HeaderBackground { get; set; } = "";
    public string HeaderTextColor { get; set; } = "";
    public string HeaderPadding { get; set; } = "16";

    // ---- Code / layout ----
    public string CustomCss { get; set; } = "";
    public string CustomJs { get; set; } = "";
    public string LayoutHtml { get; set; } = "";
    public string MenuMapJson { get; set; } = "{}";
    public string ParametersJson { get; set; } = "[]";
    public string ParamValuesJson { get; set; } = "{}";

    /// <summary>Template FORMAT version this row was authored in (MatCMS converts older ones up).</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Per-page-type layout overrides as JSON.</summary>
    public string PartsJson { get; set; } = "{}";
}

/// <summary>A reusable block type rolled out to the profile's instances. <see cref="Type"/> is the
/// identity (it is unique per instance in MatCMS).</summary>
public class ProfileComponent
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }

    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string FieldsJson { get; set; } = "[]";
    public string TemplateHtml { get; set; } = "";
}

/// <summary>Wording for one kind of mail, rolled out to the profile's instances. <see cref="Key"/>
/// is the identity, the same one MatCMS uses when it asks for a mail to send.</summary>
public class ProfileMailTemplate
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }

    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Whether the body is HTML.</summary>
    public bool IsHtml { get; set; }
}
