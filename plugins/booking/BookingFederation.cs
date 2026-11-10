using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// The guest card shared between sites (docs/booking-plugin.md §8): ONE site keeps the card index ("führt die Kartei") and offers it through the plugin
/// API (<c>/api/v1/booking/guests</c>, a site API key with the right <c>booking:personal</c>); other sites attach ("hängt an") with its address and key.
/// <list type="bullet">
/// <item>An attached site keeps a COPY of the guests it has met — a request must go through when the leading site is down — and hands its changes on
/// later: what changed since the last successful run is the queue (guest and booking rows carry <c>updated</c>), sent again until it went through.</item>
/// <item>Each synced field carries its own time stamp (<c>guests.stamps</c>); a field is taken over only when the incoming stamp is NEWER — the later
/// change wins, field by field, whichever side made it.</item>
/// <item>Of a stay only a summary travels (house, unit, period, state, amount) — never the registration data of the travellers. What a guest asked
/// to have remembered at a check-in IS part of his card (<c>checkin</c>) and travels with it.</item>
/// <item>No cloud needed: two MatCMS sites and a key are enough.</item>
/// </list>
/// </summary>
public static class BookingFederation
{
    public const string RoleKey = "federation.role", UrlKey = "federation.url", KeyKey = "federation.key";
    public const string Solo = "solo", Lead = "lead", Member = "member";
    public const string Group = "Verbund";
    private const string ApiPath = "/api/v1/booking/";
    private const int MaxBytes = 4 * 1024 * 1024, PageSize = 200;

    /// <summary>The fields of a guest card that are shared — each with its own time stamp.</summary>
    public static readonly string[] Fields = { "name", "phone", "country", "locale", "note", "checkin" };

    /// <summary>The test seam: a handler in place of the real, address-checking one (two sites of a test talk through the leader's test server).</summary>
    public static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    public static string Role(IPluginContext ctx) => ctx.Settings.Get(RoleKey) switch { Lead => Lead, Member => Member, _ => Solo };

    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(RoleKey, "Rolle dieser Seite", SettingKind.Choice)
        {
            Group = Group, Default = Solo,
            Options = new[]
            {
                new FieldOption(Solo, "für sich"),
                new FieldOption(Lead, "Zentrale – Gästekartei und Anfragen aller Häuser"),
                new FieldOption(Member, "Haus im Verbund – meldet an die Zentrale")
            },
            Help = "Für mehrere Häuser eines Gastgebers. Gemeinsame Verarbeitung – jede Datenschutzerklärung muss den Verbund nennen (Hinweis, keine Rechtsberatung). Meldedaten werden nie geteilt."
        })
        .Add(new SettingSpec(UrlKey, "Adresse der Zentrale", SettingKind.Text) { Group = Group, Help = "Nur für ein Haus im Verbund, z. B. https://villa-nika.de" })
        .Add(new SettingSpec(KeyKey, "API-Schlüssel der Zentrale", SettingKind.Secret)
        {
            Group = Group, Help = "In der Zentrale unter Admin → API-Schlüssel angelegt, Recht „Buchungen: Personendaten“."
        });

    // ---- stamps and merging (pure) --------------------------------------------------------------------------------

    private static string Stamp(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTime StampOf(JsonObject? stamps, string field) =>
        stamps?[field] is JsonValue v && v.TryGetValue<string>(out var s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : DateTime.MinValue;

    /// <summary>The value of a shared field as text — <c>checkin</c> is a JSON column, the others are text.</summary>
    public static string ValueOf(DataRow row, string field) => field == "checkin" ? row.Json("checkin")?.ToJsonString() ?? "" : row.Text(field);

    /// <summary>
    /// A change made HERE (a request, the admin's form, a check-in): adds <c>stamps</c> to <paramref name="values"/> with <paramref name="now"/> for every shared
    /// field whose value differs from <paramref name="row"/> (a new card: every field that has a value). Unchanged fields keep their old stamp.
    /// </summary>
    public static Dictionary<string, object?> StampLocal(DataRow? row, Dictionary<string, object?> values, DateTime now)
    {
        var stamps = row?.Json("stamps") as JsonObject is { } o ? (JsonObject)o.DeepClone() : new JsonObject();
        var changed = false;
        foreach (var f in Fields)
        {
            if (!values.TryGetValue(f, out var v)) continue;
            var text = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            if (row is null ? text.Length == 0 : ValueOf(row, f) == text) continue;
            stamps[f] = Stamp(now);
            changed = true;
        }
        if (changed || row is null) values["stamps"] = stamps.ToJsonString();
        return values;
    }

    /// <summary>
    /// Merges what the other side has into what is here: a field is taken over when its stamp is newer; with no stamp on either side (cards from before
    /// the federation) only into an empty field. Returns the columns to write (with <c>stamps</c>), empty when nothing changes.
    /// </summary>
    public static Dictionary<string, object?> Merge(IReadOnlyDictionary<string, string> here, JsonObject? hereStamps, IReadOnlyDictionary<string, string> there, JsonObject? thereStamps)
    {
        var stamps = hereStamps is null ? new JsonObject() : (JsonObject)hereStamps.DeepClone();
        var changes = new Dictionary<string, object?>();
        foreach (var f in Fields)
        {
            if (!there.TryGetValue(f, out var theirs)) continue;
            var mine = here.GetValueOrDefault(f, "");
            var a = StampOf(hereStamps, f);
            var b = StampOf(thereStamps, f);
            var take = b > a || (b == DateTime.MinValue && a == DateTime.MinValue && mine.Length == 0 && theirs.Length > 0);
            if (!take) continue;
            if (b > DateTime.MinValue) stamps[f] = Stamp(b);
            if (mine == theirs) continue;
            changes[f] = f == "checkin" && theirs.Length == 0 ? "" : theirs;
        }
        if (changes.Count > 0 || stamps.ToJsonString() != (hereStamps?.ToJsonString() ?? "{}")) changes["stamps"] = stamps.ToJsonString();
        return changes;
    }

    private static Dictionary<string, string> FieldsOf(DataRow row) => Fields.ToDictionary(f => f, f => ValueOf(row, f));

    private static Dictionary<string, string> FieldsOf(JsonObject? o)
    {
        var d = new Dictionary<string, string>();
        if (o is null) return d;
        foreach (var f in Fields)
        {
            var n = o[f];
            if (n is null && !o.ContainsKey(f)) continue;
            d[f] = n switch
            {
                null => "",
                JsonValue v when v.TryGetValue<string>(out var s) => f == "checkin" ? Checkin(s) : B.Clip(s, f == "note" ? 4000 : 200),
                JsonArray a when f == "checkin" => Checkin(a.ToJsonString()),
                _ => ""
            };
        }
        return d;
    }

    /// <summary>A remembered check-in from the other side: kept only when it is a list of objects with the known columns (nothing else gets in).</summary>
    private static string Checkin(string json)
    {
        if (json.Length == 0 || json.Length > 64_000) return "";
        try
        {
            if (JsonNode.Parse(json) is not JsonArray arr) return "";
            var clean = new JsonArray();
            foreach (var n in arr.Take(30))
            {
                if (n is not JsonObject o) continue;
                var c = new JsonObject();
                foreach (var col in BookingCheckin.Columns)
                    if (o[col] is JsonValue v && v.TryGetValue<string>(out var s)) c[col] = B.Clip(s, 200);
                clean.Add(c);
            }
            return clean.Count == 0 ? "" : clean.ToJsonString();
        }
        catch { return ""; }
    }

    private static JsonObject GuestJson(DataRow row)
    {
        var fields = new JsonObject();
        foreach (var f in Fields)
            fields[f] = f == "checkin" ? row.Json("checkin")?.ToJsonString() ?? "" : row.Text(f);
        return new JsonObject
        {
            ["email"] = row.Text("email"), ["fields"] = fields, ["stamps"] = (row.Json("stamps") as JsonObject)?.DeepClone() ?? new JsonObject(),
            ["updated"] = row.Timestamp("updated") is { } u ? Stamp(u) : null
        };
    }

    /// <summary>Writes a merge into a card (or makes the card). <paramref name="touch"/>: the leading site marks the card changed, so the others fetch it.</summary>
    private static async Task<DataRow?> ApplyAsync(IPluginData data, string email, JsonObject? fields, JsonObject? stamps, bool touch, CancellationToken ct)
    {
        var t = data.Table(BookingStore.TGuests);
        var row = await t.FindAsync(DataQuery.Everything.Eq("email", email), ct);
        var there = FieldsOf(fields);
        if (row is null)
        {
            var values = new Dictionary<string, object?> { ["email"] = email, ["created"] = DateTime.UtcNow, ["updated"] = DateTime.UtcNow };
            foreach (var (k, v) in Merge(new Dictionary<string, string>(), null, there, stamps)) values[k] = v;
            try { await t.InsertAsync(values, ct); }
            catch { /* made in parallel by a request — merged below */ }
            row = await t.FindAsync(DataQuery.Everything.Eq("email", email), ct);
            return row;
        }
        var changes = Merge(FieldsOf(row), row.Json("stamps") as JsonObject, there, stamps);
        if (changes.Count == 0) return row;
        if (touch && changes.Keys.Any(k => k != "stamps")) changes["updated"] = DateTime.UtcNow;
        await t.UpdateAsync(row.Id, changes, ct);
        return await t.GetAsync(row.Id, ct);
    }

    private static string Email(string? s) => B.Clip(s, 200).ToLowerInvariant() is var e && e.Contains('@') ? e : "";

    // ---- the leading site: the API -----------------------------------------------------------------------------------

    public static void Configure(IPluginBuilder b)
    {
        b.Api("GET", "guests", GetGuestsAsync, new ApiRouteOptions
        {
            Access = ApiAccess.PersonalData,
            Summary = "Verbund: ein Gast (?email=…, mit seinen Aufenthalten auf allen Seiten) oder die seit einem Zeitpunkt geänderten (?since=2026-11-01T00:00:00Z&page=1).",
        });
        b.Api("PUT", "guests", PutGuestAsync, new ApiRouteOptions
        {
            Access = ApiAccess.PersonalData,
            Summary = "Verbund: legt einen Gast an oder gleicht ihn ab – je Feld gewinnt der neuere Zeitstempel. Antwort: der Gast, wie er jetzt in der Kartei steht.",
            Example = """{"email":"anna@example.org","fields":{"name":"Anna Muster","phone":"+49 …"},"stamps":{"name":"2026-11-10T09:00:00.000Z"}}"""
        });
        b.Api("POST", "stays", PostStaysAsync, new ApiRouteOptions
        {
            Access = ApiAccess.PersonalData,
            Summary = "Verbund: die Aufenthalte eines Hauses im Verbund als Zusammenfassung (Haus, Einheit, Zeitraum, Status, Betrag – keine Meldedaten), höchstens 500.",
        });
        b.Job("federation", JobSchedule.EveryInterval(TimeSpan.FromMinutes(10)), async (ctx, ct) => { if (Role(ctx) == Member) await SyncAsync(ctx, ct); });
    }

    public const string TRemote = BookingStore.TRemoteStays;

    private static PluginResult Error(string text, int status) => PluginResult.Json(new JsonObject { ["error"] = text }, status);
    private static long Count(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? Math.Clamp(x, 0, 50) : 0;

    private static async Task<PluginResult?> RefuseAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (Role(ctx) != Lead) return Error("Diese Seite ist keine Zentrale (Buchungen → Einstellungen → Verbund).", 409);
        // which sites attach, for the admin page — by the NAME of the key they use (the handler never sees the key itself)
        await SetMetaAsync(ctx.Data, "fed.seen." + B.Clip(ctx.Request.ApiCaller, 80), Stamp(DateTime.UtcNow), ct);
        return null;
    }

    private static async Task<PluginResult> GetGuestsAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (await RefuseAsync(ctx, ct) is { } no) return no;
        var t = ctx.Data.Table(BookingStore.TGuests);
        if (ctx.Request.Q("email") is { Length: > 0 } raw)
        {
            var email = Email(raw);
            if (email.Length == 0 || await t.FindAsync(DataQuery.Everything.Eq("email", email), ct) is not { } row) return Error("Unbekannter Gast.", 404);
            var g = GuestJson(row);
            g["stays"] = await StaysOfAsync(ctx, email, ctx.Request.ApiCaller, ct);
            return PluginResult.Json(g);
        }
        var now = DateTime.UtcNow;
        var since = DateTime.TryParse(ctx.Request.Q("since"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var s) ? s : DateTime.MinValue;
        var page = int.TryParse(ctx.Request.Q("page"), out var p) ? Math.Clamp(p, 1, 100_000) : 1;
        var q = DataQuery.Everything.OrderBy("updated").OrderBy("id").Page((page - 1) * PageSize, PageSize + 1);
        if (since > DateTime.MinValue) q = q.Where("updated", DataOp.Ge, since);
        var rows = await t.QueryAsync(q, ct);
        var list = new JsonArray();
        foreach (var r in rows.Take(PageSize)) list.Add(GuestJson(r));
        return PluginResult.Json(new JsonObject { ["now"] = Stamp(now), ["guests"] = list, ["more"] = rows.Count > PageSize });
    }

    private static async Task<PluginResult> PutGuestAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (await RefuseAsync(ctx, ct) is { } no) return no;
        if (ctx.Request.Json() is not JsonObject body || Email(body["email"]?.GetValue<string>()) is not { Length: > 0 } email) return Error("email fehlt.", 400);
        var row = await ApplyAsync(ctx.Data, email, body["fields"] as JsonObject, body["stamps"] as JsonObject, touch: true, ct);
        return row is null ? Error("Der Gast konnte nicht gespeichert werden.", 500) : PluginResult.Json(GuestJson(row));
    }

    private static async Task<PluginResult> PostStaysAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (await RefuseAsync(ctx, ct) is { } no) return no;
        if (ctx.Request.Json() is not JsonObject body || body["stays"] is not JsonArray stays) return Error("stays fehlt.", 400);
        if (stays.Count > 500) return Error("Höchstens 500 Aufenthalte je Aufruf.", 400);
        var site = B.Clip(ctx.Request.ApiCaller, 80);
        var siteName = B.Clip(body["site"]?.GetValue<string>(), 120);
        var t = ctx.Data.Table(TRemote);
        var n = 0;
        foreach (var node in stays)
        {
            if (node is not JsonObject o) continue;
            string S(string k, int max = 120) => o[k] is JsonValue v && v.TryGetValue<string>(out var x) ? B.Clip(x, max) : "";
            var reference = S("ref", 60);
            if (reference.Length == 0 || B.Day(S("arrival")) is null || B.Day(S("departure")) is null) continue;
            var values = new Dictionary<string, object?>
            {
                ["site"] = site, ["site_name"] = siteName, ["ref"] = reference, ["email"] = Email(S("email", 200)), ["name"] = S("name"), ["number"] = S("number", 40),
                ["property"] = S("property"), ["unit"] = S("unit"), ["arrival"] = S("arrival", 10), ["departure"] = S("departure", 10),
                ["status"] = S("status", 20) is var st && BookingStatus.Label(st) != st ? st : "", ["total"] = o["total"] is JsonValue tv && tv.TryGetValue<long>(out var tot) ? Math.Max(0, tot) : 0,
                ["currency"] = S("currency", 3), ["updated"] = DateTime.UtcNow,
                ["phone"] = S("phone", 60), ["adults"] = Count(o["adults"]), ["children"] = Count(o["children"]), ["message"] = S("message", 3000),
                ["hold_until"] = S("hold_until", 40), ["offer_until"] = S("offer_until", 40), ["created"] = S("created", 40),
                // a link into the house's own admin: only an https address (or the test server's http://localhost)
                ["origin"] = S("origin", 200) is var og && (og.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || og.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)) && !og.Contains('"') ? og.TrimEnd('/') : ""
            };
            var row = await t.FindAsync(DataQuery.Everything.Eq("site", site).Eq("ref", reference), ct);
            if (row is null) await t.InsertAsync(values, ct); else await t.UpdateAsync(row.Id, values, ct);
            n++;
        }
        return PluginResult.Json(new JsonObject { ["ok"] = true, ["stays"] = n });
    }

    /// <summary>A guest's stays on every site of the federation as the leading site knows them — its own and the reported ones (not those of <paramref name="exceptSite"/>, who asks).</summary>
    private static async Task<JsonArray> StaysOfAsync(IPluginContext ctx, string email, string exceptSite, CancellationToken ct)
    {
        var list = new JsonArray();
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).ToDictionary(u => u.Id);
        var houses = (await store.PropertiesAsync(ct)).ToDictionary(p => p.Id, p => p.Name);
        foreach (var b in (await BookingFlow.ListAsync(ctx.Data, null, ct)).Where(b => b.Email == email))
        {
            units.TryGetValue(b.UnitId, out var u);
            list.Add(new JsonObject
            {
                ["site"] = ctx.Site.Name, ["number"] = b.Number, ["property"] = u is null ? "" : houses.GetValueOrDefault(u.PropertyId, ""), ["unit"] = u?.Name ?? "",
                ["arrival"] = B.Iso(b.Arrival), ["departure"] = B.Iso(b.Departure), ["status"] = b.Status, ["total"] = b.Total, ["currency"] = BookingPlugin.Currency(ctx)
            });
        }
        foreach (var r in await ctx.Data.Table(TRemote).QueryAsync(DataQuery.Everything.Eq("email", email).OrderBy("arrival"), ct))
        {
            if (r.Text("site") == exceptSite) continue;
            list.Add(RemoteJson(r));
        }
        return list;
    }

    private static JsonObject RemoteJson(DataRow r) => new()
    {
        ["site"] = r.Text("site_name").Length > 0 ? r.Text("site_name") : r.Text("site"), ["number"] = r.Text("number"), ["property"] = r.Text("property"), ["unit"] = r.Text("unit"),
        ["arrival"] = r.Text("arrival"), ["departure"] = r.Text("departure"), ["status"] = r.Text("status"), ["total"] = r.Int("total"), ["currency"] = r.Text("currency")
    };

    /// <summary>The stays the attached sites reported for a guest (the leading site's guest card).</summary>
    public static async Task<List<RemoteStay>> RemoteStaysAsync(IPluginData data, string email, CancellationToken ct) =>
        email.Length == 0 ? new() : (await data.Table(TRemote).QueryAsync(DataQuery.Everything.Eq("email", email).OrderBy("arrival"), ct)).Select(r => RemoteStay.From(RemoteJson(r))).ToList();

    /// <summary>The attached sites that called, with when (from the key names).</summary>
    public static async Task<List<(string Site, DateTime? At)>> SeenAsync(IPluginData data, CancellationToken ct) =>
        (await data.Table(BookingStore.TMeta).QueryAsync(DataQuery.Everything.Where("name", DataOp.StartsWith, "fed.seen."), ct))
            .Select(r => (r.Text("name")["fed.seen.".Length..], DateTime.TryParse(r.Text("value"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : (DateTime?)null))
            .OrderBy(x => x.Item1).ToList();

    // ---- the attached site: the client ---------------------------------------------------------------------------------

    /// <summary>A stay as the federation shows it.</summary>
    public sealed record RemoteStay(string Site, string Number, string Property, string Unit, DateOnly? Arrival, DateOnly? Departure, string Status, long Total, string Currency)
    {
        public static RemoteStay From(JsonObject o)
        {
            string S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x : "";
            return new(S("site"), S("number"), S("property"), S("unit"), B.Day(S("arrival")), B.Day(S("departure")), S("status"),
                o["total"] is JsonValue t && t.TryGetValue<long>(out var n) ? n : 0, S("currency"));
        }
    }

    /// <summary>The outcome of a run, shown on the admin page.</summary>
    public sealed record SyncResult(bool Ok, int Pushed, int Stays, int Pulled, string Error);

    private static Uri Base(IPluginContext ctx)
    {
        var url = (ctx.Settings.Get(UrlKey) ?? "").Trim();
        if (url.Length == 0) throw new HttpRequestException("Die Adresse der Zentrale fehlt (Einstellungen → Verbund).");
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        Uri u;
        try { u = BookingIcal.Check(url); }
        catch (HttpRequestException e) when (e.Message.StartsWith("Nur https", StringComparison.Ordinal)) { throw new HttpRequestException("Die Zentrale muss über https erreichbar sein."); }
        return new Uri(u.GetLeftPart(UriPartial.Authority) + ApiPath);
    }

    private static HttpClient Client(IPluginContext ctx)
    {
        var key = (ctx.Settings.Get(KeyKey) ?? "").Trim();
        if (key.Length == 0) throw new HttpRequestException("Der API-Schlüssel der Zentrale fehlt (Einstellungen → Verbund).");
        var handler = HandlerFactory?.Invoke() ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            // the same wall as for the portals' calendars: only PUBLIC addresses, checked on the resolved IP inside the connect itself
            ConnectCallback = async (context, token) =>
            {
                var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                var allowed = addresses.Where(BookingIcal.IsPublic).ToList();
                if (allowed.Count == 0) throw new HttpRequestException("Die Adresse führt nicht ins öffentliche Internet.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(allowed[0], context.DnsEndPoint.Port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Buchungen/1.0");
        return client;
    }

    private static async Task<JsonObject?> SendAsync(HttpClient client, HttpMethod method, Uri uri, JsonNode? body, CancellationToken ct, bool notFoundIsNull = false)
    {
        using var req = new HttpRequestMessage(method, uri);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (notFoundIsNull && res.StatusCode == HttpStatusCode.NotFound) return null;
        if (res.Content.Headers.ContentLength > MaxBytes) throw new HttpRequestException("Die Antwort der Zentrale ist zu groß.");
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > MaxBytes) throw new HttpRequestException("Die Antwort der Zentrale ist zu groß.");
        JsonObject? json = null;
        try { json = JsonNode.Parse(bytes) as JsonObject; } catch { /* not JSON — said below */ }
        if (res.IsSuccessStatusCode) return json ?? throw new HttpRequestException("Die Zentrale antwortet nicht wie eine MatCMS-Seite mit dem Buchungs-Plugin.");
        var why = json?["error"] is JsonValue v && v.TryGetValue<string>(out var e) ? e : res.ReasonPhrase ?? "";
        throw new HttpRequestException((int)res.StatusCode switch
        {
            401 => "Der API-Schlüssel wird von der Zentrale nicht angenommen.",
            403 => "Dem API-Schlüssel fehlt das Recht „Buchungen: Personendaten“.",
            404 => "Unter der Adresse gibt es kein Buchungs-Plugin (oder es ist abgeschaltet).",
            _ => "Antwort " + (int)res.StatusCode + (why.Length > 0 ? ": " + why : "")
        });
    }

    /// <summary>Asks the leading site whether address and key work. Returns null when they do, else the reason.</summary>
    public static async Task<string?> ProbeAsync(IPluginContext ctx, CancellationToken ct)
    {
        try
        {
            using var c = Client(ctx);
            await SendAsync(c, HttpMethod.Get, new Uri(Base(ctx), "guests?email=probe%40matcms.invalid"), null, ct, notFoundIsNull: true);
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return e is TaskCanceledException ? "Die Zentrale antwortet nicht (Zeitüberschreitung)." : e.Message; }
    }

    /// <summary>A guest as the leading site has him, with his stays on the other sites — for the guest card. Null when unknown there or not reachable.</summary>
    public static async Task<(JsonObject? Guest, string? Error)> LookupAsync(IPluginContext ctx, string email, CancellationToken ct)
    {
        try
        {
            using var c = Client(ctx);
            return (await SendAsync(c, HttpMethod.Get, new Uri(Base(ctx), "guests?email=" + Uri.EscapeDataString(email)), null, ct, notFoundIsNull: true), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (null, e is TaskCanceledException ? "Die Zentrale antwortet nicht." : e.Message); }
    }

    /// <summary>
    /// One run of an attached site: (1) hands on every card and every stay changed since the last successful run (the queue), taking back what the card
    /// index answers; (2) fetches the cards changed there since the last run — only those of guests this site knows. The watermarks move only after a
    /// run went through, so whatever failed is sent again next time.
    /// </summary>
    public static async Task<SyncResult> SyncAsync(IPluginContext ctx, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        await SetMetaAsync(ctx.Data, "fed.last_run", Stamp(started), ct);
        int pushed = 0, staysSent = 0, pulled = 0;
        try
        {
            using var c = Client(ctx);
            var api = Base(ctx);
            (pushed, staysSent) = await PushAsync(ctx, c, api, started, ct);
            var guests = ctx.Data.Table(BookingStore.TGuests);

            // 3) cards changed there
            var pulledSince = await MetaTimeAsync(ctx.Data, "fed.pulled", ct);
            string? now = null;
            for (var page = 1; page <= 500; page++)
            {
                var url = "guests?page=" + page + (pulledSince > DateTime.MinValue ? "&since=" + Uri.EscapeDataString(Stamp(pulledSince.AddSeconds(-2))) : "");
                var answer = await SendAsync(c, HttpMethod.Get, new Uri(api, url), null, ct) ?? new JsonObject();
                now ??= answer["now"]?.GetValue<string>();
                foreach (var g in (answer["guests"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var email = Email(g["email"]?.GetValue<string>());
                    if (email.Length == 0 || await guests.FindAsync(DataQuery.Everything.Eq("email", email), ct) is null) continue;      // only the guests met here
                    await ApplyAsync(ctx.Data, email, g["fields"] as JsonObject, g["stamps"] as JsonObject, touch: false, ct);
                    pulled++;
                }
                if (answer["more"]?.GetValue<bool>() != true) break;
            }
            if (now is not null) await SetMetaAsync(ctx.Data, "fed.pulled", now, ct);
            await SetMetaAsync(ctx.Data, "fed.last_ok", Stamp(DateTime.UtcNow), ct);
            await SetMetaAsync(ctx.Data, "fed.last_error", "", ct);
            return new SyncResult(true, pushed, staysSent, pulled, "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            var why = e is TaskCanceledException ? "Die Zentrale antwortet nicht (Zeitüberschreitung)." : e.Message;
            await SetMetaAsync(ctx.Data, "fed.last_error", why, ct);
            return new SyncResult(false, pushed, staysSent, pulled, why);
        }
    }

    /// <summary>
    /// A house hands on at once what just changed (a request, an answer), so the central inbox does not wait for the 10-minute job: the same push as the
    /// job's, at most 5 seconds; whatever does not get through stays in the queue and goes with the next run. Does nothing on any other role.
    /// </summary>
    public static async Task PushNowAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (Role(ctx) != Member || (ctx.Settings.Get(UrlKey) ?? "").Trim().Length == 0 || (ctx.Settings.Get(KeyKey) ?? "").Trim().Length == 0) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var c = Client(ctx);
            await PushAsync(ctx, c, Base(ctx), DateTime.UtcNow, cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException) { /* the job sends it again */ }
    }

    /// <summary>A page or route that changes bookings: after a POST, the house hands the change on at once (see <see cref="PushNowAsync"/>).</summary>
    public static PluginHandler Pushing(PluginHandler handler) => async (ctx, ct) =>
    {
        var result = await handler(ctx, ct);
        if (ctx.Request.IsPost) await PushNowAsync(ctx, ct);
        return result;
    };

    private static string Iso(DataRow r, string column) => r.Text(column) is { Length: > 0 } t && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? Stamp(d) : "";

    /// <summary>Steps 1 and 2 of a run: the cards and the stays changed since the last successful push; moves the watermark only when both went through.</summary>
    private static async Task<(int Pushed, int Stays)> PushAsync(IPluginContext ctx, HttpClient c, Uri api, DateTime started, CancellationToken ct)
    {
        int pushed = 0, staysSent = 0;
        {
            var pushedUntil = await MetaTimeAsync(ctx.Data, "fed.pushed", ct);
            var since = pushedUntil == DateTime.MinValue ? DateTime.MinValue : pushedUntil.AddSeconds(-2);     // a small overlap; sending twice changes nothing

            // 1) cards changed here
            var guests = ctx.Data.Table(BookingStore.TGuests);
            var q = DataQuery.Everything.OrderBy("id");
            if (since > DateTime.MinValue) q = q.Where("updated", DataOp.Ge, since);
            foreach (var row in await guests.QueryAsync(q, ct))
            {
                var fields = new JsonObject();
                foreach (var f in Fields) fields[f] = ValueOf(row, f);
                var answer = await SendAsync(c, HttpMethod.Put, new Uri(api, "guests"), new JsonObject
                {
                    ["email"] = row.Text("email"), ["fields"] = fields, ["stamps"] = (row.Json("stamps") as JsonObject)?.DeepClone() ?? new JsonObject()
                }, ct);
                await ApplyAsync(ctx.Data, row.Text("email"), answer?["fields"] as JsonObject, answer?["stamps"] as JsonObject, touch: false, ct);
                pushed++;
            }

            // 2) stays changed here, as summaries
            var bq = DataQuery.Everything.OrderBy("id");
            if (since > DateTime.MinValue) bq = bq.Where("updated", DataOp.Ge, since);
            var rows = await ctx.Data.Table(BookingStore.TBookings).QueryAsync(bq, ct);
            if (rows.Count > 0)
            {
                var store = new BookingStore(ctx.Data);
                var units = (await store.UnitsAsync(ct)).ToDictionary(u => u.Id);
                var houses = (await store.PropertiesAsync(ct)).ToDictionary(p => p.Id, p => p.Name);
                var cur = BookingPlugin.Currency(ctx);
                foreach (var chunk in rows.Chunk(500))
                {
                    var arr = new JsonArray();
                    foreach (var r in chunk)
                    {
                        units.TryGetValue(r.Int("unit_id"), out var u);
                        arr.Add(new JsonObject
                        {
                            ["ref"] = r.Id.ToString(CultureInfo.InvariantCulture), ["number"] = r.Text("number"), ["email"] = r.Text("email"), ["name"] = r.Text("name"),
                            ["property"] = u is null ? "" : houses.GetValueOrDefault(u.PropertyId, ""), ["unit"] = u?.Name ?? "",
                            ["arrival"] = r.Text("arrival"), ["departure"] = r.Text("departure"), ["status"] = r.Text("status"), ["total"] = r.Int("total"), ["currency"] = cur,
                            ["phone"] = r.Text("phone"), ["adults"] = r.Int("adults"), ["children"] = r.Int("children"), ["message"] = B.Clip(r.Text("message"), 3000),
                            ["hold_until"] = Iso(r, "hold_until"), ["offer_until"] = Iso(r, "offer_until"), ["created"] = Iso(r, "created"), ["origin"] = ctx.Site.Origin
                        });
                    }
                    await SendAsync(c, HttpMethod.Post, new Uri(api, "stays"), new JsonObject { ["site"] = ctx.Site.Name, ["stays"] = arr }, ct);
                    staysSent += arr.Count;
                }
            }
            await SetMetaAsync(ctx.Data, "fed.pushed", Stamp(started), ct);
        }
        return (pushed, staysSent);
    }

    /// <summary>What waits to be handed on: cards and stays changed since the last successful run.</summary>
    public static async Task<(int Guests, int Stays)> PendingAsync(IPluginData data, CancellationToken ct)
    {
        var since = await MetaTimeAsync(data, "fed.pushed", ct);
        if (since == DateTime.MinValue)
            return ((await data.Table(BookingStore.TGuests).QueryAsync(DataQuery.Everything, ct)).Count, (await data.Table(BookingStore.TBookings).QueryAsync(DataQuery.Everything, ct)).Count);
        return ((await data.Table(BookingStore.TGuests).QueryAsync(DataQuery.Everything.Where("updated", DataOp.Gt, since), ct)).Count,
            (await data.Table(BookingStore.TBookings).QueryAsync(DataQuery.Everything.Where("updated", DataOp.Gt, since), ct)).Count);
    }

    // ---- meta ------------------------------------------------------------------------------------------------------------

    public static async Task<string> MetaAsync(IPluginData data, string name, CancellationToken ct) =>
        (await data.Table(BookingStore.TMeta).FindAsync(DataQuery.Everything.Eq("name", name), ct))?.Text("value") ?? "";

    public static async Task<DateTime> MetaTimeAsync(IPluginData data, string name, CancellationToken ct) =>
        DateTime.TryParse(await MetaAsync(data, name, ct), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;

    private static async Task SetMetaAsync(IPluginData data, string name, string value, CancellationToken ct)
    {
        var t = data.Table(BookingStore.TMeta);
        if (await t.FindAsync(DataQuery.Everything.Eq("name", name), ct) is { } row) await t.UpdateAsync(row.Id, new Dictionary<string, object?> { ["value"] = value }, ct);
        else await t.InsertAsync(new Dictionary<string, object?> { ["name"] = name, ["value"] = value }, ct);
    }
}
