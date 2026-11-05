// The back office of the template store: the review queue (one submitted file = one version to approve or reject), the list of
// templates in the store (hide, edit texts, delete) and the plugin's settings. Approving a version makes it the live one of its entry
// and mails the submitter; rejecting mails the reason. The operator can download any submitted file to look at it before deciding.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

public static class MtAdmin
{
    public const string Root = "/admin/p/matmon-templates/";

    public static void Configure(IPluginBuilder b)
    {
        b.Settings
            .Text("notifyEmail", "mt.set.notify", "", "mt.set.notifyHelp")
            .TextArea("intro", "mt.set.intro", "Ready-made sensor templates from the Matmon community. Download a file and import it in Matmon under Monitoring → Templates → Import.", "mt.set.introHelp")
            .TextArea("terms", "mt.set.terms", "I created this template (or may share it) and agree that it is published here free of charge for everybody to use.", "mt.set.termsHelp")
            .Number("maxPending", "mt.set.maxPending", 5, 0, 100, "mt.set.maxPendingHelp");

        b.Admin.Section("mt.admin.title", "ti-template", AdminGroup.Content, 60)
            .Page("queue", "mt.a.tabQueue", QueueAsync)
            .Page("entries", "mt.a.tabEntries", EntriesAsync)
            .Page("versions/{id}", null, VersionAsync)
            .Page("versions/{id}/file", null, FileAsync)
            .Page("entries/{id}", null, EntryAsync)
            .Badge(async (ctx, ct) => await MtStore.PendingCountAsync(ctx, ct) is var n and > 0 ? n.ToString(CultureInfo.InvariantCulture) : null);

        b.Admin.Attention(async (ctx, ct) =>
        {
            var n = await MtStore.PendingCountAsync(ctx, ct);
            return n == 0 ? Array.Empty<AttentionNote>()
                : new[] { new AttentionNote(ctx.T("mt.admin.title"), ctx.T(n == 1 ? "mt.a.attention1" : "mt.a.attentionN", n), Root + "queue") { Tone = UiTone.Warning } };
        });
    }

    private static string D(DateTime? d) => d is DateTime x ? x.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "";

    private static UiBadge State(IPluginContext ctx, string status) => status switch
    {
        MtStore.Pending => Ui.Badge(ctx.T("mt.a.pending"), UiTone.Warning),
        MtStore.Approved => Ui.Badge(ctx.T("mt.a.approved"), UiTone.Success),
        _ => Ui.Badge(ctx.T("mt.a.rejected"), UiTone.Error)
    };

    // ---- queue --------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> QueueAsync(IPluginContext ctx, CancellationToken ct)
    {
        var status = ctx.Request.Q("status") is MtStore.Approved or MtStore.Rejected or "all" ? ctx.Request.Q("status") : MtStore.Pending;
        var q = DataQuery.Everything.OrderBy("created", true);
        if (status != "all") q = q.Eq("status", status);
        var rows = await MtStore.V(ctx).QueryAsync(q.Page(0, 500), ct);
        var entries = (await MtStore.E(ctx).QueryAsync(DataQuery.Everything, ct)).ToDictionary(e => e.Id);

        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("mt.a.colTitle")), Ui.Column(ctx.T("mt.a.colKind")), Ui.Column(ctx.T("mt.a.colBy")), Ui.Column(ctx.T("mt.a.colWhen")), Ui.Column(ctx.T("mt.a.colCount"), right: true), Ui.Column(ctx.T("mt.a.colState")) },
            rows.Select(v =>
            {
                var isUpdate = entries.TryGetValue(v.Int("entry_id"), out var e) && e.Int("version_id") > 0 && e.Int("version_id") != v.Id;
                return Ui.RowFor(v.Text("title") + " " + v.Text("author") + " " + v.Text("email"),
                    Ui.Link(v.Text("title") + (v.Text("version").Length > 0 ? " v" + v.Text("version") : ""), Root + "versions/" + v.Id),
                    isUpdate ? ctx.T("mt.a.kindUpdate") : ctx.T("mt.a.kindNew"),
                    v.Text("author") + (v.Text("email").Length > 0 ? " <" + v.Text("email") + ">" : ""),
                    D(v.Timestamp("created")), v.Int("template_count"), State(ctx, v.Text("status")));
            }),
            searchPlaceholder: ctx.T("mt.a.search"), pageSize: 50, emptyText: ctx.T(status == MtStore.Pending ? "mt.a.queueEmpty" : "mt.a.none"));

        return PluginResult.Page(Ui.Stack(
            Ui.Tabs(
                Ui.Tab(ctx.T("mt.a.pending"), Root + "queue", status == MtStore.Pending),
                Ui.Tab(ctx.T("mt.a.approved"), Root + "queue?status=approved", status == MtStore.Approved),
                Ui.Tab(ctx.T("mt.a.rejected"), Root + "queue?status=rejected", status == MtStore.Rejected),
                Ui.Tab(ctx.T("mt.a.all"), Root + "queue?status=all", status == "all")),
            table), ctx.T("mt.a.tabQueue"));
    }

    // ---- one submitted version ----------------------------------------------------------------------------------

    private static async Task<PluginResult> VersionAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await MtStore.V(ctx).GetAsync(id, ct) is not DataRow v) return PluginResult.NotFound();
        var entry = await MtStore.E(ctx).GetAsync(v.Int("entry_id"), ct);
        if (entry is null) return PluginResult.NotFound();

        if (ctx.Request.IsPost)
        {
            if (v.Text("status") != MtStore.Pending) return PluginResult.Redirect(Root + "versions/" + id, ctx.T("mt.a.alreadyDecided"), isError: true);
            if (ctx.Request.Action == "approve")
            {
                await MtStore.ApproveAsync(ctx, v, ct);
                entry = (await MtStore.E(ctx).GetAsync(entry.Id, ct))!;
                await MtMail.DecisionAsync(ctx, (await MtStore.V(ctx).GetAsync(id, ct))!, entry, approved: true, ct);
                ctx.Log($"Version #{id} of \"{entry.Text("title")}\" approved by {ctx.User.Name}");
                return PluginResult.Redirect(Root + "queue", ctx.T("mt.a.approvedNotice", v.Text("title")));
            }
            if (ctx.Request.Action == "reject")
            {
                var reason = MtStore.Clip(ctx.Request.F("reason"), 1000);
                if (reason.Length == 0) return PluginResult.Redirect(Root + "versions/" + id, ctx.T("mt.a.reasonMissing"), isError: true);
                await MtStore.RejectAsync(ctx, v, reason, ct);
                await MtMail.DecisionAsync(ctx, (await MtStore.V(ctx).GetAsync(id, ct))!, entry, approved: false, ct);
                ctx.Log($"Version #{id} of \"{v.Text("title")}\" rejected by {ctx.User.Name}");
                return PluginResult.Redirect(Root + "queue", ctx.T("mt.a.rejectedNotice", v.Text("title")));
            }
            return PluginResult.Redirect(Root + "versions/" + id);
        }

        var templates = MtStore.Templates(v);
        var removed = MtStore.Removed(v);
        var isUpdate = entry.Int("version_id") > 0 && entry.Int("version_id") != v.Id;
        var main = new List<UiNode?>
        {
            Ui.Card(ctx.T("mt.a.texts"), Ui.KeyValue(
                (ctx.T("mt.f.title"), v.Text("title")), (ctx.T("mt.f.summary"), v.Text("summary")), (ctx.T("mt.f.tags"), v.Text("tags")),
                (ctx.T("mt.f.version"), v.Text("version")), (ctx.T("mt.f.notes"), v.Text("notes"))),
                v.Text("description").Length > 0 ? Ui.Text(v.Text("description")) : null),
            removed.Count > 0 ? Ui.Alert(ctx.T("mt.a.removed", string.Join("; ", removed)), UiTone.Info) : null,
            Ui.Card(ctx.T("mt.a.inside", templates.Count), Ui.Table(new[] { Ui.Column(ctx.T("mt.colName")), Ui.Column(ctx.T("mt.colSensor")) },
                templates.Select(t => Ui.Row(t.Name, t.SensorType.Length > 0 ? t.SensorType : t.Scope)), emptyText: "–"))
        };
        var side = new List<UiNode?>
        {
            Ui.Card(ctx.T("mt.a.facts"), Ui.KeyValue(
                (ctx.T("mt.a.colState"), ctx.T("mt.a." + v.Text("status"))),
                (ctx.T("mt.a.colKind"), isUpdate ? ctx.T("mt.a.kindUpdateOf", entry.Text("version")) : ctx.T("mt.a.kindNew")),
                (ctx.T("mt.a.colBy"), v.Text("author")), (ctx.T("mt.a.email"), v.Text("email")),
                (ctx.T("mt.a.colWhen"), D(v.Timestamp("created"))), (ctx.T("mt.a.decided"), D(v.Timestamp("decided"))),
                (ctx.T("mt.a.size"), (v.Int("size") / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB")),
                v.Text("reason").Length > 0 ? Ui.Alert(v.Text("reason"), UiTone.Error) : null,
                Ui.Inline(Ui.Button(ctx.T("mt.a.downloadFile"), Root + "versions/" + id + "/file", UiButtonKind.Ghost, small: true),
                    MtStore.IsVisible(entry) ? Ui.Button(ctx.T("mt.a.viewPublic"), MtPublic.Base + "/" + entry.Text("slug"), UiButtonKind.Ghost, small: true) : null))
        };
        if (v.Text("status") == MtStore.Pending)
        {
            side.Add(Ui.Card(ctx.T("mt.a.decide"),
                Ui.Form(Ui.Muted(ctx.T("mt.a.approveHint")), Ui.FormActions(Ui.Submit(ctx.T("mt.a.approve"), name: "action", value: "approve"))),
                Ui.Form(Ui.TextArea("reason", ctx.T("mt.a.reason"), "", 3) with { Help = ctx.T("mt.a.reasonHelp") },
                    Ui.FormActions(Ui.Submit(ctx.T("mt.a.reject"), UiButtonKind.Danger, name: "action", value: "reject", confirm: ctx.T("mt.a.rejectConfirm"))))));
        }
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.T("mt.a.versionOf", v.Text("version").Length > 0 ? "v" + v.Text("version") : "#" + id), v.Text("title"),
                Ui.Button(ctx.T("mt.a.back"), Root + "queue", UiButtonKind.Ghost, small: true)),
            Ui.Columns(main, side)), v.Text("title"));
    }

    private static async Task<PluginResult> FileAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await MtStore.V(ctx).GetAsync(id, ct) is not DataRow v) return PluginResult.NotFound();
        var stream = await ctx.Files.OpenAsync(MtStore.FileName(id), ct);
        if (stream is null) return PluginResult.NotFound();
        using (stream) using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms, ct);
            return PluginResult.File(ms.ToArray(), "application/json", "submission-" + id + ".json");
        }
    }

    // ---- the store's entries ------------------------------------------------------------------------------------

    private static async Task<PluginResult> EntriesAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("id"), out var pid) && await MtStore.E(ctx).GetAsync(pid, ct) is DataRow pe)
        {
            switch (ctx.Request.Action)
            {
                case "hide":
                case "show":
                    await MtStore.E(ctx).UpdateAsync(pid, new Dictionary<string, object?> { ["hidden"] = ctx.Request.Action == "hide" }, ct);
                    return PluginResult.Redirect(Root + "entries", ctx.T(ctx.Request.Action == "hide" ? "mt.a.hiddenNotice" : "mt.a.shownNotice", pe.Text("title")));
                case "delete":
                    await MtStore.DeleteEntryAsync(ctx, pid, ct);
                    ctx.Log($"Entry \"{pe.Text("title")}\" deleted by {ctx.User.Name}");
                    return PluginResult.Redirect(Root + "entries", ctx.T("mt.a.deletedNotice", pe.Text("title")));
            }
        }

        var rows = await MtStore.E(ctx).QueryAsync(DataQuery.Everything.OrderBy("updated", true), ct);
        var pending = (await MtStore.V(ctx).QueryAsync(DataQuery.Everything.Eq("status", MtStore.Pending), ct)).Select(v => v.Int("entry_id")).ToHashSet();
        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("mt.a.colTitle")), Ui.Column(ctx.T("mt.a.colState")), Ui.Column(ctx.T("mt.f.version")), Ui.Column(ctx.T("mt.a.colBy")), Ui.Column(ctx.T("mt.colDownloads"), right: true), Ui.Column("", actions: true) },
            rows.Select(e =>
            {
                UiNode state = MtStore.IsVisible(e) ? Ui.Badge(ctx.T("mt.a.live"), UiTone.Success)
                    : e.Bool("hidden") ? Ui.Badge(ctx.T("mt.a.hidden"), UiTone.Muted) : Ui.Badge(ctx.T("mt.a.notYet"), UiTone.Warning);
                return Ui.RowFor(e.Text("title") + " " + e.Text("author") + " " + e.Text("tags"),
                    Ui.Link(e.Text("title"), Root + "entries/" + e.Id),
                    pending.Contains(e.Id) ? Ui.Inline(state, Ui.Badge(ctx.T("mt.a.pending"), UiTone.Warning)) : state,
                    e.Text("version"), e.Text("author"), e.Int("downloads"),
                    Ui.Inline(
                        e.Bool("hidden") ? Ui.Action(ctx.T("mt.a.show"), "show", e.Id.ToString(CultureInfo.InvariantCulture))
                                         : Ui.Action(ctx.T("mt.a.hide"), "hide", e.Id.ToString(CultureInfo.InvariantCulture)),
                        Ui.Action(ctx.T("mt.a.delete"), "delete", e.Id.ToString(CultureInfo.InvariantCulture), UiButtonKind.Danger, ctx.T("mt.a.deleteConfirm", e.Text("title")))));
            }),
            searchPlaceholder: ctx.T("mt.a.search"), pageSize: 50, emptyText: ctx.T("mt.a.none"));
        return PluginResult.Page(table, ctx.T("mt.a.tabEntries"));
    }

    private static async Task<PluginResult> EntryAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await MtStore.E(ctx).GetAsync(id, ct) is not DataRow e) return PluginResult.NotFound();
        if (ctx.Request.IsPost && ctx.Request.Action == "save")
        {
            var title = MtStore.Clip(ctx.Request.F("title"), 80);
            if (title.Length < 3) return PluginResult.Redirect(Root + "entries/" + id, ctx.T("mt.err.title"), isError: true);
            var tags = MtStore.ParseTags(ctx.Request.F("tags"));
            await MtStore.E(ctx).UpdateAsync(id, new Dictionary<string, object?>
            {
                ["title"] = title, ["summary"] = MtStore.Clip(ctx.Request.F("summary"), 200), ["description"] = MtStore.Clip(ctx.Request.F("description"), 4000),
                ["tags"] = string.Join(", ", tags), ["tagkey"] = MtStore.TagKey(tags), ["hidden"] = ctx.Request.F("hidden") is "on" or "true" or "1"
            }, ct);
            return PluginResult.Redirect(Root + "entries/" + id, ctx.T("mt.a.saved"));
        }

        var versions = await MtStore.V(ctx).QueryAsync(DataQuery.Everything.Eq("entry_id", id).OrderBy("created", true), ct);
        var form = Ui.Form(
            Ui.Input("title", ctx.T("mt.f.title"), e.Text("title")) with { Required = true },
            Ui.Input("summary", ctx.T("mt.f.summary"), e.Text("summary")),
            Ui.TextArea("description", ctx.T("mt.f.description"), e.Text("description"), 8),
            Ui.Input("tags", ctx.T("mt.f.tags"), e.Text("tags")),
            Ui.Checkbox("hidden", ctx.T("mt.a.hideBox"), e.Bool("hidden")),
            Ui.FormActions(Ui.Submit(ctx.T("mt.a.save"), name: "action", value: "save")));
        var history = Ui.Table(
            new[] { Ui.Column(ctx.T("mt.f.version")), Ui.Column(ctx.T("mt.a.colWhen")), Ui.Column(ctx.T("mt.a.colBy")), Ui.Column(ctx.T("mt.a.colState")) },
            versions.Select(v => Ui.Row(Ui.Link((v.Text("version").Length > 0 ? "v" + v.Text("version") : "#" + v.Id) + (v.Id == e.Int("version_id") ? " ★" : ""), Root + "versions/" + v.Id),
                D(v.Timestamp("created")), v.Text("author"), State(ctx, v.Text("status")))), emptyText: "–");
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(MtPublic.Base + "/" + e.Text("slug"), e.Text("title"),
                MtStore.IsVisible(e) ? Ui.Button(ctx.T("mt.a.viewPublic"), MtPublic.Base + "/" + e.Text("slug"), UiButtonKind.Ghost, small: true) : null,
                Ui.Button(ctx.T("mt.a.back"), Root + "entries", UiButtonKind.Ghost, small: true)),
            Ui.Columns(new UiNode?[] { Ui.Card(ctx.T("mt.a.texts"), form) },
                new UiNode?[]
                {
                    Ui.Card(ctx.T("mt.a.facts"), Ui.KeyValue((ctx.T("mt.a.colBy"), e.Text("author")), (ctx.T("mt.a.email"), e.Text("email")),
                        (ctx.T("mt.colDownloads"), e.Int("downloads").ToString(CultureInfo.InvariantCulture)), (ctx.T("mt.a.created"), D(e.Timestamp("created"))))),
                    Ui.Card(ctx.T("mt.versions"), history)
                })), e.Text("title"));
    }
}
