using System.Globalization;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// The club's content as plugin operations — for the site's AI assistant, the cloud's MCP and the site's own MCP server: members (personal data),
/// meetings, documents, templates, uploads and the roles/fields an agent needs to fill them in. The events are in <see cref="VereinEvents"/>.
/// <para>Rules: every write answers with an <c>undo</c> naming an INTERNAL operation of this plugin; values are cleaned like the admin's own forms clean
/// them and written through <see cref="VereinStore"/>; no operation sends a mail, creates a site account or touches a password — that stays in the back
/// office, where a person decides. Answers stay German (the operator and the model read them).</para>
/// </summary>
public static class VereinOps
{
    public static void Configure(IPluginBuilder b)
    {
        b.Operation(RoleList, RoleListAsync);

        b.Operation(MemberList, MemberListAsync);
        b.Operation(MemberRead, MemberReadAsync);
        b.Operation(MemberCreate, MemberCreateAsync);
        b.Operation(MemberUpdate, MemberUpdateAsync);
        b.Operation(MemberRemove, MemberRemoveAsync);
        b.Operation(MemberRestore, MemberRestoreAsync);

        b.Operation(MeetingList, MeetingListAsync);
        b.Operation(MeetingRead, MeetingReadAsync);
        b.Operation(MeetingCreate, MeetingCreateAsync);
        b.Operation(MeetingUpdate, MeetingUpdateAsync);
        b.Operation(MeetingRemove, MeetingRemoveAsync);
        b.Operation(MeetingRestore, MeetingRestoreAsync);

        b.Operation(DocList, DocListAsync);
        b.Operation(DocRead, DocReadAsync);
        b.Operation(DocCreate, DocCreateAsync);
        b.Operation(DocUpdate, DocUpdateAsync);
        b.Operation(DocRemove, DocRemoveAsync);
        b.Operation(DocRestore, DocRestoreAsync);

        b.Operation(TemplateList, TemplateListAsync);
        b.Operation(TemplateRead, TemplateReadAsync);
        b.Operation(TemplateCreate, TemplateCreateAsync);
        b.Operation(TemplateUpdate, TemplateUpdateAsync);
        b.Operation(TemplateDelete, TemplateDeleteAsync);
        b.Operation(TemplateRemove, TemplateRemoveAsync);
        b.Operation(TemplateRestore, TemplateRestoreAsync);
        b.Operation(TemplateRecreate, TemplateRecreateAsync);

        b.Operation(UploadList, UploadListAsync);
        b.Operation(UploadUpdate, UploadUpdateAsync);
        b.Operation(UploadRestore, UploadRestoreAsync);
    }

    private const string AdminRoot = "/admin/p/verein/";

    // ---- the specs ------------------------------------------------------------------------------------------------

    public static readonly PluginOperationSpec RoleList = new("role.list",
        "Rollen des Vereins (mit Vorstand ja/nein), eigene Felder (Hexe-Nr. …), erlaubte Zielgruppen (audience) und Arten von Sitzungen, Dokumenten und Vorlagen – zum Ausfüllen der anderen Aktionen.",
        "{}", Writes: false);

    public static readonly PluginOperationSpec MemberList = new("member.list",
        "Mitglieder auflisten (personenbezogen): id, Name, Rollen, E-Mail, Telefon, Mitglied seit, Kind, passiv, ehemalig, Zugang. status: \"aktiv\" (Standard: alle, die noch im Verein sind), \"erwachsene\", \"kinder\", \"passiv\", \"ohne-zugang\", \"ehemalige\", \"alle\". role: nur diese Rolle. search: Name, Ort, Telefon, eigene Felder.",
        """{"status"?: "aktiv", "role"?: "Kassierer/in", "search"?: "text", "limit"?: 100}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec MemberRead = new("member.read",
        "Ein Mitglied vollständig lesen (personenbezogen): Kontakt, Adresse, Geburtsdatum, Rollen, eigene Felder, Eltern bzw. Kinder, Zugang ja/nein. Passwort und geheimer Kalender-Link stehen nie darin.",
        """{"id": 7}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec MemberCreate = new("member.create",
        "Person in die Mitgliederliste eintragen (personenbezogen) – immer OHNE Zugang zur Website: kein Konto, keine Einladung, keine E-Mail. Den Zugang richtet der Vorstand im Admin ein (Mitglied → „Zugang einrichten“). roles: Liste oder \"A, B\" (ohne Angabe die letzte Rolle der Einstellung, meist „Mitglied“). fields: eigene Felder nach Namen (siehe role.list). child: true = Kind ohne eigene Adresse/E-Mail, guardians = ids der Eltern (Pflicht). Datum als 2027-01-09.",
        """{"first": "Anna", "last": "Muster", "email"?: "anna@example.org", "phone"?: "0171 …", "roles"?: ["Mitglied"], "joined"?: "2020-03-01", "born"?: "1990-05-17", "street"?: "…", "zip"?: "74321", "city"?: "…", "note"?: "…", "passive"?: false, "fields"?: {"Hexe-Nr.": "12"}, "child"?: false, "guardians"?: [3]}""",
        PersonalData: true);
    public static readonly PluginOperationSpec MemberUpdate = new("member.update",
        "Ein Mitglied ändern (personenbezogen); nicht genannte Felder bleiben. active: false = ausgetreten/ehemalig (bleibt in der Liste, bekommt nichts mehr; Grund in die erste Zeile von note), true = wieder Mitglied. passive: Fördermitglied. Die E-Mail eines Mitglieds MIT Zugang ist die Adresse seines Kontos und bleibt hier unverändert. Es geht keine E-Mail raus.",
        """{"id": 7, "first"?, "last"?, "email"?, "phone"?, "roles"?, "joined"?, "born"?, "street"?, "zip"?, "city"?, "note"?, "active"?: true, "passive"?: false, "fields"?: {}, "guardians"?: []}""",
        PersonalData: true);
    public static readonly PluginOperationSpec MemberRemove = new("member.remove", "Entfernt eine eben eingetragene Person (Rückgängig).", PersonalData: true, Internal: true);
    public static readonly PluginOperationSpec MemberRestore = new("member.restore", "Stellt eine Person aus einer Rückgängig-Aufzeichnung wieder her.", PersonalData: true, Internal: true);

    public static readonly PluginOperationSpec MeetingList = new("meeting.list",
        "Sitzungen auflisten (Vorstandssitzung, Mitgliederversammlung …): id, Titel, Art, Termin, Ort, Eingeladene (audience), Status (planned/held/cancelled), Protokoll (draft/final), Zu-/Absagen als Zahl. upcoming: nur kommende.",
        """{"upcoming"?: false, "search"?: "text", "limit"?: 50}""", Writes: false);
    public static readonly PluginOperationSpec MeetingRead = new("meeting.read", "Eine Sitzung vollständig lesen: Tagesordnung, Protokoll, wie viele eingeladen sind und zu-/abgesagt haben.", """{"id": 3}""", Writes: false);
    public static readonly PluginOperationSpec MeetingCreate = new("meeting.create",
        "Neue Sitzung anlegen. starts: \"2027-03-14T19:30\". kind: eine der Arten aus role.list (Standard Vorstandssitzung). audience: wer eingeladen ist (Standard board = Vorstand; all, active, role:Name). agenda: Tagesordnung als Text. Es geht keine Einladung raus – die verschickt der Vorstand im Admin.",
        """{"title": "Vorstandssitzung März", "starts": "2027-03-14T19:30", "kind"?: "Vorstandssitzung", "place"?: "Vereinsheim", "audience"?: "board", "agenda"?: "1. Begrüßung\n2. …"}""");
    public static readonly PluginOperationSpec MeetingUpdate = new("meeting.update",
        "Eine Sitzung ändern; nicht genannte Felder bleiben. status: planned, held (stattgefunden) oder cancelled (abgesagt). minutes: Protokoll als Text, minutes_status: draft (nur im Admin) oder final (für die Eingeladenen sichtbar; eine geplante Sitzung gilt dann als stattgefunden). Es geht keine E-Mail raus.",
        """{"id": 3, "title"?, "kind"?, "starts"?, "place"?, "audience"?, "agenda"?, "status"?: "held", "minutes"?: "…", "minutes_status"?: "draft"}""");
    public static readonly PluginOperationSpec MeetingRemove = new("meeting.remove", "Entfernt eine eben angelegte Sitzung (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec MeetingRestore = new("meeting.restore", "Stellt eine Sitzung aus einer Rückgängig-Aufzeichnung wieder her.", Internal: true);

    public static readonly PluginOperationSpec DocList = new("doc.list",
        "Dokumente zum Bestätigen, Unterschreiben oder Lesen auflisten: id, Titel, Art (confirm/sign/read), Zielgruppe, Frist, wie viele geantwortet haben. archived: true = das Archiv.",
        """{"archived"?: false}""", Writes: false);
    public static readonly PluginOperationSpec DocRead = new("doc.read", "Ein Dokument lesen: Text, Art, Zielgruppe, Frist, Anhang, Zahl der Antworten (ohne Namen).", """{"id": 4}""", Writes: false);
    public static readonly PluginOperationSpec DocCreate = new("doc.create",
        "Neues Dokument für die Mitglieder veröffentlichen (steht sofort im Mitgliederbereich). kind: confirm (Haken), sign (Name eintippen) oder read (nur lesen). audience: all, active, board oder role:Name. due: Frist 2027-02-01. Es geht KEINE E-Mail raus – benachrichtigen per „Offene erinnern“ im Admin.",
        """{"title": "Datenschutz-Einwilligung 2027", "body": "Text …", "kind"?: "confirm", "audience"?: "all", "due"?: "2027-02-01"}""");
    public static readonly PluginOperationSpec DocUpdate = new("doc.update",
        "Ein Dokument ändern; nicht genannte Felder bleiben. archived: true = ins Archiv (das „Ausschalten“), false = wieder aktiv. due: Frist (\"\" = ohne). Titel, Text, Art und Zielgruppe nur, solange noch niemand geantwortet hat – die Antworten sind der Nachweis.",
        """{"id": 4, "archived"?: true, "due"?: "2027-02-01", "title"?, "body"?, "kind"?, "audience"?}""");
    public static readonly PluginOperationSpec DocRemove = new("doc.remove", "Entfernt ein eben angelegtes Dokument ohne Antworten (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec DocRestore = new("doc.restore", "Stellt ein Dokument aus einer Rückgängig-Aufzeichnung wieder her.", Internal: true);

    public static readonly PluginOperationSpec TemplateList = new("template.list",
        "Vorlagen auflisten (Startinhalt für Dokumente, Tagesordnungen, Protokolle): id, Name, Art (document/agenda/minutes), Titel. kind: nur diese Art.",
        """{"kind"?: "agenda"}""", Writes: false);
    public static readonly PluginOperationSpec TemplateRead = new("template.read", "Eine Vorlage mit Text lesen (Platzhalter wie {{verein}}, {{datum}}, {{jahr}}, im Protokoll {{titel}}, {{termin}}, {{ort}}, {{tagesordnung}}).", """{"id": 2}""", Writes: false);
    public static readonly PluginOperationSpec TemplateCreate = new("template.create",
        "Neue Vorlage anlegen. kind: document (Dokument), agenda (Tagesordnung) oder minutes (Protokoll). Platzhalter siehe template.read.",
        """{"name": "Tagesordnung JHV", "kind"?: "agenda", "title"?: "Jahreshauptversammlung {{jahr}}", "body"?: "1. Begrüßung …"}""");
    public static readonly PluginOperationSpec TemplateUpdate = new("template.update", "Eine Vorlage ändern; nicht genannte Felder bleiben.",
        """{"id": 2, "name"?, "kind"?, "title"?, "body"?}""");
    public static readonly PluginOperationSpec TemplateDelete = new("template.delete", "Eine Vorlage löschen (bereits erstellte Dokumente und Sitzungen bleiben unberührt).", """{"id": 2}""");
    public static readonly PluginOperationSpec TemplateRemove = new("template.remove", "Entfernt eine eben angelegte Vorlage (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec TemplateRestore = new("template.restore", "Stellt eine geänderte Vorlage wieder her (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec TemplateRecreate = new("template.recreate", "Legt eine gelöschte Vorlage wieder an (Rückgängig).", Internal: true);

    public static readonly PluginOperationSpec UploadList = new("upload.list",
        "Bilder und Material, das Mitglieder hochgeladen haben (personenbezogen): id, Titel, Datei, Art (photo/material), von wem, Aufnahmedatum, Termin, freigegeben, für alle sichtbar. pending: nur die, die auf Freigabe warten.",
        """{"pending"?: false, "kind"?: "photo", "event_id"?: 12, "limit"?: 100}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec UploadUpdate = new("upload.update",
        "Einen Upload freigeben oder zuordnen (personenbezogen). approved: true = freigeben. shared: false = nur für den Uploader und den Vorstand (das „Ausschalten“), true = für alle Mitglieder. event_id: Termin zuordnen (0 = keiner). Löschen nur im Admin.",
        """{"id": 5, "approved"?: true, "shared"?: true, "event_id"?: 12}""", PersonalData: true);
    public static readonly PluginOperationSpec UploadRestore = new("upload.restore", "Stellt Freigabe, Sichtbarkeit und Termin eines Uploads wieder her (Rückgängig).", PersonalData: true, Internal: true);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    internal static JsonObject Answer(string status, string message, JsonNode? data = null, JsonNode? undo = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        if (undo is not null) o["undo"] = undo;
        return o;
    }

    private static JsonObject Undo(PluginOperationSpec op, JsonObject args) => new() { ["name"] = op.Name, ["args"] = args };

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : o.ContainsKey(key) && o[key] is null ? "" : null;

    private static long Long(JsonNode? o, string key)
    {
        if (o is not JsonObject obj || obj[key] is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 1e15) return (long)d;
        return v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    private static bool? Bool(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        return v.TryGetValue<string>(out var s) ? s.Trim().ToLowerInvariant() switch { "true" or "1" or "ja" or "yes" => true, "false" or "0" or "nein" or "no" => false, _ => null } : null;
    }

    private static int Limit(JsonNode? args, int def, int max)
    {
        var n = Long(args, "limit");
        return (int)Math.Clamp(n <= 0 ? def : n, 1, max);
    }

    /// <summary>A list given as a JSON array or as one string "A, B".</summary>
    private static List<string>? StrList(JsonObject o, string key) => o[key] switch
    {
        JsonArray a => a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x?.ToString() ?? "").ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split(',').ToList(),
        _ => null
    };

    private static List<long>? LongList(JsonObject o, string key) => o[key] switch
    {
        JsonArray a => a.Select(x => x is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) ? p : 0) : 0).ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split(',').Select(x => long.TryParse(x.Trim(), out var p) ? p : 0).ToList(),
        JsonValue v when v.TryGetValue<long>(out var l) => new List<long> { l },
        JsonValue v when v.TryGetValue<int>(out var i) => new List<long> { i },
        _ => null
    };

    /// <summary>A date argument: "" clears; anything else must be a date (2027-01-09) or a date with time (2027-01-09T14:30).</summary>
    private static bool Date(JsonObject o, string key, out string? value)
    {
        value = null;
        if (Str(o, key) is not string raw) return true;
        if (raw.Trim().Length == 0) { value = ""; return true; }
        value = V.CleanDate(raw);
        return value.Length > 0;
    }

    /// <summary>Roles cleaned as the admin's chips field cleans them.</summary>
    private static List<string> CleanRoles(IEnumerable<string> raw) =>
        raw.Select(r => V.Clip(r.Trim().Replace("|", "/"), 60)).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();

    private static IReadOnlyList<string> Audiences(IPluginContext ctx) =>
        new[] { VereinStore.AudienceAll, VereinStore.AudienceActive, VereinStore.AudienceBoard }.Concat(VereinSettings.RoleList(ctx).Select(r => "role:" + r)).ToList();

    /// <summary>An audience as typed ("Vorstand", "role:kassierer/in" …) as the stored value, or null.</summary>
    private static string? Audience(IPluginContext ctx, string typed)
    {
        var t = typed.Trim();
        if (t.Equals("vorstand", StringComparison.OrdinalIgnoreCase)) t = VereinStore.AudienceBoard;
        if (t.Equals("alle", StringComparison.OrdinalIgnoreCase)) t = VereinStore.AudienceAll;
        if (t.Equals("aktive", StringComparison.OrdinalIgnoreCase)) t = VereinStore.AudienceActive;
        return Audiences(ctx).FirstOrDefault(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase));
    }

    private static string AudienceError(IPluginContext ctx) => "Unbekannte Zielgruppe – erlaubt: " + string.Join(", ", Audiences(ctx)) + ".";

    // ---- roles and fields -----------------------------------------------------------------------------------------

    private static Task<JsonNode?> RoleListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var board = VereinSettings.BoardList(ctx);
        var roles = new JsonArray();
        foreach (var r in VereinSettings.RoleList(ctx)) roles.Add(new JsonObject { ["name"] = r, ["board"] = board.Contains(r, StringComparer.OrdinalIgnoreCase) });
        var fields = new JsonArray();
        foreach (var f in VereinSettings.Fields(ctx)) fields.Add(new JsonObject { ["label"] = f.Label, ["for_all"] = f.ForAll });
        var data = new JsonObject
        {
            ["roles"] = roles, ["fields"] = fields,
            ["audiences"] = new JsonArray(Audiences(ctx).Select(a => (JsonNode?)a).ToArray()),
            ["meeting_kinds"] = new JsonArray(VereinAdmin.MeetingKinds.Select(k => (JsonNode?)k).ToArray()),
            ["doc_kinds"] = new JsonArray(VereinStore.Confirm, VereinStore.Sign, VereinStore.Read),
            ["template_kinds"] = new JsonArray(VereinStore.TplDocument, VereinStore.TplAgenda, VereinStore.TplMinutes)
        };
        return Task.FromResult<JsonNode?>(Answer("applied", roles.Count + " Rolle(n), " + fields.Count + " eigene(s) Feld(er).", data));
    }

    // ---- members ------------------------------------------------------------------------------------------------------

    /// <summary>Everything an operation may write about a person, starting from what is stored.</summary>
    private sealed class Draft
    {
        public string First = "", Last = "", Email = "", Phone = "", Joined = "", Born = "", Street = "", Zip = "", City = "", Note = "";
        public List<string> Roles = new();
        public bool Active = true, Passive;
        public Dictionary<string, string> Extra = new(StringComparer.Ordinal);
        public List<long> Guardians = new();

        public static Draft Of(Person p) => new()
        {
            First = p.First, Last = p.Last, Email = p.Email, Phone = p.Phone, Joined = p.Joined, Born = p.Born, Street = p.Street, Zip = p.Zip, City = p.City,
            Note = p.Note, Roles = p.Roles.ToList(), Active = p.Active, Passive = p.Passive, Extra = new Dictionary<string, string>(p.Extra, StringComparer.Ordinal),
            Guardians = p.Guardians.ToList()
        };

        public string Name => (First + " " + Last).Trim();
    }

    /// <summary>Lays the arguments over the draft, cleaned like the admin's forms. Answers an error, or null; notes what was left out.</summary>
    private static string? Overlay(IPluginContext ctx, Draft d, JsonObject o, bool child, bool hasAccount, IReadOnlyList<Person> people, long selfId, List<string> notes)
    {
        if (Str(o, "first") is string first) d.First = V.Clip(first, 80);
        if (Str(o, "last") is string last) d.Last = V.Clip(last, 80);
        if (Str(o, "email") is string email)
        {
            if (child) notes.Add("Ein Kind hat keine E-Mail – ignoriert.");
            else if (hasAccount) { if (!string.Equals(email.Trim(), d.Email, StringComparison.OrdinalIgnoreCase)) notes.Add("Die E-Mail eines Mitglieds mit Zugang ist die seines Kontos – unverändert."); }
            else d.Email = V.Clip(email, 200).ToLowerInvariant();
        }
        if (Str(o, "phone") is string phone) { if (child) notes.Add("Ein Kind hat kein Telefon – ignoriert."); else d.Phone = V.Clip(phone, 40); }
        if (Str(o, "street") is string street) { if (!child) d.Street = V.Clip(street, 120); }
        if (Str(o, "zip") is string zip) { if (!child) d.Zip = V.Clip(zip, 10); }
        if (Str(o, "city") is string city) { if (!child) d.City = V.Clip(city, 80); }
        if (Str(o, "note") is string note) d.Note = V.Clip(note, 2000);
        if (!Date(o, "joined", out var joined)) return "„joined“ ist kein Datum (2020-03-01).";
        if (joined is not null) d.Joined = joined.Length >= 10 ? joined[..10] : joined;
        if (!Date(o, "born", out var born)) return "„born“ ist kein Datum (1990-05-17).";
        if (born is not null) d.Born = born.Length >= 10 ? born[..10] : born;
        if (StrList(o, "roles") is List<string> roles) d.Roles = CleanRoles(roles);
        if (Bool(o, "active") is bool active) d.Active = active;
        if (Bool(o, "passive") is bool passive) d.Passive = passive;
        if (o["fields"] is JsonObject fields)
        {
            var declared = VereinSettings.Fields(ctx);
            foreach (var (k, v) in fields)
            {
                var f = declared.FirstOrDefault(x => string.Equals(x.Label, k.Trim(), StringComparison.OrdinalIgnoreCase));
                if (f is null) { notes.Add("Feld „" + k + "“ gibt es nicht – ignoriert."); continue; }
                var value = V.Clip(v is JsonValue jv ? (jv.TryGetValue<string>(out var s) ? s : jv.ToString()) : "", 200);
                if (value.Length == 0) d.Extra.Remove(f.Label); else d.Extra[f.Label] = value;
            }
        }
        if (LongList(o, "guardians") is List<long> guardians)
        {
            if (!child) notes.Add("Eltern gibt es nur bei einem Kind – ignoriert.");
            else
            {
                var adults = people.Where(p => !p.Child && p.Id != selfId).Select(p => p.Id).ToHashSet();
                var unknown = guardians.Where(g => !adults.Contains(g)).ToList();
                if (unknown.Count > 0) return "Eltern müssen erwachsene Personen des Vereins sein – unbekannt: " + string.Join(", ", unknown) + ".";
                d.Guardians = guardians.Distinct().ToList();
            }
        }
        if (d.First.Length == 0 && d.Last.Length == 0) return "Bitte einen Namen angeben (first/last).";
        if (child && d.First.Length == 0) return "Ein Kind braucht einen Vornamen.";
        if (child && d.Guardians.Count == 0) return "Ein Kind braucht mindestens einen Elternteil (guardians = ids aus member.list).";
        return null;
    }

    private static async Task<long> SaveAsync(VereinStore store, long id, long memberId, bool child, Draft d, CancellationToken ct)
    {
        var nid = child
            ? await store.SaveChildAsync(id, d.First, d.Last, d.Born, d.Roles, d.Joined, d.Active, d.Note, d.Guardians, ct)
            : await store.SavePersonAsync(id, memberId, d.First, d.Last, d.Email, d.Phone, d.Roles, d.Joined, d.Active, d.Note, ct);
        await store.SaveDetailsAsync(nid, d.Born, child ? "" : d.Street, child ? "" : d.Zip, child ? "" : d.City, d.Extra, ct);
        await store.SetPassiveAsync(nid, d.Passive, ct);
        return nid;
    }

    /// <summary>The record an undo puts back (never the calendar token or the photo file — those are not changed by an operation).</summary>
    private static JsonObject Snapshot(Person p)
    {
        var fields = new JsonObject();
        foreach (var (k, v) in p.Extra) fields[k] = v;
        return new JsonObject
        {
            ["id"] = p.Id, ["first"] = p.First, ["last"] = p.Last, ["email"] = p.Email, ["phone"] = p.Phone,
            ["roles"] = new JsonArray(p.Roles.Select(r => (JsonNode?)r).ToArray()), ["joined"] = p.Joined, ["born"] = p.Born,
            ["street"] = p.Street, ["zip"] = p.Zip, ["city"] = p.City, ["note"] = p.Note, ["active"] = p.Active, ["passive"] = p.Passive,
            ["fields"] = fields, ["guardians"] = new JsonArray(p.Guardians.Select(g => (JsonNode?)g).ToArray())
        };
    }

    private static string ShownEmail(Person p) => p.Child || VereinMail.Parkable(p.Email) ? "" : p.Email;

    private static readonly (string Key, Func<Person, bool> Fits)[] Status =
    {
        ("aktiv", p => p.Active), ("erwachsene", p => p.Active && !p.Child), ("kinder", p => p.Active && p.Child), ("passiv", p => p.Active && p.Passive),
        ("ohne-zugang", p => p.Active && !p.Child && !p.HasAccount), ("ehemalige", p => !p.Active), ("inaktiv", p => !p.Active), ("alle", _ => true)
    };

    private static async Task<JsonNode?> MemberListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var statusKey = (Str(o, "status") ?? "aktiv").Trim().ToLowerInvariant();
        var status = Status.FirstOrDefault(s => s.Key == statusKey);
        if (status.Key is null) return Answer("failed", "Unbekannter status – erlaubt: aktiv, erwachsene, kinder, passiv, ohne-zugang, ehemalige, alle.");
        var role = (Str(o, "role") ?? "").Trim();
        var search = (Str(o, "search") ?? "").Trim().ToLowerInvariant();
        var people = await new VereinStore(ctx.Data).PeopleAsync(ct);
        var list = people.Where(status.Fits)
            .Where(p => role.Length == 0 || p.Has(role))
            .Where(p => search.Length == 0 || (p.Name + " " + p.Email + " " + p.Phone + " " + p.City + " " + string.Join(" ", p.Roles) + " " + string.Join(" ", p.Extra.Values)).ToLowerInvariant().Contains(search))
            .ToList();
        var arr = new JsonArray();
        foreach (var p in list.Take(Limit(args, 100, 1000)))
            arr.Add(new JsonObject
            {
                ["id"] = p.Id, ["name"] = p.Name, ["roles"] = new JsonArray(p.Roles.Select(r => (JsonNode?)r).ToArray()), ["email"] = ShownEmail(p), ["phone"] = p.Phone,
                ["joined"] = p.Joined, ["child"] = p.Child, ["passive"] = p.Passive, ["active"] = p.Active, ["has_account"] = p.HasAccount
            });
        return Answer("applied", list.Count + " Person(en)" + (arr.Count < list.Count ? ", die ersten " + arr.Count + "." : "."), arr);
    }

    private static async Task<JsonNode?> MemberReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        if (await store.PersonAsync(Long(args, "id"), ct) is not Person p) return Answer("failed", "Person nicht gefunden.");
        var people = await store.PeopleAsync(ct);
        var byId = people.ToDictionary(x => x.Id);
        var fields = new JsonObject();
        foreach (var (k, v) in p.Extra) fields[k] = v;
        var data = new JsonObject
        {
            ["id"] = p.Id, ["first"] = p.First, ["last"] = p.Last, ["name"] = p.Name, ["email"] = ShownEmail(p), ["phone"] = p.Phone,
            ["roles"] = new JsonArray(p.Roles.Select(r => (JsonNode?)r).ToArray()), ["joined"] = p.Joined, ["born"] = p.Born, ["age"] = VereinAdmin.Age(p.Born),
            ["street"] = p.Street, ["zip"] = p.Zip, ["city"] = p.City, ["note"] = p.Note, ["active"] = p.Active, ["passive"] = p.Passive, ["child"] = p.Child,
            ["has_account"] = p.HasAccount, ["has_photo"] = p.Photo.Length > 0, ["fields"] = fields,
            ["guardians"] = new JsonArray(p.Guardians.Where(byId.ContainsKey).Select(g => (JsonNode?)new JsonObject { ["id"] = g, ["name"] = byId[g].Name }).ToArray()),
            ["children"] = new JsonArray(VereinStore.ChildrenOf(people, p).Select(c => (JsonNode?)new JsonObject { ["id"] = c.Id, ["name"] = c.Name }).ToArray()),
            ["admin_url"] = AdminRoot + (p.Child ? "children/" : "members/") + p.Id
        };
        return Answer("applied", (p.Child ? "Kind " : "Mitglied ") + "„" + p.Name + "“.", data);
    }

    private static async Task<JsonNode?> MemberCreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var store = new VereinStore(ctx.Data);
        var people = await store.PeopleAsync(ct);
        var child = Bool(o, "child") == true;
        var roles = VereinSettings.RoleList(ctx);
        var d = new Draft { Roles = roles.Count > 0 ? new List<string> { roles[^1] } : new List<string>() };
        var notes = new List<string>();
        if (Overlay(ctx, d, o, child, hasAccount: false, people, 0, notes) is string error) return Answer("failed", error);
        d.Active = true;   // a new entry is a member — "ehemalig" is a later change
        var same = people.FirstOrDefault(p => p.Child == child && string.Equals(p.Name, d.Name, StringComparison.OrdinalIgnoreCase)
            && (p.Born.Length == 0 || d.Born.Length == 0 || p.Born == d.Born));
        if (same is not null) return Answer("skipped-exists", "„" + d.Name + "“ steht schon in der Mitgliederliste.", new JsonObject { ["id"] = same.Id });
        var id = await SaveAsync(store, 0, VereinStore.Placeholder(), child, d, ct);
        ctx.Log($"{(child ? "Kind" : "Mitglied")} „{d.Name}“ (ohne Zugang) eingetragen über eine Operation");
        return Answer("applied", (child ? "Kind „" : "Mitglied „") + d.Name + "“ eingetragen (ohne Zugang, keine E-Mail)." + (notes.Count > 0 ? " " + string.Join(" ", notes) : ""),
            new JsonObject { ["id"] = id, ["admin_url"] = AdminRoot + (child ? "children/" : "members/") + id }, Undo(MemberRemove, new JsonObject { ["id"] = id }));
    }

    private static async Task<JsonNode?> MemberUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var id = Long(args, "id");
        if (args is not JsonObject o || id <= 0) return Answer("failed", "Bitte die id der Person angeben (member.list).");
        var store = new VereinStore(ctx.Data);
        if (await store.PersonAsync(id, ct) is not Person p) return Answer("failed", "Person nicht gefunden.");
        var d = Draft.Of(p);
        var notes = new List<string>();
        if (Overlay(ctx, d, o, p.Child, p.HasAccount, await store.PeopleAsync(ct), p.Id, notes) is string error) return Answer("failed", error);
        await SaveAsync(store, p.Id, p.MemberId, p.Child, d, ct);
        ctx.Log($"Mitglied „{d.Name}“ geändert über eine Operation");
        return Answer("applied", "„" + d.Name + "“ geändert." + (notes.Count > 0 ? " " + string.Join(" ", notes) : ""), new JsonObject { ["id"] = p.Id },
            Undo(MemberRestore, Snapshot(p)));
    }

    private static async Task<JsonNode?> MemberRemoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        if (await store.PersonAsync(Long(args, "id"), ct) is not Person p) return Answer("skipped", "Die Person gibt es nicht mehr.");
        // an entry that has an account by now was taken up by the board in the meantime — removing it is the board's decision (Admin → Mitglied)
        if (p.HasAccount) return Answer("skipped", "„" + p.Name + "“ hat inzwischen einen Zugang – entfernen nur im Admin.");
        if (p.Photo.Length > 0) await ctx.Files.DeleteAsync(p.Photo, ct);
        await store.People.DeleteAsync(p.Id, ct);
        return Answer("applied", "„" + p.Name + "“ wieder entfernt.");
    }

    private static async Task<JsonNode?> MemberRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var store = new VereinStore(ctx.Data);
        if (await store.PersonAsync(Long(o, "id"), ct) is not Person p) return Answer("skipped", "Die Person gibt es nicht mehr.");
        var d = Draft.Of(p);
        var notes = new List<string>();
        var people = await store.PeopleAsync(ct);
        if (Overlay(ctx, d, o, p.Child, p.HasAccount, people, p.Id, notes) is string error) return Answer("failed", error);
        // the snapshot holds every own field as it was — declared since or not; what it does not hold was empty before
        if (o["fields"] is JsonObject fs)
            d.Extra = fs.Where(kv => kv.Value is JsonValue jv && jv.TryGetValue<string>(out _)).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.Ordinal);
        await SaveAsync(store, p.Id, p.MemberId, p.Child, d, ct);
        return Answer("applied", "„" + d.Name + "“ wiederhergestellt.");
    }

    // ---- meetings -------------------------------------------------------------------------------------------------------

    private static readonly string[] MeetingStatuses = { VereinStore.Planned, VereinStore.Held, VereinStore.Cancelled };

    private static string? MeetingValues(IPluginContext ctx, JsonObject o, Dictionary<string, object?> v)
    {
        if (Str(o, "title") is string title) v["title"] = V.Clip(title, 160);
        if (Str(o, "kind") is string kind)
        {
            var k = kind.Trim().Length == 0 ? "" : VereinAdmin.MeetingKinds.FirstOrDefault(x => string.Equals(x, kind.Trim(), StringComparison.OrdinalIgnoreCase));
            if (k is null) return "Unbekannte Art – erlaubt: " + string.Join(", ", VereinAdmin.MeetingKinds) + ".";
            v["kind"] = k;
        }
        if (!Date(o, "starts", out var starts)) return "„starts“ ist kein Termin (2027-03-14T19:30).";
        if (starts is not null) v["starts"] = starts;
        if (Str(o, "place") is string place) v["place"] = V.Clip(place, 200);
        if (Str(o, "agenda") is string agenda) v["agenda"] = V.Clip(agenda, 20000);
        if (Str(o, "minutes") is string minutes) v["minutes"] = V.Clip(minutes, 60000);
        if (Str(o, "audience") is string audience)
        {
            if (Audience(ctx, audience) is not string a) return AudienceError(ctx);
            v["audience"] = a;
        }
        if (Str(o, "status") is string status)
        {
            if (!MeetingStatuses.Contains(status.Trim())) return "Unbekannter status – erlaubt: planned, held, cancelled.";
            v["status"] = status.Trim();
        }
        if (Str(o, "minutes_status") is string ms)
        {
            if (ms.Trim() is not (VereinStore.Draft or VereinStore.Final)) return "minutes_status ist draft oder final.";
            v["minutes_status"] = ms.Trim();
        }
        return null;
    }

    private static JsonObject MeetingFull(Meeting m) => new()
    {
        ["id"] = m.Id, ["title"] = m.Title, ["kind"] = m.Kind, ["starts"] = m.Starts, ["place"] = m.Place, ["agenda"] = m.Agenda, ["audience"] = m.Audience,
        ["status"] = m.Status, ["minutes"] = m.Minutes, ["minutes_status"] = m.MinutesStatus.Length > 0 ? m.MinutesStatus : VereinStore.Draft
    };

    private static async Task<JsonNode?> MeetingListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var upcoming = Bool(o, "upcoming") == true;
        var search = (Str(o, "search") ?? "").Trim().ToLowerInvariant();
        var store = new VereinStore(ctx.Data);
        var list = (await store.MeetingsAsync(ct))
            .Where(m => !upcoming || string.CompareOrdinal(m.Starts.Length >= 10 ? m.Starts[..10] : m.Starts, V.Today) >= 0)
            .Where(m => search.Length == 0 || (m.Title + " " + m.Kind + " " + m.Place).ToLowerInvariant().Contains(search))
            .Take(Limit(args, 50, 500)).ToList();
        var arr = new JsonArray();
        foreach (var m in list)
        {
            var r = await store.RsvpsAsync(m.Id, ct);
            arr.Add(new JsonObject
            {
                ["id"] = m.Id, ["title"] = m.Title, ["kind"] = m.Kind, ["starts"] = m.Starts, ["place"] = m.Place, ["audience"] = m.Audience, ["status"] = m.Status,
                ["minutes_status"] = m.Minutes.Length == 0 ? "" : m.MinutesStatus, ["yes"] = r.Count(x => x.Answer == VereinStore.Yes), ["no"] = r.Count(x => x.Answer == VereinStore.No)
            });
        }
        return Answer("applied", arr.Count + " Sitzung(en).", arr);
    }

    private static async Task<JsonNode?> MeetingReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        if (await store.MeetingAsync(Long(args, "id"), ct) is not Meeting m) return Answer("failed", "Sitzung nicht gefunden.");
        var r = await store.RsvpsAsync(m.Id, ct);
        var data = MeetingFull(m);
        data["invited"] = VereinStore.Audience(await store.PeopleAsync(ct), m.Audience, VereinSettings.BoardList(ctx)).Count();
        data["invited_at"] = m.InvitedAt?.ToString("o");
        data["yes"] = r.Count(x => x.Answer == VereinStore.Yes);
        data["no"] = r.Count(x => x.Answer == VereinStore.No);
        data["maybe"] = r.Count(x => x.Answer == VereinStore.Maybe);
        data["admin_url"] = AdminRoot + "meetings/" + m.Id;
        return Answer("applied", "Sitzung „" + m.Title + "“.", data);
    }

    private static async Task<JsonNode?> MeetingCreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var v = new Dictionary<string, object?>();
        if (MeetingValues(ctx, o, v) is string error) return Answer("failed", error);
        if (v.GetValueOrDefault("title") is not string { Length: >= 3 } title) return Answer("failed", "Bitte einen Titel angeben (mindestens 3 Zeichen).");
        if (v.GetValueOrDefault("starts") is not string { Length: >= 10 } starts) return Answer("failed", "Bitte den Termin angeben (2027-03-14T19:30).");
        var store = new VereinStore(ctx.Data);
        var same = (await store.MeetingsAsync(ct)).FirstOrDefault(m => string.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase) && m.Starts == starts);
        if (same is not null) return Answer("skipped-exists", "Die Sitzung „" + title + "“ gibt es schon.", new JsonObject { ["id"] = same.Id });
        v.TryAdd("kind", VereinAdmin.MeetingKinds[0]);
        v.TryAdd("place", "");
        v.TryAdd("agenda", "");
        v.TryAdd("audience", VereinStore.AudienceBoard);
        v.TryAdd("status", VereinStore.Planned);
        v.TryAdd("minutes", "");
        v.TryAdd("minutes_status", VereinStore.Draft);
        if (v["minutes_status"] as string == VereinStore.Final && v["status"] as string == VereinStore.Planned && !o.ContainsKey("status")) v["status"] = VereinStore.Held;
        v["created"] = DateTime.UtcNow;
        var id = await store.Meetings.InsertAsync(v, ct);
        ctx.Log($"Sitzung #{id} „{title}“ angelegt über eine Operation");
        return Answer("applied", "Sitzung „" + title + "“ angelegt (keine Einladung verschickt).", new JsonObject { ["id"] = id, ["admin_url"] = AdminRoot + "meetings/" + id },
            Undo(MeetingRemove, new JsonObject { ["id"] = id }));
    }

    private static async Task<JsonNode?> MeetingUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var id = Long(args, "id");
        if (args is not JsonObject o || id <= 0) return Answer("failed", "Bitte die id der Sitzung angeben.");
        var store = new VereinStore(ctx.Data);
        if (await store.MeetingAsync(id, ct) is not Meeting m) return Answer("failed", "Sitzung nicht gefunden.");
        var v = new Dictionary<string, object?>();
        if (MeetingValues(ctx, o, v) is string error) return Answer("failed", error);
        if (v.TryGetValue("title", out var t) && t is string { Length: < 3 }) return Answer("failed", "Der Titel braucht mindestens 3 Zeichen.");
        if (v.TryGetValue("starts", out var s) && s is "") return Answer("failed", "Eine Sitzung braucht einen Termin.");
        if (v.Count == 0) return Answer("skipped", "Nichts zu ändern.");
        // as in the admin: publishing the minutes of a planned meeting means it took place
        if (v.GetValueOrDefault("minutes_status") as string == VereinStore.Final && m.Status == VereinStore.Planned && !v.ContainsKey("status")) v["status"] = VereinStore.Held;
        await store.Meetings.UpdateAsync(m.Id, v, ct);
        return Answer("applied", "Sitzung „" + m.Title + "“ geändert (keine E-Mail verschickt).", new JsonObject { ["id"] = m.Id }, Undo(MeetingRestore, MeetingFull(m)));
    }

    private static async Task<JsonNode?> MeetingRemoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        if (await store.MeetingAsync(Long(args, "id"), ct) is not Meeting m) return Answer("skipped", "Die Sitzung gibt es nicht mehr.");
        await store.Rsvps.DeleteWhereAsync(DataQuery.Everything.Eq("meeting_id", m.Id), ct);
        await store.Meetings.DeleteAsync(m.Id, ct);
        return Answer("applied", "Sitzung entfernt.");
    }

    private static async Task<JsonNode?> MeetingRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var store = new VereinStore(ctx.Data);
        if (await store.MeetingAsync(Long(o, "id"), ct) is not Meeting m) return Answer("skipped", "Die Sitzung gibt es nicht mehr.");
        var v = new Dictionary<string, object?>();
        if (MeetingValues(ctx, o, v) is string error) return Answer("failed", error);
        if (v.Count > 0) await store.Meetings.UpdateAsync(m.Id, v, ct);
        return Answer("applied", "Sitzung wiederhergestellt.");
    }

    // ---- documents ------------------------------------------------------------------------------------------------------

    private static string? DocValues(IPluginContext ctx, JsonObject o, Dictionary<string, object?> v)
    {
        if (Str(o, "title") is string title) v["title"] = V.Clip(title, 160);
        if (Str(o, "body") is string body) v["body"] = V.Clip(body, 30000);
        if (Str(o, "kind") is string kind)
        {
            if (kind.Trim() is not (VereinStore.Confirm or VereinStore.Sign or VereinStore.Read)) return "kind ist confirm, sign oder read.";
            v["kind"] = kind.Trim();
        }
        if (Str(o, "audience") is string audience)
        {
            if (Audience(ctx, audience) is not string a) return AudienceError(ctx);
            v["audience"] = a;
        }
        if (!Date(o, "due", out var due)) return "„due“ ist kein Datum (2027-02-01).";
        if (due is not null) v["due"] = due.Length >= 10 ? due[..10] : due;
        if (Bool(o, "archived") is bool archived) v["archived"] = archived;
        return null;
    }

    private static JsonObject DocFull(Doc d) => new()
    {
        ["id"] = d.Id, ["title"] = d.Title, ["body"] = d.Body, ["kind"] = d.Kind, ["audience"] = d.Audience, ["due"] = d.Due, ["archived"] = d.Archived
    };

    private static async Task<(int Done, int Target)> DocCountsAsync(IPluginContext ctx, VereinStore store, Doc d, IReadOnlyList<Person> people, CancellationToken ct)
    {
        var target = VereinStore.Audience(people, d.Audience, VereinSettings.BoardList(ctx)).Select(p => p.MemberId).ToHashSet();
        var answers = await store.AnswersOfDocAsync(d.Id, ct);
        return (answers.Count(a => target.Contains(a.MemberId)), target.Count);
    }

    private static async Task<JsonNode?> DocListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var archived = args is JsonObject o && Bool(o, "archived") == true;
        var store = new VereinStore(ctx.Data);
        var people = await store.PeopleAsync(ct);
        var arr = new JsonArray();
        foreach (var d in await store.DocsAsync(archived, ct))
        {
            var (done, target) = await DocCountsAsync(ctx, store, d, people, ct);
            arr.Add(new JsonObject { ["id"] = d.Id, ["title"] = d.Title, ["kind"] = d.Kind, ["audience"] = d.Audience, ["due"] = d.Due, ["archived"] = d.Archived, ["created"] = d.Created?.ToString("o"), ["answered"] = done, ["of"] = target });
        }
        return Answer("applied", arr.Count + " Dokument(e)" + (archived ? " im Archiv." : "."), arr);
    }

    private static async Task<JsonNode?> DocReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        if (await store.DocAsync(Long(args, "id"), ct) is not Doc d) return Answer("failed", "Dokument nicht gefunden.");
        var (done, target) = await DocCountsAsync(ctx, store, d, await store.PeopleAsync(ct), ct);
        var data = DocFull(d);
        data["file_name"] = d.FileName;
        data["answered"] = done;
        data["of"] = target;
        data["answers_total"] = (await store.AnswersOfDocAsync(d.Id, ct)).Count;
        data["admin_url"] = AdminRoot + "docs/" + d.Id;
        return Answer("applied", "Dokument „" + d.Title + "“.", data);
    }

    private static async Task<JsonNode?> DocCreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var v = new Dictionary<string, object?>();
        if (DocValues(ctx, o, v) is string error) return Answer("failed", error);
        if (v.GetValueOrDefault("title") is not string { Length: >= 3 } title) return Answer("failed", "Bitte einen Titel angeben (mindestens 3 Zeichen).");
        var store = new VereinStore(ctx.Data);
        var same = (await store.DocsAsync(false, ct)).FirstOrDefault(d => string.Equals(d.Title, title, StringComparison.OrdinalIgnoreCase));
        if (same is not null) return Answer("skipped-exists", "Das Dokument „" + title + "“ gibt es schon.", new JsonObject { ["id"] = same.Id });
        var id = await store.InsertDocAsync(title, v.GetValueOrDefault("body") as string ?? "", v.GetValueOrDefault("kind") as string ?? VereinStore.Confirm,
            v.GetValueOrDefault("audience") as string ?? VereinStore.AudienceAll, v.GetValueOrDefault("due") as string ?? "", "Operation", ct);
        ctx.Log($"Dokument #{id} „{title}“ angelegt über eine Operation");
        return Answer("applied", "Dokument „" + title + "“ veröffentlicht (keine E-Mail verschickt).", new JsonObject { ["id"] = id, ["admin_url"] = AdminRoot + "docs/" + id },
            Undo(DocRemove, new JsonObject { ["id"] = id }));
    }

    private static async Task<JsonNode?> DocUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var id = Long(args, "id");
        if (args is not JsonObject o || id <= 0) return Answer("failed", "Bitte die id des Dokuments angeben.");
        var store = new VereinStore(ctx.Data);
        if (await store.DocAsync(id, ct) is not Doc d) return Answer("failed", "Dokument nicht gefunden.");
        var v = new Dictionary<string, object?>();
        if (DocValues(ctx, o, v) is string error) return Answer("failed", error);
        if (v.TryGetValue("title", out var t) && t is string { Length: < 3 }) return Answer("failed", "Der Titel braucht mindestens 3 Zeichen.");
        var answers = (await store.AnswersOfDocAsync(d.Id, ct)).Count;
        var proof = new[] { "title", "body", "kind", "audience" }.Where(k => v.ContainsKey(k) && !Equals(v[k], DocFull(d)[k]!.GetValue<string>())).ToList();
        if (answers > 0 && proof.Count > 0)
            return Answer("failed", "Auf „" + d.Title + "“ haben schon " + answers + " geantwortet – Titel, Text, Art und Zielgruppe bleiben (Nachweis). Ändern lassen sich Frist und Archiv; für einen neuen Text ein neues Dokument anlegen.");
        if (v.Count == 0) return Answer("skipped", "Nichts zu ändern.");
        await store.Docs.UpdateAsync(d.Id, v, ct);
        return Answer("applied", "Dokument „" + d.Title + "“ geändert" + (v.GetValueOrDefault("archived") is true ? " (archiviert)." : "."), new JsonObject { ["id"] = d.Id }, Undo(DocRestore, DocFull(d)));
    }

    private static async Task<JsonNode?> DocRemoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        if (await store.DocAsync(Long(args, "id"), ct) is not Doc d) return Answer("skipped", "Das Dokument gibt es nicht mehr.");
        if ((await store.AnswersOfDocAsync(d.Id, ct)).Count > 0) return Answer("skipped", "Auf „" + d.Title + "“ wurde schon geantwortet – es bleibt (Nachweis); archivieren statt löschen.");
        if (d.File.Length > 0) await ctx.Files.DeleteAsync(d.File, ct);
        await store.Docs.DeleteAsync(d.Id, ct);
        return Answer("applied", "Dokument entfernt.");
    }

    private static async Task<JsonNode?> DocRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var store = new VereinStore(ctx.Data);
        if (await store.DocAsync(Long(o, "id"), ct) is not Doc d) return Answer("skipped", "Das Dokument gibt es nicht mehr.");
        var v = new Dictionary<string, object?>();
        if (DocValues(ctx, o, v) is string error) return Answer("failed", error);
        // answered since: what the members answered to stays as they saw it
        if ((await store.AnswersOfDocAsync(d.Id, ct)).Count > 0) foreach (var k in new[] { "title", "body", "kind", "audience" }) v.Remove(k);
        if (v.Count > 0) await store.Docs.UpdateAsync(d.Id, v, ct);
        return Answer("applied", "Dokument wiederhergestellt.");
    }

    // ---- templates ------------------------------------------------------------------------------------------------------

    private static string? TemplateValues(JsonObject o, Dictionary<string, object?> v)
    {
        if (Str(o, "name") is string name) v["name"] = V.Clip(name, 120);
        if (Str(o, "kind") is string kind)
        {
            if (kind.Trim() is not (VereinStore.TplDocument or VereinStore.TplAgenda or VereinStore.TplMinutes)) return "kind ist document, agenda oder minutes.";
            v["kind"] = kind.Trim();
        }
        if (Str(o, "title") is string title) v["title"] = V.Clip(title, 160);
        if (Str(o, "body") is string body) v["body"] = V.Clip(body, 30000);
        if (v.TryGetValue("name", out var n) && n is string { Length: < 2 }) return "Der Name braucht mindestens 2 Zeichen.";
        return null;
    }

    private static JsonObject TemplateFull(Template t) => new() { ["id"] = t.Id, ["name"] = t.Name, ["kind"] = t.Kind, ["title"] = t.Title, ["body"] = t.Body };

    private static async Task<JsonNode?> TemplateListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var kind = args is JsonObject o && Str(o, "kind") is { Length: > 0 } k ? k.Trim() : null;
        var arr = new JsonArray();
        foreach (var t in await new VereinStore(ctx.Data).TemplatesAsync(kind, ct)) arr.Add(new JsonObject { ["id"] = t.Id, ["name"] = t.Name, ["kind"] = t.Kind, ["title"] = t.Title });
        return Answer("applied", arr.Count + " Vorlage(n).", arr);
    }

    private static async Task<JsonNode?> TemplateReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) =>
        await new VereinStore(ctx.Data).TemplateAsync(Long(args, "id"), ct) is Template t ? Answer("applied", "Vorlage „" + t.Name + "“.", TemplateFull(t)) : Answer("failed", "Vorlage nicht gefunden.");

    private static async Task<JsonNode?> TemplateCreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var v = new Dictionary<string, object?>();
        if (TemplateValues(o, v) is string error) return Answer("failed", error);
        if (v.GetValueOrDefault("name") is not string { Length: >= 2 } name) return Answer("failed", "Bitte einen Namen angeben.");
        v.TryAdd("kind", VereinStore.TplDocument);
        v.TryAdd("title", "");
        v.TryAdd("body", "");
        var store = new VereinStore(ctx.Data);
        var same = (await store.TemplatesAsync(ct: ct)).FirstOrDefault(t => t.Kind == (string)v["kind"]! && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (same is not null) return Answer("skipped-exists", "Die Vorlage „" + name + "“ gibt es schon.", new JsonObject { ["id"] = same.Id });
        v["created"] = DateTime.UtcNow;
        var id = await store.Templates.InsertAsync(v, ct);
        return Answer("applied", "Vorlage „" + name + "“ angelegt.", new JsonObject { ["id"] = id }, Undo(TemplateRemove, new JsonObject { ["id"] = id }));
    }

    private static async Task<JsonNode?> TemplateUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var id = Long(args, "id");
        if (args is not JsonObject o || id <= 0) return Answer("failed", "Bitte die id der Vorlage angeben.");
        var store = new VereinStore(ctx.Data);
        if (await store.TemplateAsync(id, ct) is not Template t) return Answer("failed", "Vorlage nicht gefunden.");
        var v = new Dictionary<string, object?>();
        if (TemplateValues(o, v) is string error) return Answer("failed", error);
        if (v.Count == 0) return Answer("skipped", "Nichts zu ändern.");
        await store.Templates.UpdateAsync(t.Id, v, ct);
        return Answer("applied", "Vorlage „" + t.Name + "“ geändert.", new JsonObject { ["id"] = t.Id }, Undo(TemplateRestore, TemplateFull(t)));
    }

    private static async Task<JsonNode?> TemplateDeleteAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        if (await store.TemplateAsync(Long(args, "id"), ct) is not Template t) return Answer("failed", "Vorlage nicht gefunden.");
        await store.Templates.DeleteAsync(t.Id, ct);
        var snapshot = TemplateFull(t);
        snapshot.Remove("id");
        return Answer("applied", "Vorlage „" + t.Name + "“ gelöscht.", null, Undo(TemplateRecreate, snapshot));
    }

    private static async Task<JsonNode?> TemplateRemoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        if (await store.TemplateAsync(Long(args, "id"), ct) is not Template t) return Answer("skipped", "Die Vorlage gibt es nicht mehr.");
        await store.Templates.DeleteAsync(t.Id, ct);
        return Answer("applied", "Vorlage entfernt.");
    }

    private static async Task<JsonNode?> TemplateRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var store = new VereinStore(ctx.Data);
        if (await store.TemplateAsync(Long(o, "id"), ct) is not Template t) return Answer("skipped", "Die Vorlage gibt es nicht mehr.");
        var v = new Dictionary<string, object?>();
        if (TemplateValues(o, v) is string error) return Answer("failed", error);
        if (v.Count > 0) await store.Templates.UpdateAsync(t.Id, v, ct);
        return Answer("applied", "Vorlage wiederhergestellt.");
    }

    private static async Task<JsonNode?> TemplateRecreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var v = new Dictionary<string, object?>();
        if (TemplateValues(o, v) is string error || v.GetValueOrDefault("name") is not string { Length: >= 2 }) return Answer("failed", "Die Vorlage lässt sich so nicht anlegen.");
        v.TryAdd("kind", VereinStore.TplDocument);
        v["created"] = DateTime.UtcNow;
        var id = await new VereinStore(ctx.Data).Templates.InsertAsync(v, ct);
        return Answer("applied", "Vorlage wieder angelegt.", new JsonObject { ["id"] = id });
    }

    // ---- uploads --------------------------------------------------------------------------------------------------------

    private static async Task<JsonNode?> UploadListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var pending = Bool(o, "pending") == true;
        var kind = Str(o, "kind") is { Length: > 0 } k ? k.Trim() : null;
        var eventId = Long(o, "event_id");
        var store = new VereinStore(ctx.Data);
        var names = (await store.PeopleAsync(ct)).Where(p => p.HasAccount).ToDictionary(p => p.MemberId, p => p.Name);
        var list = (await store.UploadsAsync(kind, ct)).Where(u => !pending || !u.Approved).Where(u => eventId <= 0 || u.EventId == eventId).ToList();
        var arr = new JsonArray();
        foreach (var u in list.Take(Limit(args, 100, 1000)))
            arr.Add(new JsonObject
            {
                ["id"] = u.Id, ["title"] = u.Title, ["file_name"] = u.FileName, ["kind"] = u.Kind, ["from"] = names.TryGetValue(u.MemberId, out var n) ? n : "",
                ["taken"] = u.Taken, ["created"] = u.Created?.ToString("o"), ["event_id"] = u.EventId, ["approved"] = u.Approved, ["shared"] = u.Shared,
                ["admin_url"] = AdminRoot + "uploads/" + u.Id
            });
        return Answer("applied", list.Count + " Upload(s)" + (pending ? ", die auf Freigabe warten." : "."), arr);
    }

    private static async Task<string?> UploadValuesAsync(VereinStore store, JsonObject o, Dictionary<string, object?> v, CancellationToken ct)
    {
        if (Bool(o, "approved") is bool approved) v["approved"] = approved;
        if (Bool(o, "shared") is bool shared) v["shared"] = shared;
        if (o.ContainsKey("event_id"))
        {
            var ev = Long(o, "event_id");
            if (ev < 0 || (ev > 0 && await store.EventAsync(ev, ct) is null)) return "Termin " + ev + " gibt es nicht (event.list; 0 = keiner).";
            v["event_id"] = ev;
        }
        return null;
    }

    private static async Task<JsonNode?> UploadUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var id = Long(args, "id");
        if (args is not JsonObject o || id <= 0) return Answer("failed", "Bitte die id des Uploads angeben.");
        var store = new VereinStore(ctx.Data);
        if (await store.UploadAsync(id, ct) is not Upload u) return Answer("failed", "Upload nicht gefunden.");
        var v = new Dictionary<string, object?>();
        if (await UploadValuesAsync(store, o, v, ct) is string error) return Answer("failed", error);
        if (v.Count == 0) return Answer("skipped", "Nichts zu ändern.");
        await store.Uploads.UpdateAsync(u.Id, v, ct);
        return Answer("applied", "Upload „" + (u.Title.Length > 0 ? u.Title : u.FileName) + "“ geändert.", new JsonObject { ["id"] = u.Id },
            Undo(UploadRestore, new JsonObject { ["id"] = u.Id, ["approved"] = u.Approved, ["shared"] = u.Shared, ["event_id"] = u.EventId }));
    }

    private static async Task<JsonNode?> UploadRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var store = new VereinStore(ctx.Data);
        if (await store.UploadAsync(Long(o, "id"), ct) is not Upload u) return Answer("skipped", "Den Upload gibt es nicht mehr.");
        var v = new Dictionary<string, object?>();
        // a restored event that is gone meanwhile leaves the upload without one
        if (await UploadValuesAsync(store, o, v, ct) is not null) v["event_id"] = 0L;
        if (v.Count > 0) await store.Uploads.UpdateAsync(u.Id, v, ct);
        return Answer("applied", "Upload wiederhergestellt.");
    }
}
