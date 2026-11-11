// Matmon live — the template store: GET {cloud}/api/store/templates (+ /{slug} for the details), as a block for matmon.eu's /templates.
// The store lives in Matmon Cloud: people submit there (signed in, Templates), the platform admins review there, and every Matmon
// instance adds a set from its Templates > Store. This block only SHOWS the published sets, read in the background like the
// partner list. There is no download here (removed in 1.7): a set is added inside Matmon, from its Templates > Store.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

public sealed record MmTpl(string Slug, string Title, string Summary, string[] Tags, string Author, string Version, int Count,
    string[] SensorTypes, int Downloads, DateTimeOffset Updated);

public sealed record MmTplPart(string Name, string SensorType);

public sealed record MmTplVersion(string Version, string Notes, DateTimeOffset Date);

public sealed record MmTplDetail(DateTimeOffset Updated, string Description, IReadOnlyList<MmTplPart> Templates, IReadOnlyList<MmTplVersion> Versions);

public static class MmTemplates
{
    public const string BlockType = "matmon_templates";
    private const int MaxPages = 10;               // 10 × 24 sets; the page filters in the browser, so it holds them all
    private const int MaxDetailFetches = 60;       // per run; the rest follow ten minutes later
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private static readonly Regex SlugPattern = new("^[a-z0-9][a-z0-9-]{0,79}$", RegexOptions.CultureInvariant);

    private static IReadOnlyList<MmTpl>? _list;
    private static readonly ConcurrentDictionary<string, MmTplDetail> Details = new();
    private static DateTime _fetched = DateTime.MinValue;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Configure(IPluginBuilder b)
    {
        b.Strings("de", MmTemplateText.De);
        b.Strings("en", MmTemplateText.En);
        b.Block(new BlockSpec(BlockType, "mmt.block.name", "mmt.block.desc")
        {
            Category = "design",
            IconSvg = "<rect x=\"3\" y=\"4\" width=\"7\" height=\"7\" rx=\"1.5\"/><rect x=\"14\" y=\"4\" width=\"7\" height=\"7\" rx=\"1.5\"/><rect x=\"3\" y=\"14\" width=\"7\" height=\"7\" rx=\"1.5\"/><path d=\"M17.5 14v7M14 17.5h7\"/>",
            Fields = new[]
            {
                new FieldSpec("eyebrow", "mmt.f.eyebrow") { Default = "Templates" },
                new FieldSpec("heading", "mmt.f.heading") { Default = "Ready-made sensor templates" },
                new FieldSpec("intro", "mmt.f.intro", FieldKind.Textarea)
                {
                    Default = "Sets of sensors from the Matmon community. Add them in Matmon under Templates → Store."
                },
                new FieldSpec("empty", "mmt.f.empty") { Default = "No templates yet — be the first to submit one." },
                new FieldSpec("submitText", "mmt.f.submitText") { Default = "Submit a template" },
                new FieldSpec("submitUrl", "mmt.f.submitUrl", FieldKind.Url),
            }
        }, RenderAsync);
        b.Job("templates", JobSchedule.EveryInterval(TimeSpan.FromMinutes(10)), ctx => RefreshAsync(ctx, CancellationToken.None));
    }

    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
    private static int I(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static DateTimeOffset D(JsonNode? n) =>
        DateTimeOffset.TryParse(S(n), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : DateTimeOffset.MinValue;

    private static string[] Strings(JsonNode? n) =>
        (n as JsonArray ?? new JsonArray()).Select(S).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToArray();

    public static bool IsSlug(string slug) => SlugPattern.IsMatch(slug);

    /// <summary>One page of the catalogue; null when the answer is not the catalogue (then the last list stays).</summary>
    public static (List<MmTpl> Items, int Total, int PageSize, int Sent)? ParsePage(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o["items"] is not JsonArray arr) return null;
            var items = arr.OfType<JsonObject>()
                .Select(t => new MmTpl(S(t["slug"]), S(t["title"]), S(t["summary"]), Strings(t["tags"]), S(t["author"]), S(t["version"]),
                    I(t["templateCount"]), Strings(t["sensorTypes"]), I(t["downloads"]), D(t["updated"])))
                .Where(t => IsSlug(t.Slug) && t.Title.Length > 0)
                .ToList();
            return (items, I(o["total"]), I(o["pageSize"]), arr.Count);
        }
        catch { return null; }
    }

    public static MmTplDetail? ParseDetail(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var parts = (o["templates"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(t => new MmTplPart(S(t["name"]), S(t["sensorType"]))).Where(t => t.Name.Length > 0).Take(200).ToList();
            var versions = (o["versions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(v => new MmTplVersion(S(v["version"]), S(v["notes"]), D(v["date"]))).Where(v => v.Version.Length > 0).Take(30).ToList();
            return new MmTplDetail(D(o["updated"]), S(o["description"]), parts, versions);
        }
        catch { return null; }
    }

    /// <summary>
    /// Fetches the catalogue when it is older than ten minutes, and the details of every set that is new or changed since. A failed
    /// fetch keeps the last list: a page that goes blank because the cloud restarts is worse than one a few minutes old.
    /// </summary>
    public static async Task RefreshAsync(IPluginContext ctx, CancellationToken ct, bool force = false)
    {
        if (!force && DateTime.UtcNow - _fetched < Fresh) return;
        if (!await Gate.WaitAsync(0, ct)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Matmon-Live/1.7");
            var cloud = MmLive.Cloud(ctx) + "/api/store/templates";
            var all = new List<MmTpl>();
            for (var page = 1; page <= MaxPages; page++)
            {
                string raw;
                try { raw = await http.GetStringAsync(cloud + "?sort=new&page=" + page, ct); }
                catch (HttpRequestException) when (page > 1) { break; }      // the first pages stand; the rest follow ten minutes later
                var got = ParsePage(raw);
                if (got is null) { if (page == 1) return; break; }
                all.AddRange(got.Value.Items.Where(t => all.All(x => x.Slug != t.Slug)));
                // Paged by what the cloud SENT, not by what survived the checks here (a dropped entry must not ask for a page that is not there).
                if (got.Value.Sent == 0 || page * Math.Max(1, got.Value.PageSize) >= got.Value.Total) break;
            }

            _list = all;
            _fetched = DateTime.UtcNow;
            foreach (var gone in Details.Keys.Where(k => all.All(t => t.Slug != k)).ToList()) Details.TryRemove(gone, out _);

            var fetches = 0;
            foreach (var t in all)
            {
                if (Details.TryGetValue(t.Slug, out var known) && known.Updated == t.Updated) continue;
                if (++fetches > MaxDetailFetches) { _fetched = DateTime.UtcNow - Fresh + TimeSpan.FromMinutes(1); break; }
                try
                {
                    if (ParseDetail(await http.GetStringAsync(cloud + "/" + t.Slug, ct)) is { } detail) Details[t.Slug] = detail with { Updated = t.Updated };
                }
                catch (HttpRequestException) { /* gone between the list and now, or a hiccup: the card shows without its details */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ctx.Log("Matmon templates: " + ex.Message);
            _fetched = DateTime.UtcNow - Fresh + TimeSpan.FromMinutes(2);         // try again in two minutes
        }
        finally { Gate.Release(); }
    }

    // ---- the block --------------------------------------------------------------------------------------------------------

    private static string Paragraphs(string text) =>
        string.Join("", text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => "<p>" + string.Join("<br>", p.Split('\n').Select(E)) + "</p>"));

    public static async Task<PluginResult> RenderAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (_list is null) await RefreshAsync(ctx, ct, force: true);          // the first view after a start
        else _ = RefreshAsync(ctx, CancellationToken.None);
        string F(string id) => ctx.Block?.Get(id) ?? "";
        var list = _list ?? Array.Empty<MmTpl>();
        var id = "mmt" + Guid.NewGuid().ToString("N")[..8];

        var submitUrl = F("submitUrl") is { Length: > 0 } su && (su.StartsWith('/') && !su.StartsWith("//") || MmPartners.SafeUrl(su).Length > 0)
            ? su : MmLive.Cloud(ctx) + "/templates/submit";
        var submit = F("submitText") is { Length: > 0 } st
            ? "<a class=\"btn\" href=\"" + E(submitUrl) + "\" target=\"_blank\" rel=\"noopener\"><i class=\"ti ti-upload\" aria-hidden=\"true\"></i> " + E(st) + "</a>"
            : "";

        var sb = new StringBuilder($"<section class=\"section mm-live mmt\" id=\"{id}\"><div class=\"container\"><div class=\"mmt-head\"><div>");
        if (F("eyebrow") is { Length: > 0 } eb) sb.Append("<span class=\"mm-eyebrow\"><i class=\"ti ti-template\" aria-hidden=\"true\"></i>").Append(E(eb)).Append("</span>");
        if (F("heading") is { Length: > 0 } h) sb.Append("<h2 class=\"mm-title\">").Append(E(h)).Append("</h2>");
        if (F("intro") is { Length: > 0 } i) sb.Append("<p class=\"mm-intro\">").Append(E(i)).Append("</p>");
        sb.Append("</div>").Append(submit).Append("</div>");

        if (list.Count == 0)
            sb.Append("<p class=\"mmt-empty\">").Append(E(F("empty") is { Length: > 0 } em ? em : ctx.T("mmt.none"))).Append("</p>");
        else
        {
            var tags = list.SelectMany(t => t.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(40).ToList();
            sb.Append("<div class=\"mmt-tools\"><input type=\"search\" class=\"mmt-q\" placeholder=\"").Append(E(ctx.T("mmt.search")))
                .Append("\" aria-label=\"").Append(E(ctx.T("mmt.search"))).Append("\">");
            if (tags.Count > 0)
            {
                sb.Append("<select class=\"mmt-t\" aria-label=\"").Append(E(ctx.T("mmt.tag"))).Append("\"><option value=\"\">").Append(E(ctx.T("mmt.allTags"))).Append("</option>");
                foreach (var t in tags) sb.Append("<option value=\"").Append(E(t.ToLowerInvariant())).Append("\">").Append(E(t)).Append("</option>");
                sb.Append("</select>");
            }
            sb.Append("<select class=\"mmt-s\" aria-label=\"").Append(E(ctx.T("mmt.sort"))).Append("\">")
                .Append("<option value=\"new\">").Append(E(ctx.T("mmt.sort.new"))).Append("</option>")
                .Append("<option value=\"pop\">").Append(E(ctx.T("mmt.sort.pop"))).Append("</option>")
                .Append("<option value=\"name\">").Append(E(ctx.T("mmt.sort.name"))).Append("</option></select></div>");

            sb.Append("<div class=\"mmt-grid\">");
            foreach (var t in list)
            {
                Details.TryGetValue(t.Slug, out var d);
                var text = string.Join(" ", new[] { t.Title, t.Summary, t.Author, d?.Description ?? "" }
                    .Concat(t.Tags).Concat(t.SensorTypes).Concat(d?.Templates.Select(p => p.Name) ?? Enumerable.Empty<string>())).ToLowerInvariant();
                sb.Append("<article class=\"mm-card mmt-card\" data-text=\"").Append(E(text))
                    .Append("\" data-tags=\"|").Append(E(string.Join("|", t.Tags.Select(x => x.ToLowerInvariant())))).Append("|\"")
                    .Append(" data-new=\"").Append(t.Updated.ToUnixTimeSeconds()).Append("\" data-pop=\"").Append(t.Downloads)
                    .Append("\" data-name=\"").Append(E(t.Title.ToLowerInvariant())).Append("\">");
                sb.Append("<h3>").Append(E(t.Title)).Append("</h3>");
                if (t.Summary.Length > 0) sb.Append("<p>").Append(E(t.Summary)).Append("</p>");
                sb.Append("<p class=\"mmt-meta\">");
                if (t.Version.Length > 0) sb.Append("<span>").Append(E(ctx.T("mmt.version"))).Append(' ').Append(E(t.Version)).Append("</span>");
                sb.Append("<span>").Append(E(string.Format(ctx.T(t.Count == 1 ? "mmt.count1" : "mmt.count"), t.Count))).Append("</span>");
                if (t.Author.Length > 0) sb.Append("<span>").Append(E(ctx.T("mmt.by"))).Append(' ').Append(E(t.Author)).Append("</span>");
                if (t.Updated > DateTimeOffset.MinValue) sb.Append("<span>").Append(t.Updated.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</span>");
                sb.Append("</p>");
                if (t.Tags.Length > 0)
                {
                    sb.Append("<div class=\"mmt-tags\">");
                    foreach (var tag in t.Tags) sb.Append("<button type=\"button\" class=\"mmt-tag\" data-tag=\"").Append(E(tag.ToLowerInvariant())).Append("\">").Append(E(tag)).Append("</button>");
                    sb.Append("</div>");
                }
                if (d is not null && (d.Description.Length > 0 || d.Templates.Count > 0 || d.Versions.Count > 0))
                {
                    sb.Append("<details class=\"mmt-more\"><summary>").Append(E(ctx.T("mmt.details"))).Append("</summary>");
                    if (d.Description.Length > 0) sb.Append("<div class=\"mmt-desc\">").Append(Paragraphs(d.Description)).Append("</div>");
                    if (d.Templates.Count > 0)
                    {
                        sb.Append("<h4>").Append(E(ctx.T("mmt.contains"))).Append("</h4><ul class=\"mmt-list\">");
                        foreach (var p in d.Templates)
                        {
                            sb.Append("<li>").Append(E(p.Name));
                            if (p.SensorType.Length > 0) sb.Append(" <code>").Append(E(p.SensorType)).Append("</code>");
                            sb.Append("</li>");
                        }
                        sb.Append("</ul>");
                    }
                    if (d.Versions.Count > 0)
                    {
                        sb.Append("<h4>").Append(E(ctx.T("mmt.versions"))).Append("</h4><ul class=\"mmt-list\">");
                        foreach (var v in d.Versions)
                        {
                            sb.Append("<li><strong>").Append(E(v.Version)).Append("</strong>");
                            if (v.Date > DateTimeOffset.MinValue) sb.Append(" · ").Append(v.Date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                            if (v.Notes.Length > 0) sb.Append("<br>").Append(E(v.Notes));
                            sb.Append("</li>");
                        }
                        sb.Append("</ul>");
                    }
                    sb.Append("</details>");
                }
                sb.Append("<div class=\"mmt-actions\"><span class=\"mmt-hint\"><i class=\"ti ti-apps\" aria-hidden=\"true\"></i> ").Append(E(ctx.T("mmt.inMatmon"))).Append("</span></div></article>");
            }
            sb.Append("</div><p class=\"mmt-empty mmt-nomatch\" hidden>").Append(E(ctx.T("mmt.noMatch"))).Append("</p>");
        }
        sb.Append("</div></section>").Append(MmLive.Css).Append(Css).Append(Script(id));
        return PluginResult.Fragment(sb.ToString());
    }

    private static string Script(string id) => "<script>(function(){var r=document.getElementById('" + id + "');if(!r)return;"
        + "var q=r.querySelector('.mmt-q'),t=r.querySelector('.mmt-t'),s=r.querySelector('.mmt-s'),g=r.querySelector('.mmt-grid'),none=r.querySelector('.mmt-nomatch');"
        + "if(!g)return;"
        + "function f(){var x=(q&&q.value||'').trim().toLowerCase(),k=t&&t.value||'',n=0;g.querySelectorAll('.mmt-card').forEach(function(a){"
        + "var ok=(!x||a.getAttribute('data-text').indexOf(x)>=0)&&(!k||a.getAttribute('data-tags').indexOf('|'+k+'|')>=0);a.hidden=!ok;if(ok)n++;});"
        + "if(none)none.hidden=n>0;}"
        + "function o(){var m=s&&s.value||'new',c=Array.prototype.slice.call(g.querySelectorAll('.mmt-card'));c.sort(function(a,b){"
        + "if(m==='name')return a.getAttribute('data-name').localeCompare(b.getAttribute('data-name'));"
        + "var k=m==='pop'?'data-pop':'data-new';return (+b.getAttribute(k))-(+a.getAttribute(k));});c.forEach(function(a){g.appendChild(a);});}"
        + "if(q)q.addEventListener('input',f);if(t)t.addEventListener('change',f);if(s)s.addEventListener('change',o);"
        + "g.addEventListener('click',function(e){var b=e.target.closest('.mmt-tag');if(!b||!t)return;t.value=b.getAttribute('data-tag');f();});})();</script>";

    private const string Css = "<style>.mmt .mmt-head{display:flex;flex-wrap:wrap;gap:16px 24px;align-items:flex-end;justify-content:space-between;margin:0 0 18px}"
        + ".mmt .mmt-head>div{flex:1 1 420px;min-width:0}.mmt .mmt-head .mm-intro{margin-bottom:0}"
        + ".mmt .mmt-tools{display:flex;flex-wrap:wrap;gap:10px;margin:0 0 22px}"
        + ".mmt .mmt-q{flex:1 1 260px;padding:10px 14px;border:1px solid var(--line,#e5e7eb);border-radius:10px;font:inherit;background:var(--bg,#fff);color:inherit}"
        + ".mmt .mmt-t,.mmt .mmt-s{padding:10px 12px;border:1px solid var(--line,#e5e7eb);border-radius:10px;font:inherit;background:var(--bg,#fff);color:inherit}"
        + ".mmt .mmt-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:18px;align-items:start}"
        + ".mmt .mmt-card[hidden]{display:none}.mmt .mmt-card h3{margin:0;font-size:18px}"
        + ".mmt .mmt-meta{display:flex;flex-wrap:wrap;gap:4px 12px;font-size:13px!important}"
        + ".mmt .mmt-tags{display:flex;flex-wrap:wrap;gap:6px}"
        + ".mmt .mmt-tag{border:0;border-radius:999px;padding:3px 10px;font:inherit;font-size:12.5px;cursor:pointer;"
        + "background:color-mix(in srgb,var(--accent,#295cff) 10%,transparent);color:var(--accent,#295cff)}"
        + ".mmt .mmt-more summary{cursor:pointer;font-size:14px;font-weight:600}.mmt .mmt-more h4{margin:12px 0 4px;font-size:14px}"
        + ".mmt .mmt-desc p{margin:8px 0 0}.mmt .mmt-list{margin:0;padding-left:18px;font-size:14px;line-height:1.55}"
        + ".mmt .mmt-list code{font-size:12px;opacity:.75}"
        + ".mmt .mmt-actions{display:flex;flex-wrap:wrap;align-items:center;gap:8px 12px;margin-top:6px}"
        + ".mmt .mmt-hint{display:inline-flex;align-items:center;gap:6px;font-size:13px;color:var(--muted,#6b7280)}"
        + ".mmt .mmt-empty{color:var(--muted,#6b7280)}"
        + "@media (max-width:900px){.mmt .mmt-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media (max-width:600px){.mmt .mmt-grid{grid-template-columns:minmax(0,1fr)}}</style>";
}

public static class MmTemplateText
{
    public static readonly Dictionary<string, string> En = new()
    {
        ["mmt.block.name"] = "Matmon: templates", ["mmt.block.desc"] = "The published sets of the Matmon template store, with search, keywords and details – from Matmon Cloud.",
        ["mmt.f.eyebrow"] = "Line above the heading", ["mmt.f.heading"] = "Heading", ["mmt.f.intro"] = "Introduction",
        ["mmt.f.empty"] = "Text when the store is empty", ["mmt.f.submitText"] = "Submit button: text (empty = no button)",
        ["mmt.f.submitUrl"] = "Submit button: link (empty = the cloud's Templates page)",
        ["mmt.none"] = "No templates yet.", ["mmt.search"] = "Search templates", ["mmt.tag"] = "Keyword", ["mmt.allTags"] = "All keywords",
        ["mmt.sort"] = "Order", ["mmt.sort.new"] = "Newest", ["mmt.sort.pop"] = "Most used", ["mmt.sort.name"] = "Name",
        ["mmt.version"] = "Version", ["mmt.count"] = "{0} templates", ["mmt.count1"] = "{0} template", ["mmt.by"] = "by",
        ["mmt.details"] = "Details", ["mmt.contains"] = "Contains", ["mmt.versions"] = "Versions",
        ["mmt.inMatmon"] = "In Matmon: Templates → Store", ["mmt.noMatch"] = "No template matches your search.",
    };

    public static readonly Dictionary<string, string> De = new()
    {
        ["mmt.block.name"] = "Matmon: Templates", ["mmt.block.desc"] = "Die veröffentlichten Sets des Matmon-Template-Stores mit Suche, Schlagwörtern und Details – aus der Matmon Cloud.",
        ["mmt.f.eyebrow"] = "Zeile über der Überschrift", ["mmt.f.heading"] = "Überschrift", ["mmt.f.intro"] = "Einleitung",
        ["mmt.f.empty"] = "Text, wenn der Store leer ist", ["mmt.f.submitText"] = "Knopf Einreichen: Text (leer = kein Knopf)",
        ["mmt.f.submitUrl"] = "Knopf Einreichen: Link (leer = die Templates-Seite der Cloud)",
        ["mmt.none"] = "Noch keine Templates.", ["mmt.search"] = "Templates suchen", ["mmt.tag"] = "Schlagwort", ["mmt.allTags"] = "Alle Schlagwörter",
        ["mmt.sort"] = "Reihenfolge", ["mmt.sort.new"] = "Neueste", ["mmt.sort.pop"] = "Meistgenutzt", ["mmt.sort.name"] = "Name",
        ["mmt.version"] = "Version", ["mmt.count"] = "{0} Templates", ["mmt.count1"] = "{0} Template", ["mmt.by"] = "von",
        ["mmt.details"] = "Details", ["mmt.contains"] = "Enthält", ["mmt.versions"] = "Versionen",
        ["mmt.inMatmon"] = "In Matmon: Templates → Store", ["mmt.noMatch"] = "Kein Template passt zur Suche.",
    };
}
