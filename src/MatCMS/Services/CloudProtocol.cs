namespace MatCMS.Services;

/// <summary>
/// The instance side of the MatCMS.Cloud contract. MUST stay in lockstep with
/// <c>MatCMS.Cloud/Services/InstanceProtocol.cs</c> — both repos change together.
/// <para>This instance always calls the cloud (outbound), so a site behind NAT/firewall needs no
/// inbound port. The one exception is cloud-initiated adoption, where the cloud reaches us ONCE at
/// <c>/api/cloud/link</c> to hand over the credentials; everything after that is outbound again.</para>
/// </summary>
public static class CloudProtocol
{
    /// <summary>Contract version this build speaks. Bump on every change to the payloads below; the
    /// cloud badges an instance reporting an older one as "veraltet".</summary>
    public const int Version = 4;

    /// <summary>Header carrying the instance token.</summary>
    public const string TokenHeader = "X-MatCMS-Instance-Token";
}

/// <summary>
/// What a cloud sends to <c>/api/cloud/link</c> when the operator adopts this instance from the
/// cloud side. The credentials are an ADMIN ACCOUNT OF THIS INSTANCE and are verified here before
/// the link is accepted; they are never stored.
/// </summary>
public sealed class CloudLinkRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? CloudUrl { get; set; }
    public string? InstanceId { get; set; }
    public string? Token { get; set; }
}

/// <summary>Enrollment: we introduce ourselves with a profile's join code.</summary>
public sealed class CloudRegisterRequest
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

/// <summary>What we store after a successful enrollment.</summary>
public sealed class CloudRegisterResponse
{
    public string InstanceId { get; set; } = "";
    public string Token { get; set; } = "";

    /// <summary>"Pending" or "Approved" — with approval required we must wait before we get config.</summary>
    public string Status { get; set; } = "";

    public string? ProfileName { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>What we report on every beat (~60 s).</summary>
public sealed class CloudHeartbeatRequest
{
    public int ProtocolVersion { get; set; } = CloudProtocol.Version;
    public string? Version { get; set; }
    public string? SiteName { get; set; }
    public string? Url { get; set; }
    public string? HostName { get; set; }

    /// <summary>Our own container id. This is what lets the cloud recognise that we run on ITS
    /// Docker daemon — without it the cloud can only notify, never update us.</summary>
    public string? ContainerId { get; set; }

    public string? ImageRef { get; set; }
    public int PageCount { get; set; }
    public int PluginCount { get; set; }
    public int UserCount { get; set; }

    /// <summary>Profile revision we last applied successfully.</summary>
    public int AppliedRevision { get; set; }

    /// <summary>Why the last apply failed, if it did.</summary>
    public string? SyncError { get; set; }

    /// <summary>What the last apply did, item by item.</summary>
    public List<CloudSyncItemReport>? SyncReport { get; set; }
}

/// <summary>The cloud's answer.</summary>
public sealed class CloudHeartbeatResponse
{
    public int ProtocolVersion { get; set; }

    /// <summary>"Pending" | "Approved" — pending means the operator has not accepted us yet.</summary>
    public string? Status { get; set; }

    public string? LatestVersion { get; set; }
    public bool UpdateAvailable { get; set; }

    /// <summary>True when the cloud found our container on its own daemon and could update us itself.</summary>
    public bool CloudCanUpdate { get; set; }

    public string? DisplayName { get; set; }
    public string? ProfileName { get; set; }

    /// <summary>Current revision of our assigned profile. Differs from what we applied = pull the
    /// config. 0 = nothing to sync.</summary>
    public int ConfigRevision { get; set; }
}

// --- Configuration payload ------------------------------------------------

public sealed class CloudConfig
{
    public int Revision { get; set; }
    public string? ProfileName { get; set; }

    /// <summary>Which cloud profile this came from. Scopes the "seeded once" marks: after a move to
    /// another profile the once-mode payloads must be allowed to seed again.</summary>
    public int ProfileId { get; set; }

    /// <summary>Setting key → value. Null = the profile does not sync settings, so don't touch them.</summary>
    public Dictionary<string, string?>? Settings { get; set; }

    public List<CloudConfigUser>? Users { get; set; }
    public List<CloudConfigComponent>? Components { get; set; }
    public List<CloudConfigPlugin>? Plugins { get; set; }
    public List<CloudConfigTemplate>? Templates { get; set; }

    /// <summary>Name of the template that should become the ACTIVE design. Empty = the site keeps
    /// its own choice; switching a live design is never a side effect of syncing.</summary>
    public string? ActivateTemplate { get; set; }

    /// <summary>Per-payload strategy, as a string: "keep" (make this instance match the profile on
    /// every revision), "add" (only add what is missing) or "once" (roll out on the first apply and
    /// never again). Anything unknown is read as "add" — the cautious end.</summary>
    public string SettingsMode { get; set; } = "keep";
    public string ComponentsMode { get; set; } = "keep";
    public string PluginsMode { get; set; } = "keep";
    public string TemplatesMode { get; set; } = "keep";
    public string UsersMode { get; set; } = "add";
}

/// <summary>A theme rolled out to this instance. <c>Name</c> is the identity.</summary>
public sealed class CloudConfigTemplate
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

public sealed class CloudConfigUser
{
    public string Username { get; set; } = "";
    public string? Email { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>Already-hashed password, copied verbatim — the cloud never holds the plaintext.</summary>
    public string PasswordHash { get; set; } = "";

    public string Role { get; set; } = "Admin";
}

public sealed class CloudConfigComponent
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string FieldsJson { get; set; } = "[]";
    public string TemplateHtml { get; set; } = "";
}

/// <summary>A plugin we should have. The bundle is downloaded separately, and only when the version
/// differs from what is installed.</summary>
public sealed class CloudConfigPlugin
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>What we did with one item while applying a configuration. Mirrors
/// <c>MatCMS.Cloud/Services/InstanceProtocol.cs</c> — both change together.</summary>
public sealed class CloudSyncItemReport
{
    /// <summary>"setting" | "user" | "component" | "template" | "plugin".</summary>
    public string Kind { get; set; } = "";

    /// <summary>The identity here: setting key, username, component type, template name, plugin key.</summary>
    public string Id { get; set; } = "";

    /// <summary>"installed" | "updated" | "skipped-exists" | "skipped-once" | "failed".</summary>
    public string Outcome { get; set; } = "";

    public string? Detail { get; set; }
}
