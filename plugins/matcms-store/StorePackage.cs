using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.MatcmsStore;

/// <summary>What an uploaded package says about itself, read from its <c>plugin.json</c>, plus its hash and file list.</summary>
internal sealed record PackageInfo(string Key, string Name, string Version, string Kind, string Api, string Description, string MinCms, string Sha256,
    long Size, IReadOnlyList<string> Files, string Look = "")
{
    /// <summary>A template or a component: one JSON file, no zip.</summary>
    public bool IsJson => Kind is StorePackage.KindTemplate or StorePackage.KindComponent;
}

/// <summary>
/// Reads an uploaded plugin package BEFORE anything is stored: the same container rules the sites apply when they import it (MatCMS.Shared/PluginBundle —
/// repeated here, a plugin is written against the plugin API only), so the store never accepts what a site would refuse. Sizes are counted while
/// reading, not taken from the zip's own length fields.
/// </summary>
internal static class StorePackage
{
    public const string Manifest = "plugin.json";
    private const long MaxManifest = 1 * 1024 * 1024;
    private const long MaxUnpacked = 100L * 1024 * 1024;
    private const int MaxEntries = 600;

    private static readonly Regex KeyRx = new("^[a-z][a-z0-9-]{1,40}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionRx = new("^[0-9][0-9A-Za-z.+-]{0,19}$", RegexOptions.CultureInvariant);

    public const string KindTemplate = "template", KindComponent = "component";
    private const int MaxJson = 2 * 1024 * 1024;

    /// <summary>Plugin, template or component — what a package kind belongs to. A key keeps its group for good.</summary>
    public static string Group(string kind) => kind is KindTemplate or KindComponent ? kind : "plugin";

    /// <summary>
    /// The key a template or component has in the store — the SAME rule the sites apply when they check a package (MatCMS.Shared/StoreItemPackage):
    /// a template by its name, a component by its type, lower case, everything but letters and digits a hyphen.
    /// </summary>
    public static string JsonKey(string kind, string nameOrType)
    {
        var s = (nameOrType ?? "").Trim().ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>Reads the package — a plugin zip, or the JSON a MatCMS template or component editor exports (then <paramref name="version"/> comes from
    /// the form: that JSON names no version) — or says (as a string key of the plugin) why it is not one.</summary>
    public static (PackageInfo? Info, string? Error) Read(byte[] bytes, string? formVersion = null)
    {
        var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        while (start < bytes.Length && bytes[start] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') start++;
        if (start < bytes.Length && bytes[start] == (byte)'{') return ReadJson(bytes, (formVersion ?? "").Trim());
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) return (null, "store.err.pkgEntries");
            var files = new List<string>();
            long unpacked = 0;
            JsonObject? manifest = null;
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (name.Length == 0 || name.EndsWith('/')) continue;
                if (name.StartsWith('/') || name.Split('/').Any(s => s == "..")) return (null, "store.err.pkgPath");
                files.Add(name);
                // count what the entry really unpacks to, not what its header claims
                using var s = e.Open();
                var buf = new byte[81920];
                using var ms = name == Manifest ? new MemoryStream() : null;
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    unpacked += n;
                    if (unpacked > MaxUnpacked) return (null, "store.err.pkgSize");
                    if (ms is not null)
                    {
                        if (ms.Length + n > MaxManifest) return (null, "store.err.pkgManifest");
                        ms.Write(buf, 0, n);
                    }
                }
                // A manifest written with a byte-order mark (StreamWriter with Encoding.UTF8 writes one) is still a manifest.
                if (ms is not null)
                {
                    try { manifest = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿')) as JsonObject; }
                    catch (System.Text.Json.JsonException) { return (null, "store.err.pkgManifest"); }
                }
            }
            if (manifest is null) return (null, "store.err.pkgManifest");

            string F(string field) => manifest[field] is JsonValue v ? (v.TryGetValue<string>(out var t) ? t : v.ToJsonString()).Trim() : "";
            var key = F("Key").ToLowerInvariant();
            var version = F("Version");
            var kind = F("Kind").ToLowerInvariant() is { Length: > 0 } k ? k : "script";
            if (!KeyRx.IsMatch(key)) return (null, "store.err.pkgKey");
            if (!VersionRx.IsMatch(version)) return (null, "store.err.pkgVersion");
            if (kind is not ("script" or "native")) return (null, "store.err.pkgKind");
            if (kind == "native" && !files.Any(f => f.StartsWith("src/", StringComparison.Ordinal))) return (null, "store.err.pkgNative");
            if (kind == "script" && F("Code").Length == 0) return (null, "store.err.pkgScript");
            var title = F("Name") is { Length: > 0 } nm ? nm : key;
            return (new PackageInfo(key, StoreText.Cut(title, 80), version, kind, F("Api"), F("Description"), F("MinCms"),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength, files), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException or NotSupportedException)
        {
            return (null, "store.err.pkgZip");
        }
    }

    /// <summary>A template (it has a layout or colours) or a component (it has a type and its markup), as the editors export them.</summary>
    private static (PackageInfo? Info, string? Error) ReadJson(byte[] bytes, string version)
    {
        if (bytes.Length > MaxJson) return (null, "store.err.pkgSize");
        JsonObject? o;
        try { o = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) as JsonObject; }
        catch (System.Text.Json.JsonException) { return (null, "store.err.pkgJson"); }
        if (o is null) return (null, "store.err.pkgJson");
        string F(string field) => o[field] is JsonValue v && v.TryGetValue<string>(out var t) ? t.Trim() : "";
        bool Has(string field) => o.ContainsKey(field);

        string kind, key, name;
        var look = new JsonObject();
        if (Has("Type") && (Has("TemplateHtml") || Has("FieldsJson")))
        {
            kind = KindComponent;
            name = F("Name") is { Length: > 0 } n ? n : F("Type");
            key = JsonKey(kind, F("Type"));
            if (F("Icon") is { Length: > 0 } icon) look["icon"] = StoreText.Cut(icon, 60);
        }
        else if (F("Name").Length > 0 && (Has("LayoutHtml") || Has("AccentColor") || Has("CustomCss")))
        {
            kind = KindTemplate;
            name = F("Name");
            key = JsonKey(kind, name);
            // What a catalogue may show of a template's look — colours and fonts, never its code. Values are checked where they are drawn.
            foreach (var (field, prop) in new[] { ("AccentColor", "accent"), ("SecondaryColor", "secondary"), ("HeadingColor", "heading"), ("TextColor", "text"),
                         ("BackgroundColor", "background"), ("AltBackground", "alt"), ("HeaderBackground", "headerBackground"), ("HeaderTextColor", "headerText"),
                         ("HeadingFont", "headingFont"), ("BodyFont", "bodyFont"), ("ButtonStyle", "buttonStyle"), ("ButtonRadius", "buttonRadius") })
                if (F(field) is { Length: > 0 and <= 60 } v) look[prop] = v;
        }
        else return (null, "store.err.pkgJson");

        if (!KeyRx.IsMatch(key)) return (null, "store.err.pkgKey");
        if (version.Length == 0) return (null, "store.err.pkgNoVersion");
        if (!VersionRx.IsMatch(version)) return (null, "store.err.pkgVersion");
        return (new PackageInfo(key, StoreText.Cut(name, 80), version, kind, "", F("Description"), "",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength, [kind + ".json"], look.ToJsonString()), null);
    }
}

/// <summary>
/// The store's signature — the SAME scheme as <c>MatCMS.Shared/StoreSignature</c>, which sites and clouds check (a plugin is written against the plugin
/// API only, so it keeps its own copy; <c>MatcmsStoreTests</c> signs with the shared code and has this copy accept it). The store server never signs:
/// it only checks that what the operator pastes is a valid signature of exactly this package before it publishes it.
/// </summary>
internal static class StoreSig
{
    public const string Scheme = "matcms-store-v1";

    public static string Text(string kind, string key, string version, string sha256) =>
        string.Join('\n', Scheme, kind.Trim().ToLowerInvariant(), key.Trim().ToLowerInvariant(), version.Trim(), sha256.Trim().ToLowerInvariant());

    /// <summary>The key ids and public keys this store accepts (setting <c>signingKeys</c>, one <c>id=base64</c> per line).</summary>
    public static Dictionary<string, string> Keys(IPluginContext ctx)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (ctx.Settings.Get(StoreSettings.SigningKeys) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = line.IndexOf('=');
            if (i > 0) keys[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return keys;
    }

    /// <summary>Splits "keyid:signature" as the signing tool prints it.</summary>
    public static (string KeyId, string Signature) Parse(string token)
    {
        token = (token ?? "").Trim();
        var i = token.IndexOf(':');
        return i <= 0 ? ("", "") : (token[..i].Trim().ToLowerInvariant(), token[(i + 1)..].Trim());
    }

    public static bool Verify(IPluginContext ctx, string text, string keyId, string signature)
    {
        if (!Keys(ctx).TryGetValue(keyId, out var spki) || signature.Length == 0) return false;
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            return ec.VerifyData(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
