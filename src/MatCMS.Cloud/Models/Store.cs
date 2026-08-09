namespace MatCMS.Cloud.Models;

/// <summary>
/// The global store: plugins, templates, components, users and settings that exist independently of
/// any profile. Three consumers:
/// <list type="number">
/// <item>a <b>profile</b> picks entries to roll out (a reference, not a copy — updating the store
/// entry reaches every profile using it),</item>
/// <item>a profile can still define its OWN item of the same identity, which then <b>overrides</b>
/// the store entry for that profile,</item>
/// <item>an <b>instance</b> can browse the store itself and install an entry on demand
/// ("Weiter durchsuchen…" in MatCMS), without any profile being involved.</item>
/// </list>
/// <para>The store types deliberately mirror the <c>Profile*</c> types field for field: the same
/// payload travels either way, so <c>ProfileService.BuildConfigAsync</c> can merge both into one
/// configuration without special cases.</para>
/// </summary>
public class StorePlugin
{
    public int Id { get; set; }

    /// <summary>Identity — matches <c>Plugin.Key</c> on the instance. A same-key entry updates in
    /// place there and runs the plugin's own migration.</summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>The exact ZIP MatCMS's <c>PluginPackager.Export</c> produces.</summary>
    public byte[] Bundle { get; set; } = [];

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A theme in the store. <see cref="Name"/> is the identity.</summary>
public class StoreTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

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
    public string CustomCss { get; set; } = "";
    public string CustomJs { get; set; } = "";
    public string LayoutHtml { get; set; } = "";
    public string MenuMapJson { get; set; } = "{}";
    public string ParametersJson { get; set; } = "[]";
    public string ParamValuesJson { get; set; } = "{}";
    public int SchemaVersion { get; set; } = 1;
    public string PartsJson { get; set; } = "{}";
}

/// <summary>A reusable block in the store. <see cref="Type"/> is the identity.</summary>
public class StoreComponent
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string FieldsJson { get; set; } = "[]";
    public string TemplateHtml { get; set; } = "";
}


/// <summary>
/// The text of one kind of mail, in the catalogue. <see cref="Key"/> is the identity — it names
/// WHAT the mail is (e.g. <c>form.submission</c>) and is what the instance matches on.
/// <para>Which mails exist is decided by the CMS that sends them, so this table only ever carries
/// wording for keys MatCMS already knows. A key nobody sends is harmless but dead.</para>
/// </summary>
public class StoreMailTemplate
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

// --- Selection: which store entries a profile rolls out ----------------------
// Plain join rows rather than a many-to-many navigation, so a selection can be added or removed
// without loading the payload it points at.

public class ProfileStorePlugin
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public int StorePluginId { get; set; }
    public StorePlugin? StorePlugin { get; set; }
}

public class ProfileStoreTemplate
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public int StoreTemplateId { get; set; }
    public StoreTemplate? StoreTemplate { get; set; }
}

public class ProfileStoreComponent
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public int StoreComponentId { get; set; }
    public StoreComponent? StoreComponent { get; set; }
}

public class ProfileStoreMailTemplate
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public int StoreMailTemplateId { get; set; }
    public StoreMailTemplate? StoreMailTemplate { get; set; }
}


/// <summary>
/// Assigns one of the cloud's OWN users (Admin → Benutzer) to a profile, so the account is rolled
/// out to that profile's instances. There is no store table for users on purpose: they already exist
/// here, and a catalogue an instance can browse must never contain accounts.
/// </summary>
public class ProfileGlobalUser
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
}
