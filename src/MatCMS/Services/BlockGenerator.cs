using System.Text.Json.Nodes;
using MatCMS.Content;

namespace MatCMS.Services;

/// <summary>
/// Shared "AI generates blocks" logic: the compact spec of blocks the model may use, and the
/// TRUSTED-BOUNDARY validation of what it returns. Used by both the page generator (one page) and the
/// site generator (many pages) so there is ONE validator, not two that drift. Only known block types
/// and their known TEXT fields (Text/Textarea/RichText) survive; unknown types/fields, empty and
/// non-string values are dropped, and the result is bounded in count and length.
/// </summary>
public class BlockGenerator
{
    private readonly BlockRegistry _registry;
    private readonly Localizer _t;

    public BlockGenerator(BlockRegistry registry, Localizer t)
    {
        _registry = registry;
        _t = t;
    }

    public sealed record GenField(string Id, string Label, string Kind);
    public sealed record GenBlock(string Type, string Name, List<GenField> Fields);

    /// <summary>Top-level, fully-text-fillable blocks: no containers, no child-only, no repeaters (a
    /// half-filled list block renders wrong), at least one text field. Text fields only — the model
    /// writes prose, not colours or image URLs.</summary>
    public List<GenBlock> AllowedBlocks()
    {
        var result = new List<GenBlock>();
        foreach (var def in _registry.All)
        {
            if (def.ChildOnly || def.IsContainer) continue;
            if (def.Fields.Any(f => f.ItemFields.Count > 0)) continue;   // skip repeaters
            var fields = new List<GenField>();
            foreach (var f in def.Fields)
            {
                var kind = f.Type switch
                {
                    FieldType.Text => "text",
                    FieldType.Textarea => "multiline",
                    FieldType.RichText => "rich",
                    _ => null
                };
                if (kind is null) continue;
                fields.Add(new GenField(f.Id, _t[f.Label], kind));
            }
            if (fields.Count == 0) continue;
            result.Add(new GenBlock(def.Type, _t[def.Name], fields));
        }
        return result;
    }

    /// <summary>The block catalogue as compact prompt text (one line per block, its text fields).</summary>
    public string BuildSpecText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in AllowedBlocks())
        {
            sb.Append("- type \"").Append(b.Type).Append("\" (").Append(b.Name).Append("): ");
            sb.Append(string.Join(", ", b.Fields.Select(f =>
                $"{f.Id} [{f.Label}{(f.Kind == "multiline" ? ", mehrzeilig" : f.Kind == "rich" ? ", HTML erlaubt" : "")}]")));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The model is asked for a bare JSON array; tolerate a stray Markdown fence or prose.</summary>
    public static string ExtractJsonArray(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();
        }
        var a = s.IndexOf('[');
        var b = s.LastIndexOf(']');
        return (a >= 0 && b > a) ? s[a..(b + 1)] : s;
    }

    /// <summary>Validates a JSON string of blocks against the registry (parses first).</summary>
    public List<(string Type, string Name, JsonObject Data)> ValidateBlocks(string? json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(ExtractJsonArray(json)); } catch { return new(); }
        return ValidateBlocks(root as JsonArray);
    }

    /// <summary>Validates an already-parsed JSON array of blocks: only known types, only their known text
    /// fields, only non-empty strings; bounded to 12 blocks and 3000 chars per field. Returns clean
    /// (type, name, data). The returned data objects are fresh and unparented — safe to add anywhere.</summary>
    public List<(string Type, string Name, JsonObject Data)> ValidateBlocks(JsonArray? arr)
    {
        var result = new List<(string, string, JsonObject)>();
        if (arr is null) return result;
        var allowed = AllowedBlocks().GroupBy(b => b.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in arr)
        {
            if (result.Count >= 12) break;
            if (item is not JsonObject obj) continue;
            var type = (obj["type"] as JsonValue)?.ToString() ?? "";
            if (!allowed.TryGetValue(type, out var spec)) continue;
            var textIds = spec.Fields.ToDictionary(f => f.Id, f => f, StringComparer.OrdinalIgnoreCase);
            var data = new JsonObject();
            if (obj["data"] is JsonObject d)
                foreach (var kv in d)
                {
                    if (!textIds.TryGetValue(kv.Key, out var field)) continue;
                    if (kv.Value is JsonValue v && v.TryGetValue<string>(out var sv))
                    {
                        var val = sv.Trim();
                        if (val.Length == 0) continue;
                        if (val.Length > 3000) val = val[..3000];
                        // A "rich" field is rendered RAW on the public site (@Html.Raw). Since this whole
                        // method exists for MODEL-generated blocks (in-app AI generators AND the cloud's MCP
                        // content ops), that value is untrusted HTML → SANITISE it, or a generated block is a
                        // stored-XSS vector. Plain text/multiline fields are HTML-encoded on render, so they
                        // are left as-is (sanitising them would corrupt legitimate text like "a < b").
                        if (field.Kind == "rich")
                        {
                            val = SafeHtml.Sanitize(val);
                            if (val.Length == 0) continue;
                        }
                        data[kv.Key] = val;
                    }
                }
            if (data.Count == 0) continue;
            result.Add((spec.Type, spec.Name, data));
        }
        return result;
    }
}
