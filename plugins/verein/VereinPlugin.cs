using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// Club management for a MatCMS site: the club's members (site accounts with the club's own record and roles), a member area under /mitglied, documents
/// to confirm or sign with the proof of who answered, meetings with invitations, replies and minutes, events (public ones as a block on the website),
/// templates, and photos and material the members upload.
/// <para>A native plugin, written against <c>MatCMS.Plugin.Abstractions</c> only — no host type, no reflection-based JSON over its own types.</para>
/// </summary>
public sealed class VereinPlugin : IPlugin
{
    public const string EventsBlock = "verein_events";

    /// <inheritdoc />
    public void Configure(IPluginBuilder b)
    {
        VereinStore.Declare(b.Data);
        VereinSettings.Declare(b.Settings);
        VereinInstagram.Declare(b.Settings);
        foreach (var spec in VereinMail.Specs) b.MailTemplate(spec);
        VereinPublic.Configure(b);
        VereinAdmin.Configure(b);

        b.Block(new BlockSpec(EventsBlock, "Vereinstermine", "Die kommenden öffentlichen Termine des Vereins (z. B. die nächsten Umzüge) – gepflegt unter Admin → Verein → Termine.")
        {
            Category = "design",
            IconSvg = "<rect x=\"3\" y=\"5\" width=\"18\" height=\"16\" rx=\"2\"/><path d=\"M3 10h18\"/><path d=\"M8 3v4\"/><path d=\"M16 3v4\"/>",
            Fields = new[]
            {
                new FieldSpec("heading", "Überschrift") { Placeholder = "Kommende Termine", Help = "Leer = die Überschrift aus den Einstellungen des Vereins." },
                new FieldSpec("intro", "Einleitung", FieldKind.Textarea),
                new FieldSpec("count", "Wie viele Termine", FieldKind.Select)
                {
                    Default = "5", Options = new[] { new FieldOption("3", "3"), new FieldOption("5", "5"), new FieldOption("8", "8"), new FieldOption("12", "12"), new FieldOption("50", "alle") }
                },
                new FieldSpec("kind", "Nur diese Art", FieldKind.Text) { Placeholder = "z. B. Umzug", Help = "Leer = alle Arten. Sonst nur Termine, deren Art so heißt." },
                new FieldSpec("layout", "Darstellung", FieldKind.Select) { Default = "list", Options = new[] { new FieldOption("list", "Liste"), new FieldOption("cards", "Karten") } },
                new FieldSpec("empty", "Text, wenn nichts ansteht") { Placeholder = "Gerade stehen keine Termine an." }
            }
        }, EventsBlockAsync);

        b.On(HostEvents.MemberErase, async (ctx, payload) =>
        {
            var id = IdOf(payload);
            if (id > 0) await new VereinStore(ctx.Data).EraseMemberAsync(id, ctx.Files);
        });
        b.On(HostEvents.MemberExport, ExportAsync);

        // A new club starts with templates to adapt — once: a marker row says it happened, so a deleted template stays deleted. On LOAD, not install:
        // a plugin that arrives with a restored backup counts as installed already and would never get them.
        b.Lifecycle.OnLoad(async ctx =>
        {
            var store = new VereinStore(ctx.Data);
            if (await store.Meta.FindAsync(DataQuery.Everything.Eq("name", "seeded"), default) is not null) return;
            if (await store.Templates.CountAsync(null) == 0)
                foreach (var (name, kind, title, body) in VereinSeed.Templates)
                    await store.Templates.InsertAsync(new Dictionary<string, object?> { ["name"] = name, ["kind"] = kind, ["title"] = title, ["body"] = body, ["created"] = DateTime.UtcNow });
            await store.Meta.InsertAsync(new Dictionary<string, object?> { ["name"] = "seeded", ["value"] = "1" });
        });
    }

    private static long IdOf(JsonNode? payload) => payload?["id"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : 0;

    /// <summary>The block: upcoming PUBLIC events only (a block is drawn for every visitor; internal dates stay in the member area).</summary>
    private static async Task<PluginResult> EventsBlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var block = ctx.Block;
        string F(string k) => block?.Get(k) ?? "";
        var count = int.TryParse(F("count"), out var n) ? Math.Clamp(n, 1, 50) : 5;
        var kind = F("kind").Trim();
        var events = (await new VereinStore(ctx.Data).UpcomingAsync(V.Today, publicOnly: true, 500, ct))
            .Where(e => kind.Length == 0 || string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)).Take(count).ToList();
        var heading = F("heading").Trim() is { Length: > 0 } h ? h : ctx.Settings.Get(VereinSettings.EventsTitle) is { Length: > 0 } s ? s : "Kommende Termine";
        var sb = new StringBuilder("<style>").Append(VereinCss.Css).Append("</style><section class=\"section verein-events").Append(F("layout") == "cards" ? " cards" : "").Append("\"><div class=\"container\">");
        sb.Append("<div class=\"section__intro\"><h2>").Append(V.H(heading)).Append("</h2>");
        if (F("intro").Trim().Length > 0) sb.Append("<p>").Append(V.H(F("intro").Trim())).Append("</p>");
        sb.Append("</div>");
        sb.Append(events.Count == 0
            ? "<p class=\"vm-muted\" style=\"text-align:center\">" + V.H(F("empty").Trim() is { Length: > 0 } em ? em : "Gerade stehen keine Termine an.") + "</p>"
            : VereinPublic.EventList(events, internalBadge: false));
        sb.Append("</div></section>");
        return PluginResult.Fragment(sb.ToString());
    }

    /// <summary>The data-protection export: the club's record of the person, their answers, meeting replies and uploads.</summary>
    private static async Task<JsonNode?> ExportAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var id = IdOf(payload);
        if (id <= 0) return null;
        var store = new VereinStore(ctx.Data);
        var p = await store.PersonOfMemberAsync(id, ct);
        var docs = (await store.DocsAsync(false, ct)).Concat(await store.DocsAsync(true, ct)).ToDictionary(d => d.Id, d => d.Title);
        var answers = new JsonArray();
        foreach (var a in await store.AnswersOfMemberAsync(id, ct))
            answers.Add(new JsonObject { ["document"] = docs.TryGetValue(a.DocId, out var t) ? t : "#" + a.DocId, ["answer"] = a.Status, ["signature"] = a.Signature, ["at"] = a.At?.ToString("o"), ["address"] = a.Ip });
        var rsvps = new JsonArray();
        foreach (var r in await store.RsvpsOfMemberAsync(id, ct)) rsvps.Add(new JsonObject { ["meeting"] = (await store.MeetingAsync(r.MeetingId, ct))?.Title ?? "", ["answer"] = r.Answer });
        var uploads = new JsonArray();
        foreach (var u in (await store.UploadsAsync(ct: ct)).Where(u => u.MemberId == id)) uploads.Add(new JsonObject { ["title"] = u.Title, ["file"] = u.FileName, ["at"] = u.Created?.ToString("o") });
        return new JsonObject
        {
            ["plugin"] = "verein",
            ["data"] = new JsonObject
            {
                ["person"] = p is null ? null : new JsonObject { ["first"] = p.First, ["last"] = p.Last, ["email"] = p.Email, ["phone"] = p.Phone, ["roles"] = string.Join(", ", p.Roles), ["joined"] = p.Joined },
                ["answers"] = answers, ["meetingReplies"] = rsvps, ["uploads"] = uploads
            }
        };
    }
}
