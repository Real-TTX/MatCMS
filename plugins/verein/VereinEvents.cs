using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// The club's PUBLIC events as part of the website — and of whatever else runs on it:
/// <list type="bullet">
/// <item>a page per public event (<c>/verein/termine/{id}</c>) and an overview (<c>/verein/termine</c>) with "add to calendar" — the block links there,
/// the sitemap lists them, the link picker offers them for menus and buttons;</item>
/// <item>an answer to <c>site.search</c>, so the site's search and the AI bot know the dates ("Wann ist der nächste Umzug?");</item>
/// <item>operations (<c>event.list/read/create/update</c>) for the site's AI assistant, the cloud and MCP — a parade schedule can be entered by asking.</item>
/// </list>
/// Internal events, drafts and the planning (meeting point, running order) never leave the member area.
/// </summary>
public static class VereinEvents
{
    public const string ListPath = "/verein/termine";
    public static string PagePath(long id) => "/verein/termine/" + id.ToString(CultureInfo.InvariantCulture);
    public static string IcsPath(long id) => "/verein/ics/" + id.ToString(CultureInfo.InvariantCulture);

    public static void Configure(IPluginBuilder b)
    {
        var read = new PublicRouteOptions { Methods = PluginMethod.Get, DefaultLanguageOnly = true };
        b.Route(ListPath, ListAsync, read);
        b.Route("/verein/termine/{id}", PageAsync, read);
        b.Route("/verein/ics/{id}", IcsAsync, read);

        b.Sitemap(async (ctx, ct) => (await PublicAsync(ctx, ct)).Select(e => new SitemapEntry(PagePath(e.Id), e.Created ?? DateTime.UtcNow)).Prepend(new SitemapEntry(ListPath)));

        b.LinkTargets("Verein", async (ctx, ct) =>
            new[] { new LinkTarget("Termine (Übersicht)", ListPath), new LinkTarget("Mitgliederbereich", VereinPublic.Base), new LinkTarget("Termine als Kalender (iCal)", VereinIcal.PublicFeed) }
                .Concat((await PublicAsync(ctx, ct)).Where(e => string.CompareOrdinal(e.Day, V.Today) >= 0).Take(100)
                    .Select(e => new LinkTarget("Termin: " + e.Title + " (" + V.Date(e.Starts) + ")", PagePath(e.Id)))));

        b.On("site.search", SearchAsync);

        b.Operation(OpList, ListOpAsync);
        b.Operation(OpRead, ReadOpAsync);
        b.Operation(OpCreate, CreateOpAsync);
        b.Operation(OpUpdate, UpdateOpAsync);
        b.Operation(OpRemove, RemoveOpAsync);
        b.Operation(OpRestore, RestoreOpAsync);
    }

    /// <summary>Public, published events (cancelled ones too — they stay listed, struck through).</summary>
    private static async Task<List<Event>> PublicAsync(IPluginContext ctx, CancellationToken ct) =>
        (await new VereinStore(ctx.Data).EventsAsync(ct)).Where(e => e.Public && !e.Draft).ToList();

    private static PluginResult Frame(IPluginContext ctx, string title, string body) =>
        PluginResult.Page("<style>" + VereinCss.Css + "</style><section class=\"section vm vm-public\"><div class=\"container\">" + body + "</div></section>", title);

    // ---- pages ----------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> ListAsync(IPluginContext ctx, CancellationToken ct)
    {
        var all = await PublicAsync(ctx, ct);
        var upcoming = all.Where(e => string.CompareOrdinal(e.Ends.Length >= 10 ? e.Ends[..10] : e.Day, V.Today) >= 0).ToList();
        var title = ctx.Settings.Get(VereinSettings.EventsTitle) is { Length: > 0 } t ? t : "Termine";
        var sb = new StringBuilder("<header class=\"vm-head\"><div><p class=\"vm-kicker\">").Append(V.H(VereinSettings.Club(ctx))).Append("</p><h1>").Append(V.H(title)).Append("</h1></div></header>");
        sb.Append("<section class=\"vm-card\">").Append(VereinPublic.EventList(upcoming, internalBadge: false, publicLinks: true)).Append("</section>");
        sb.Append("<p class=\"verein-more\"><a href=\"").Append(VereinIcal.PublicFeed).Append("\"><i class=\"ti ti-calendar-plus\" aria-hidden=\"true\"></i> Alle Termine im eigenen Kalender abonnieren</a></p>");
        return Frame(ctx, title, sb.ToString());
    }

    private static long IdOf(IPluginContext ctx) => long.TryParse(ctx.Request.R("id").Replace(".ics", ""), out var id) ? id : 0;

    private static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var e = (await PublicAsync(ctx, ct)).FirstOrDefault(x => x.Id == IdOf(ctx));
        if (e is null) return PluginResult.NotFound();
        var (day, month, wd) = V.Badge(e.Starts);
        var sb = new StringBuilder("<p class=\"vm-back\"><a href=\"").Append(ListPath).Append("\">← Alle Termine</a></p>");
        sb.Append("<article class=\"vm-card vm-event-page").Append(e.Cancelled ? " is-cancelled" : "").Append("\"><div class=\"vm-event-head\"><span class=\"vm-date big\"><b>").Append(V.H(day))
          .Append("</b><small>").Append(V.H(month)).Append("</small><em>").Append(V.H(wd)).Append("</em></span><div><p class=\"vm-kicker\">").Append(V.H(e.Kind.Length > 0 ? e.Kind : "Termin"))
          .Append("</p><h1>").Append(V.H(e.Title)).Append("</h1>");
        if (e.Cancelled) sb.Append("<p><span class=\"vm-badge err\">abgesagt</span></p>");
        sb.Append("</div></div><dl class=\"vm-facts\"><dt>Wann</dt><dd>").Append(V.H(V.When(e.Starts)));
        if (e.Ends.Length > 0 && e.Ends != e.Starts) sb.Append(" – ").Append(V.H(e.Ends[..Math.Min(10, e.Ends.Length)] == e.Day ? V.When(e.Ends).Split(", ").Last() : V.When(e.Ends)));
        sb.Append("</dd>");
        if (e.Place.Length > 0) sb.Append("<dt>Wo</dt><dd>").Append(V.H(e.Place)).Append("</dd>");
        sb.Append("</dl>");
        if (e.Description.Length > 0) sb.Append("<div class=\"vm-text\">").Append(V.Paragraphs(e.Description)).Append("</div>");
        sb.Append("<p class=\"vm-actions\"><a class=\"vm-btn\" href=\"").Append(IcsPath(e.Id)).Append("\"><i class=\"ti ti-calendar-plus\"></i> In den Kalender</a>");
        if (e.Place.Length > 0) sb.Append("<a class=\"vm-btn ghost\" href=\"https://www.openstreetmap.org/search?query=").Append(Uri.EscapeDataString(e.Place)).Append("\" rel=\"noopener\" target=\"_blank\"><i class=\"ti ti-map-pin\"></i> Karte</a>");
        if (e.Link.StartsWith("https://", StringComparison.Ordinal) || (e.Link.StartsWith('/') && !e.Link.StartsWith("//", StringComparison.Ordinal)))
            sb.Append("<a class=\"vm-btn ghost\" href=\"").Append(V.H(e.Link)).Append("\">Mehr erfahren</a>");
        sb.Append("</p></article>");
        // the club's members get a way to their planning (running order, meeting point) — the page itself never shows it
        if (ctx.Member is not null) sb.Append("<p class=\"vm-muted\"><a href=\"").Append(VereinPublic.Base).Append("/termine/").Append(e.Id).Append("\">Ablauf, Treffpunkt und Bilder im Mitgliederbereich →</a></p>");
        return Frame(ctx, e.Title, sb.ToString());
    }

    private static async Task<PluginResult> IcsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var e = (await PublicAsync(ctx, ct)).FirstOrDefault(x => x.Id == IdOf(ctx));
        return e is null ? PluginResult.NotFound() : VereinIcal.Calendar(ctx, e.Title, new[] { e }, Array.Empty<Meeting>(), "termin-" + e.Id + ".ics");
    }

    // ---- site search ----------------------------------------------------------------------------------------------

    private static async Task<JsonNode?> SearchAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var q = (payload?["q"]?.GetValue<string>() ?? "").ToLowerInvariant();
        var terms = q.Split(new[] { ' ', ',', '.', '?', '!', ';', ':' }, StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 3).Distinct().ToList();
        if (terms.Count == 0) return null;
        // "Termin", "Umzug", "wann" … are what a date question is made of: every upcoming public event is a candidate then
        var dateQuestion = terms.Any(t => t is "termin" or "termine" or "wann" or "nächste" or "nächster" or "nächsten" or "kalender" or "veranstaltung" or "veranstaltungen" or "events");
        var max = Math.Clamp(payload?["max"] is JsonValue m && m.TryGetValue<int>(out var n) ? n : 5, 1, 20);
        var items = new JsonArray();
        foreach (var hit in (await PublicAsync(ctx, ct)).Where(e => !e.Cancelled && string.CompareOrdinal(e.Ends.Length >= 10 ? e.Ends[..10] : e.Day, V.Today) >= 0)
                     .Select(e => (e, Score: terms.Sum(t => (e.Title.ToLowerInvariant().Contains(t) ? 3 : 0) + ((e.Kind + " " + e.Place + " " + e.Description).ToLowerInvariant().Contains(t) ? 1 : 0)) + (dateQuestion ? 1 : 0)))
                     .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.e.Starts).Take(max))
            items.Add(new JsonObject
            {
                ["title"] = hit.e.Title, ["url"] = PagePath(hit.e.Id), ["score"] = hit.Score, ["kind"] = "Termin",
                ["text"] = V.When(hit.e.Starts) + (hit.e.Place.Length > 0 ? " · " + hit.e.Place : "") + (hit.e.Description.Length > 0 ? " – " + V.Clip(hit.e.Description, 200) : "")
            });
        return items.Count == 0 ? null : new JsonObject { ["items"] = items };
    }

    // ---- operations (AI assistant, cloud, MCP) ----------------------------------------------------------------------

    public static readonly PluginOperationSpec OpList = new("event.list",
        "Termine des Vereins auflisten (Umzüge, Feste, Ausflüge …): id, Titel, Art, Beginn, Ende, Ort, öffentlich, Entwurf, abgesagt. Ohne \"all\" nur kommende.",
        """{"all"?: false, "search"?: "text", "limit"?: 50}""", Writes: false);
    public static readonly PluginOperationSpec OpRead = new("event.read", "Einen Termin vollständig lesen, mit Planung (Treffpunkt, Ablauf, Hinweise).", """{"id": 12}""", Writes: false);
    public static readonly PluginOperationSpec OpCreate = new("event.create",
        "Neuen Termin anlegen. starts/ends: \"2027-01-09T14:30\" oder nur \"2027-01-09\". public: auf der Website im Block „Vereinstermine“. draft: nur im Admin. schedule: Ablauf, eine Zeile \"14:30|Umzug\" je Programmpunkt. ask: Mitglieder sollen zu- oder absagen (ohne Angabe: die Einstellung des Vereins), ask_required: Rückmeldung ist Pflicht, ask_due: bis wann. Es geht dabei keine E-Mail raus.",
        """{"title": "Fasnetsumzug in Brackenheim", "kind"?: "Umzug", "starts": "2027-01-09T14:30", "ends"?: "…", "place"?: "74344 Brackenheim", "description"?: "…", "public"?: true, "draft"?: false, "meeting"?: "Treffpunkt", "schedule"?: "17:00|Zunftmeisterempfang", "ask"?: true, "ask_required"?: false, "ask_due"?: "2027-01-05"}""");
    public static readonly PluginOperationSpec OpUpdate = new("event.update", "Einen Termin ändern; nicht genannte Felder bleiben. cancelled: absagen (bleibt durchgestrichen stehen). ask: Mitglieder sollen zu- oder absagen (Teilnahme abfragen), ask_required: Rückmeldung ist Pflicht, ask_due: bis wann (\"\" = ohne). Es geht dabei keine E-Mail raus.",
        """{"id": 12, "title"?, "kind"?, "starts"?, "ends"?, "place"?, "description"?, "public"?, "draft"?, "cancelled"?, "meeting"?, "parking"?, "notes"?, "schedule"?, "ask"?, "ask_required"?, "ask_due"?}""");
    public static readonly PluginOperationSpec OpRemove = new("event.remove", "Entfernt einen eben angelegten Termin (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec OpRestore = new("event.restore", "Stellt einen Termin aus einer Rückgängig-Aufzeichnung wieder her.", Internal: true);

    private static readonly string[] TextFields = { "title", "kind", "place", "description", "link", "meeting", "parking", "notes", "schedule" };
    private static readonly string[] Flags = { "public", "draft", "cancelled", "ask", "ask_required" };

    private static JsonObject Answer(string status, string message, JsonNode? data = null, JsonNode? undo = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        if (undo is not null) o["undo"] = undo;
        return o;
    }

    private static JsonObject Full(Event e) => new()
    {
        ["id"] = e.Id, ["title"] = e.Title, ["kind"] = e.Kind, ["starts"] = e.Starts, ["ends"] = e.Ends, ["place"] = e.Place, ["description"] = e.Description,
        ["public"] = e.Public, ["draft"] = e.Draft, ["cancelled"] = e.Cancelled, ["link"] = e.Link, ["meeting"] = e.Meeting, ["parking"] = e.Parking,
        ["notes"] = e.Notes, ["schedule"] = e.Schedule, ["ask"] = e.Ask, ["ask_required"] = e.AskRequired, ["ask_due"] = e.AskDue
    };

    /// <summary>The values an operation may write — cleaned like the admin form cleans them.</summary>
    private static Dictionary<string, object?> Values(JsonObject o, bool isNew)
    {
        var v = new Dictionary<string, object?>();
        foreach (var f in TextFields)
            if (o[f] is JsonValue jv)
            {
                var s = (jv.TryGetValue<string>(out var str) ? str : jv.ToString()).Trim();
                v[f] = f switch { "title" => V.Clip(s, 160), "description" => V.Clip(s, 2000), "notes" => V.Clip(s, 3000), "schedule" => V.Clip(s.Replace("\r", ""), 4000), _ => V.Clip(s, 500) };
            }
        if (v.TryGetValue("link", out var l) && l is string link && link.Length > 0 && !(link.StartsWith("https://", StringComparison.Ordinal) || (link.StartsWith('/') && !link.StartsWith("//", StringComparison.Ordinal)))) v["link"] = "";
        foreach (var k in new[] { "starts", "ends", "ask_due" })
            if (o[k] is JsonValue d && d.TryGetValue<string>(out var ds)) v[k] = V.CleanDate(ds);
        foreach (var f in Flags)
            if (o[f] is JsonValue b && b.TryGetValue<bool>(out var flag)) v[f] = flag;
        if (isNew) { v.TryAdd("public", true); v.TryAdd("draft", false); v.TryAdd("cancelled", false); v["created"] = DateTime.UtcNow; }
        return v;
    }

    private static async Task<JsonNode?> ListOpAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var all = args?["all"] is JsonValue a && a.TryGetValue<bool>(out var x) && x;
        var search = (args?["search"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        var limit = Math.Clamp(args?["limit"] is JsonValue lv && lv.TryGetValue<int>(out var n) ? n : 50, 1, 500);
        var events = (await new VereinStore(ctx.Data).EventsAsync(ct))
            .Where(e => all || string.CompareOrdinal(e.Ends.Length >= 10 ? e.Ends[..10] : e.Day, V.Today) >= 0)
            .Where(e => search.Length == 0 || (e.Title + " " + e.Kind + " " + e.Place).ToLowerInvariant().Contains(search)).Take(limit);
        var arr = new JsonArray();
        foreach (var e in events)
            arr.Add(new JsonObject { ["id"] = e.Id, ["title"] = e.Title, ["kind"] = e.Kind, ["starts"] = e.Starts, ["ends"] = e.Ends, ["place"] = e.Place, ["public"] = e.Public, ["draft"] = e.Draft, ["cancelled"] = e.Cancelled });
        return Answer("applied", arr.Count + " Termin(e).", arr);
    }

    private static async Task<JsonNode?> ReadOpAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var id = args?["id"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : 0;
        var e = await new VereinStore(ctx.Data).EventAsync(id, ct);
        return e is null ? Answer("failed", "Termin nicht gefunden.") : Answer("applied", "Termin „" + e.Title + "“.", Full(e));
    }

    private static async Task<JsonNode?> CreateOpAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var v = Values(o, isNew: true);
        v.TryAdd("ask", ctx.Settings.GetBool(VereinSettings.AskDefault));
        if (v.GetValueOrDefault("title") is not string { Length: >= 2 } title) return Answer("failed", "Bitte einen Titel angeben.");
        if (v.GetValueOrDefault("starts") is not string { Length: >= 10 }) return Answer("failed", "Bitte den Beginn angeben (2027-01-09T14:30 oder 2027-01-09).");
        var store = new VereinStore(ctx.Data);
        var same = (await store.EventsAsync(ct)).FirstOrDefault(e => string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase) && e.Starts == (string)v["starts"]!);
        if (same is not null) return Answer("skipped-exists", "Den Termin „" + title + "“ gibt es schon.", new JsonObject { ["id"] = same.Id });
        var id = await store.Events.InsertAsync(v, ct);
        ctx.Log($"Termin #{id} „{title}“ angelegt über eine Operation");
        return Answer("applied", "Termin „" + title + "“ angelegt" + (v["draft"] is true ? " (Entwurf)." : "."), new JsonObject { ["id"] = id, ["url"] = PagePath(id) },
            new JsonObject { ["name"] = OpRemove.Name, ["args"] = new JsonObject { ["id"] = id } });
    }

    private static async Task<JsonNode?> UpdateOpAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o || o["id"] is not JsonValue iv || !iv.TryGetValue<long>(out var id)) return Answer("failed", "Bitte die id des Termins angeben.");
        var store = new VereinStore(ctx.Data);
        var e = await store.EventAsync(id, ct);
        if (e is null) return Answer("failed", "Termin nicht gefunden.");
        var v = Values(o, isNew: false);
        if (v.Count == 0) return Answer("skipped", "Nichts zu ändern.");
        await store.Events.UpdateAsync(id, v, ct);
        return Answer("applied", "Termin „" + e.Title + "“ geändert.", new JsonObject { ["id"] = id }, new JsonObject { ["name"] = OpRestore.Name, ["args"] = Full(e) });
    }

    private static async Task<JsonNode?> RemoveOpAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var id = args?["id"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : 0;
        var store = new VereinStore(ctx.Data);
        if (await store.EventAsync(id, ct) is null) return Answer("skipped", "Termin gibt es nicht mehr.");
        await store.Replies.DeleteWhereAsync(DataQuery.Everything.Eq("event_id", id), ct);
        await store.Events.DeleteAsync(id, ct);
        return Answer("applied", "Termin entfernt.");
    }

    private static async Task<JsonNode?> RestoreOpAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o || o["id"] is not JsonValue iv || !iv.TryGetValue<long>(out var id)) return Answer("failed", "id fehlt.");
        var store = new VereinStore(ctx.Data);
        if (await store.EventAsync(id, ct) is null) return Answer("skipped", "Termin gibt es nicht mehr.");
        await store.Events.UpdateAsync(id, Values(o, isNew: false), ct);
        return Answer("applied", "Termin wiederhergestellt.");
    }
}
