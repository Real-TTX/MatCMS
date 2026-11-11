using System.Globalization;

namespace MatCMS.Plugin.MatcmsStore;

/// <summary>
/// The store's back office, built from the host's components only: the review queue (every submitted version, approved only with a valid
/// signature from the operator's own machine), the entries (withdraw a version or a whole entry), the publishers (verify, block).
/// </summary>
internal static class StoreAdmin
{
    private const string Base = StoreUrls.Admin;
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static string When(DateTime? d) => d?.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) ?? "–";

    public static async Task<string?> BadgeAsync(IPluginContext ctx, CancellationToken ct)
    {
        var n = await new StoreDb(ctx.Data).ReviewCountAsync(ct);
        return n > 0 ? n.ToString(CultureInfo.InvariantCulture) : null;
    }

    // ---- review queue ---------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> ReviewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var rows = new List<UiTableRow>();
        foreach (var v in await db.VersionsInStatusAsync(VersionStatus.Review, ct))
        {
            var item = await db.ItemAsync(v.ItemId, ct);
            var pub = item is null ? null : await db.PublisherAsync(item.PublisherId, ct);
            rows.Add(Ui.RowFor((item?.Name ?? "") + " " + (item?.Key ?? "") + " " + (pub?.Name ?? ""),
                Ui.Stack(Ui.Link((item?.Name ?? "?") + " " + v.Version, Base + "review/" + Id(v.Id)), Ui.Muted(item?.Key ?? "")),
                Ui.Badge(ctx.T(StoreText.KindKey(v.Kind)), v.Kind == "native" ? UiTone.Warning : UiTone.Default),
                Ui.Stack(Ui.Text(pub?.Name ?? "?"), pub?.Verified == true ? Ui.Badge(ctx.T("store.verified"), UiTone.Success) : null),
                item?.Status == ItemStatus.Draft ? ctx.T("store.admin.newEntry") : ctx.T("store.admin.update"),
                When(v.Created)));
        }
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, null, Ui.Button(ctx.T("store.admin.viewStore"), StoreUrls.Home, UiButtonKind.Ghost, "ti-external-link") with { NewTab = true }),
            Ui.Table(new[] { Ui.Column(ctx.T("store.col.item")), Ui.Column(ctx.T("store.fact.kind")), Ui.Column(ctx.T("store.fact.publisher")),
                    Ui.Column(ctx.T("store.admin.col.what")), Ui.Column(ctx.T("store.col.submitted")) },
                rows, ctx.T("store.admin.search"), emptyText: ctx.T("store.admin.queueEmpty"))), ctx.T("store.admin.review"));
    }

    public static async Task<PluginResult> ReviewItemAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        if (!long.TryParse(ctx.Request.R("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || await db.VersionAsync(id, ct) is not { } v
            || await db.ItemAsync(v.ItemId, ct) is not { } item)
            return PluginResult.NotFound();
        var pub = await db.PublisherAsync(item.PublisherId, ct);
        var text = StoreSig.Text(v.Kind, item.Key, v.Version, v.Sha256);

        string? error = null;
        if (ctx.Request.IsPost && v.Status == VersionStatus.Review)
        {
            if (ctx.Request.Action == "approve")
            {
                var (keyId, sig) = StoreSig.Parse(ctx.Request.F("signature"));
                if (!StoreSig.Verify(ctx, text, keyId, sig)) error = ctx.T("store.admin.err.signature");
                else
                {
                    await db.Versions.UpdateAsync(v.Id, new Dictionary<string, object?>
                    {
                        ["status"] = VersionStatus.Published, ["signature"] = sig, ["key_id"] = keyId, ["published_at"] = DateTime.UtcNow, ["reason"] = ""
                    }, ct);
                    await db.RefreshItemAsync(item.Id, ct);
                    await MailPublisherAsync(ctx, pub, StoreMail.Approved, item, v, "", ct);
                    return PluginResult.Redirect(Base + "review", ctx.T("store.admin.approved", item.Name, v.Version));
                }
            }
            else if (ctx.Request.Action == "reject")
            {
                var reason = StoreText.Cut(ctx.Request.F("reason").Trim(), 2000);
                if (reason.Length == 0) error = ctx.T("store.admin.err.reason");
                else
                {
                    await db.Versions.UpdateAsync(v.Id, new Dictionary<string, object?> { ["status"] = VersionStatus.Rejected, ["reason"] = reason }, ct);
                    await db.RefreshItemAsync(item.Id, ct);
                    await MailPublisherAsync(ctx, pub, StoreMail.Rejected, item, v, reason, ct);
                    return PluginResult.Redirect(Base + "review", ctx.T("store.admin.rejected", item.Name, v.Version));
                }
            }
        }

        var command = $"dotnet run --project tools/StoreSign -- sign {v.Kind} {item.Key} {v.Version} {v.Sha256}";
        var files = v.Files.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var facts = Ui.Card(ctx.T("store.admin.package"), Ui.KeyValue(
                (ctx.T("store.col.item"), item.Name + " (" + item.Key + ")"), (ctx.T("store.fact.version"), v.Version),
                (ctx.T("store.fact.kind"), ctx.T(StoreText.KindKey(v.Kind)) + (v.Api.Length > 0 ? " · API " + v.Api : "")),
                (ctx.T("store.fact.publisher"), (pub?.Name ?? "?") + (pub?.Verified == true ? " ✓" : "")),
                (ctx.T("store.admin.size"), StoreText.Size(v.Size)), (ctx.T("store.fact.sha"), v.Sha256), (ctx.T("store.col.submitted"), When(v.Created)),
                (ctx.T("store.col.status"), ctx.T("store.vstatus." + v.Status))),
            Ui.Button(ctx.T("store.admin.download"), Base + "package/" + Id(v.Id), UiButtonKind.Ghost, "ti-download"));
        var notes = Ui.Card(ctx.T("store.admin.notes"),
            Ui.Text(v.Notes.Length > 0 ? v.Notes : "–"),
            Ui.Heading(ctx.T("store.admin.files", files.Length), 3),
            Ui.Muted(string.Join("  ·  ", files.Take(60)) + (files.Length > 60 ? " …" : "")));

        UiNode decide = v.Status != VersionStatus.Review
            ? Ui.Alert(ctx.T("store.admin.decided", ctx.T("store.vstatus." + v.Status)) + (v.Reason.Length > 0 ? " — " + v.Reason : ""), UiTone.Info)
            : Ui.Card(ctx.T("store.admin.decide"),
                Ui.Text(ctx.T("store.admin.signHow")),
                Ui.Html("<pre style=\"white-space:pre-wrap;word-break:break-all;user-select:all\">" + System.Net.WebUtility.HtmlEncode(command) + "</pre>"),
                Ui.Form(
                    error is null ? null : Ui.Alert(error, UiTone.Error),
                    Ui.Input("signature", ctx.T("store.admin.signature")) with { Help = ctx.T("store.admin.signatureHelp") },
                    Ui.TextArea("reason", ctx.T("store.admin.reason"), ctx.Request.F("reason"), 3) with { Help = ctx.T("store.admin.reasonHelp") },
                    Ui.FormActions(
                        Ui.Submit(ctx.T("store.admin.approve"), UiButtonKind.Primary, "action", "approve"),
                        Ui.Submit(ctx.T("store.admin.reject"), UiButtonKind.Danger, "action", "reject"))));
        return PluginResult.Page(Ui.Columns(new UiNode?[] { decide, notes }, new UiNode?[] { facts }), item.Name + " " + v.Version);
    }

    private static async Task MailPublisherAsync(IPluginContext ctx, Publisher? pub, string template, Item item, PackageVersion v, string reason, CancellationToken ct)
    {
        if (pub is null || await ctx.Members.FindAsync(pub.MemberId, ct) is not { } acc || acc.Email.Length == 0) return;
        await ctx.Mail.SendAsync(template, [acc.Email], new Dictionary<string, string>
        {
            ["publisher"] = pub.Name, ["name"] = item.Name, ["key"] = item.Key, ["version"] = v.Version, ["reason"] = reason,
            ["url"] = ctx.Site.Origin.TrimEnd('/') + StoreUrls.Item(item.Key), ["account_url"] = ctx.Site.Origin.TrimEnd('/') + StoreUrls.AccountItem(item.Key)
        }, ct: ct);
    }

    /// <summary>The submitted package itself, for reading it before approving.</summary>
    public static async Task<PluginResult> PackageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        if (!long.TryParse(ctx.Request.R("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || await db.VersionAsync(id, ct) is not { } v
            || await db.ItemAsync(v.ItemId, ct) is not { } item || await ctx.Files.OpenAsync(v.File, ct) is not { } stream)
            return PluginResult.NotFound();
        var json = StorePackage.Group(v.Kind) != "plugin";
        return PluginResult.FromStream(stream, json ? "application/json" : "application/zip", item.Key + "-" + v.Version + (json ? ".json" : ".zip"));
    }

    // ---- entries --------------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> ItemsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var pubs = (await db.PublishersAsync(ct)).ToDictionary(p => p.Id);
        var rows = new List<UiTableRow>();
        foreach (var item in await db.ItemsAsync(null, ct))
        {
            pubs.TryGetValue(item.PublisherId, out var pub);
            rows.Add(Ui.RowFor(item.Name + " " + item.Key + " " + (pub?.Name ?? ""),
                Ui.Stack(Ui.Link(item.Name, Base + "items/" + Id(item.Id)), Ui.Muted(item.Key)),
                Ui.Badge(ctx.T("store.istatus." + item.Status), item.Status == ItemStatus.Published ? UiTone.Success : item.Status == ItemStatus.Withdrawn ? UiTone.Error : UiTone.Default),
                item.LatestVersion.Length > 0 ? item.LatestVersion : "–", pub?.Name ?? "?", StoreHtml.N(item.Downloads)));
        }
        return PluginResult.Page(Ui.Table(new[] { Ui.Column(ctx.T("store.col.item")), Ui.Column(ctx.T("store.col.status")), Ui.Column(ctx.T("store.fact.version")),
                Ui.Column(ctx.T("store.fact.publisher")), Ui.Column(ctx.T("store.col.downloads"), right: true) },
            rows, ctx.T("store.admin.search"), emptyText: ctx.T("store.admin.noItems")), ctx.T("store.admin.items"));
    }

    public static async Task<PluginResult> ItemAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        if (!long.TryParse(ctx.Request.R("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || await db.ItemAsync(id, ct) is not { } item)
            return PluginResult.NotFound();
        if (ctx.Request.IsPost)
        {
            var reason = StoreText.Cut(ctx.Request.F("reason").Trim(), 2000);
            switch (ctx.Request.Action)
            {
                case "withdraw-version" when long.TryParse(ctx.Request.F("id"), out var vid) && await db.VersionAsync(vid, ct) is { Status: VersionStatus.Published } v && v.ItemId == item.Id:
                    await db.Versions.UpdateAsync(v.Id, new Dictionary<string, object?> { ["status"] = VersionStatus.Withdrawn, ["reason"] = reason }, ct);
                    await db.RefreshItemAsync(item.Id, ct);
                    return PluginResult.Redirect(Base + "items/" + Id(item.Id), ctx.T("store.admin.versionWithdrawn", v.Version));
                case "withdraw-item":
                    await db.UpdateItemAsync(item.Id, new Dictionary<string, object?> { ["status"] = ItemStatus.Withdrawn }, ct);
                    return PluginResult.Redirect(Base + "items/" + Id(item.Id), ctx.T("store.admin.itemWithdrawn"));
                case "restore-item":
                    await db.UpdateItemAsync(item.Id, new Dictionary<string, object?> { ["status"] = ItemStatus.Draft }, ct);
                    await db.RefreshItemAsync(item.Id, ct);
                    return PluginResult.Redirect(Base + "items/" + Id(item.Id), ctx.T("store.admin.itemRestored"));
            }
        }
        var pub = await db.PublisherAsync(item.PublisherId, ct);
        var rows = new List<UiTableRow>();
        foreach (var v in await db.VersionsOfAsync(item.Id, ct))
            rows.Add(Ui.Row(
                v.Status == VersionStatus.Review ? Ui.Link(v.Version, Base + "review/" + Id(v.Id)) : (UiNode)Ui.Text(v.Version),
                ctx.T(StoreText.KindKey(v.Kind)),
                Ui.Badge(ctx.T("store.vstatus." + v.Status), v.Status == VersionStatus.Published ? UiTone.Success : v.Status is VersionStatus.Rejected or VersionStatus.Withdrawn ? UiTone.Error : UiTone.Warning),
                When(v.PublishedAt ?? v.Created), StoreHtml.N(v.Downloads),
                v.Status == VersionStatus.Published
                    ? Ui.Action(ctx.T("store.admin.withdraw"), "withdraw-version", Id(v.Id), UiButtonKind.Danger, ctx.T("store.admin.confirmWithdraw", v.Version)) with { WithLabel = true }
                    : (UiNode)Ui.Muted(v.Reason)));
        var side = new UiNode?[]
        {
            Ui.Card(ctx.T("store.admin.entry"), Ui.KeyValue((ctx.T("store.col.status"), ctx.T("store.istatus." + item.Status)), (ctx.T("store.fact.publisher"), pub?.Name ?? "?"),
                    (ctx.T("store.fact.downloads"), StoreHtml.N(item.Downloads)), (ctx.T("store.fact.license"), item.License.Length > 0 ? item.License : "–")),
                item.Status == ItemStatus.Published ? Ui.Button(ctx.T("store.viewPublic"), StoreUrls.Item(item.Key), UiButtonKind.Ghost, "ti-external-link") with { NewTab = true } : null),
            Ui.Card(ctx.T("store.admin.danger"),
                item.Status == ItemStatus.Withdrawn
                    ? Ui.Action(ctx.T("store.admin.restoreItem"), "restore-item", null, UiButtonKind.Ghost) with { WithLabel = true }
                    : Ui.Action(ctx.T("store.admin.withdrawItem"), "withdraw-item", null, UiButtonKind.Danger, ctx.T("store.admin.confirmWithdrawItem", item.Name)) with { WithLabel = true })
        };
        var main = new UiNode?[]
        {
            Ui.Card(ctx.T("store.col.versions"), Ui.Table(new[] { Ui.Column(ctx.T("store.fact.version")), Ui.Column(ctx.T("store.fact.kind")), Ui.Column(ctx.T("store.col.status")),
                Ui.Column(ctx.T("store.col.submitted")), Ui.Column(ctx.T("store.col.downloads"), right: true), Ui.Column("", actions: true) }, rows)),
            Ui.Card(ctx.T("store.texts"), Ui.Text(item.Summary.Length > 0 ? item.Summary : "–"), Ui.Muted(item.Description))
        };
        return PluginResult.Page(Ui.Columns(main, side), item.Name);
    }

    // ---- publishers -----------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> PublishersAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("id"), out var pid) && await db.PublisherAsync(pid, ct) is { } target)
        {
            var change = ctx.Request.Action switch
            {
                "verify" => new Dictionary<string, object?> { ["verified"] = true },
                "unverify" => new Dictionary<string, object?> { ["verified"] = false },
                "block" => new Dictionary<string, object?> { ["blocked"] = true },
                "unblock" => new Dictionary<string, object?> { ["blocked"] = false },
                _ => null
            };
            if (ctx.Request.Action == "assign")
            {
                // A publisher that came without its account (a restored backup leaves member accounts out) is tied to an account of this site.
                var acc = await ctx.Members.FindByEmailAsync(ctx.Request.F("email").Trim(), ct);
                if (acc is null) return PluginResult.Redirect(Base + "publishers", ctx.T("store.admin.err.noAccount"), isError: true);
                if (await db.PublisherOfMemberAsync(acc.Id, ct) is { } other && other.Id != target.Id)
                    return PluginResult.Redirect(Base + "publishers", ctx.T("store.admin.err.accountTaken", other.Name), isError: true);
                change = new Dictionary<string, object?> { ["member_id"] = acc.Id };
            }
            if (change is not null) await db.Publishers.UpdateAsync(target.Id, change, ct);
            return PluginResult.Redirect(Base + "publishers", ctx.T("store.ok.saved"));
        }
        var rows = new List<UiTableRow>();
        foreach (var p in await db.PublishersAsync(ct))
        {
            var acc = await ctx.Members.FindAsync(p.MemberId, ct);
            var count = (await db.ItemsOfPublisherAsync(p.Id, ct)).Count;
            rows.Add(Ui.RowFor(p.Name + " " + (acc?.Email ?? ""),
                Ui.Stack(Ui.Link(p.Name, StoreUrls.Publisher(p.Slug), newTab: true), Ui.Muted(acc?.Email ?? "")),
                Ui.Inline(p.Verified ? Ui.Badge(ctx.T("store.verified"), UiTone.Success) : null, p.Blocked ? Ui.Badge(ctx.T("store.admin.blocked"), UiTone.Error) : null),
                count.ToString(CultureInfo.InvariantCulture), When(p.Created),
                Ui.Inline(
                    p.Verified ? Ui.Action(ctx.T("store.admin.unverify"), "unverify", Id(p.Id)) with { WithLabel = true }
                               : Ui.Action(ctx.T("store.admin.verify"), "verify", Id(p.Id)) with { WithLabel = true },
                    p.Blocked ? Ui.Action(ctx.T("store.admin.unblock"), "unblock", Id(p.Id)) with { WithLabel = true }
                              : Ui.Action(ctx.T("store.admin.block"), "block", Id(p.Id), UiButtonKind.Danger, ctx.T("store.admin.confirmBlock", p.Name)) with { WithLabel = true })));
        }
        var pubs = await db.PublishersAsync(ct);
        return PluginResult.Page(Ui.Stack(
            Ui.Table(new[] { Ui.Column(ctx.T("store.fact.publisher")), Ui.Column(ctx.T("store.col.status")), Ui.Column(ctx.T("store.admin.col.items"), right: true),
                    Ui.Column(ctx.T("store.admin.col.since")), Ui.Column("", actions: true) },
                rows, ctx.T("store.admin.search"), emptyText: ctx.T("store.admin.noPublishers")),
            pubs.Count == 0 ? null : Ui.Card(ctx.T("store.admin.assign"),
                Ui.Muted(ctx.T("store.admin.assignHelp")),
                Ui.Form(
                    Ui.Select("id", ctx.T("store.fact.publisher"), pubs[0].Id.ToString(CultureInfo.InvariantCulture), pubs.Select(p => Ui.Option(p.Id.ToString(CultureInfo.InvariantCulture), p.Name)).ToArray()),
                    Ui.Input("email", ctx.T("store.field.email"), "", UiInputType.Email) with { Required = true },
                    Ui.FormActions(Ui.Submit(ctx.T("store.admin.assignSend"), UiButtonKind.Primary, "action", "assign"))))), ctx.T("store.admin.publishers"));
    }
}
