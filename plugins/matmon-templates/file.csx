// Reading an uploaded Matmon template file (Matmon's TemplateTransfer, format "matmon-templates") and making it safe to publish.
// Matmon's own export never carries a secret, but a file can be written by hand or by an older build: whatever Matmon's export
// would have left out is taken out here too (secret-looking parameters, protected values, credential values, a target's
// user:password, the instance-bound dependency sensor and built-in keys). What was removed is listed, so the submitter and the
// reviewer can see it. The checks mirror TemplateTransfer.Read: format, version, 2 MB, at most 500 templates.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed record MtTemplateInfo(string Name, string SensorType, string Scope);

public sealed record MtParsed(byte[] Bytes, IReadOnlyList<MtTemplateInfo> Templates, IReadOnlyList<string> Removed);

public sealed class MtFileException : Exception
{
    public string TextKey { get; }
    public object[] Args { get; }
    public MtFileException(string key, params object[] args) : base(key) { TextKey = key; Args = args; }
}

public static class MtFile
{
    public const string Format = "matmon-templates";
    public const int FormatVersion = 1;
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxTemplates = 500;
    public const string ProtectedPrefix = "enc:dp1:";
    private static readonly string[] SecretSuffixes = { "password", "secret", "apikey", "token", "passphrase", "privatekey", "community" };

    public static bool IsSecretParameter(string key)
    {
        var name = key[(key.LastIndexOf('.') + 1)..];
        return SecretSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A property by name, ignoring case (Matmon reads its files case-insensitively, so a hand-written file may differ).</summary>
    private static string? Key(JsonObject o, string name) =>
        o.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static JsonNode? Get(JsonObject o, string name) => Key(o, name) is string k ? o[k] : null;

    private static string Str(JsonObject o, string name) =>
        Get(o, name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static void Remove(JsonObject o, string name) { if (Key(o, name) is string k) o.Remove(k); }

    public static MtParsed Read(byte[] bytes)
    {
        if (bytes.Length == 0) throw new MtFileException("mt.err.empty");
        if (bytes.Length > MaxBytes) throw new MtFileException("mt.err.tooLarge");

        JsonNode? root;
        try { root = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw new MtFileException("mt.err.notJson"); }
        if (root is not JsonObject package || !string.Equals(Str(package, "format"), Format, StringComparison.Ordinal))
            throw new MtFileException("mt.err.format");

        if (Get(package, "version") is JsonValue ver && ver.TryGetValue<int>(out var version) && version > FormatVersion)
            throw new MtFileException("mt.err.newer", version);

        if (Get(package, "templates") is not JsonArray templates || templates.Count == 0) throw new MtFileException("mt.err.noTemplates");
        if (templates.Count > MaxTemplates) throw new MtFileException("mt.err.tooMany", templates.Count, MaxTemplates);

        var infos = new List<MtTemplateInfo>();
        var removed = new List<string>();
        foreach (var node in templates)
        {
            if (node is not JsonObject t) continue;
            var name = Str(t, "name").Trim();
            if (name.Length == 0) name = "?";
            if (name.Length > 120) name = name[..120];
            infos.Add(new MtTemplateInfo(name, Str(t, "sensorTypeKey").Trim(), Str(t, "targetKind").Trim()));

            Remove(t, "key");                        // a built-in key would let a copy stand in for a template the instance seeds itself
            var target = Str(t, "target");
            if (target.Length > 0 && Uri.TryCreate(target.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            {
                t[Key(t, "target")!] = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString();
                removed.Add(name + ": target user/password");
            }

            if (Get(t, "settings") is not JsonObject settings) continue;
            Remove(settings, "dependencySensorId");  // a sensor of the submitter's instance; means nothing anywhere else
            if (Get(settings, "parameters") is JsonObject parameters)
            {
                foreach (var key in parameters.Select(kv => kv.Key).ToList())
                {
                    var value = parameters[key] is JsonValue pv && pv.TryGetValue<string>(out var s) ? s : "";
                    if (IsSecretParameter(key) || value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
                    {
                        parameters.Remove(key);
                        removed.Add(name + ": " + key);
                    }
                }
            }
            if (Get(settings, "credentials") is JsonArray credentials)
            {
                foreach (var c in credentials.OfType<JsonObject>())
                {
                    var had = Get(c, "values") is JsonObject vals && vals.Count > 0 || Str(c, "protectedValues").Length > 0;
                    if (Key(c, "values") is string vk) c[vk] = new JsonObject();
                    if (Key(c, "protectedValues") is string pk) c[pk] = "";
                    if (had) removed.Add(name + ": " + (Str(c, "name") is { Length: > 0 } cn ? cn : "credentials"));
                }
            }
        }
        if (infos.Count == 0) throw new MtFileException("mt.err.noTemplates");

        var clean = Encoding.UTF8.GetBytes(package.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new MtParsed(clean, infos, removed);
    }

    /// <summary>The file name a download gets: the entry's slug and version, nothing a visitor typed.</summary>
    public static string DownloadName(string slug, string version) =>
        "matmon-templates-" + slug + (string.IsNullOrWhiteSpace(version) ? "" : "-" + new string(version.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-').ToArray())) + ".json";
}
