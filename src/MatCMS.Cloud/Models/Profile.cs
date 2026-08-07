namespace MatCMS.Cloud.Models;

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

    // --- Which payloads this profile pushes ---------------------------------
    public bool SyncSettings { get; set; }
    public bool SyncUsers { get; set; }
    public bool SyncPlugins { get; set; }
    public bool SyncComponents { get; set; }
    public bool SyncTemplates { get; set; }

    /// <summary>
    /// Name of the template that should be the ACTIVE design on every assigned instance. Empty means
    /// "roll the templates out but leave the choice to the site" — switching the live design of a
    /// customer's website is a decision that deserves its own switch, not a side effect of syncing.
    /// </summary>
    public string? ActivateTemplateName { get; set; }

    /// <summary>
    /// Overwrite semantics per payload. Settings/plugins/components default to overwriting (that is
    /// what "keep in sync" means), users only ADD — silently rewriting local accounts, or worse
    /// removing them, is how an operator gets locked out of their own site.
    /// </summary>
    public bool OverwriteSettings { get; set; } = true;
    public bool OverwritePlugins { get; set; } = true;
    public bool OverwriteComponents { get; set; } = true;
    public bool OverwriteTemplates { get; set; } = true;

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
