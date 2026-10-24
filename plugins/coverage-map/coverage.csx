// Abdeckungskarte — a block that shows on a map of Europe where something is available (an app's maps, a delivery area, a service).
// Written like store-einblick/showcase.csx: plain C# valid as a script (#load) and in a library, so a native variant can compile the same
// text later — usings, types, no namespace, no top-level statements, only the plugin API. The outlines are map.csx (generated).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

/// <summary>One line of the block's list: a country (ISO code) or a region (IT-N), its status and an optional note.</summary>
public sealed record CovEntry(string Code, string Status, string Note);

public static class CovPlugin
{
    public const string BlockType = "coverage_map";
    public static readonly string[] Statuses = { "available", "partial", "soon", "planned" };
    public const string DefaultList = "DE | available\nAT | available\nCH | available\nFR | available\nIT-N | available";

    public static void Configure(IPluginBuilder b)
    {
        b.Strings("de", CovText.De);
        b.Strings("en", CovText.En);
        b.Block(new BlockSpec(BlockType, "cov.block.name", "cov.block.desc")
        {
            Category = "design",
            IconSvg = "<path d=\"M3 7l6-3 6 3 6-3v13l-6 3-6-3-6 3z\"/><path d=\"M9 4v13\"/><path d=\"M15 7v13\"/>",
            Fields = new[]
            {
                new FieldSpec("heading", "cov.f.heading"),
                new FieldSpec("intro", "cov.f.intro", FieldKind.Textarea),
                new FieldSpec("list", "cov.f.list", FieldKind.Textarea) { Default = DefaultList, Help = "cov.f.listHelp" },
                new FieldSpec("view", "cov.f.view", FieldKind.Select)
                {
                    Default = "europe", Options = new[] { new FieldOption("europe", "cov.o.europe"), new FieldOption("fit", "cov.o.fit") }
                },
                new FieldSpec("labels", "cov.f.labels", FieldKind.Select)
                {
                    Default = "yes", Options = new[] { new FieldOption("yes", "cov.o.yes"), new FieldOption("no", "cov.o.no") }
                },
                new FieldSpec("asOf", "cov.f.asOf") { Help = "cov.f.asOfHelp" },
            }
        }, RenderAsync);
    }

    /// <summary>Reads the list field: one entry per line, <c>CODE | status | note</c>; unknown codes and blank lines are skipped, an unknown
    /// status counts as available, a code listed twice keeps its first line.</summary>
    public static List<CovEntry> Parse(string? text)
    {
        var list = new List<CovEntry>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var parts = raw.Split('|').Select(p => p.Trim()).ToArray();
            var code = parts[0].ToUpperInvariant();
            if (code.Length == 0 || !CovMap.Countries.Any(c => c.Iso == code) || list.Any(e => e.Code == code)) continue;
            var status = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            status = Statuses.Contains(status) ? status : "available";
            list.Add(new CovEntry(code, status, parts.Length > 2 ? string.Join(" | ", parts.Skip(2)) : ""));
        }
        return list;
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public static Task<PluginResult> RenderAsync(IPluginContext ctx, CancellationToken ct)
    {
        string F(string id) => ctx.Block?.Get(id) ?? "";
        var de = ctx.Locale.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        // A block that never had the field (placed before it existed, or built by hand) shows the default; an emptied one shows nothing.
        var entries = Parse(ctx.Block?.Values.ContainsKey("list") == true ? F("list") : DefaultList);
        var shapes = CovMap.Countries.ToDictionary(c => c.Iso);
        string Name(string code) => de ? shapes[code].De : shapes[code].En;
        // One id per block on the page, so two maps never answer each other's clicks.
        var id = "cov" + Guid.NewGuid().ToString("N")[..8];

        // The frame: the European mainland, or the covered places with room around them (never narrower than a few countries).
        var (vx, vy, vw, vh) = CovMap.Europe;
        if (F("view") == "fit" && entries.Count > 0)
        {
            double x0 = entries.Min(e => shapes[e.Code].X0), y0 = entries.Min(e => shapes[e.Code].Y0);
            double x1 = entries.Max(e => shapes[e.Code].X1), y1 = entries.Max(e => shapes[e.Code].Y1);
            var w = Math.Max(x1 - x0, 220) * 1.3; var h = Math.Max(y1 - y0, 220) * 1.3;
            (vx, vy, vw, vh) = ((int)((x0 + x1 - w) / 2), (int)((y0 + y1 - h) / 2), (int)w, (int)h);
        }

        var sb = new StringBuilder($"<section class=\"section cov\" id=\"{id}\"><div class=\"container\">");
        if (F("heading") is { Length: > 0 } heading) sb.Append("<h2>").Append(E(heading)).Append("</h2>");
        if (F("intro") is { Length: > 0 } intro) sb.Append("<p class=\"cov-intro\">").Append(E(intro)).Append("</p>");
        sb.Append("<div class=\"cov-wrap\"><figure class=\"cov-map\">")
          .Append($"<svg viewBox=\"{vx} {vy} {vw} {vh}\" role=\"img\" aria-label=\"{E(ctx.T("cov.aria"))}\" preserveAspectRatio=\"xMidYMid meet\">")
          .Append($"<defs><pattern id=\"{id}-hatch\" width=\"8\" height=\"8\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\">")
          .Append("<rect width=\"8\" height=\"8\" class=\"cov-hatch-bg\"/><rect width=\"3\" height=\"8\" class=\"cov-hatch-fg\"/></pattern></defs>")
          .Append($"<rect x=\"{vx - 50}\" y=\"{vy - 50}\" width=\"{vw + 100}\" height=\"{vh + 100}\" class=\"cov-sea\"/>");
        // Every country as background; a region (IT-N) is drawn over its country, so the country itself stays neutral.
        foreach (var c in CovMap.Countries.Where(c => c.Iso.Length == 2))
            sb.Append($"<path class=\"cov-land\" d=\"{c.Path}\"><title>{E(de ? c.De : c.En)}</title></path>");
        foreach (var e in entries)
        {
            var s = shapes[e.Code];
            var fill = e.Status == "planned" ? $" style=\"fill:url(#{id}-hatch)\"" : "";
            sb.Append($"<a href=\"#{id}-{e.Code}\" class=\"cov-area cov-{e.Status}\" data-cov=\"{e.Code}\" aria-label=\"{E(Name(e.Code))}: {E(ctx.T("cov.s." + e.Status))}\">")
              .Append($"<path d=\"{s.Path}\"{fill}><title>{E(Name(e.Code))} – {E(ctx.T("cov.s." + e.Status))}</title></path></a>");
        }
        if (F("labels") != "no")
            foreach (var e in entries)
            {
                var s = shapes[e.Code];
                sb.Append($"<text class=\"cov-label\" x=\"{N((s.X0 + s.X1) / 2.0)}\" y=\"{N((s.Y0 + s.Y1) / 2.0)}\" text-anchor=\"middle\" dominant-baseline=\"middle\">{E(e.Code[..2])}</text>");
            }
        sb.Append("</svg>");

        // Legend: only the statuses that occur.
        sb.Append("<figcaption class=\"cov-legend\">");
        foreach (var st in Statuses.Where(st => entries.Any(e => e.Status == st)))
            sb.Append($"<span><i class=\"cov-sw cov-{st}\"></i>{E(ctx.T("cov.s." + st))}</span>");
        sb.Append("</figcaption></figure>");

        // The list beside the map: what a click on the map selects (and what a screen reader and a phone get without the map).
        sb.Append("<div class=\"cov-side\"><p class=\"cov-hint\">").Append(E(ctx.T("cov.hint"))).Append("</p><ul class=\"cov-list\">");
        foreach (var e in entries)
        {
            sb.Append($"<li id=\"{id}-{e.Code}\" data-cov=\"{e.Code}\"><a href=\"#{id}-{e.Code}\" data-cov=\"{e.Code}\"><i class=\"cov-sw cov-{e.Status}\"></i>")
              .Append($"<strong>{E(Name(e.Code))}</strong><span class=\"cov-badge cov-{e.Status}\">{E(ctx.T("cov.s." + e.Status))}</span></a>");
            if (e.Note.Length > 0) sb.Append("<p>").Append(E(e.Note)).Append("</p>");
            sb.Append("</li>");
        }
        if (entries.Count == 0) sb.Append("<li class=\"cov-none\">").Append(E(ctx.T("cov.none"))).Append("</li>");
        sb.Append("</ul>");
        if (F("asOf") is { Length: > 0 } asOf) sb.Append("<p class=\"cov-asof\">").Append(E(asOf)).Append("</p>");
        sb.Append("</div></div></div></section>");
        sb.Append(Css).Append(Script(id));
        return Task.FromResult(PluginResult.Fragment(sb.ToString()));
    }

    // A click on the map or the list marks the place in both, without the jump an anchor would make; without the script the anchor still works
    // (the :target rule marks the list entry).
    private static string Script(string id) => "<script>(function(){var r=document.getElementById('" + id + "');if(!r)return;"
        + "function on(c){r.querySelectorAll('[data-cov]').forEach(function(x){x.classList.toggle('is-on',x.getAttribute('data-cov')===c);});"
        + "var li=r.querySelector('li[data-cov=\"'+c+'\"]');var ul=r.querySelector('.cov-list');if(li&&ul&&ul.scrollHeight>ul.clientHeight)ul.scrollTop=li.offsetTop-ul.offsetTop-8;}"
        + "r.addEventListener('click',function(ev){var a=ev.target.closest('a[data-cov]');if(!a||!r.contains(a))return;ev.preventDefault();on(a.getAttribute('data-cov'));});})();</script>";

    // Labels are light with a dark rim: readable on a dark accent and on a light one alike.
    // Theme-able: the site's accent colours the covered places; --cov-* lets a template choose its own.
    private const string Css = "<style>.cov .cov-intro{color:var(--muted,#6b7280);max-width:720px}"
        + ".cov .cov-wrap{display:grid;grid-template-columns:minmax(0,2fr) minmax(240px,1fr);gap:28px;align-items:start;margin-top:18px}"
        + ".cov .cov-map{margin:0}.cov svg{display:block;width:100%;height:auto;border-radius:16px;border:1px solid var(--line,#e5e7eb)}"
        + ".cov .cov-sea{fill:var(--cov-sea,color-mix(in srgb,var(--accent,#2563eb) 6%,var(--bg,#fff)))}"
        + ".cov .cov-land{fill:var(--cov-land,color-mix(in srgb,var(--ink,#111827) 9%,var(--bg,#fff)));stroke:var(--bg,#fff);stroke-width:.8;stroke-linejoin:round}"
        + ".cov .cov-area path{stroke:var(--bg,#fff);stroke-width:1;stroke-linejoin:round;cursor:pointer;transition:filter .15s,stroke-width .15s}"
        + ".cov .cov-area:hover path,.cov .cov-area:focus path,.cov .cov-area.is-on path{filter:brightness(.88);stroke:var(--ink,#111827);stroke-width:1.6}"
        + ".cov .cov-available path,.cov i.cov-available{fill:var(--cov-available,var(--accent,#2563eb));background:var(--cov-available,var(--accent,#2563eb))}"
        + ".cov .cov-partial path,.cov i.cov-partial{fill:var(--cov-partial,color-mix(in srgb,var(--accent,#2563eb) 60%,var(--bg,#fff)));background:var(--cov-partial,color-mix(in srgb,var(--accent,#2563eb) 60%,var(--bg,#fff)))}"
        + ".cov .cov-soon path,.cov i.cov-soon{fill:var(--cov-soon,color-mix(in srgb,var(--accent,#2563eb) 32%,var(--bg,#fff)));background:var(--cov-soon,color-mix(in srgb,var(--accent,#2563eb) 32%,var(--bg,#fff)))}"
        + ".cov i.cov-planned{background:repeating-linear-gradient(45deg,var(--cov-planned,var(--accent,#2563eb)) 0 3px,var(--bg,#fff) 3px 6px)}"
        + ".cov .cov-hatch-bg{fill:var(--bg,#fff)}.cov .cov-hatch-fg{fill:var(--cov-planned,var(--accent,#2563eb))}"
        + ".cov .cov-label{font:700 12px/1 var(--font-body,system-ui,sans-serif);letter-spacing:.04em;fill:var(--cov-label,#fff);pointer-events:none;paint-order:stroke;stroke:rgba(0,0,0,.45);stroke-width:2.5px;stroke-linejoin:round}"
        + ".cov .cov-legend{display:flex;flex-wrap:wrap;gap:6px 18px;margin-top:12px;font-size:14px;color:var(--muted,#4b5563)}.cov .cov-legend span{display:inline-flex;align-items:center;gap:7px}"
        + ".cov .cov-sw{display:inline-block;width:14px;height:14px;border-radius:4px;flex:0 0 auto;box-shadow:inset 0 0 0 1px color-mix(in srgb,var(--ink,#111827) 15%,transparent)}"
        + ".cov .cov-hint{margin:0 0 10px;font-size:14px;color:var(--muted,#6b7280)}"
        + ".cov .cov-list{list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:8px;max-height:560px;overflow:auto}"
        + ".cov .cov-list li{border:1px solid var(--line,#e5e7eb);border-radius:12px;background:var(--bg,#fff);transition:border-color .15s,box-shadow .15s}"
        + ".cov .cov-list li>a{display:flex;align-items:center;gap:10px;padding:12px 14px;color:inherit;text-decoration:none}"
        + ".cov .cov-list li>a strong{flex:1 1 auto}.cov .cov-list li p{margin:-4px 14px 12px 38px;font-size:14px;line-height:1.5;color:var(--muted,#4b5563)}"
        + ".cov .cov-list li.is-on,.cov .cov-list li:target{border-color:var(--accent,#2563eb);box-shadow:0 0 0 2px color-mix(in srgb,var(--accent,#2563eb) 35%,transparent)}"
        + ".cov .cov-badge{font-size:12px;font-weight:600;padding:3px 9px;border-radius:999px;white-space:nowrap;background:color-mix(in srgb,var(--ink,#111827) 7%,transparent)}"
        + ".cov .cov-asof{margin:12px 0 0;font-size:13px;color:var(--muted,#6b7280)}.cov .cov-none{padding:12px 14px;color:var(--muted,#6b7280)}"
        + "@media (max-width:860px){.cov .cov-wrap{grid-template-columns:minmax(0,1fr)}.cov .cov-list{max-height:none}}</style>";
}

/// <summary>The plugin's texts.</summary>
public static class CovText
{
    public static readonly Dictionary<string, string> De = new()
    {
        ["cov.block.name"] = "Abdeckungskarte", ["cov.block.desc"] = "Eine Europakarte, auf der die Länder und Regionen markiert sind, in denen etwas verfügbar ist – anklickbar, mit Liste.",
        ["cov.f.heading"] = "Überschrift", ["cov.f.intro"] = "Einleitung", ["cov.f.list"] = "Länder und Regionen",
        ["cov.f.listHelp"] = "Eine Zeile je Land: Code | Status | Hinweis. Code = Ländercode (DE, AT, CH, FR …) oder IT-N für Norditalien. Status: available (verfügbar), partial (teilweise), soon (demnächst), planned (geplant).",
        ["cov.f.view"] = "Kartenausschnitt", ["cov.o.europe"] = "Europa", ["cov.o.fit"] = "Auf die markierten Länder zoomen",
        ["cov.f.labels"] = "Ländercodes auf der Karte", ["cov.o.yes"] = "Ja", ["cov.o.no"] = "Nein",
        ["cov.f.asOf"] = "Stand", ["cov.f.asOfHelp"] = "Kleine Zeile unter der Liste, z. B. „Stand: Oktober 2026“.",
        ["cov.s.available"] = "Verfügbar", ["cov.s.partial"] = "Teilweise", ["cov.s.soon"] = "Demnächst", ["cov.s.planned"] = "Geplant",
        ["cov.hint"] = "Land auf der Karte oder in der Liste wählen.", ["cov.none"] = "Noch keine Länder eingetragen.",
        ["cov.aria"] = "Karte der Abdeckung in Europa",
    };

    public static readonly Dictionary<string, string> En = new()
    {
        ["cov.block.name"] = "Coverage map", ["cov.block.desc"] = "A map of Europe marking the countries and regions where something is available – clickable, with a list.",
        ["cov.f.heading"] = "Heading", ["cov.f.intro"] = "Introduction", ["cov.f.list"] = "Countries and regions",
        ["cov.f.listHelp"] = "One line per country: code | status | note. Code = country code (DE, AT, CH, FR …) or IT-N for northern Italy. Status: available, partial, soon, planned.",
        ["cov.f.view"] = "Map section", ["cov.o.europe"] = "Europe", ["cov.o.fit"] = "Zoom to the marked countries",
        ["cov.f.labels"] = "Country codes on the map", ["cov.o.yes"] = "Yes", ["cov.o.no"] = "No",
        ["cov.f.asOf"] = "As of", ["cov.f.asOfHelp"] = "Small line below the list, e.g. “As of October 2026”.",
        ["cov.s.available"] = "Available", ["cov.s.partial"] = "Partly", ["cov.s.soon"] = "Coming soon", ["cov.s.planned"] = "Planned",
        ["cov.hint"] = "Choose a country on the map or in the list.", ["cov.none"] = "No countries entered yet.",
        ["cov.aria"] = "Map of coverage in Europe",
    };
}
