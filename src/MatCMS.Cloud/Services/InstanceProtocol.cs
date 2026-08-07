namespace MatCMS.Cloud.Services;

/// <summary>
/// The cloud↔instance wire contract. Both sides must move together — bump
/// <see cref="InstanceService.CurrentProtocolVersion"/> whenever anything here changes so an old
/// instance is flagged instead of silently misbehaving.
/// <para>The instance always calls US (outbound), so a site behind NAT/firewall needs no inbound
/// port and the cloud never holds a connection open. The one exception is cloud-initiated adoption,
/// where the cloud reaches the instance ONCE to hand over the link — everything after that is
/// outbound again.</para>
/// </summary>
public static class InstanceProtocol
{
    /// <summary>Header carrying the instance's bearer token.</summary>
    public const string TokenHeader = "X-MatCMS-Instance-Token";
}

/// <summary>Enrollment: an instance introduces itself with a profile's join code.</summary>
public sealed class RegisterRequest
{
    public string? JoinCode { get; set; }
    public int ProtocolVersion { get; set; }
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
    public int ProtocolVersion { get; set; }

    /// <summary>Running MatCMS version (InformationalVersion), e.g. "1.0.42-20260806120000".</summary>
    public string? Version { get; set; }

    /// <summary>Site name — used as the display name until an operator renames the instance here.</summary>
    public string? SiteName { get; set; }

    /// <summary>Public URL of the site, for the "open site" link.</summary>
    public string? Url { get; set; }

    public string? HostName { get; set; }

    /// <summary>The instance's own container id, read from /proc/self/cgroup (or the hostname, which
    /// Docker sets to the short id). THE key to the local/remote decision — without it we can never
    /// match a container on our daemon and the instance stays remote.</summary>
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
}

/// <summary>The cloud's answer. Pull-based: we only ever TELL the instance what is pending; the
/// instance decides when to fetch and apply it.</summary>
public sealed class HeartbeatResponse
{
    /// <summary>Contract version the cloud speaks, so an instance can warn about a mismatch too.</summary>
    public int ProtocolVersion { get; set; }

    /// <summary>"Pending" | "Approved" — a pending instance gets no configuration.</summary>
    public string Status { get; set; } = "";

    /// <summary>Newest published MatCMS release, or null when the registry check has not succeeded yet.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>True when <see cref="LatestVersion"/> is newer than what the instance reported.</summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>True when the cloud can update this instance itself (it found the container on its
    /// own daemon). The instance can use this to show "your cloud can do this for you".</summary>
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

    /// <summary>Setting key → value, applied to the instance's own settings table. Null when the
    /// profile does not sync settings.</summary>
    public Dictionary<string, string?>? Settings { get; set; }

    public List<ConfigUser>? Users { get; set; }
    public List<ConfigComponent>? Components { get; set; }
    public List<ConfigPlugin>? Plugins { get; set; }
    public List<ConfigTemplate>? Templates { get; set; }

    /// <summary>Name of the template that should become the ACTIVE design. Empty = leave the site's
    /// own choice alone.</summary>
    public string? ActivateTemplate { get; set; }

    /// <summary>Per-payload conflict strategy. False = only add what is missing, true = make the
    /// instance match the profile. Users are add-only by design and have no flag.</summary>
    public bool OverwriteSettings { get; set; }
    public bool OverwriteComponents { get; set; }
    public bool OverwritePlugins { get; set; }
    public bool OverwriteTemplates { get; set; }
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
/// <c>/api/instances/{id}/plugin/{key}</c>, and only when the version differs from what is installed.</summary>
public sealed class ConfigPlugin
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}
