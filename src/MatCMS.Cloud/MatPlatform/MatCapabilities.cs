// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using System.Text.RegularExpressions;

namespace MatPlatform;

/// <summary>A value of a binding: its key in code (<c>url</c>), the environment variable it becomes (<c>MAT_OFFICE_EDIT_URL</c>), its label and whether it is a secret.</summary>
public sealed record MatBindingValue(string Key, string Variable, string LabelDe, string LabelEn, bool Secret = false, bool Required = true, string Type = "text")
{
    public string Label(string language) => language == "en" ? LabelEn : LabelDe;
}

/// <summary>
/// What a capability is (docs/PLATFORM.md "Capabilities"): its name, its version, what it is for in words, the values of a binding and whether an administrator
/// may enter them by hand in the Connections area (<see cref="ByHand"/>; the capabilities of the engines are set with their variables only).
/// </summary>
public sealed record MatCapabilitySpec(string Name, int Version, string LabelDe, string LabelEn, IReadOnlyList<MatBindingValue> Values, bool ByHand = true)
{
    public string Label(string language) => language == "en" ? LabelEn : LabelDe;

    /// <summary>The value with this key, or null.</summary>
    public MatBindingValue? Value(string key) => Values.FirstOrDefault(v => v.Key == key);
}

/// <summary>
/// The capabilities of the family. A capability that is not written down here is a <em>key capability</em> (<see cref="KeyStyle"/>): a binding is the address of the
/// provider and a key for the consumer, as <c>MAT_&lt;CAPABILITY&gt;_URL</c>, <c>_INTERNAL_URL</c> and <c>_KEY</c>. So a new capability between two apps needs no change here.
/// </summary>
public static partial class MatCapabilities
{
    public const string IdentityOidc = "identity.oidc";
    public const string MailSmtp = "mail.smtp";
    public const string AiOpenAi = "ai.openai";
    public const string ShellEmbed = "shell.embed";
    public const string WebRoute = "web.route";
    public const string OfficeEdit = "office.edit";

    /// <summary>The keys of the values of a key capability.</summary>
    public const string Url = "url";
    public const string InternalUrl = "internalUrl";
    public const string Key = "key";

    private static readonly Dictionary<string, MatCapabilitySpec> Known = new(StringComparer.Ordinal)
    {
        [IdentityOidc] = new(IdentityOidc, 1, "Anmeldung über", "Signs people in with",
        [
            new("authority", "MATIDENTITY_AUTHORITY", "Adresse", "Address", Type: "url"),
            new("internalUrl", "MATIDENTITY_INTERNAL_URL", "Interne Adresse", "Internal address", Required: false, Type: "url"),
            new("clientId", "MATIDENTITY_CLIENT_ID", "Client", "Client", Required: false),
            new("clientSecret", "MATIDENTITY_CLIENT_SECRET", "Geheimnis des Clients", "Client secret", Secret: true),
        ], ByHand: false),
        [MailSmtp] = new(MailSmtp, 1, "Sendet Mails über", "Sends mail through",
        [
            new("host", "MATSMTP_HOST", "Server", "Server"),
            new("port", "MATSMTP_PORT", "Port", "Port", Required: false, Type: "number"),
            new("username", "MATSMTP_USERNAME", "Benutzer", "User", Required: false),
            new("password", "MATSMTP_PASSWORD", "Passwort", "Password", Secret: true, Required: false),
            new("from", "MATSMTP_FROM", "Absender", "Sender", Required: false, Type: "email"),
            new("tls", "MATSMTP_TLS", "Verschlüsselung", "Encryption", Required: false),
        ], ByHand: false),
        [AiOpenAi] = new(AiOpenAi, 1, "KI über", "AI through",
        [
            new("url", "MATAI_URL", "Adresse", "Address", Type: "url"),
            new("key", "MATAI_KEY", "Schlüssel", "Key", Secret: true),
            new("model", "MATAI_MODEL", "Modell", "Model", Required: false),
        ], ByHand: false),
        [ShellEmbed] = new(ShellEmbed, 1, "Wird gezeigt in", "Shown inside",
        [
            new("frameAncestors", MatFrameAncestors.Variable, "Adressen", "Addresses"),
        ], ByHand: false),
        [WebRoute] = new(WebRoute, 1, "Erreichbar unter", "Reachable at",
        [
            new("publicUrl", MatPlatformSettings.PublicUrlVariable, "Öffentliche Adresse", "Public address", Type: "url"),
        ], ByHand: false),
        [OfficeEdit] = KeyStyle(OfficeEdit, "Bearbeitet Office-Dateien mit", "Edits office files with"),
    };

    /// <summary>The capabilities written down here.</summary>
    public static IReadOnlyCollection<MatCapabilitySpec> All => Known.Values;

    /// <summary>What <paramref name="capability"/> is: the one written down here, else a key capability.</summary>
    public static MatCapabilitySpec Of(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        return Known.TryGetValue(capability, out var spec) ? spec : KeyStyle(capability, capability, capability);
    }

    /// <summary>A key capability: the address of the provider (public and on the shared network) and a key made for the consumer.</summary>
    public static MatCapabilitySpec KeyStyle(string capability, string labelDe, string labelEn)
    {
        var prefix = PrefixOf(capability);
        return new MatCapabilitySpec(capability, 1, labelDe, labelEn,
        [
            new(Url, prefix + "_URL", "Adresse", "Address", Type: "url"),
            new(InternalUrl, prefix + "_INTERNAL_URL", "Interne Adresse", "Internal address", Required: false, Type: "url"),
            new(Key, prefix + "_KEY", "Schlüssel", "Key", Secret: true),
        ]);
    }

    /// <summary>The prefix of the variables of a capability: <c>office.edit</c> → <c>MAT_OFFICE_EDIT</c>.</summary>
    public static string PrefixOf(string capability)
        => "MAT_" + NotAlphanumeric().Replace(capability.ToUpperInvariant(), "_");

    [GeneratedRegex("[^A-Z0-9]")]
    private static partial Regex NotAlphanumeric();
}
