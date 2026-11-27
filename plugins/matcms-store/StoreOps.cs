using System.Globalization;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.MatcmsStore;

/// <summary>
/// The store's catalogue for AI agents (the site's assistant, the cloud's MCP, the site's own MCP server): read the entries, take one out of the store,
/// read and moderate reviews, read the publishers. What the operator does in Admin → MatCMS Store, with the same rules — and NOTHING that publishes a
/// version: that needs the operator's signature from <c>tools/StoreSign</c> on his own machine and stays a human step (there is no operation for
/// approving, rejecting or putting a withdrawn entry back).
/// <para>Reviews name their author and the publishers are accounts with an e-mail address: those reads are <c>PersonalData</c> and never reach the
/// assistant. Every write answers with an <c>undo</c> naming an internal operation of this plugin.</para>
/// </summary>
internal static class StoreOps
{
    public const int MaxRows = 200;

    public static readonly PluginOperationSpec ItemList = new("item.list",
        "Einträge des Stores (Plugins, Templates, Komponenten): key, Name, Status (draft|published|withdrawn), neueste Version und Art, Downloads, Herausgeber, Adresse.",
        """{"status"?: "published", "search"?: "text", "limit"?: 50}""", Writes: false);
    public static readonly PluginOperationSpec ItemRead = new("item.read",
        "Einen Eintrag vollständig lesen: Texte (auch englisch), Schlagwörter, Lizenz, Website, Versionen mit Status, Downloads und Notizen, Bewertung (Schnitt, Anzahl).",
        """{"key": "acme-hello"}""", Writes: false);
    public static readonly PluginOperationSpec ItemWithdraw = new("item.withdraw",
        "Einen Eintrag aus dem Store nehmen (nicht mehr öffentlich, keine Version wird mehr angeboten). Wieder veröffentlichen geht nur im Admin.",
        """{"key": "acme-hello"}""");
    public static readonly PluginOperationSpec ReviewList = new("review.list",
        "Bewertungen lesen (gemeldete zuerst): id, Eintrag, Sterne, Name des Verfassers, Text, Status (visible|hidden), Meldungen, Antwort des Herausgebers.",
        """{"item"?: "acme-hello", "status"?: "visible|hidden", "reported"?: true, "limit"?: 50}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec ReviewHide = new("review.hide",
        "Eine Bewertung ausblenden (sie zählt nicht mehr in den Schnitt).", """{"id": 12}""");
    public static readonly PluginOperationSpec ReviewShow = new("review.show",
        "Eine ausgeblendete Bewertung wieder zeigen; ihre Meldungen gelten als erledigt.", """{"id": 12}""");
    public static readonly PluginOperationSpec ReviewDismiss = new("review.dismiss",
        "Die Meldungen einer Bewertung als erledigt markieren (sie bleibt sichtbar).", """{"id": 12}""");
    public static readonly PluginOperationSpec PublisherList = new("publisher.list",
        "Die Herausgeber: id, Name, Adresse (slug), Website, verifiziert, gesperrt, seit, Anzahl Einträge, E-Mail des Kontos.",
        """{"search"?: "text"}""", Writes: false, PersonalData: true);

    public static readonly PluginOperationSpec ItemUnwithdraw = new("item.unwithdraw", "Nimmt das Zurückziehen eines Eintrags zurück (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec ReviewRestore = new("review.restore", "Stellt Status und Meldungen einer Bewertung wieder her (Rückgängig).", Internal: true);

    public static void Register(IPluginBuilder b)
    {
        b.Operation(ItemList, ListItemsAsync);
        b.Operation(ItemRead, ReadItemAsync);
        b.Operation(ItemWithdraw, WithdrawItemAsync);
        b.Operation(ReviewList, ListReviewsAsync);
        b.Operation(ReviewHide, (ctx, args, ct) => ModerateAsync(ctx, args, "hide", ct));
        b.Operation(ReviewShow, (ctx, args, ct) => ModerateAsync(ctx, args, "show", ct));
        b.Operation(ReviewDismiss, (ctx, args, ct) => ModerateAsync(ctx, args, "dismiss", ct));
        b.Operation(PublisherList, ListPublishersAsync);
        b.Operation(ItemUnwithdraw, UnwithdrawItemAsync);
        b.Operation(ReviewRestore, RestoreReviewAsync);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static JsonObject Answer(string status, string message, JsonNode? data = null, JsonNode? undo = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        if (undo is not null) o["undo"] = undo;
        return o;
    }

    private static JsonObject Undo(string name, JsonNode args) => new() { ["name"] = name, ["args"] = args };
    private static string Str(JsonObject? o, string key) => (o?[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : "").Trim();
    private static bool? Flag(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static long Id(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out l) ? l : 0;
    }

    private static int Limit(JsonObject? o, int def) => Id(o, "limit") is > 0 and var l ? (int)Math.Min(l, MaxRows) : def;
    private static string? When(DateTime? d) => d?.ToString("o", CultureInfo.InvariantCulture);

    private static async Task<Item?> FindItemAsync(StoreDb db, JsonObject? o, CancellationToken ct)
    {
        if (Id(o, "id") is > 0 and var id) return await db.ItemAsync(id, ct);
        var key = Str(o, "key").ToLowerInvariant();
        if (key.Length == 0) key = Str(o, "item").ToLowerInvariant();
        return key.Length > 0 ? await db.ItemByKeyAsync(key, ct) : null;
    }

    private static JsonObject ItemJson(Item i, Publisher? pub) => new()
    {
        ["id"] = i.Id, ["key"] = i.Key, ["name"] = i.Name, ["status"] = i.Status, ["latestVersion"] = i.LatestVersion, ["latestKind"] = i.LatestKind,
        ["downloads"] = i.Downloads, ["publisher"] = pub?.Name ?? "", ["publisherSlug"] = pub?.Slug ?? "", ["url"] = StoreUrls.Item(i.Key), ["updated"] = When(i.Updated),
    };

    // ---- entries ------------------------------------------------------------------------------------------------

    public static async Task<JsonNode?> ListItemsAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject;
        var db = new StoreDb(ctx.Data);
        var status = Str(o, "status");
        if (status.Length > 0 && status is not (ItemStatus.Draft or ItemStatus.Published or ItemStatus.Withdrawn))
            return Answer("failed", "status muss draft, published oder withdrawn sein.");
        var search = Str(o, "search");
        var pubs = (await db.PublishersAsync(ct)).ToDictionary(p => p.Id);
        var all = (await db.ItemsAsync(status.Length > 0 ? status : null, ct))
            .Where(i => search.Length == 0 || (i.Name + " " + i.Key + " " + i.Summary + " " + i.Tags).Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        var picked = all.Take(Limit(o, 50)).Select(i => (JsonNode)ItemJson(i, pubs.GetValueOrDefault(i.PublisherId))).ToArray();
        return Answer("applied", $"{picked.Length} von {all.Count} Einträgen", new JsonArray(picked));
    }

    public static async Task<JsonNode?> ReadItemAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        if (await FindItemAsync(db, args as JsonObject, ct) is not { } item) return Answer("failed", "Eintrag nicht gefunden.");
        var data = ItemJson(item, await db.PublisherAsync(item.PublisherId, ct));
        data["summary"] = item.Summary; data["description"] = item.Description;
        data["nameEn"] = item.NameEn; data["summaryEn"] = item.SummaryEn; data["descriptionEn"] = item.DescriptionEn;
        data["tags"] = item.Tags; data["license"] = item.License; data["homepage"] = item.Homepage; data["created"] = When(item.Created);
        data["versions"] = new JsonArray((await db.VersionsOfAsync(item.Id, ct)).Select(v => (JsonNode)new JsonObject
        {
            ["id"] = v.Id, ["version"] = v.Version, ["kind"] = v.Kind, ["api"] = v.Api, ["minCms"] = v.MinCms, ["status"] = v.Status, ["reason"] = v.Reason,
            ["notes"] = v.Notes, ["size"] = v.Size, ["sha256"] = v.Sha256, ["downloads"] = v.Downloads, ["submitted"] = When(v.Created), ["published"] = When(v.PublishedAt),
        }).ToArray());
        data["pictures"] = (await db.PicturesOfAsync(item.Id, ct)).Count;
        var (avg, count) = await StoreReviews.RatingAsync(ctx, item.Id, ct);
        data["rating"] = new JsonObject { ["average"] = avg, ["count"] = count };
        return Answer("applied", item.Name, data);
    }

    public static async Task<JsonNode?> WithdrawItemAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        if (await FindItemAsync(db, args as JsonObject, ct) is not { } item) return Answer("failed", "Eintrag nicht gefunden.");
        if (item.Status == ItemStatus.Withdrawn) return Answer("skipped-exists", $"„{item.Name}“ ist schon aus dem Store genommen.");
        await db.UpdateItemAsync(item.Id, new Dictionary<string, object?> { ["status"] = ItemStatus.Withdrawn }, ct);
        return Answer("applied", $"„{item.Name}“ ist aus dem Store genommen.", null, Undo("item.unwithdraw", new JsonObject { ["id"] = item.Id }));
    }

    /// <summary>The undo of a withdrawal: the entry's state follows its versions again, exactly as before (draft, or public with the versions that were
    /// signed and published back then — nothing new is published).</summary>
    public static async Task<JsonNode?> UnwithdrawItemAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        if (await FindItemAsync(db, args as JsonObject, ct) is not { } item) return Answer("failed", "Den Eintrag gibt es nicht mehr.");
        await db.UpdateItemAsync(item.Id, new Dictionary<string, object?> { ["status"] = ItemStatus.Draft }, ct);
        await db.RefreshItemAsync(item.Id, ct);
        return Answer("applied", $"„{item.Name}“ ist wieder wie zuvor.");
    }

    // ---- reviews ------------------------------------------------------------------------------------------------

    public static async Task<JsonNode?> ListReviewsAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject;
        var db = new StoreDb(ctx.Data);
        var q = DataQuery.Everything.OrderBy("created", descending: true);
        if (Str(o, "item").Length > 0)
        {
            if (await FindItemAsync(db, new JsonObject { ["key"] = Str(o, "item") }, ct) is not { } item) return Answer("failed", "Eintrag nicht gefunden.");
            q = q.Eq("item_id", item.Id);
        }
        var status = Str(o, "status");
        if (status.Length > 0)
        {
            if (status is not (StoreReviews.Visible or StoreReviews.Hidden)) return Answer("failed", "status muss visible oder hidden sein.");
            q = q.Eq("status", status);
        }
        if (Flag(o, "reported") == true) q = q.Where("reported", DataOp.Gt, 0L);
        var rows = (await StoreReviews.Table(ctx).QueryAsync(q, ct)).Select(StoreReviews.R)
            .OrderByDescending(r => r.Reported > 0 && r.Status == StoreReviews.Visible).ThenByDescending(r => r.Created).ToList();
        var keys = new Dictionary<long, string>();
        var list = new JsonArray();
        foreach (var r in rows.Take(Limit(o, 50)))
        {
            if (!keys.TryGetValue(r.ItemId, out var key)) keys[r.ItemId] = key = (await db.ItemAsync(r.ItemId, ct))?.Key ?? "";
            list.Add(new JsonObject
            {
                ["id"] = r.Id, ["item"] = key, ["stars"] = r.Stars, ["name"] = r.Name, ["text"] = r.Text, ["status"] = r.Status, ["reported"] = r.Reported,
                ["answer"] = r.Answer, ["created"] = When(r.Created), ["updated"] = When(r.Updated),
            });
        }
        return Answer("applied", $"{list.Count} von {rows.Count} Bewertungen", list);
    }

    /// <summary>Hide, show again (reports count as dealt with, as in the admin) or dismiss the reports — the undo puts status and report count back.</summary>
    private static async Task<JsonNode?> ModerateAsync(IPluginContext ctx, JsonNode? args, string action, CancellationToken ct)
    {
        var id = Id(args as JsonObject, "id");
        if (id <= 0 || await StoreReviews.Table(ctx).GetAsync(id, ct) is not { } row) return Answer("failed", "Bewertung nicht gefunden (id).");
        var r = StoreReviews.R(row);
        var undo = Undo("review.restore", new JsonObject { ["id"] = r.Id, ["status"] = r.Status, ["reported"] = r.Reported });
        var change = action switch
        {
            "hide" => new Dictionary<string, object?> { ["status"] = StoreReviews.Hidden },
            "show" => new Dictionary<string, object?> { ["status"] = StoreReviews.Visible, ["reported"] = 0L },
            _ => new Dictionary<string, object?> { ["reported"] = 0L },
        };
        if (action == "hide" && r.Status == StoreReviews.Hidden) return Answer("skipped-exists", "Die Bewertung ist schon ausgeblendet.");
        if (action == "show" && r.Status == StoreReviews.Visible && r.Reported == 0) return Answer("skipped-exists", "Die Bewertung ist schon sichtbar.");
        if (action == "dismiss" && r.Reported == 0) return Answer("skipped-exists", "Die Bewertung hat keine Meldungen.");
        await StoreReviews.Table(ctx).UpdateAsync(r.Id, change, ct);
        return Answer("applied", action switch { "hide" => "Bewertung ausgeblendet.", "show" => "Bewertung wieder sichtbar.", _ => "Meldungen erledigt." }, null, undo);
    }

    public static async Task<JsonNode?> RestoreReviewAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject;
        var id = Id(o, "id");
        if (id <= 0 || await StoreReviews.Table(ctx).GetAsync(id, ct) is null) return Answer("failed", "Die Bewertung gibt es nicht mehr.");
        var status = Str(o, "status") == StoreReviews.Hidden ? StoreReviews.Hidden : StoreReviews.Visible;
        await StoreReviews.Table(ctx).UpdateAsync(id, new Dictionary<string, object?> { ["status"] = status, ["reported"] = Math.Max(0, Id(o, "reported")) }, ct);
        return Answer("applied", "Bewertung wiederhergestellt.");
    }

    // ---- publishers ---------------------------------------------------------------------------------------------

    public static async Task<JsonNode?> ListPublishersAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject;
        var db = new StoreDb(ctx.Data);
        var search = Str(o, "search");
        var list = new JsonArray();
        foreach (var p in await db.PublishersAsync(ct))
        {
            var email = (await ctx.Members.FindAsync(p.MemberId, ct))?.Email ?? "";
            if (search.Length > 0 && !(p.Name + " " + p.Slug + " " + email).Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new JsonObject
            {
                ["id"] = p.Id, ["name"] = p.Name, ["slug"] = p.Slug, ["website"] = p.Website, ["verified"] = p.Verified, ["blocked"] = p.Blocked,
                ["created"] = When(p.Created), ["items"] = (await db.ItemsOfPublisherAsync(p.Id, ct)).Count, ["email"] = email, ["url"] = StoreUrls.Publisher(p.Slug),
            });
        }
        return Answer("applied", $"{list.Count} Herausgeber", list);
    }
}
