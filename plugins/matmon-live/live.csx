// Matmon live — the two live parts of matmon.eu as blocks: the service status of Matmon Cloud and the sensor catalogue.
// Both read Matmon Cloud's anonymous APIs (/api/status, /api/sensor-types) in the background (a job), so a page view never waits for them.
// What Matmon.Web did, kept: three states only (operational | degraded | down), an unreachable cloud shows as "down" once the last answer is
// older than a quarter of an hour; the catalogue starts from the bundled snapshot (snapshot.csx) and is only replaced by one at least as new
// (the cloud's copy can lag the release), and internal sensor types are hidden.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

public sealed record MmComponent(string Name, string Status);
public sealed record MmStatus(string Overall, IReadOnlyList<MmComponent> Components, DateTimeOffset? Checked, DateTime FetchedUtc);
public sealed record MmSensor(string Key, string Name, string Description, string Category, bool NeedsCredentials, IReadOnlyList<string> CredentialKinds);
public sealed record MmCatalog(IReadOnlyList<MmSensor> Sensors, DateTimeOffset? Generated, bool Live);

public static class MmLive
{
    public const string StatusBlock = "matmon_status";
    public const string SensorsBlock = "matmon_sensors";
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "probe-heartbeat", "probe-health", "matmon-update" };

    private static MmStatus? _status;
    private static MmCatalog _catalog = ParseCatalog(MmSnapshot.Json, live: false) ?? new MmCatalog(Array.Empty<MmSensor>(), null, false);
    private static DateTime _catalogFetched = DateTime.MinValue;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Configure(IPluginBuilder b)
    {
        b.Strings("de", MmText.De);
        b.Strings("en", MmText.En);
        b.Settings.Text("cloudUrl", "mm.set.url", "https://cloud.matmon.eu", "mm.set.urlHelp");

        b.Block(new BlockSpec(StatusBlock, "mm.status.name", "mm.status.desc")
        {
            Category = "design",
            IconSvg = "<path d=\"M3 12h4l3-8 4 16 3-8h4\"/>",
            Fields = new[]
            {
                new FieldSpec("heading", "mm.f.heading") { Default = "Service status" },
                new FieldSpec("intro", "mm.f.intro", FieldKind.Textarea) { Default = "Live status of the Matmon Cloud services." },
            }
        }, RenderStatusAsync);

        b.Block(new BlockSpec(SensorsBlock, "mm.sensors.name", "mm.sensors.desc")
        {
            Category = "design",
            IconSvg = "<rect x=\"3\" y=\"3\" width=\"7\" height=\"7\" rx=\"1\"/><rect x=\"14\" y=\"3\" width=\"7\" height=\"7\" rx=\"1\"/><rect x=\"3\" y=\"14\" width=\"7\" height=\"7\" rx=\"1\"/><rect x=\"14\" y=\"14\" width=\"7\" height=\"7\" rx=\"1\"/>",
            Fields = new[]
            {
                new FieldSpec("eyebrow", "mm.f.eyebrow") { Default = "Sensor catalog" },
                new FieldSpec("heading", "mm.f.heading") { Default = "{count} sensor types, out of the box", Help = "mm.f.countHelp" },
                new FieldSpec("intro", "mm.f.intro", FieldKind.Textarea),
                new FieldSpec("jump", "mm.f.jump", FieldKind.Select)
                {
                    Default = "yes", Options = new[] { new FieldOption("yes", "mm.o.yes"), new FieldOption("no", "mm.o.no") }
                },
            }
        }, RenderSensorsAsync);

        b.Job("refresh", JobSchedule.EveryInterval(TimeSpan.FromMinutes(5)), ctx => RefreshAsync(ctx, CancellationToken.None));

        // The number of sensor types for text elsewhere on the site (the home page's feature card asks for it in the browser): one
        // source, so the home page and /sensors cannot disagree.
        b.Route("/matmon-live/count", (ctx, ct) =>
        {
            if (_catalogFetched == DateTime.MinValue) _ = RefreshAsync(ctx, CancellationToken.None);
            return Task.FromResult(PluginResult.Json(new JsonObject { ["count"] = _catalog.Sensors.Count }));
        }, new PublicRouteOptions { Methods = PluginMethod.Get });
    }

    public static string Cloud(IPluginContext ctx)
    {
        var raw = (ctx.Settings.Get("cloudUrl") ?? "").Trim().TrimEnd('/');
        return Uri.TryCreate(raw, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u.GetLeftPart(UriPartial.Authority) : "https://cloud.matmon.eu";
    }

    /// <summary>Asks the cloud for the status (always) and the catalogue (every six hours). One run at a time.</summary>
    public static async Task RefreshAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Matmon-Live/1.0");
            var cloud = Cloud(ctx);
            try
            {
                var node = JsonNode.Parse(await http.GetStringAsync(cloud + "/api/status", ct)) as JsonObject;
                var comps = (node?["components"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Select(c => new MmComponent(S(c["name"]) is { Length: > 0 } n ? n : S(c["key"]), State(S(c["status"])))).ToList();
                var at = DateTimeOffset.TryParse(S(node?["checkedUtc"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTimeOffset?)null;
                _status = new MmStatus(State(S(node?["overall"])), comps.Count > 0 ? comps : Placeholder("operational"), at, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                ctx.Log("Matmon status: " + ex.Message);
                // A blip keeps the last answer for a while; a cloud that stays away is reported as down (it IS the service).
                if (_status is null || DateTime.UtcNow - _status.FetchedUtc > TimeSpan.FromMinutes(15))
                    _status = new MmStatus("down", Placeholder("down"), DateTimeOffset.UtcNow, _status?.FetchedUtc ?? DateTime.UtcNow);
            }
            if (DateTime.UtcNow - _catalogFetched > TimeSpan.FromHours(6))
            {
                try
                {
                    var fresh = ParseCatalog(await http.GetStringAsync(cloud + "/api/sensor-types", ct), live: true);
                    _catalogFetched = DateTime.UtcNow;
                    if (fresh is { Sensors.Count: > 0 } && !(fresh.Generated < _catalog.Generated)) _catalog = fresh;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    ctx.Log("Matmon sensor catalogue: " + ex.Message);
                    _catalogFetched = DateTime.UtcNow - TimeSpan.FromHours(5);      // try again in an hour
                }
            }
        }
        finally { Gate.Release(); }
    }

    private static IReadOnlyList<MmComponent> Placeholder(string state) => new[]
    {
        new MmComponent("Cloud General", state), new MmComponent("Cloud Service Executor", state), new MmComponent("E-Mail", state)
    };

    private static string State(string s) => s is "operational" or "degraded" or "down" ? s : "unknown";
    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static string E(string s) => WebUtility.HtmlEncode(s);

    public static MmCatalog? ParseCatalog(string json, bool live)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var list = (o["sensorTypes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(t => S(t["key"]).Length > 0 && !Hidden.Contains(S(t["key"])))
                .Select(t => new MmSensor(S(t["key"]), S(t["name"]) is { Length: > 0 } n ? n : S(t["key"]), S(t["description"]),
                    S(t["category"]) is { Length: > 0 } c ? c : "Other",
                    t["needsCredentials"] is JsonValue nv && nv.TryGetValue<bool>(out var need) && need,
                    (t["credentialKinds"] as JsonArray ?? new JsonArray()).Select(k => S(k)).Where(k => k.Length > 0).ToList()))
                .ToList();
            var gen = DateTimeOffset.TryParse(S(o["generatedUtc"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var g) ? g : (DateTimeOffset?)null;
            return new MmCatalog(list, gen, live);
        }
        catch { return null; }
    }

    // ---- the status block --------------------------------------------------------------------------------------------

    public static async Task<PluginResult> RenderStatusAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (_status is null) await RefreshAsync(ctx, ct);                         // the first view after a start, before the job ran
        var s = _status ?? new MmStatus("unknown", Placeholder("unknown"), null, DateTime.UtcNow);
        string F(string id) => ctx.Block?.Get(id) ?? "";
        var (tone, icon) = s.Overall switch
        {
            "operational" => ("ok", "ti-circle-check"), "degraded" => ("warn", "ti-alert-triangle"), "down" => ("err", "ti-circle-x"), _ => ("info", "ti-clock")
        };
        var sb = new StringBuilder("<section class=\"section mm-live\"><div class=\"container mm-narrow\">");
        if (F("heading") is { Length: > 0 } h) sb.Append("<h2>").Append(E(h)).Append("</h2>");
        if (F("intro") is { Length: > 0 } i) sb.Append("<p class=\"mm-intro\">").Append(E(i)).Append("</p>");
        sb.Append($"<div class=\"mm-alert mm-{tone}\" role=\"status\"><i class=\"ti {icon}\" aria-hidden=\"true\"></i><div><strong>")
          .Append(E(ctx.T("mm.overall." + s.Overall))).Append("</strong>");
        if (s.Overall is "degraded" or "down") sb.Append("<div>").Append(E(ctx.T("mm.sub." + s.Overall))).Append("</div>");
        sb.Append("</div></div><ul class=\"mm-comps\">");
        foreach (var c in s.Components)
            sb.Append("<li><span>").Append(E(c.Name)).Append($"</span><span class=\"mm-pill mm-{Tone(c.Status)}\">").Append(E(ctx.T("mm.state." + c.Status))).Append("</span></li>");
        sb.Append("</ul>");
        if (s.Checked is { } at)
            sb.Append("<p class=\"mm-caption\">").Append(E(ctx.T("mm.checked"))).Append($" <time datetime=\"{at.UtcDateTime:o}\">{at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC</time></p>");
        return PluginResult.Fragment(sb.Append("</div></section>").Append(Css).ToString());
    }

    private static string Tone(string state) => state switch { "operational" => "ok", "degraded" => "warn", "down" => "err", _ => "info" };

    // ---- the sensor catalogue block ----------------------------------------------------------------------------------

    public static Task<PluginResult> RenderSensorsAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (_catalogFetched == DateTime.MinValue) _ = RefreshAsync(ctx, CancellationToken.None);   // the snapshot now, the cloud's list soon
        string F(string id) => ctx.Block?.Get(id) ?? "";
        var cat = _catalog;
        var groups = cat.Sensors.GroupBy(x => x.Category).ToList();                // first-seen order = the API's category order
        static string Anchor(string c) => "cat-" + new string(c.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());

        var sb = new StringBuilder("<section class=\"section mm-live\"><div class=\"container\">");
        if (F("eyebrow") is { Length: > 0 } eb) sb.Append("<span class=\"mm-eyebrow\"><i class=\"ti ti-activity\" aria-hidden=\"true\"></i>").Append(E(eb)).Append("</span>");
        if (F("heading") is { Length: > 0 } h) sb.Append("<h2 class=\"mm-title\">").Append(E(h.Replace("{count}", cat.Sensors.Count.ToString(CultureInfo.InvariantCulture)))).Append("</h2>");
        if (F("intro") is { Length: > 0 } i) sb.Append("<p class=\"mm-intro\">").Append(E(i)).Append("</p>");
        if (F("jump") != "no")
        {
            sb.Append("<nav class=\"mm-jump\" aria-label=\"").Append(E(ctx.T("mm.categories"))).Append("\">");
            foreach (var g in groups)
                sb.Append($"<a href=\"#{Anchor(g.Key)}\">").Append(E(g.Key)).Append($"<span>{g.Count()}</span></a>");
            sb.Append("</nav>");
        }
        foreach (var g in groups)
        {
            sb.Append($"<section class=\"mm-cat\" id=\"{Anchor(g.Key)}\"><h3>").Append(E(g.Key)).Append($" <span class=\"mm-pill\">{g.Count()}</span></h3><div class=\"mm-grid\">");
            foreach (var x in g)
            {
                sb.Append("<article class=\"mm-card\"><h4>").Append(E(x.Name)).Append("</h4><p>").Append(E(x.Description)).Append("</p>");
                if (x.NeedsCredentials)
                    sb.Append("<span class=\"mm-tag\" title=\"").Append(E(ctx.T("mm.credTitle", string.Join(", ", x.CredentialKinds)))).Append("\"><i class=\"ti ti-key\" aria-hidden=\"true\"></i>")
                      .Append(E(ctx.T("mm.cred"))).Append("</span>");
                sb.Append("</article>");
            }
            sb.Append("</div></section>");
        }
        return Task.FromResult(PluginResult.Fragment(sb.Append("</div></section>").Append(Css).ToString()));
    }

    // Theme-able: the site's accent and lines; the three status colours are fixed meanings (green, amber, red).
    // public: the partner block (partners.csx) draws its cards with these classes too, on a page that may have no status/catalogue block.
    public const string Css = "<style>.mm-live .mm-narrow{max-width:760px}.mm-live .mm-intro{color:var(--muted,#6b7280);max-width:720px;margin:0 0 20px}"
        + ".mm-live .mm-alert{display:flex;gap:14px;align-items:flex-start;padding:16px 18px;border-radius:12px;border:1px solid;margin:0 0 18px}.mm-live .mm-alert i{font-size:24px;line-height:1}"
        + ".mm-live .mm-alert strong{display:block;font-size:17px}.mm-live .mm-ok{background:#ecfdf3;border-color:#a6e9c1;color:#05603a}.mm-live .mm-warn{background:#fffaeb;border-color:#fedf89;color:#93370d}"
        + ".mm-live .mm-err{background:#fef3f2;border-color:#fecdca;color:#912018}.mm-live .mm-info{background:color-mix(in srgb,var(--accent,#295cff) 8%,#fff);border-color:color-mix(in srgb,var(--accent,#295cff) 30%,#fff);color:var(--ink,#111827)}"
        + ".mm-live .mm-comps{list-style:none;margin:0;padding:0;border:1px solid var(--line,#e5e7eb);border-radius:12px;overflow:hidden;background:var(--bg,#fff)}"
        + ".mm-live .mm-comps li{display:flex;justify-content:space-between;align-items:center;gap:12px;padding:14px 18px;border-top:1px solid var(--line,#e5e7eb)}.mm-live .mm-comps li:first-child{border-top:0}"
        + ".mm-live .mm-pill{display:inline-block;font-size:12px;font-weight:600;padding:3px 10px;border-radius:999px;border:0;background:color-mix(in srgb,var(--ink,#111827) 7%,transparent);color:var(--ink,#111827)}"
        + ".mm-live .mm-pill.mm-ok{background:#d1fadf;color:#05603a}.mm-live .mm-pill.mm-warn{background:#fef0c7;color:#93370d}.mm-live .mm-pill.mm-err{background:#fee4e2;color:#912018}"
        + ".mm-live .mm-caption{font-size:13px;color:var(--muted,#6b7280);margin:12px 0 0}"
        + ".mm-live .mm-eyebrow{display:inline-flex;align-items:center;gap:6px;font-size:13px;font-weight:600;color:var(--accent,#295cff);text-transform:uppercase;letter-spacing:.06em}"
        + ".mm-live .mm-title{margin:8px 0 10px}.mm-live .mm-jump{display:flex;flex-wrap:wrap;gap:8px;margin:0 0 28px}"
        + ".mm-live .mm-jump a{display:inline-flex;align-items:center;gap:8px;padding:6px 12px;border:1px solid var(--line,#e5e7eb);border-radius:999px;color:inherit;text-decoration:none;font-size:14px;background:var(--bg,#fff)}"
        + ".mm-live .mm-jump a:hover{border-color:var(--accent,#295cff);color:var(--accent,#295cff)}.mm-live .mm-jump span{font-size:12px;color:var(--muted,#6b7280)}"
        + ".mm-live .mm-cat{margin:0 0 36px;scroll-margin-top:90px}.mm-live .mm-cat h3{display:flex;align-items:center;gap:10px;margin:0 0 14px}"
        + ".mm-live .mm-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}"
        + ".mm-live .mm-card{display:flex;flex-direction:column;gap:8px;padding:18px 20px;border:1px solid var(--line,#e5e7eb);border-radius:12px;background:var(--bg,#fff)}"
        + ".mm-live .mm-card h4{margin:0;font-size:17px}.mm-live .mm-card p{margin:0;font-size:14.5px;line-height:1.55;color:var(--muted,#4b5563)}"
        + ".mm-live .mm-tag{align-self:flex-start;margin-top:auto;display:inline-flex;align-items:center;gap:6px;font-size:12px;font-weight:600;padding:3px 9px;border-radius:6px;background:color-mix(in srgb,var(--accent,#295cff) 10%,transparent);color:var(--accent,#295cff)}"
        + "@media (max-width:900px){.mm-live .mm-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media (max-width:600px){.mm-live .mm-grid{grid-template-columns:minmax(0,1fr)}}</style>";
}

/// <summary>The plugin's texts (the site is English; the admin may be German).</summary>
public static class MmText
{
    public static readonly Dictionary<string, string> En = new()
    {
        ["mm.set.url"] = "Matmon Cloud address", ["mm.set.urlHelp"] = "Where the status and the sensor catalogue are read (anonymous APIs /api/status and /api/sensor-types).",
        ["mm.status.name"] = "Matmon: service status", ["mm.status.desc"] = "The live status of the Matmon Cloud services.",
        ["mm.sensors.name"] = "Matmon: sensor catalogue", ["mm.sensors.desc"] = "Every sensor type Matmon has, grouped by category – from Matmon Cloud.",
        ["mm.f.heading"] = "Heading", ["mm.f.intro"] = "Introduction", ["mm.f.eyebrow"] = "Line above the heading", ["mm.f.jump"] = "Category chips",
        ["mm.f.countHelp"] = "{count} becomes the number of sensor types.", ["mm.o.yes"] = "Yes", ["mm.o.no"] = "No",
        ["mm.overall.operational"] = "All systems operational", ["mm.overall.degraded"] = "Some services are having issues",
        ["mm.overall.down"] = "Service disruption", ["mm.overall.unknown"] = "Checking status…",
        ["mm.sub.degraded"] = "A temporary problem – we are working on it.", ["mm.sub.down"] = "We are working on it.",
        ["mm.state.operational"] = "Operational", ["mm.state.degraded"] = "Partial outage", ["mm.state.down"] = "Down", ["mm.state.unknown"] = "Checking…",
        ["mm.checked"] = "Last checked", ["mm.categories"] = "Categories", ["mm.cred"] = "Uses credentials", ["mm.credTitle"] = "Uses a stored credential ({0})",
    };

    public static readonly Dictionary<string, string> De = new()
    {
        ["mm.set.url"] = "Adresse der Matmon Cloud", ["mm.set.urlHelp"] = "Von hier kommen Status und Sensorkatalog (anonyme APIs /api/status und /api/sensor-types).",
        ["mm.status.name"] = "Matmon: Dienststatus", ["mm.status.desc"] = "Der Live-Status der Dienste von Matmon Cloud.",
        ["mm.sensors.name"] = "Matmon: Sensorkatalog", ["mm.sensors.desc"] = "Alle Sensortypen von Matmon nach Kategorien – aus der Matmon Cloud.",
        ["mm.f.heading"] = "Überschrift", ["mm.f.intro"] = "Einleitung", ["mm.f.eyebrow"] = "Zeile über der Überschrift", ["mm.f.jump"] = "Kategorie-Chips",
        ["mm.f.countHelp"] = "{count} wird zur Anzahl der Sensortypen.", ["mm.o.yes"] = "Ja", ["mm.o.no"] = "Nein",
        ["mm.overall.operational"] = "Alle Systeme laufen", ["mm.overall.degraded"] = "Einige Dienste haben Probleme",
        ["mm.overall.down"] = "Störung", ["mm.overall.unknown"] = "Status wird geprüft …",
        ["mm.sub.degraded"] = "Ein vorübergehendes Problem – wir arbeiten daran.", ["mm.sub.down"] = "Wir arbeiten daran.",
        ["mm.state.operational"] = "In Betrieb", ["mm.state.degraded"] = "Teilweise gestört", ["mm.state.down"] = "Ausgefallen", ["mm.state.unknown"] = "Wird geprüft …",
        ["mm.checked"] = "Zuletzt geprüft", ["mm.categories"] = "Kategorien", ["mm.cred"] = "Nutzt Zugangsdaten", ["mm.credTitle"] = "Nutzt hinterlegte Zugangsdaten ({0})",
    };
}
