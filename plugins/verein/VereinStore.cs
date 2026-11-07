namespace MatCMS.Plugin.Verein;

/// <summary>
/// A person of the club: the club's own record about a site account (the account itself — login, password, e-mail confirmation — is the host's).
/// A CHILD has no account, address or mail of its own: it belongs to one or more guardians (other people of the club) and is there for the member list.
/// Its <see cref="MemberId"/> is a negative placeholder (the column is unique and no account has a negative id), so nothing that looks up an account
/// ever finds it; documents, invitations and meetings go to the guardians.
/// </summary>
public sealed record Person(long Id, long MemberId, string First, string Last, string Email, string Phone, IReadOnlyList<string> Roles, string Joined,
    bool Active, string Note, DateTime? Created, bool Child = false, IReadOnlyList<long>? GuardianIds = null, string Born = "")
{
    public string Name => (First + " " + Last).Trim();
    public bool Has(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<long> Guardians => GuardianIds ?? Array.Empty<long>();
}

/// <summary>A document the club sends its members: to confirm (a tick), to sign (the full name typed as signature) or only to read.</summary>
public sealed record Doc(long Id, string Title, string Body, string Kind, string Audience, string File, string FileName, string FileType, string Due,
    bool Archived, DateTime? Created, string CreatedBy);

/// <summary>A member's answer to a document — the proof: who, when, what was typed, from which address.</summary>
public sealed record Answer(long Id, long DocId, long MemberId, string Status, string Signature, string Ip, DateTime? At);

/// <summary>A photo or a file a member uploaded.</summary>
public sealed record Upload(long Id, long MemberId, string Kind, string Title, string File, string FileName, string FileType, long Size, bool Shared, DateTime? Created);

/// <summary>A meeting (board meeting, general assembly …) with its agenda and, afterwards, its minutes.</summary>
public sealed record Meeting(long Id, string Title, string Kind, string Starts, string Place, string Agenda, string Audience, string Status,
    string Minutes, string MinutesStatus, DateTime? Created, DateTime? InvitedAt);

/// <summary>A member's reply to a meeting invitation.</summary>
public sealed record Rsvp(long Id, long MeetingId, long MemberId, string Answer, DateTime? At);

/// <summary>An event of the club (a parade, a ball, a trip). Public ones may be shown on the website by the block „Kommende Termine“; internal ones only in the member area.</summary>
public sealed record Event(long Id, string Title, string Kind, string Starts, string Ends, string Place, string Description, bool Public, string Link, bool Cancelled, DateTime? Created,
    bool Draft = false, string Meeting = "", string Parking = "", string Notes = "", string Schedule = "")
{
    /// <summary>The day the event starts (yyyy-MM-dd), for ordering and „upcoming“.</summary>
    public string Day => Starts.Length >= 10 ? Starts[..10] : Starts;
    /// <summary>The planned running order: one "time|what" per line (Aufstellung, Zunftmeisterempfang …).</summary>
    public IReadOnlyList<(string Time, string What)> Steps => Schedule.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Split('|', 2)).Select(p => (p[0].Trim(), p.Length > 1 ? p[1].Trim() : "")).Where(p => p.Item1.Length + p.Item2.Length > 0).ToList();
    /// <summary>Anything beyond the basics a member should know.</summary>
    public bool HasPlan => Steps.Count > 0 || Meeting.Length > 0 || Parking.Length > 0 || Notes.Length > 0;
}

/// <summary>A template: the starting text of a document, an agenda or minutes.</summary>
public sealed record Template(long Id, string Name, string Kind, string Title, string Body, DateTime? Created);

/// <summary>
/// The plugin's data. Tables are declared (the host creates and extends them, never drops anything); every value goes in as a parameter.
/// Files (document attachments, uploads) are private plugin files, served only through the plugin's own routes after a check.
/// </summary>
public sealed class VereinStore(IPluginData data)
{
    public const string TPeople = "people", TDocs = "docs", TAnswers = "answers", TUploads = "uploads", TMeetings = "meetings", TRsvps = "rsvps", TTemplates = "templates", TEvents = "events", TMeta = "meta";

    // kinds and states (stored values — never renamed)
    public const string Confirm = "confirm", Sign = "sign", Read = "read";
    public const string Accepted = "accepted", Declined = "declined";
    public const string Photo = "photo", Material = "material";
    public const string Planned = "planned", Held = "held", Cancelled = "cancelled";
    public const string Draft = "draft", Final = "final";
    public const string Yes = "yes", No = "no", Maybe = "maybe";
    public const string AudienceAll = "all", AudienceBoard = "board";
    public const string TplDocument = "document", TplAgenda = "agenda", TplMinutes = "minutes";

    public static void Declare(IDataBuilder b)
    {
        b.Table(TPeople, t => t.Int("member_id", required: true).Text("first").Text("last").Text("email").Text("phone").Text("roles")
            .Text("joined").Bool("active", def: "1").Text("note").Timestamp("created")
            .Bool("child", def: "0").Text("guardians").Text("born").UniqueIndex("member_id"));
        b.Table(TDocs, t => t.Text("title", required: true).Text("body").Text("kind", required: true).Text("audience", required: true)
            .Text("file").Text("file_name").Text("file_type").Text("due").Bool("archived", def: "0").Timestamp("created").Text("created_by").Index("archived"));
        b.Table(TAnswers, t => t.Int("doc_id", required: true).Int("member_id", required: true).Text("status", required: true).Text("signature")
            .Text("ip").Timestamp("at").UniqueIndex("doc_id", "member_id").Index("member_id"));
        b.Table(TUploads, t => t.Int("member_id", required: true).Text("kind", required: true).Text("title").Text("file", required: true).Text("file_name")
            .Text("file_type").Int("size", def: "0").Bool("shared", def: "1").Timestamp("created").Index("member_id").Index("kind"));
        b.Table(TMeetings, t => t.Text("title", required: true).Text("kind").Text("starts").Text("place").Text("agenda").Text("audience", required: true)
            .Text("status", required: true).Text("minutes").Text("minutes_status").Timestamp("created").Timestamp("invited_at").Index("starts"));
        b.Table(TRsvps, t => t.Int("meeting_id", required: true).Int("member_id", required: true).Text("answer", required: true).Timestamp("at")
            .UniqueIndex("meeting_id", "member_id"));
        b.Table(TEvents, t => t.Text("title", required: true).Text("kind").Text("starts", required: true).Text("ends").Text("place").Text("description")
            .Bool("public", def: "1").Text("link").Bool("cancelled", def: "0").Timestamp("created")
            .Bool("draft", def: "0").Text("meeting").Text("parking").Text("notes").Text("schedule").Index("starts"));
        b.Table(TMeta, t => t.Text("name", required: true).Text("value").UniqueIndex("name"));
        b.Table(TTemplates, t => t.Text("name", required: true).Text("kind", required: true).Text("title").Text("body").Timestamp("created").Index("kind"));
    }

    public IDataTable People => data.Table(TPeople);
    public IDataTable Docs => data.Table(TDocs);
    public IDataTable Answers => data.Table(TAnswers);
    public IDataTable Uploads => data.Table(TUploads);
    public IDataTable Meetings => data.Table(TMeetings);
    public IDataTable Rsvps => data.Table(TRsvps);
    public IDataTable Templates => data.Table(TTemplates);
    public IDataTable Events => data.Table(TEvents);
    public IDataTable Meta => data.Table(TMeta);

    // ---- mapping ------------------------------------------------------------------------------------------------

    public static IReadOnlyList<string> SplitRoles(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static Person P(DataRow r) => new(r.Id, r.Int("member_id"), r.Text("first"), r.Text("last"), r.Text("email"), r.Text("phone"),
        SplitRoles(r.Text("roles")), r.Text("joined"), r.Bool("active"), r.Text("note"), r.Timestamp("created"), r.Bool("child"),
        r.Text("guardians").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var g) ? g : 0).Where(g => g > 0).Distinct().ToList(), r.Text("born"));
    private static Doc D(DataRow r) => new(r.Id, r.Text("title"), r.Text("body"), r.Text("kind"), r.Text("audience"), r.Text("file"), r.Text("file_name"),
        r.Text("file_type"), r.Text("due"), r.Bool("archived"), r.Timestamp("created"), r.Text("created_by"));
    private static Answer A(DataRow r) => new(r.Id, r.Int("doc_id"), r.Int("member_id"), r.Text("status"), r.Text("signature"), r.Text("ip"), r.Timestamp("at"));
    private static Upload U(DataRow r) => new(r.Id, r.Int("member_id"), r.Text("kind"), r.Text("title"), r.Text("file"), r.Text("file_name"), r.Text("file_type"),
        r.Int("size"), r.Bool("shared"), r.Timestamp("created"));
    private static Meeting M(DataRow r) => new(r.Id, r.Text("title"), r.Text("kind"), r.Text("starts"), r.Text("place"), r.Text("agenda"), r.Text("audience"),
        r.Text("status"), r.Text("minutes"), r.Text("minutes_status"), r.Timestamp("created"), r.Timestamp("invited_at"));
    private static Rsvp R(DataRow r) => new(r.Id, r.Int("meeting_id"), r.Int("member_id"), r.Text("answer"), r.Timestamp("at"));
    private static Event E(DataRow r) => new(r.Id, r.Text("title"), r.Text("kind"), r.Text("starts"), r.Text("ends"), r.Text("place"), r.Text("description"),
        r.Bool("public"), r.Text("link"), r.Bool("cancelled"), r.Timestamp("created"), r.Bool("draft"), r.Text("meeting"), r.Text("parking"), r.Text("notes"), r.Text("schedule"));
    private static Template T(DataRow r) => new(r.Id, r.Text("name"), r.Text("kind"), r.Text("title"), r.Text("body"), r.Timestamp("created"));

    // ---- people -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Person>> PeopleAsync(CancellationToken ct = default) =>
        (await People.QueryAsync(DataQuery.Everything.OrderBy("last").OrderBy("first"), ct)).Select(P).ToList();
    public async Task<Person?> PersonAsync(long id, CancellationToken ct = default) => await People.GetAsync(id, ct) is DataRow r ? P(r) : null;
    public async Task<Person?> PersonOfMemberAsync(long memberId, CancellationToken ct = default) =>
        await People.FindAsync(DataQuery.Everything.Eq("member_id", memberId), ct) is DataRow r ? P(r) : null;

    public async Task<long> SavePersonAsync(long id, long memberId, string first, string last, string email, string phone, IEnumerable<string> roles, string joined,
        bool active, string note, CancellationToken ct = default)
    {
        var v = new Dictionary<string, object?>
        {
            ["first"] = first, ["last"] = last, ["email"] = email, ["phone"] = phone, ["roles"] = string.Join(",", roles), ["joined"] = joined,
            ["active"] = active, ["note"] = note
        };
        if (id > 0) { await People.UpdateAsync(id, v, ct); return id; }
        v["member_id"] = memberId; v["created"] = DateTime.UtcNow;
        return await People.InsertAsync(v, ct);
    }

    /// <summary>A child: no account – a negative placeholder in member_id (unique, and no account ever has it), its guardians by their record ids.</summary>
    public async Task<long> SaveChildAsync(long id, string first, string last, string born, IEnumerable<string> roles, string joined, bool active, string note,
        IEnumerable<long> guardians, CancellationToken ct = default)
    {
        var v = new Dictionary<string, object?>
        {
            ["first"] = first, ["last"] = last, ["born"] = born, ["roles"] = string.Join(",", roles), ["joined"] = joined, ["active"] = active, ["note"] = note,
            ["guardians"] = string.Join(",", guardians.Distinct()), ["child"] = true, ["email"] = "", ["phone"] = ""
        };
        if (id > 0) { await People.UpdateAsync(id, v, ct); return id; }
        v["member_id"] = -(DateTime.UtcNow.Ticks / 10 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 1000));
        v["created"] = DateTime.UtcNow;
        return await People.InsertAsync(v, ct);
    }

    /// <summary>The children a person is guardian of.</summary>
    public static IEnumerable<Person> ChildrenOf(IEnumerable<Person> people, Person p) => people.Where(c => c.Child && c.Guardians.Contains(p.Id));

    /// <summary>The active people a document or meeting is meant for — never a child (it has no account; its guardians answer).</summary>
    public static IEnumerable<Person> Audience(IEnumerable<Person> people, string audience, IReadOnlyCollection<string> boardRoles) =>
        people.Where(p => p.Active && !p.Child && InAudience(p, audience, boardRoles));

    public static bool InAudience(Person p, string audience, IReadOnlyCollection<string> boardRoles)
    {
        if (audience == AudienceAll) return true;
        if (audience == AudienceBoard) return p.Roles.Any(r => boardRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
        return audience.StartsWith("role:", StringComparison.Ordinal) && p.Has(audience[5..]);
    }

    // ---- documents ----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Doc>> DocsAsync(bool archived, CancellationToken ct = default) =>
        (await Docs.QueryAsync(DataQuery.Everything.Eq("archived", archived).OrderBy("created", true), ct)).Select(D).ToList();
    public async Task<Doc?> DocAsync(long id, CancellationToken ct = default) => await Docs.GetAsync(id, ct) is DataRow r ? D(r) : null;

    public Task<long> InsertDocAsync(string title, string body, string kind, string audience, string due, string by, CancellationToken ct = default) =>
        Docs.InsertAsync(new Dictionary<string, object?>
        {
            ["title"] = title, ["body"] = body, ["kind"] = kind, ["audience"] = audience, ["due"] = due, ["archived"] = false, ["created"] = DateTime.UtcNow, ["created_by"] = by
        }, ct);

    public async Task<IReadOnlyList<Answer>> AnswersOfDocAsync(long docId, CancellationToken ct = default) =>
        (await Answers.QueryAsync(DataQuery.Everything.Eq("doc_id", docId), ct)).Select(A).ToList();
    public async Task<IReadOnlyList<Answer>> AnswersOfMemberAsync(long memberId, CancellationToken ct = default) =>
        (await Answers.QueryAsync(DataQuery.Everything.Eq("member_id", memberId), ct)).Select(A).ToList();
    public async Task<IReadOnlyList<Answer>> AllAnswersAsync(CancellationToken ct = default) => (await Answers.QueryAsync(DataQuery.Everything, ct)).Select(A).ToList();

    /// <summary>Records an answer once: a document already answered keeps its first answer (that is the proof; a change goes through the club).</summary>
    public async Task<bool> AnswerAsync(long docId, long memberId, string status, string signature, string ip, CancellationToken ct = default)
    {
        if (await Answers.FindAsync(DataQuery.Everything.Eq("doc_id", docId).Eq("member_id", memberId), ct) is not null) return false;
        await Answers.InsertAsync(new Dictionary<string, object?>
        {
            ["doc_id"] = docId, ["member_id"] = memberId, ["status"] = status, ["signature"] = signature, ["ip"] = ip, ["at"] = DateTime.UtcNow
        }, ct);
        return true;
    }

    // ---- uploads ------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Upload>> UploadsAsync(string? kind = null, CancellationToken ct = default)
    {
        var q = DataQuery.Everything.OrderBy("created", true);
        if (kind is not null) q = q.Eq("kind", kind);
        return (await Uploads.QueryAsync(q, ct)).Select(U).ToList();
    }
    public async Task<Upload?> UploadAsync(long id, CancellationToken ct = default) => await Uploads.GetAsync(id, ct) is DataRow r ? U(r) : null;

    // ---- meetings -----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Meeting>> MeetingsAsync(CancellationToken ct = default) =>
        (await Meetings.QueryAsync(DataQuery.Everything.OrderBy("starts", true), ct)).Select(M).ToList();
    public async Task<Meeting?> MeetingAsync(long id, CancellationToken ct = default) => await Meetings.GetAsync(id, ct) is DataRow r ? M(r) : null;
    public async Task<IReadOnlyList<Rsvp>> RsvpsAsync(long meetingId, CancellationToken ct = default) =>
        (await Rsvps.QueryAsync(DataQuery.Everything.Eq("meeting_id", meetingId), ct)).Select(R).ToList();
    public async Task<IReadOnlyList<Rsvp>> RsvpsOfMemberAsync(long memberId, CancellationToken ct = default) =>
        (await Rsvps.QueryAsync(DataQuery.Everything.Eq("member_id", memberId), ct)).Select(R).ToList();

    public async Task SetRsvpAsync(long meetingId, long memberId, string answer, CancellationToken ct = default)
    {
        var v = new Dictionary<string, object?> { ["answer"] = answer, ["at"] = DateTime.UtcNow };
        if (await Rsvps.FindAsync(DataQuery.Everything.Eq("meeting_id", meetingId).Eq("member_id", memberId), ct) is DataRow r) await Rsvps.UpdateAsync(r.Id, v, ct);
        else { v["meeting_id"] = meetingId; v["member_id"] = memberId; await Rsvps.InsertAsync(v, ct); }
    }

    // ---- events -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Event>> EventsAsync(CancellationToken ct = default) =>
        (await Events.QueryAsync(DataQuery.Everything.OrderBy("starts"), ct)).Select(E).ToList();
    public async Task<Event?> EventAsync(long id, CancellationToken ct = default) => await Events.GetAsync(id, ct) is DataRow r ? E(r) : null;

    /// <summary>Events from today on — never a draft (an event that started earlier but ends today or later counts too), soonest first.</summary>
    public async Task<IReadOnlyList<Event>> UpcomingAsync(string today, bool publicOnly, int max, CancellationToken ct = default) =>
        (await EventsAsync(ct)).Where(e => !e.Draft && (!publicOnly || e.Public) && string.CompareOrdinal(e.Ends.Length >= 10 ? e.Ends[..10] : e.Day, today) >= 0)
            .Take(Math.Max(1, max)).ToList();

    // ---- templates ----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Template>> TemplatesAsync(string? kind = null, CancellationToken ct = default)
    {
        var q = DataQuery.Everything.OrderBy("name");
        if (kind is not null) q = q.Eq("kind", kind);
        return (await Templates.QueryAsync(q, ct)).Select(T).ToList();
    }
    public async Task<Template?> TemplateAsync(long id, CancellationToken ct = default) => await Templates.GetAsync(id, ct) is DataRow r ? T(r) : null;

    // ---- an erased account ------------------------------------------------------------------------------------------

    /// <summary>
    /// The account is gone: the club's record of the person, their meeting replies and their uploads go with it. Answers to documents stay as a count
    /// (how many confirmed) but lose everything that names the person — the typed signature and the address.
    /// </summary>
    public async Task EraseMemberAsync(long memberId, IPluginFiles files, CancellationToken ct = default)
    {
        await People.DeleteWhereAsync(DataQuery.Everything.Eq("member_id", memberId), ct);
        await Rsvps.DeleteWhereAsync(DataQuery.Everything.Eq("member_id", memberId), ct);
        foreach (var u in (await Uploads.QueryAsync(DataQuery.Everything.Eq("member_id", memberId), ct)).Select(U))
        {
            await files.DeleteAsync(u.File, ct);
            await Uploads.DeleteAsync(u.Id, ct);
        }
        await Answers.UpdateWhereAsync(DataQuery.Everything.Eq("member_id", memberId), new Dictionary<string, object?> { ["signature"] = "", ["ip"] = "" }, ct);
    }
}
