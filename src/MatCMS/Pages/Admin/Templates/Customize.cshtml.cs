using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Templates;

/// <summary>User-facing "Anpassen": edit the values of the parameters a template designer published.</summary>
public class CustomizeModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AiService _ai;
    public CustomizeModel(AppDbContext db, AiService ai)
    {
        _db = db;
        _ai = ai;
    }

    public Template Current { get; private set; } = default!;
    public List<TemplateParam> Params { get; private set; } = new();
    public Dictionary<string, string> Values { get; private set; } = new();
    /// <summary>Whether the AI "adjust design" action is offered (AI switched on for this site).</summary>
    public bool AiEnabled { get; private set; }

    [BindProperty] public Dictionary<string, string> Val { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var t = await _db.Templates.FindAsync(id);
        if (t is null) return RedirectToPage("Index");
        Current = t;
        Params = TemplateParams.Schema(t.ParametersJson);
        Values = TemplateParams.Resolve(t);
        AiEnabled = _ai.Enabled;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var t = await _db.Templates.FindAsync(id);
        if (t is null) return RedirectToPage("Index");

        var obj = new System.Text.Json.Nodes.JsonObject();
        foreach (var p in TemplateParams.Schema(t.ParametersJson))
            obj[p.Id] = Val.TryGetValue(p.Id, out var v) ? (v ?? "") : "";
        t.ParamValuesJson = obj.ToJsonString();
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Anpassungen gespeichert.";
        return RedirectToPage("Index");
    }

    /// <summary>
    /// AI "adjust design": the model is given this template's published parameters (label, type, options,
    /// current value) and a design instruction, and proposes new values through the cloud relay. Every
    /// proposed value is validated against its parameter's declared type here — nothing the model returns
    /// is trusted. Returns a before/after per changed parameter WITHOUT saving; the client writes the
    /// accepted values into the real form controls and submits the normal save (PRG).
    /// </summary>
    public async Task<IActionResult> OnPostAiThemeAsync(int id, string? instruction)
    {
        if (!_ai.Enabled)
            return new JsonResult(new { ok = false, error = "KI ist für diese Website nicht aktiviert." });

        var t = await _db.Templates.FindAsync(id);
        if (t is null) return new JsonResult(new { ok = false, error = "Vorlage nicht gefunden." });

        var schema = TemplateParams.Schema(t.ParametersJson);
        if (schema.Count == 0)
            return new JsonResult(new { ok = false, error = "Diese Vorlage hat keine anpassbaren Parameter." });

        var resolved = TemplateParams.Resolve(t);
        var instr = string.IsNullOrWhiteSpace(instruction)
            ? "Modernisiere das Design dezent: harmonische Farben und gute Lesbarkeit."
            : instruction!.Trim();

        var infos = schema
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Select(p => (p.Id, p.Label, p.Type, p.Options,
                          resolved.TryGetValue(p.Id, out var v) ? v : (p.Default ?? "")))
            .ToList();

        var (ok, text, error) = await _ai.SuggestThemeAsync(instr, infos, HttpContext.RequestAborted);
        if (!ok) return new JsonResult(new { ok = false, error = error ?? "KI-Aufruf fehlgeschlagen." });

        JsonObject? proposal;
        try { proposal = JsonNode.Parse(ExtractJson(text)) as JsonObject; }
        catch { proposal = null; }
        if (proposal is null)
            return new JsonResult(new { ok = false, error = "Die KI hat keinen verwertbaren Vorschlag geliefert." });

        var byId = schema.Where(p => !string.IsNullOrWhiteSpace(p.Id))
                         .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var changes = new List<object>();
        foreach (var kv in proposal)
        {
            if (!byId.TryGetValue(kv.Key, out var p)) continue;                  // unknown param → ignore
            if (kv.Value is not JsonValue jv) continue;
            var raw = jv.TryGetValue<string>(out var s) ? s : kv.Value!.ToString();
            var after = Normalize(p, raw);
            if (after is null) continue;                                         // invalid for this type
            var before = resolved.TryGetValue(p.Id, out var bv) ? bv : (p.Default ?? "");
            if (string.Equals(after, before, StringComparison.Ordinal)) continue; // no change
            changes.Add(new { id = p.Id, label = p.Label, type = p.Type, before, after });
        }

        if (changes.Count == 0)
            return new JsonResult(new { ok = false, error = "Die KI hatte keine passende Anpassung vorzuschlagen." });

        return new JsonResult(new { ok = true, changes });
    }

    // The model is asked for bare JSON, but tolerate a stray Markdown fence or surrounding prose.
    private static string ExtractJson(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();
        }
        var a = s.IndexOf('{');
        var b = s.LastIndexOf('}');
        return (a >= 0 && b > a) ? s[a..(b + 1)] : s;
    }

    // Validates/canonicalises a proposed value against the parameter's declared type; null = reject.
    private static string? Normalize(TemplateParam p, string? raw)
    {
        var v = (raw ?? "").Trim();
        if (v.Length == 0) return null;
        switch (p.Type)
        {
            case "color":
                if (!v.StartsWith('#')) v = "#" + v;
                v = v.ToLowerInvariant();
                if (Regex.IsMatch(v, "^#[0-9a-f]{3}$"))
                    v = "#" + new string(new[] { v[1], v[1], v[2], v[2], v[3], v[3] });
                return Regex.IsMatch(v, "^#[0-9a-f]{6}$") ? v : null;
            case "select":
                return p.OptionList().FirstOrDefault(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase));
            case "number":
                return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out _) ? v : null;
            case "bool":
                if (v is "true" or "1" or "yes" or "ja") return "true";
                if (v is "false" or "0" or "no" or "nein") return "false";
                return null;
            default:
                return v.Length <= 400 ? v : v[..400];   // text — keep it bounded
        }
    }
}
