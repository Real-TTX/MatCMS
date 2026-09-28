namespace MatCMS.Cloud.Models;

/// <summary>
/// A key for the operator API (<c>/api/v1</c>). It lets an external client run the backup cycle —
/// pull a site's backup, upload an edited one, and (only with the restore right) put it back live —
/// without a cookie session.
/// <para>Stored as <b>SHA-256 only</b>, shown in the clear exactly once at creation, exactly like an
/// instance token. A short <see cref="Prefix"/> is kept in the clear so the list can identify a key
/// without ever holding the secret.</para>
/// </summary>
public class ApiKey
{
    public int Id { get; set; }

    /// <summary>Admin label, e.g. "Claude – Kabri". Free text, only for the operator's own overview.</summary>
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the raw key. The raw value is shown once and cannot be recovered.</summary>
    public string KeyHash { get; set; } = "";

    /// <summary>The leading characters of the raw key (the "mck_ab12…" part). Enough to recognise a
    /// key in the list, never enough to authenticate with.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>
    /// Whether this key may trigger a LIVE restore. Off by default: pulling and uploading a backup is
    /// the base right; overwriting a running customer site is the one that must be granted on purpose.
    /// </summary>
    public bool CanRestore { get; set; }

    /// <summary>
    /// Whether this key may MANAGE PROFILES over the API (create/edit profiles and their payloads —
    /// settings, users, plugins, components, templates — and assign instances to a profile). Off by
    /// default: a profile change pushes configuration to every instance assigned to it, so it is as
    /// consequential as a restore and must be granted on purpose. Reading profiles needs only a valid
    /// key; writing needs this right. Instance↔profile assignment ALSO honours the key's instance scope.
    /// </summary>
    public bool CanManageProfiles { get; set; }

    /// <summary>
    /// Whether this key may MANAGE THE STORE over the API (create/edit/remove the cloud-wide catalogue of
    /// templates, plugins, components and mail-templates under <c>/api/v1/store</c>). Off by default: a
    /// store entry is shared by every profile that selects it, so editing one reaches every instance of
    /// every such profile — even more far-reaching than a single profile change — and must be granted on
    /// purpose. Reading the store needs only a valid key; writing needs this right. The store is
    /// cloud-wide and carries no per-instance scope, so this right ignores the key's instance list.
    /// </summary>
    public bool CanManageStore { get; set; }

    /// <summary>
    /// True = every instance. False = only the instances listed in <see cref="Instances"/>.
    /// <para>A key with <c>AllInstances = false</c> and no scope rows can reach nothing — deliberately,
    /// so a mis-created key is inert rather than accidentally global.</para>
    /// </summary>
    public bool AllInstances { get; set; }

    public List<ApiKeyInstance> Instances { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the key authenticated a request. Written at most once a minute so a busy
    /// client does not turn every call into a database write.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Set when the key was revoked. A revoked key fails authentication but stays in the
    /// list: a key that could restore a live site is worth an audit trail, so it is turned off rather
    /// than quietly deleted.</summary>
    public DateTime? RevokedAt { get; set; }

    public bool Revoked => RevokedAt is not null;
}

/// <summary>One instance a scoped key may act on. Stored by internal <see cref="InstanceId"/>: an API
/// key is a cloud-side object that never travels to a site, so it has no reason to use the public id.
/// Cascades from both ends — deleting the key or the instance removes only the link.</summary>
public class ApiKeyInstance
{
    public int Id { get; set; }
    public int ApiKeyId { get; set; }
    public ApiKey? ApiKey { get; set; }
    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }
}
