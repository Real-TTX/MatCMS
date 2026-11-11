using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static MatCMS.Plugin.MatcmsStore.StoreHtml;

namespace MatCMS.Plugin.MatcmsStore;

/// <summary>One review of an entry: one per account and entry (a second one changes the first), shown under the name the reviewer chose.</summary>
internal sealed record Review(long Id, long ItemId, long MemberId, string Name, int Stars, string Text, string Status, long Reported, string Answer,
    DateTime? Answered, DateTime? Created, DateTime? Updated);

/// <summary>
/// Reviews ("Bewertungen") of the store's entries: any signed-in account of the site rates a PUBLISHED entry with 1–5 stars and a text — not its own
/// publisher's entry; the publisher answers each review once (and may change the answer); everybody can report a review, the operator sees reported
/// ones first and hides or deletes them (Admin → MatCMS Store → Bewertungen). The average and the count are part of the catalogue.
/// <para>Everything is text, never markup. A deleted account takes its reviews with it (<c>host.member.erase</c>) and an export lists them
/// (<c>host.member.export</c>). Switched off in the settings, nothing is shown or taken.</para>
/// </summary>
internal static class StoreReviews
{
    public const string Visible = "visible", Hidden = "hidden";
    private const int MaxText = 2000, MaxName = 40;

    public static void Declare(IDataBuilder d) => d.Table("reviews", t => t
        .Int("item_id", required: true).Int("member_id", required: true).Text("name", def: "").Int("stars", def: "5").Text("text", def: "")
        .Text("status", def: Visible).Int("reported", def: "0").Text("answer", def: "").Timestamp("answered").Timestamp("created").Timestamp("updated")
        .UniqueIndex("item_id", "member_id").Index("item_id").Index("member_id"));

    private static IDataTable Table(IPluginContext ctx) => ctx.Data.Table("reviews");

    private static Review R(DataRow r) => new(r.Id, r.Int("item_id"), r.Int("member_id"), r.Text("name"), (int)Math.Clamp(r.Int("stars"), 1, 5), r.Text("text"),
        r.Text("status"), r.Int("reported"), r.Text("answer"), r.Timestamp("answered"), r.Timestamp("created"), r.Timestamp("updated"));

    public static bool Enabled(IPluginContext ctx) => ctx.Settings.GetBool(StoreSettings.Reviews);

    public static async Task<IReadOnlyList<Review>> OfItemAsync(IPluginContext ctx, long itemId, bool all, CancellationToken ct)
    {
        var q = DataQuery.Everything.Eq("item_id", itemId).OrderBy("created", descending: true);
        if (!all) q = q.Eq("status", Visible);
        return (await Table(ctx).QueryAsync(q, ct)).Select(R).ToList();
    }

    /// <summary>Average (one decimal) and count of an entry's visible reviews; (0, 0) when there are none or reviews are off.</summary>
    public static async Task<(double Average, int Count)> RatingAsync(IPluginContext ctx, long itemId, CancellationToken ct)
    {
        if (!Enabled(ctx)) return (0, 0);
        var list = await OfItemAsync(ctx, itemId, all: false, ct);
        return list.Count == 0 ? (0, 0) : (Math.Round(list.Average(r => r.Stars), 1), list.Count);
    }

    public static string Stars(double value) =>
        new string('★', (int)Math.Round(value, MidpointRounding.AwayFromZero)) + new string('☆', 5 - (int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static string Avg(double v) => v.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>"★★★★☆ 4,2 (12)" — for a card and the catalogue page; empty without reviews.</summary>
    public static string Summary(IPluginContext ctx, (double Average, int Count) r) => r.Count == 0 ? ""
        : $"<span class=\"mst-stars\" title=\"{E(ctx.T("store.rev.average", Avg(r.Average), r.Count))}\">{Stars(r.Average)} <span>{Avg(r.Average)} ({r.Count})</span></span>";

    /// <summary>What the last review action did — shown at the TOP of the entry page (a plugin's redirect cannot carry a #fragment, so the page opens at
    /// its top and the answer has to be seen there).</summary>
    public static string NoticeFor(IPluginContext ctx) => ctx.Request.Q("rev") switch
    {
        "saved" => Notice(ctx.T("store.rev.saved")),
        "deleted" => Notice(ctx.T("store.rev.deleted")),
        "reported" => Notice(ctx.T("store.rev.reported")),
        "own" => Notice(ctx.T("store.rev.ownEntry"), "err"),
        "invalid" => Notice(ctx.T("store.rev.invalid"), "err"),
        _ => ""
    };

    // ---- the entry's public page --------------------------------------------------------------------------------------

    /// <summary>The reviews section of an entry's page: the average, the visitor's own form (or the way to sign in), the reviews with answers and a report button.</summary>
    public static async Task<string> SectionAsync(IPluginContext ctx, Item item, Publisher? pub, CancellationToken ct)
    {
        if (!Enabled(ctx)) return "";
        var list = await OfItemAsync(ctx, item.Id, all: false, ct);
        var sb = new StringBuilder("<section class=\"mst-reviews\" id=\"bewertungen\"><h2>").Append(E(ctx.T("store.rev.title"))).Append("</h2>");
        if (list.Count > 0) sb.Append("<p>").Append(Summary(ctx, (Math.Round(list.Average(r => r.Stars), 1), list.Count))).Append("</p>");

        var here = StoreUrls.Item(item.Key);
        if (ctx.Member is not { } m)
            sb.Append("<p><a class=\"btn btn-outline\" href=\"").Append(E(MemberPaths.SignIn + "?returnUrl=" + Uri.EscapeDataString(here))).Append("\">")
              .Append(E(ctx.T("store.rev.signIn"))).Append("</a></p>");
        else if (pub is not null && pub.MemberId == m.Id)
            sb.Append("<p class=\"mst-hint\">").Append(E(ctx.T("store.rev.ownEntry"))).Append("</p>");
        else
        {
            var mine = (await Table(ctx).FindAsync(DataQuery.Everything.Eq("item_id", item.Id).Eq("member_id", m.Id), ct)) is { } row ? R(row) : null;
            sb.Append("<form method=\"post\" action=\"").Append(E(StoreUrls.Rate(item.Key))).Append("\" class=\"mst-form mst-rate\">").Append(ctx.Antiforgery)
              .Append("<fieldset class=\"mst-starpick\"><legend>").Append(E(ctx.T(mine is null ? "store.rev.yours" : "store.rev.yoursEdit"))).Append("</legend>");
            for (var s = 5; s >= 1; s--)
                sb.Append("<input type=\"radio\" name=\"stars\" id=\"st-star-").Append(s).Append("\" value=\"").Append(s).Append('"')
                  .Append((mine?.Stars ?? 0) == s ? " checked" : "").Append(s == 5 ? " required" : "").Append("><label for=\"st-star-").Append(s)
                  .Append("\" title=\"").Append(E(ctx.T("store.rev.starsN", s))).Append("\">★</label>");
            sb.Append("</fieldset><label for=\"st-rev-name\">").Append(E(ctx.T("store.rev.name"))).Append("</label><input id=\"st-rev-name\" name=\"name\" maxlength=\"")
              .Append(MaxName).Append("\" required value=\"").Append(E(mine?.Name ?? StoreText.Cut(m.DisplayName.Length > 0 ? m.DisplayName : m.Username, MaxName))).Append("\">")
              .Append("<p class=\"mst-hint\">").Append(E(ctx.T("store.rev.nameHint"))).Append("</p>")
              .Append("<label for=\"st-rev-text\">").Append(E(ctx.T("store.rev.text"))).Append("</label><textarea id=\"st-rev-text\" name=\"text\" rows=\"4\" maxlength=\"")
              .Append(MaxText).Append("\">").Append(E(mine?.Text ?? "")).Append("</textarea><div class=\"mst-actions\"><button class=\"btn\" type=\"submit\" name=\"action\" value=\"save\">")
              .Append(E(ctx.T("store.rev.send"))).Append("</button>");
            if (mine is not null)
                sb.Append("<button class=\"btn btn-outline\" type=\"submit\" name=\"action\" value=\"delete\" formnovalidate>").Append(E(ctx.T("store.rev.delete"))).Append("</button>");
            sb.Append("</div></form>");
        }

        if (list.Count == 0) sb.Append("<p class=\"mst-empty\">").Append(E(ctx.T("store.rev.none"))).Append("</p>");
        foreach (var r in list)
        {
            sb.Append("<article class=\"mst-review\"><div class=\"mst-review-head\"><span class=\"mst-stars\" title=\"").Append(E(ctx.T("store.rev.starsN", r.Stars))).Append("\">")
              .Append(Stars(r.Stars)).Append("</span> <strong>").Append(E(r.Name.Length > 0 ? r.Name : ctx.T("store.rev.anonymous"))).Append("</strong> <span class=\"mst-by\">")
              .Append(E(Day(r.Updated ?? r.Created))).Append("</span></div>");
            foreach (var para in r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) sb.Append("<p>").Append(E(para)).Append("</p>");
            if (r.Answer.Length > 0)
            {
                sb.Append("<div class=\"mst-answer\"><strong>").Append(E(ctx.T("store.rev.answerBy", pub?.Name ?? ""))).Append("</strong>");
                foreach (var para in r.Answer.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) sb.Append("<p>").Append(E(para)).Append("</p>");
                sb.Append("</div>");
            }
            sb.Append("<form method=\"post\" action=\"").Append(E(StoreUrls.Report(item.Key))).Append("\" class=\"mst-report\">").Append(ctx.Antiforgery)
              .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append("\"><button type=\"submit\" class=\"mst-link\">")
              .Append(E(ctx.T("store.rev.report"))).Append("</button></form></article>");
        }
        return sb.Append("</section>").ToString();
    }

    private static async Task<(Item? Item, Publisher? Pub)> PublicItemAsync(IPluginContext ctx, StoreDb db, CancellationToken ct)
    {
        if (await db.ItemByKeyAsync(ctx.Request.R("key").ToLowerInvariant(), ct) is not { Status: ItemStatus.Published } item) return (null, null);
        var pub = await db.PublisherAsync(item.PublisherId, ct);
        return pub?.Blocked == true ? (null, null) : (item, pub);
    }

    /// <summary>POST <c>/store/{key}/bewerten</c> (a signed-in account): writes, changes or deletes the account's review of the entry.</summary>
    public static async Task<PluginResult> RateAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var (item, pub) = await PublicItemAsync(ctx, db, ct);
        if (item is null || !Enabled(ctx)) return PluginResult.NotFound();
        var back = StoreUrls.Item(item.Key);
        var m = ctx.Member!;
        if (pub is not null && pub.MemberId == m.Id) return Go(ctx, back + "?rev=own");
        var mine = await Table(ctx).FindAsync(DataQuery.Everything.Eq("item_id", item.Id).Eq("member_id", m.Id), ct);
        if (ctx.Request.Action == "delete")
        {
            if (mine is not null) await Table(ctx).DeleteAsync(mine.Id, ct);
            return Go(ctx, back + "?rev=deleted");
        }
        if (!int.TryParse(ctx.Request.F("stars"), NumberStyles.None, CultureInfo.InvariantCulture, out var stars) || stars is < 1 or > 5)
            return Go(ctx, back + "?rev=invalid");
        var name = StoreText.Cut(OneLine(ctx.Request.F("name")), MaxName);
        var values = new Dictionary<string, object?>
        {
            ["stars"] = (long)stars, ["name"] = name, ["text"] = StoreText.Cut(ctx.Request.F("text").Replace("\r", "").Trim(), MaxText), ["updated"] = DateTime.UtcNow
        };
        if (mine is null)
        {
            values["item_id"] = item.Id; values["member_id"] = m.Id; values["created"] = DateTime.UtcNow;
            await Table(ctx).InsertAsync(values, ct);
        }
        else
        {
            // A changed review is new text: it is seen again (unless the operator hid it — that stays) and earlier reports no longer apply.
            values["reported"] = 0L;
            await Table(ctx).UpdateAsync(mine.Id, values, ct);
        }
        return Go(ctx, back + "?rev=saved");
    }

    /// <summary>POST <c>/store/{key}/melden</c> (anybody): marks a review as reported — the operator sees it first in the admin.</summary>
    public static async Task<PluginResult> ReportAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var (item, _) = await PublicItemAsync(ctx, db, ct);
        if (item is null || !Enabled(ctx)) return PluginResult.NotFound();
        if (long.TryParse(ctx.Request.F("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && await Table(ctx).GetAsync(id, ct) is { } row && row.Int("item_id") == item.Id)
            await Table(ctx).IncrementAsync(id, "reported", 1, ct: ct);
        return Go(ctx, StoreUrls.Item(item.Key) + "?rev=reported");
    }

    private static string OneLine(string s) => string.Join(' ', (s ?? "").Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    // ---- the publisher's answers (the account's entry page) -----------------------------------------------------------

    /// <summary>The reviews of the publisher's entry with an answer field each (form action "answer").</summary>
    public static async Task<string> PublisherSectionAsync(IPluginContext ctx, Item item, CancellationToken ct)
    {
        if (!Enabled(ctx)) return "";
        var list = await OfItemAsync(ctx, item.Id, all: false, ct);
        var sb = new StringBuilder("<h2>").Append(E(ctx.T("store.rev.title"))).Append("</h2>");
        if (list.Count == 0) return sb.Append("<p class=\"mst-hint\">").Append(E(ctx.T("store.rev.none"))).Append("</p>").ToString();
        foreach (var r in list)
        {
            sb.Append("<div class=\"mst-review\"><div class=\"mst-review-head\"><span class=\"mst-stars\">").Append(Stars(r.Stars)).Append("</span> <strong>")
              .Append(E(r.Name)).Append("</strong> <span class=\"mst-by\">").Append(E(Day(r.Updated ?? r.Created))).Append("</span></div><p>").Append(E(r.Text)).Append("</p>")
              .Append("<form method=\"post\" class=\"mst-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"answer\"><input type=\"hidden\" name=\"id\" value=\"")
              .Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append("\"><label for=\"st-ans-").Append(r.Id).Append("\">").Append(E(ctx.T("store.rev.answer"))).Append("</label><textarea id=\"st-ans-")
              .Append(r.Id).Append("\" name=\"answer\" rows=\"3\" maxlength=\"").Append(MaxText).Append("\">").Append(E(r.Answer)).Append("</textarea><div class=\"mst-actions\"><button class=\"btn btn-outline\" type=\"submit\">")
              .Append(E(ctx.T("store.rev.answerSend"))).Append("</button></div></form></div>");
        }
        return sb.ToString();
    }

    /// <summary>The publisher answers (or changes / removes the answer of) one review of their own entry.</summary>
    public static async Task<bool> AnswerAsync(IPluginContext ctx, Item item, CancellationToken ct)
    {
        if (!long.TryParse(ctx.Request.F("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || await Table(ctx).GetAsync(id, ct) is not { } row
            || row.Int("item_id") != item.Id) return false;
        var answer = StoreText.Cut(ctx.Request.F("answer").Replace("\r", "").Trim(), MaxText);
        await Table(ctx).UpdateAsync(id, new Dictionary<string, object?> { ["answer"] = answer, ["answered"] = answer.Length > 0 ? DateTime.UtcNow : null }, ct);
        return true;
    }

    // ---- the operator -------------------------------------------------------------------------------------------------

    public static Task<long> ReportedCountAsync(IPluginContext ctx, CancellationToken ct) =>
        Table(ctx).CountAsync(DataQuery.Everything.Where("reported", DataOp.Gt, 0L).Eq("status", Visible), ct);

    /// <summary>Admin → Bewertungen: every review, reported ones first; hide, show again, delete.</summary>
    public static async Task<PluginResult> AdminAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var url = StoreUrls.Admin + "reviews";
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("id"), out var rid) && await Table(ctx).GetAsync(rid, ct) is { } target)
        {
            switch (ctx.Request.Action)
            {
                case "hide": await Table(ctx).UpdateAsync(target.Id, new Dictionary<string, object?> { ["status"] = Hidden }, ct); break;
                case "show": await Table(ctx).UpdateAsync(target.Id, new Dictionary<string, object?> { ["status"] = Visible, ["reported"] = 0L }, ct); break;
                case "dismiss": await Table(ctx).UpdateAsync(target.Id, new Dictionary<string, object?> { ["reported"] = 0L }, ct); break;
                case "delete": await Table(ctx).DeleteAsync(target.Id, ct); break;
            }
            return PluginResult.Redirect(url, ctx.T("store.ok.saved"));
        }
        var all = (await Table(ctx).QueryAsync(DataQuery.Everything.OrderBy("created", descending: true), ct)).Select(R)
            .OrderByDescending(r => r.Reported > 0 && r.Status == Visible).ThenByDescending(r => r.Created).ToList();
        var items = new Dictionary<long, Item?>();
        var rows = new List<UiTableRow>();
        foreach (var r in all)
        {
            if (!items.TryGetValue(r.ItemId, out var item)) items[r.ItemId] = item = await db.ItemAsync(r.ItemId, ct);
            var id = r.Id.ToString(CultureInfo.InvariantCulture);
            rows.Add(Ui.RowFor((item?.Name ?? "") + " " + r.Name + " " + r.Text,
                Ui.Stack(Ui.Text(Stars(r.Stars) + " " + r.Name), Ui.Muted(StoreText.Cut(r.Text, 300))),
                item is null ? (UiNode)Ui.Muted("?") : Ui.Link(item.Name, StoreUrls.Item(item.Key) + "#bewertungen", newTab: true),
                Ui.Inline(r.Status == Hidden ? Ui.Badge(ctx.T("store.rev.hidden"), UiTone.Default) : Ui.Badge(ctx.T("store.rev.visible"), UiTone.Success),
                    r.Reported > 0 && r.Status == Visible ? Ui.Badge(ctx.T("store.rev.reportedN", r.Reported), UiTone.Error) : null),
                (r.Updated ?? r.Created)?.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) ?? "–",
                Ui.Inline(
                    r.Status == Hidden ? Ui.Action(ctx.T("store.rev.show"), "show", id) with { WithLabel = true }
                                       : Ui.Action(ctx.T("store.rev.hide"), "hide", id) with { WithLabel = true },
                    r.Reported > 0 && r.Status == Visible ? Ui.Action(ctx.T("store.rev.dismiss"), "dismiss", id) with { WithLabel = true } : null,
                    Ui.Action(ctx.T("store.rev.delete"), "delete", id, UiButtonKind.Danger, ctx.T("store.rev.confirmDelete")))));
        }
        return PluginResult.Page(Ui.Table(new[] { Ui.Column(ctx.T("store.rev.col.review")), Ui.Column(ctx.T("store.col.item")), Ui.Column(ctx.T("store.col.status")),
                Ui.Column(ctx.T("store.col.submitted")), Ui.Column("", actions: true) },
            rows, ctx.T("store.admin.search"), emptyText: ctx.T("store.rev.none")), ctx.T("store.rev.title"));
    }

    // ---- accounts -----------------------------------------------------------------------------------------------------

    private static long MemberOf(JsonNode? payload) => payload?["id"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : 0;

    /// <summary>An account is deleted: its reviews go with it.</summary>
    public static async Task EraseAsync(IPluginContext ctx, JsonNode? payload)
    {
        if (MemberOf(payload) is > 0 and var id) await Table(ctx).DeleteWhereAsync(DataQuery.Everything.Eq("member_id", id));
    }

    /// <summary>An account's data export: its reviews, as written.</summary>
    public static async Task<JsonNode?> ExportAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        if (MemberOf(payload) is not (> 0 and var id)) return null;
        var db = new StoreDb(ctx.Data);
        var arr = new JsonArray();
        foreach (var r in (await Table(ctx).QueryAsync(DataQuery.Everything.Eq("member_id", id), ct)).Select(R))
            arr.Add(new JsonObject
            {
                ["entry"] = (await db.ItemAsync(r.ItemId, ct))?.Key ?? "", ["name"] = r.Name, ["stars"] = r.Stars, ["text"] = r.Text,
                ["created"] = r.Created?.ToString("O", CultureInfo.InvariantCulture) ?? ""
            });
        return arr.Count == 0 ? null : new JsonObject { ["plugin"] = "matcms-store", ["data"] = new JsonObject { ["reviews"] = arr } };
    }
}
