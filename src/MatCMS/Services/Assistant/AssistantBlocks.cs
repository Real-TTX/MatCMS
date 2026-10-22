using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Content;
using MatCMS.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Assistant;

/// <summary>
/// The blocks as the assistant knows them: the catalogue it is told (every block, EVERY field — pictures, lists, choices,
/// the layout options) and the trusted boundary for what it writes back. The old generators offered text fields only,
/// which is why a generated page never had a picture, a column or a style; this offers what the editor offers.
/// <para>Two kinds of block stay out: those with a raw-HTML field (the HTML block — a model writing unescaped markup onto a
/// live page, steered by whatever document was attached, is a door nobody should open by accident) and, implicitly, the
/// fields nobody may set from outside (<c>_showIf</c> is a plugin page's). Everything else is validated per field kind
/// here: a choice must be one of its options, a picture must be in the media library (or an https address), a link may
/// not be <c>javascript:</c>, formatted text is sanitised. What does not fit is dropped WITH a warning that goes back to
/// the model, so it can correct itself instead of the editor finding a block that silently lost half its content.</para>
/// </summary>
public sealed class AssistantBlocks
{
    private readonly BlockRegistry _registry;
    private readonly Localizer _t;
    private readonly AppDbContext _db;
    private readonly IServiceProvider _services;
    private Dictionary<string, List<SelectOption>> _sources = new();
    private HashSet<string> _media = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Upper bounds per write: blocks per call, items per list, nesting depth.</summary>
    public const int MaxBlocksPerCall = 120, MaxListItems = 60, MaxDepth = 6;

    public AssistantBlocks(BlockRegistry registry, Localizer t, AppDbContext db, IServiceProvider services)
    {
        _registry = registry;
        _t = t;
        _db = db;
        _services = services;
    }

    /// <summary>Loads what validation needs from the database: the dynamic option lists (forms, media tags, theme colours,
    /// plugin lists) and the media library's addresses. Call again after the library changed (an upload).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var plugins = _services.GetService<Plugins.PluginContributions>();
        _sources = await BlockSchema.LoadSourcesAsync(_db, Usable(), plugins, _services);
        _media = (await _db.Media.AsNoTracking().Select(m => m.Url).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void KnowMedia(string url) => _media.Add(url);

    /// <summary>After the assistant made or changed a component: the registry builds its list again (it caches it for the request) and the
    /// catalogue and validation see the new block type at once.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        _registry.Reset();
        await LoadAsync(ct);
    }

    /// <summary>Every block type there is (built-in, components, plugins) — what a new component's type must not be.</summary>
    public HashSet<string> AllTypes() => _registry.All.Select(d => d.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The block types the assistant may use.</summary>
    public IEnumerable<BlockDefinition> Usable() => _registry.All.Where(d => !HasRawHtml(d.Fields));

    public BlockDefinition? Get(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var def = _registry.Get(type.Trim());
        return def is not null && !HasRawHtml(def.Fields) ? def : null;
    }

    private static bool HasRawHtml(IEnumerable<BlockField> fields) => fields.Any(f => f.RawHtml || HasRawHtml(f.ItemFields));

    // ---------------------------------------------------------------------------------------------- catalogue

    /// <summary>The catalogue as prompt text: one entry per block with its description, its fields (kind, choices) and
    /// where it may stand. Compact on purpose — it goes with every call.</summary>
    public string CatalogText()
    {
        var usable = Usable().ToList();
        // Where a child-only block may stand: the containers that list it.
        var parents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in usable)
            foreach (var c in d.AllowedChildren)
                (parents.TryGetValue(c, out var l) ? l : parents[c] = new()).Add(d.Type);

        var sb = new StringBuilder();
        foreach (var d in usable.OrderBy(d => d.ChildOnly).ThenBy(d => d.Category).ThenBy(d => d.Type))
        {
            sb.Append("- ").Append(d.Type).Append(" — ").Append(Plain(_t[d.Name]));
            var desc = Plain(_t[d.Description]);
            if (desc.Length > 0) sb.Append(": ").Append(desc.Length > 160 ? desc[..160] + "…" : desc);
            sb.Append('\n');
            if (d.Fields.Count > 0)
                sb.Append("    Felder: ").Append(string.Join("; ", d.Fields.Select(FieldText))).Append('\n');
            if (d.IsContainer) sb.Append("    enthält Kinder: ").Append(string.Join(", ", d.AllowedChildren)).Append('\n');
            if (d.ChildOnly) sb.Append("    nur innerhalb von: ").Append(parents.TryGetValue(d.Type, out var p) ? string.Join(", ", p.Distinct()) : "?").Append('\n');
        }
        return sb.ToString();
    }

    private string FieldText(BlockField f)
    {
        var kind = f.Type switch
        {
            FieldType.Text => "Text",
            FieldType.Textarea => "mehrzeiliger Text",
            FieldType.RichText => "HTML (p, h2, h3, ul, li, strong, em, a)",
            FieldType.Image => "Bild-URL aus der Mediathek",
            FieldType.Url => "Link",
            FieldType.Select => "Auswahl " + OptionsText(f),
            FieldType.MultiSelect => "Mehrfachauswahl, kommagetrennt " + OptionsText(f),
            FieldType.List => "Liste von {" + string.Join("; ", f.ItemFields.Select(FieldText)) + "}",
            _ => "Text"
        };
        var label = Plain(_t[f.Label]);
        var s = $"{f.Id} ({label}: {kind}";
        if (!string.IsNullOrEmpty(f.Default) && f.Type is FieldType.Select) s += $", Standard \"{f.Default}\"";
        if (f.ShowWhenField is not null) s += $", nur wenn {f.ShowWhenField}=\"{f.ShowWhenValue}\"";
        return s + ")";
    }

    private string OptionsText(BlockField f)
    {
        var opts = OptionsOf(f);
        if (f.OptionsSource == "themeColors") return "[Farbe als #rrggbb, z. B. " + string.Join(", ", opts.Where(o => o.Value.Length > 0).Take(4).Select(o => o.Value)) + "]";
        if (opts.Count == 0) return "[keine Optionen vorhanden]";
        var shown = opts.Take(24).Select(o =>
        {
            var label = Plain(_t[o.Label]);
            return string.Equals(label, o.Value, StringComparison.OrdinalIgnoreCase) || label.Length == 0 ? $"\"{o.Value}\"" : $"\"{o.Value}\"={label}";
        });
        return "[" + string.Join(", ", shown) + (opts.Count > 24 ? ", …" : "") + "]";
    }

    private List<SelectOption> OptionsOf(BlockField f) =>
        f.OptionsSource is not null && _sources.TryGetValue(f.OptionsSource, out var dyn) ? dyn : f.Options;

    private static string Plain(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    // ---------------------------------------------------------------------------------------------- validation

    /// <summary>A block as the model sent it, already validated.</summary>
    public sealed record Input(string Type, JsonObject Data, List<Input> Children);

    /// <summary>Validates a list of blocks (a page, or the children of one container). <paramref name="parentType"/> null =
    /// top level. Bad entries are dropped with a warning; the counter bounds the whole call.</summary>
    public List<Input> ParseList(JsonNode? node, string? parentType, List<string> warnings, ref int budget, int depth = 0, string path = "blocks")
    {
        var result = new List<Input>();
        if (node is null) return result;
        if (node is not JsonArray arr) { warnings.Add($"{path}: erwartet eine Liste von Blöcken."); return result; }
        var i = 0;
        foreach (var item in arr)
        {
            var here = $"{path}[{i++}]";
            if (budget <= 0) { warnings.Add($"{here}: zu viele Blöcke in einem Aufruf (höchstens {MaxBlocksPerCall}) – Rest ignoriert."); break; }
            var b = Parse(item, parentType, warnings, ref budget, depth, here);
            if (b is not null) result.Add(b);
        }
        return result;
    }

    /// <summary>Validates ONE block (type, data, children).</summary>
    public Input? Parse(JsonNode? node, string? parentType, List<string> warnings, ref int budget, int depth = 0, string path = "block")
    {
        if (node is not JsonObject obj) { warnings.Add($"{path}: kein Block-Objekt."); return null; }
        var type = Str(obj["type"]);
        var def = Get(type);
        if (def is null) { warnings.Add($"{path}: unbekannter oder nicht erlaubter Blocktyp \"{type}\" – übersprungen."); return null; }
        if (parentType is null && def.ChildOnly)
        {
            warnings.Add($"{path}: \"{def.Type}\" kann nur innerhalb eines Containers stehen – übersprungen.");
            return null;
        }
        if (parentType is not null)
        {
            var parent = _registry.Get(parentType);
            if (parent is null || !parent.AllowedChildren.Contains(def.Type, StringComparer.OrdinalIgnoreCase))
            {
                warnings.Add($"{path}: \"{def.Type}\" darf nicht in \"{parentType}\" stehen – übersprungen.");
                return null;
            }
        }
        budget--;

        // The fields belong under "data"; a model that puts them next to "type" means the same thing.
        var dataNode = obj["data"] as JsonObject;
        if (dataNode is null)
        {
            dataNode = new JsonObject();
            foreach (var kv in obj)
                if (kv.Key is not ("type" or "children" or "id")) dataNode[kv.Key] = kv.Value?.DeepClone();
        }
        var data = ValidateData(def, dataNode, parentType is null, warnings, $"{path}.data");
        // A new block starts like one added in the editor (a new hero = overlay) — unless the model chose otherwise. A block that
        // carries an id is an existing one being re-sent: it keeps what it had.
        if (obj["id"] is null && BlockRegistry.NewBlockData.TryGetValue(def.Type, out var start))
            foreach (var (k, v) in start) if (!dataNode.ContainsKey(k)) data[k] = v;

        var children = new List<Input>();
        if (obj["children"] is JsonNode kids)
        {
            if (!def.IsContainer) warnings.Add($"{path}: \"{def.Type}\" kann keine Kinder enthalten – children ignoriert.");
            else if (depth >= MaxDepth) warnings.Add($"{path}: zu tief verschachtelt – children ignoriert.");
            else children = ParseList(kids, def.Type, warnings, ref budget, depth + 1, $"{path}.children");
        }
        return new Input(def.Type, data, children);
    }

    /// <summary>Validates the fields of one block. Returns only what passed; every value that did not becomes a warning.
    /// A JSON null keeps its place (as null) so a PATCH can say "clear this field".</summary>
    public JsonObject ValidateData(BlockDefinition def, JsonObject? input, bool topLevel, List<string> warnings, string path, bool keepNulls = false)
    {
        var data = new JsonObject();
        if (input is null) return data;
        var fields = def.Fields.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var g in BlockSchema.GlobalLayoutFields) if (topLevel) fields.TryAdd(g.Id, g);
        fields.TryAdd(BlockSchema.HiddenId, BlockSchema.HiddenField);
        fields.TryAdd(BlockSchema.CssField.Id, BlockSchema.CssField);

        foreach (var (key, value) in input)
        {
            if (!fields.TryGetValue(key, out var field))
            {
                warnings.Add($"{path}.{key}: Feld gibt es bei \"{def.Type}\" nicht – ignoriert.");
                continue;
            }
            if (value is null)
            {
                if (keepNulls) data[field.Id] = null;
                continue;
            }
            var clean = Field(field, value, warnings, $"{path}.{field.Id}");
            if (clean is not null) data[field.Id] = clean;
        }
        return data;
    }

    private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    private JsonNode? Field(BlockField f, JsonNode value, List<string> warnings, string path)
    {
        if (f.Type == FieldType.List) return ListField(f, value, warnings, path);
        // A list sent as JSON text inside a string is still a list; anything else is read as text.
        var s = value is JsonValue v ? (v.TryGetValue<string>(out var str) ? str : v.ToJsonString()) : null;
        if (s is null)
        {
            warnings.Add($"{path}: erwartet einen Text-Wert.");
            return null;
        }
        if (f.Id == BlockSchema.CssField.Id)
            return Cap(s.Replace("<", ""), 6000);   // scoped block CSS; no markup may ride along into the <style>

        switch (f.Type)
        {
            case FieldType.Text: return Cap(s, 4000);
            case FieldType.Textarea: return Cap(s, 20000);
            case FieldType.RichText: return Cap(SafeHtml.Sanitize(s), 60000);
            case FieldType.Image:
            {
                var u = s.Trim();
                if (u.Length == 0) return "";
                if (u.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
                {
                    if (_media.Contains(u)) return u;
                    warnings.Add($"{path}: \"{u}\" ist nicht in der Mediathek – Bild weggelassen (list_media zeigt, was es gibt).");
                    return null;
                }
                if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !u.Any(char.IsWhiteSpace) && u.IndexOfAny(['"', '\'', '(', ')', '<', '>']) < 0) return u;
                warnings.Add($"{path}: \"{u}\" ist keine erlaubte Bildadresse (Mediathek /uploads/… oder https://) – weggelassen.");
                return null;
            }
            case FieldType.Url:
            {
                var u = s.Trim();
                if (u.Length == 0 || IsSafeLink(u)) return Cap(u, 2000);
                warnings.Add($"{path}: \"{u}\" ist kein erlaubter Link – weggelassen.");
                return null;
            }
            case FieldType.Select:
            {
                var match = MatchOption(f, s);
                if (match is not null) return match;
                if (f.OptionsSource == "themeColors" && Hex.IsMatch(s.Trim())) return s.Trim().ToLowerInvariant();
                warnings.Add($"{path}: \"{s}\" ist keine gültige Auswahl. Erlaubt: {string.Join(", ", OptionsOf(f).Take(30).Select(o => "\"" + o.Value + "\""))}.");
                return null;
            }
            case FieldType.MultiSelect:
            {
                var parts = value is JsonArray a ? a.Select(x => Str(x)) : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var picked = new List<string>();
                foreach (var p in parts)
                {
                    var m = MatchOption(f, p);
                    if (m is null) warnings.Add($"{path}: \"{p}\" ist keine gültige Auswahl – weggelassen.");
                    else if (!picked.Contains(m)) picked.Add(m);
                }
                return string.Join(f.Separator ?? ", ", picked);
            }
            default: return Cap(s, 4000);
        }
    }

    private JsonNode? ListField(BlockField f, JsonNode value, List<string> warnings, string path)
    {
        var node = value;
        if (value is JsonValue jv && jv.TryGetValue<string>(out var text))
        {
            try { node = JsonNode.Parse(text) ?? new JsonArray(); } catch { node = null; }
        }
        if (node is not JsonArray arr) { warnings.Add($"{path}: erwartet eine Liste von Einträgen."); return null; }
        var items = new JsonArray();
        var sub = f.ItemFields.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var it in arr)
        {
            var here = $"{path}[{i++}]";
            if (items.Count >= MaxListItems) { warnings.Add($"{path}: mehr als {MaxListItems} Einträge – Rest ignoriert."); break; }
            if (it is not JsonObject o) { warnings.Add($"{here}: kein Eintrag-Objekt."); continue; }
            var clean = new JsonObject();
            foreach (var (k, v) in o)
            {
                if (!sub.TryGetValue(k, out var sf)) { warnings.Add($"{here}.{k}: Feld gibt es in dieser Liste nicht – ignoriert."); continue; }
                if (v is null) continue;
                var c = Field(sf, v, warnings, $"{here}.{sf.Id}");
                if (c is not null) clean[sf.Id] = c;
            }
            if (clean.Count > 0) items.Add(clean);
        }
        return items;
    }

    private string? MatchOption(BlockField f, string raw)
    {
        var v = raw.Trim();
        var opts = OptionsOf(f);
        var hit = opts.FirstOrDefault(o => string.Equals(o.Value, v, StringComparison.OrdinalIgnoreCase))
               ?? opts.FirstOrDefault(o => string.Equals(Plain(_t[o.Label]), v, StringComparison.OrdinalIgnoreCase));
        return hit?.Value;
    }

    /// <summary>Links the site may carry: on the site (/…, #…), the web (http/https), mail and phone. Never script or data.</summary>
    public static bool IsSafeLink(string u)
    {
        if (u.Any(char.IsControl)) return false;
        if (u.StartsWith('/') && !u.StartsWith("//")) return true;
        if (u.StartsWith('#') || u.StartsWith('?')) return true;
        return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("tel:", StringComparison.OrdinalIgnoreCase);
    }

    private static string Cap(string s, int max) => s.Length > max ? s[..max] : s;

    public static string Str(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : "";
}
