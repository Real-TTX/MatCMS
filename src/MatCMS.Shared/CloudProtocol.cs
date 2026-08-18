namespace MatCMS.Shared;

/// <summary>
/// The cloud↔instance wire contract — <b>one</b> definition, referenced by both applications. It
/// used to be two hand-kept copies (<c>MatCMS/Services/CloudProtocol.cs</c> and
/// <c>MatCMS.Cloud/Services/InstanceProtocol.cs</c>) that had to be changed together, including two
/// version constants that had to be bumped together. Now there is nothing to keep in step.
/// <para>The instance always calls the cloud (outbound), so a site behind NAT/firewall needs no
/// inbound port and the cloud never holds a connection open. The one exception is cloud-initiated
/// adoption, where the cloud reaches the instance ONCE at <c>/api/cloud/link</c> to hand over the
/// credentials — everything after that is outbound again.</para>
/// </summary>
public static class CloudProtocol
{
    /// <summary>Contract version. Bump on <b>every</b> change to the payloads in this file: the cloud
    /// badges an instance reporting an older one as "veraltet", and both sides read this constant, so
    /// one edit covers both.</summary>
    public const int Version = 11;

    /// <summary>Header carrying the instance's bearer token.</summary>
    public const string TokenHeader = "X-MatCMS-Instance-Token";

    /// <summary>
    /// Header on a backup upload naming the request it answers (<see cref="PendingBackup.RequestId"/>);
    /// absent or <c>0</c> for a backup the site made on its own.
    /// <para>A constant rather than a magic string like its neighbours, because this one is
    /// load-bearing: it is what lets the cloud tell "the backup we asked for has arrived" from "a
    /// backup happened to arrive". A site that was offline for a week uploads last week's file the
    /// moment it comes back — without this header that stale upload would answer a request it never
    /// saw, and something would then be deleted on the strength of it.</para>
    /// </summary>
    public const string BackupRequestHeader = "X-MatCMS-Backup-Request";
}

/// <summary>
/// What a cloud sends to <c>/api/cloud/link</c> when the operator adopts an instance from the cloud
/// side. The credentials are an ADMIN ACCOUNT OF THAT INSTANCE and are verified there before the
/// link is accepted; they are never stored.
/// </summary>
public sealed class LinkRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? CloudUrl { get; set; }
    public string? InstanceId { get; set; }
    public string? Token { get; set; }
}

/// <summary>Enrollment: an instance introduces itself with a profile's join code.</summary>
public sealed class RegisterRequest
{
    public string? JoinCode { get; set; }
    public int ProtocolVersion { get; set; } = CloudProtocol.Version;
    public string? Version { get; set; }
    public string? SiteName { get; set; }
    public string? Url { get; set; }
    public string? HostName { get; set; }
    public string? ContainerId { get; set; }
    public string? ImageRef { get; set; }
}

/// <summary>What the instance stores after a successful enrollment.</summary>
public sealed class RegisterResponse
{
    public string InstanceId { get; set; } = "";
    public string Token { get; set; } = "";

    /// <summary>"Pending" or "Approved" — with auto-approve off the instance must wait.</summary>
    public string Status { get; set; } = "";

    public string? ProfileName { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>What an instance reports on every beat (~60 s).</summary>
public sealed class HeartbeatRequest
{
    /// <summary>Contract version the instance speaks.</summary>
    public int ProtocolVersion { get; set; } = CloudProtocol.Version;

    /// <summary>Running MatCMS version (InformationalVersion), e.g. "1.0.42-20260806120000".</summary>
    public string? Version { get; set; }

    /// <summary>Site name — used as the display name until an operator renames the instance in the
    /// cloud.</summary>
    public string? SiteName { get; set; }

    /// <summary>Public URL of the site, for the "open site" link and the preview tile.</summary>
    public string? Url { get; set; }

    public string? HostName { get; set; }

    /// <summary>The instance's own container id, read from /proc/self/cgroup (or the hostname, which
    /// Docker sets to the short id). THE key to the local/remote decision — without it the cloud can
    /// never match a container on its daemon and the instance stays remote.</summary>
    public string? ContainerId { get; set; }

    /// <summary>Image reference the instance believes it runs.</summary>
    public string? ImageRef { get; set; }

    public int PageCount { get; set; }
    public int PluginCount { get; set; }
    public int UserCount { get; set; }

    /// <summary>Profile revision the instance has successfully applied. Anything below the profile's
    /// current revision means "out of sync" and makes the instance pull the config.</summary>
    public int AppliedRevision { get; set; }

    /// <summary>Why the last apply failed, if it did. Surfaced verbatim in the cloud UI.</summary>
    public string? SyncError { get; set; }

    /// <summary>What the last apply did, item by item. Empty from an instance that predates the
    /// report, which is why the cloud must treat it as "no information", not "nothing happened".</summary>
    public List<SyncItemReport>? SyncReport { get; set; }

    /// <summary>
    /// When the instance finished that apply (UTC). The same report rides on every beat until the
    /// next apply, so this is what tells the cloud "this is a NEW run" — without it, a re-apply that
    /// happened to produce an identical report would silently vanish from the history.
    /// </summary>
    public DateTime? SyncRunAt { get; set; }
}

/// <summary>The cloud's answer. Pull-based: it only ever TELLS the instance what is pending; the
/// instance decides when to fetch and apply it.</summary>
public sealed class HeartbeatResponse
{
    /// <summary>Contract version the cloud speaks, so an instance can warn about a mismatch too.</summary>
    public int ProtocolVersion { get; set; }

    /// <summary>"Pending" | "Approved" — a pending instance gets no configuration.</summary>
    public string? Status { get; set; }

    /// <summary>
    /// The address under which an OPERATOR reaches this cloud — which is not necessarily the one the
    /// instance calls it at. A site in a Docker network may reach the cloud as http://cloud:8080 while
    /// the browser opens https://cloud.example.com; the instance needs the second one to allow the
    /// embedding, and only the cloud knows it.
    /// <para>Null when no canonical URL is configured there — the instance then falls back to the
    /// address it uses itself, which is right whenever the two are the same.</para>
    /// </summary>
    public string? CloudPublicUrl { get; set; }

    /// <summary>Newest published MatCMS release, or null when the registry check has not succeeded
    /// yet.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>True when <see cref="LatestVersion"/> is newer than what the instance reported.</summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>A backup an operator asked to be restored, or null. Null is the normal case, so an
    /// instance that does not know this field yet simply never restores anything — which is the right
    /// way for an unknown instruction about overwriting a live site to fail.</summary>
    public PendingRestore? Restore { get; set; }

    /// <summary>A backup the cloud has asked this instance to make and upload, or null. Same shape
    /// and same reasoning as <see cref="Restore"/>: the cloud asks, the instance acts.</summary>
    public PendingBackup? Backup { get; set; }

    /// <summary>True when the cloud can update this instance itself (it found the container on its
    /// own daemon).</summary>
    public bool CloudCanUpdate { get; set; }

    /// <summary>Name the cloud knows this instance by — lets the instance display the same label.</summary>
    public string? DisplayName { get; set; }

    public string? ProfileName { get; set; }

    /// <summary>Current revision of the assigned profile. When it differs from what the instance
    /// applied, the instance pulls <c>/api/instances/{id}/config</c>. 0 = nothing to sync.</summary>
    public int ConfigRevision { get; set; }
}

// --- Configuration payload ------------------------------------------------
// Delivered by GET /api/instances/{id}/config to an APPROVED instance. Plugin bundles are NOT
// inlined — they are fetched per plugin, so a profile with many plugins does not turn every sync
// into a multi-megabyte response.

public sealed class InstanceConfig
{
    public int Revision { get; set; }
    public string? ProfileName { get; set; }

    /// <summary>Which profile this configuration comes from. The instance needs it to scope its
    /// "already seeded once" marks: moving a site to another profile must let that profile seed
    /// again, otherwise a once-mode payload would silently never arrive.</summary>
    public int ProfileId { get; set; }

    /// <summary>Setting key → value, applied to the instance's own settings table. Null when the
    /// profile does not sync settings.</summary>
    public Dictionary<string, string?>? Settings { get; set; }

    public List<ConfigUser>? Users { get; set; }
    public List<ConfigComponent>? Components { get; set; }
    public List<ConfigPlugin>? Plugins { get; set; }
    public List<ConfigTemplate>? Templates { get; set; }
    public List<ConfigMailTemplate>? MailTemplates { get; set; }

    /// <summary>Name of the template that should become the ACTIVE design. Empty = leave the site's
    /// own choice alone.</summary>
    public string? ActivateTemplate { get; set; }

    /// <summary>Per-payload strategy: "keep" (make the instance match the profile on every revision),
    /// "add" (only add what is missing) or "once" (roll out on the first apply and never again).
    /// Sent as strings on purpose — an instance that does not know a mode yet falls back to "add"
    /// instead of misreading a number and overwriting a live site.</summary>
    public string SettingsMode { get; set; } = "keep";
    public string ComponentsMode { get; set; } = "keep";
    public string PluginsMode { get; set; } = "keep";
    public string TemplatesMode { get; set; } = "keep";
    public string UsersMode { get; set; } = "add";
    public string MailTemplatesMode { get; set; } = "keep";

    /// <summary>
    /// How the instance should SEND mail: "smtp" (its own or the rolled-out configuration) or
    /// "cloud" (hand the message to the cloud, which queues and delivers it).
    /// <para>A string, like the modes above, and for the same reason: an instance that predates the
    /// relay must fall back to sending it itself rather than misreading a number and quietly
    /// dropping every notification a site produces.</para>
    /// </summary>
    public string MailTransport { get; set; } = "smtp";
}

/// <summary>
/// One message an instance hands to the cloud for delivery (POST /api/instances/{id}/mail).
/// <para>There is no sender field on purpose. The cloud sends with ITS OWN address, so an instance
/// cannot claim to be somebody else and the cloud domain's SPF/DKIM always match. What the
/// instance may steer is where a reply goes.</para>
/// <para>Subject and body arrive already rendered: the instance owns its templates, the cloud only
/// carries the result.</para>
/// </summary>
/// <summary>
/// A backup the cloud wants this instance to restore. It rides on the heartbeat because the cloud
/// never reaches into a site: the instance learns of the request, fetches its own file and does the
/// work itself — the same pull that carries every other change.
/// </summary>
public sealed class PendingRestore
{
    public int BackupId { get; set; }
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the stored file, so the instance can tell a truncated download from a
    /// good one BEFORE it overwrites a live site with it.</summary>
    public string Sha256 { get; set; } = "";
}

/// <summary>What an instance reports back after attempting a restore it was asked for.</summary>
public sealed class RestoreReport
{
    public int BackupId { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// A backup the cloud wants this instance to MAKE and upload — the mirror of
/// <see cref="PendingRestore"/>, and deliberately built the same way: it rides on the heartbeat, the
/// cloud only ever asks, and the instance does the work with the code it already uses for its own
/// backups.
/// <para>Null is the normal case, so an instance that does not know this field yet simply never
/// makes one. That is the right way for an unknown instruction to fail here: the cloud sees that no
/// backup arrived, and whatever was waiting on it goes on waiting instead of proceeding without
/// it.</para>
/// </summary>
public sealed class PendingBackup
{
    /// <summary>
    /// Identifies THIS request. It comes back on the upload (<see cref="CloudProtocol.BackupRequestHeader"/>)
    /// and in <see cref="BackupReport"/>, and it is the only thing that lets the cloud say "the file
    /// we asked for is the one that arrived".
    /// <para>A counter rather than the request's timestamp: the value survives JSON, an HTTP header
    /// and two SQLite round trips as itself, while a time compared for equality across all of that
    /// is a coin toss. A comparison that quietly never matches would leave every removal waiting for
    /// ever; one that matches too eagerly would delete a site against the wrong file.</para>
    /// </summary>
    public int RequestId { get; set; }

    /// <summary>Why the cloud is asking, in the site's own language. Written into the instance's log
    /// so its operator can see that the cloud asked for this backup rather than the schedule.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// What an instance reports back after attempting a backup it was asked for.
/// <para>A courtesy, not the gate. The cloud believes a backup exists because the FILE arrived,
/// never because a report said so — see <see cref="PendingBackup.RequestId"/>. A report that never
/// comes therefore costs nothing, and one claiming success while the upload failed changes
/// nothing.</para>
/// </summary>
public sealed class BackupReport
{
    public int RequestId { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }

    /// <summary>Name of the file the instance produced, for the log entry.</summary>
    public string? FileName { get; set; }
}

public sealed class MailRequest
{
    public List<string> To { get; set; } = new();
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string? ReplyTo { get; set; }

    /// <summary>Whether <see cref="Body"/> is HTML. The cloud then sends it as multipart with a
    /// text alternative it derives itself — the instance already rendered the markup, and deriving
    /// the fallback where the message is actually built keeps one implementation of it.</summary>
    public bool IsHtml { get; set; }
}

/// <summary>
/// What the cloud answers. <c>Queued</c> means accepted for delivery — NOT delivered: the cloud
/// spools every message and a worker sends it, so the instance is not left waiting on a foreign
/// SMTP server and a failed attempt can be retried instead of being lost.
/// </summary>
public sealed class MailResponse
{
    public bool Queued { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// The wording of one kind of mail. <c>Key</c> is the identity — it names WHAT the mail is, and it
/// is the same key the CMS asks for when it sends one.
/// <para>Only wording travels. Which mails a site can send is decided by the CMS that sends them,
/// so a key the instance does not know is stored and simply never used — the alternative, refusing
/// it, would break rollout the moment the two sides differ by one release.</para>
/// </summary>
public sealed class ConfigMailTemplate
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Whether the body is HTML.</summary>
    public bool IsHtml { get; set; }
}

/// <summary>A theme rolled out to the instance. <c>Name</c> is the identity; whether it becomes the
/// live design is decided once by <see cref="InstanceConfig.ActivateTemplate"/>.</summary>
public sealed class ConfigTemplate
{
    public string Name { get; set; } = "";
    public string AccentColor { get; set; } = "";
    public string SecondaryColor { get; set; } = "";
    public string HeadingFont { get; set; } = "";
    public string BodyFont { get; set; } = "";
    public string ButtonStyle { get; set; } = "";
    public string HeadingColor { get; set; } = "";
    public string TextColor { get; set; } = "";
    public string BackgroundColor { get; set; } = "";
    public string AltBackground { get; set; } = "";
    public string ContainerWidth { get; set; } = "";
    public string ButtonRadius { get; set; } = "";
    public string HeaderBackground { get; set; } = "";
    public string HeaderTextColor { get; set; } = "";
    public string HeaderPadding { get; set; } = "";
    public string CustomCss { get; set; } = "";
    public string CustomJs { get; set; } = "";
    public string LayoutHtml { get; set; } = "";
    public string MenuMapJson { get; set; } = "{}";
    public string ParametersJson { get; set; } = "[]";
    public string ParamValuesJson { get; set; } = "{}";
    public int SchemaVersion { get; set; } = 1;
    public string PartsJson { get; set; } = "{}";
}

public sealed class ConfigUser
{
    public string Username { get; set; } = "";
    public string? Email { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>Already-hashed password, copied verbatim. The cloud never holds the plaintext.</summary>
    public string PasswordHash { get; set; } = "";

    public string Role { get; set; } = "Admin";
}

public sealed class ConfigComponent
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string FieldsJson { get; set; } = "[]";
    public string TemplateHtml { get; set; } = "";
}

/// <summary>A plugin the instance should have. The bundle is downloaded separately from
/// <c>/api/instances/{id}/plugin/{key}</c>, and only when the version differs from what is
/// installed.</summary>
public sealed class ConfigPlugin
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>
/// What the instance did with one item while applying a configuration. The cloud computes none of
/// this — only the instance knows whether a component already existed or a plugin import failed, so
/// it says so and the cloud is a record keeper. Adding a payload type later needs no cloud-side
/// logic, just another line in the report.
/// </summary>
public sealed class SyncItemReport
{
    /// <summary>"setting" | "user" | "component" | "template" | "plugin".</summary>
    public string Kind { get; set; } = "";

    /// <summary>The identity on the instance: setting key, username, component type, template name,
    /// plugin key.</summary>
    public string Id { get; set; } = "";

    /// <summary>"installed" | "updated" | "skipped-exists" | "skipped-once" | "failed".</summary>
    public string Outcome { get; set; } = "";

    /// <summary>Why it failed, or why it was skipped when that is not obvious. Shown verbatim.</summary>
    public string? Detail { get; set; }
}

// --- Catalogue ------------------------------------------------------------
// What an approved instance sees when it browses the store itself ("Weiter durchsuchen…" in MatCMS),
// independent of any profile. Deliberately only the three catalogue types: users and settings are
// shared configuration, not something a site shops for, and must never be listed here.

public sealed class StoreCatalog
{
    public List<CatalogPlugin> Plugins { get; set; } = new();
    public List<CatalogTemplate> Templates { get; set; } = new();
    public List<CatalogComponent> Components { get; set; } = new();
}

public sealed class CatalogPlugin
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class CatalogTemplate
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string AccentColor { get; set; } = "";
}

public sealed class CatalogComponent
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}
