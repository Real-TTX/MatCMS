// The public side of the template store: the catalogue (/templates), one template's page with its download, the submission form and
// "my submissions" for signed-in members. Everything a visitor or a submitter typed is encoded on output; downloads are only ever the
// stored, cleaned file of an APPROVED version of a visible entry.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

public static class MtPublic
{
    public const string Base = "/templates";
    private const int PageSize = 24;

    public static void Configure(IPluginBuilder b)
    {
        b.Route(Base, CatalogAsync, new PublicRouteOptions { Methods = PluginMethod.Get });
        b.Route(Base + "/submit", SubmitAsync, new PublicRouteOptions { RequireMember = true, Antiforgery = true, PostsPerMinute = 10 });
        b.Route(Base + "/mine", MineAsync, new PublicRouteOptions { Methods = PluginMethod.Get, RequireMember = true });
        b.Route(Base + "/{slug}", DetailAsync, new PublicRouteOptions { Methods = PluginMethod.Get });
        b.Route(Base + "/{slug}/download", DownloadAsync, new PublicRouteOptions { Methods = PluginMethod.Get });

        b.Sitemap(async (ctx, ct) =>
            (await MtStore.E(ctx).QueryAsync(MtStore.Visible.OrderBy("updated", true), ct))
                .Select(e => new SitemapEntry(Base + "/" + e.Text("slug"), e.Timestamp("updated")))
                .Prepend(new SitemapEntry(Base)).ToList());
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    public static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string U(string? s) => Uri.EscapeDataString(s ?? "");

    public static string Date(DateTime? d) => d is DateTime x ? x.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "";

    /// <summary>Plain text with paragraphs and line breaks — no markup from the submitter survives.</summary>
    public static string Paragraphs(string text) =>
        string.Concat((text ?? "").Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => "<p>" + H(p.Trim()).Replace("\n", "<br>") + "</p>"));

    private static PluginResult Page(IPluginContext ctx, string body, string title, string? description = null) =>
        PluginResult.Page("<style>" + MtCss.Css + "</style><section class=\"section\"><div class=\"container\"><div class=\"mtpl\">" + body + "</div></div></section>",
            title, description);

    private static string Chips(string tags, bool links) =>
        string.Concat(MtStore.ParseTags(tags).Select(t => links
            ? "<a class=\"mtpl-chip\" href=\"" + Base + "?tag=" + U(t) + "\">" + H(t) + "</a>"
            : "<span class=\"mtpl-chip\">" + H(t) + "</span>"));

    private static string Meta(IPluginContext ctx, DataRow e) =>
        "<span>" + H(ctx.T("mt.by", e.Text("author").Length > 0 ? e.Text("author") : ctx.T("mt.anonymous"))) + "</span>" +
        (e.Text("version").Length > 0 ? "<span>v" + H(e.Text("version")) + "</span>" : "") +
        "<span>" + H(ctx.T(e.Int("template_count") == 1 ? "mt.count1" : "mt.countN", e.Int("template_count"))) + "</span>" +
        "<span>" + H(ctx.T("mt.downloads", e.Int("downloads"))) + "</span>";

    // ---- catalogue ------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> CatalogAsync(IPluginContext ctx, CancellationToken ct)
    {
        var q = MtStore.Clip(ctx.Request.Q("q"), 80);
        var tag = MtStore.Clip(ctx.Request.Q("tag"), 30);
        var sort = ctx.Request.Q("sort") is "popular" or "name" ? ctx.Request.Q("sort") : "new";
        int.TryParse(ctx.Request.Q("page"), out var page);
        page = Math.Max(1, page);

        var query = MtStore.Visible;
        if (q.Length > 0)
            query = query.Either(new DataFilter("title", DataOp.Contains, q), new DataFilter("summary", DataOp.Contains, q),
                new DataFilter("tags", DataOp.Contains, q), new DataFilter("sensor_types", DataOp.Contains, q), new DataFilter("author", DataOp.Contains, q));
        if (tag.Length > 0) query = query.Where("tagkey", DataOp.Contains, "," + tag.ToLowerInvariant() + ",");
        query = sort switch { "popular" => query.OrderBy("downloads", true).OrderBy("updated", true), "name" => query.OrderBy("title"), _ => query.OrderBy("updated", true) };

        var total = await MtStore.E(ctx).CountAsync(query, ct);
        var pages = (int)Math.Max(1, (total + PageSize - 1) / PageSize);
        page = Math.Min(page, pages);
        var rows = await MtStore.E(ctx).QueryAsync(query.Page((page - 1) * PageSize, PageSize), ct);

        var sb = new StringBuilder();
        sb.Append("<header class=\"mtpl-head\"><div><h1>").Append(H(ctx.T("mt.title"))).Append("</h1>");
        var intro = ctx.Settings.Get("intro");
        if (!string.IsNullOrWhiteSpace(intro)) sb.Append("<div class=\"mtpl-lead\">").Append(Paragraphs(intro)).Append("</div>");
        sb.Append("</div><div class=\"mtpl-actions\"><a class=\"mtpl-btn\" href=\"").Append(Base).Append("/submit\">").Append(H(ctx.T("mt.submit"))).Append("</a>");
        if (ctx.Member is not null) sb.Append("<a class=\"mtpl-btn ghost\" href=\"").Append(Base).Append("/mine\">").Append(H(ctx.T("mt.mine"))).Append("</a>");
        sb.Append("</div></header>");

        sb.Append("<form class=\"mtpl-bar\" method=\"get\" action=\"").Append(Base).Append("\">")
          .Append("<input type=\"search\" name=\"q\" value=\"").Append(H(q)).Append("\" placeholder=\"").Append(H(ctx.T("mt.searchPh"))).Append("\" aria-label=\"").Append(H(ctx.T("mt.search"))).Append("\">");
        if (tag.Length > 0) sb.Append("<input type=\"hidden\" name=\"tag\" value=\"").Append(H(tag)).Append("\">");
        sb.Append("<select name=\"sort\" aria-label=\"").Append(H(ctx.T("mt.sort"))).Append("\">");
        foreach (var (v, k) in new[] { ("new", "mt.sortNew"), ("popular", "mt.sortPopular"), ("name", "mt.sortName") })
            sb.Append("<option value=\"").Append(v).Append('"').Append(v == sort ? " selected" : "").Append('>').Append(H(ctx.T(k))).Append("</option>");
        sb.Append("</select><button class=\"mtpl-btn\" type=\"submit\">").Append(H(ctx.T("mt.search"))).Append("</button></form>");

        if (tag.Length > 0)
            sb.Append("<p class=\"mtpl-active\">").Append(H(ctx.T("mt.taggedWith"))).Append(" <span class=\"mtpl-chip\">").Append(H(tag))
              .Append("</span> <a href=\"").Append(Base).Append(q.Length > 0 ? "?q=" + U(q) : "").Append("\">").Append(H(ctx.T("mt.clear"))).Append("</a></p>");

        if (rows.Count == 0)
            sb.Append("<div class=\"mtpl-empty\">").Append(H(ctx.T(q.Length > 0 || tag.Length > 0 ? "mt.noHits" : "mt.noneYet"))).Append("</div>");
        else
        {
            sb.Append("<div class=\"mtpl-grid\">");
            foreach (var e in rows)
            {
                var href = Base + "/" + e.Text("slug");
                sb.Append("<article class=\"mtpl-card\"><h2><a href=\"").Append(H(href)).Append("\">").Append(H(e.Text("title"))).Append("</a></h2>");
                if (e.Text("summary").Length > 0) sb.Append("<p>").Append(H(e.Text("summary"))).Append("</p>");
                sb.Append("<div class=\"mtpl-meta\">").Append(Meta(ctx, e)).Append("</div>");
                if (e.Text("tags").Length > 0) sb.Append("<div class=\"mtpl-chips\">").Append(Chips(e.Text("tags"), true)).Append("</div>");
                sb.Append("</article>");
            }
            sb.Append("</div>");
        }

        if (pages > 1)
        {
            string Link(int p) => Base + "?" + string.Join("&", new[] { q.Length > 0 ? "q=" + U(q) : "", tag.Length > 0 ? "tag=" + U(tag) : "", sort != "new" ? "sort=" + sort : "", "page=" + p }.Where(x => x.Length > 0));
            sb.Append("<nav class=\"mtpl-pager\">");
            if (page > 1) sb.Append("<a href=\"").Append(H(Link(page - 1))).Append("\">&larr; ").Append(H(ctx.T("mt.prev"))).Append("</a>");
            sb.Append("<span>").Append(H(ctx.T("mt.pageOf", page, pages))).Append("</span>");
            if (page < pages) sb.Append("<a href=\"").Append(H(Link(page + 1))).Append("\">").Append(H(ctx.T("mt.next"))).Append(" &rarr;</a>");
            sb.Append("</nav>");
        }
        return Page(ctx, sb.ToString(), ctx.T("mt.title"), ctx.T("mt.metaDescription"));
    }

    // ---- one template ---------------------------------------------------------------------------------------------

    private static async Task<PluginResult> DetailAsync(IPluginContext ctx, CancellationToken ct)
    {
        var e = await MtStore.EntryBySlugAsync(ctx, ctx.Request.R("slug"), ct);
        var own = e is not null && ctx.Member is not null && e.Int("member_id") == ctx.Member.Id;
        if (e is null || !MtStore.IsVisible(e)) return PluginResult.NotFound();

        var live = await MtStore.V(ctx).GetAsync(e.Int("version_id"), ct);
        var history = (await MtStore.V(ctx).QueryAsync(DataQuery.Everything.Eq("entry_id", e.Id).Eq("status", MtStore.Approved).OrderBy("decided", true), ct)).ToList();
        var href = Base + "/" + e.Text("slug");

        var sb = new StringBuilder();
        sb.Append("<nav class=\"mtpl-crumbs\"><a href=\"").Append(Base).Append("\">").Append(H(ctx.T("mt.title"))).Append("</a></nav>");
        sb.Append("<header class=\"mtpl-head\"><div><h1>").Append(H(e.Text("title"))).Append("</h1>");
        if (e.Text("summary").Length > 0) sb.Append("<p class=\"mtpl-lead\">").Append(H(e.Text("summary"))).Append("</p>");
        sb.Append("<div class=\"mtpl-meta\">").Append(Meta(ctx, e)).Append("<span>").Append(H(ctx.T("mt.updated", Date(e.Timestamp("updated"))))).Append("</span></div>");
        if (e.Text("tags").Length > 0) sb.Append("<div class=\"mtpl-chips\">").Append(Chips(e.Text("tags"), true)).Append("</div>");
        sb.Append("</div><div class=\"mtpl-actions\"><a class=\"mtpl-btn\" href=\"").Append(H(href)).Append("/download\" download>").Append(H(ctx.T("mt.download"))).Append("</a>");
        if (own) sb.Append("<a class=\"mtpl-btn ghost\" href=\"").Append(Base).Append("/submit?entry=").Append(U(e.Text("slug"))).Append("\">").Append(H(ctx.T("mt.newVersion"))).Append("</a>");
        sb.Append("</div></header>");

        sb.Append("<div class=\"mtpl-cols\"><div>");
        if (e.Text("description").Length > 0) sb.Append("<div class=\"mtpl-text\">").Append(Paragraphs(e.Text("description"))).Append("</div>");
        if (live is not null)
        {
            var templates = MtStore.Templates(live);
            sb.Append("<h2>").Append(H(ctx.T("mt.inside"))).Append("</h2><table class=\"mtpl-table\"><thead><tr><th>").Append(H(ctx.T("mt.colName")))
              .Append("</th><th>").Append(H(ctx.T("mt.colSensor"))).Append("</th></tr></thead><tbody>");
            foreach (var t in templates)
                sb.Append("<tr><td>").Append(H(t.Name)).Append("</td><td><code>").Append(H(t.SensorType.Length > 0 ? t.SensorType : t.Scope)).Append("</code></td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</div><aside>");
        sb.Append("<div class=\"mtpl-box\"><h3>").Append(H(ctx.T("mt.howTitle"))).Append("</h3>").Append(Paragraphs(ctx.T("mt.howText"))).Append("</div>");
        if (history.Count > 0)
        {
            sb.Append("<div class=\"mtpl-box\"><h3>").Append(H(ctx.T("mt.versions"))).Append("</h3><ul class=\"mtpl-versions\">");
            foreach (var v in history)
            {
                sb.Append("<li><div><strong>").Append(H(v.Text("version").Length > 0 ? "v" + v.Text("version") : "#" + v.Id)).Append("</strong> <span class=\"mtpl-muted\">")
                  .Append(H(Date(v.Timestamp("decided")))).Append("</span>");
                if (v.Id != e.Int("version_id"))
                    sb.Append(" <a href=\"").Append(H(href)).Append("/download?v=").Append(v.Id).Append("\" download>").Append(H(ctx.T("mt.downloadShort"))).Append("</a>");
                sb.Append("</div>");
                if (v.Text("notes").Length > 0) sb.Append("<div class=\"mtpl-muted\">").Append(H(v.Text("notes"))).Append("</div>");
                sb.Append("</li>");
            }
            sb.Append("</ul></div>");
        }
        sb.Append("</aside></div>");
        return Page(ctx, sb.ToString(), e.Text("title") + " – " + ctx.T("mt.title"), e.Text("summary"));
    }

    private static async Task<PluginResult> DownloadAsync(IPluginContext ctx, CancellationToken ct)
    {
        var e = await MtStore.EntryBySlugAsync(ctx, ctx.Request.R("slug"), ct);
        if (e is null || !MtStore.IsVisible(e)) return PluginResult.NotFound();
        var versionId = long.TryParse(ctx.Request.Q("v"), NumberStyles.None, CultureInfo.InvariantCulture, out var asked) ? asked : e.Int("version_id");
        var v = await MtStore.V(ctx).GetAsync(versionId, ct);
        if (v is null || v.Int("entry_id") != e.Id || v.Text("status") != MtStore.Approved) return PluginResult.NotFound();
        var stream = await ctx.Files.OpenAsync(MtStore.FileName(v.Id), ct);
        if (stream is null) return PluginResult.NotFound();
        byte[] bytes;
        using (stream) using (var ms = new MemoryStream()) { await stream.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
        await MtStore.E(ctx).IncrementAsync(e.Id, "downloads", 1, null, ct);
        return PluginResult.File(bytes, "application/json", MtFile.DownloadName(e.Text("slug"), v.Text("version")))
            .WithHeader("X-Content-Type-Options", "nosniff");
    }

    // ---- submitting ---------------------------------------------------------------------------------------------

    private static async Task<PluginResult> SubmitAsync(IPluginContext ctx, CancellationToken ct)
    {
        var member = ctx.Member!;
        // An update of the member's own entry (?entry=slug), or a new one.
        var entrySlug = ctx.Request.IsPost ? ctx.Request.F("entry") : ctx.Request.Q("entry");
        DataRow? entry = null;
        if (entrySlug.Length > 0)
        {
            entry = await MtStore.EntryBySlugAsync(ctx, entrySlug, ct);
            if (entry is null || entry.Int("member_id") != member.Id) return PluginResult.NotFound();
        }

        if (!ctx.Request.IsPost)
        {
            var values = new Dictionary<string, string>
            {
                ["title"] = entry?.Text("title") ?? "", ["summary"] = entry?.Text("summary") ?? "", ["description"] = entry?.Text("description") ?? "",
                ["tags"] = entry?.Text("tags") ?? "", ["version"] = "", ["notes"] = ""
            };
            return Page(ctx, Form(ctx, entry, values, null), ctx.T(entry is null ? "mt.submitTitle" : "mt.updateTitle"));
        }

        var form = new Dictionary<string, string>
        {
            ["title"] = MtStore.Clip(ctx.Request.F("title"), 80), ["summary"] = MtStore.Clip(ctx.Request.F("summary"), 200),
            ["description"] = MtStore.Clip(ctx.Request.F("description"), 4000), ["tags"] = string.Join(", ", MtStore.ParseTags(ctx.Request.F("tags"))),
            ["version"] = MtStore.Clip(ctx.Request.F("version"), 20), ["notes"] = MtStore.Clip(ctx.Request.F("notes"), 500)
        };
        PluginResult Fail(string message) => Page(ctx, Form(ctx, entry, form, message), ctx.T(entry is null ? "mt.submitTitle" : "mt.updateTitle"));

        if (form["title"].Length < 3) return Fail(ctx.T("mt.err.title"));
        if (ctx.Request.F("terms") != "1") return Fail(ctx.T("mt.err.terms"));
        var waiting = await MtStore.V(ctx).CountAsync(DataQuery.Everything.Eq("member_id", member.Id).Eq("status", MtStore.Pending), ct);
        var max = (long)ctx.Settings.GetNumber("maxPending", 5);
        if (max > 0 && waiting >= max) return Fail(ctx.T("mt.err.tooManyPending", max));

        var upload = ctx.Request.File("file");
        if (upload is null || upload.Length == 0) return Fail(ctx.T("mt.err.noFile"));
        if (upload.Length > MtFile.MaxBytes) return Fail(ctx.T("mt.err.tooLarge"));
        MtParsed parsed;
        try
        {
            byte[] bytes;
            using (var s = upload.OpenRead()) using (var ms = new MemoryStream()) { await s.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
            parsed = MtFile.Read(bytes);
        }
        catch (MtFileException ex) { return Fail(ctx.T(ex.TextKey, ex.Args)); }

        var (saved, versionId) = await MtStore.SubmitAsync(ctx, member, entry,
            new MtStore.Submission(form["title"], form["summary"], form["description"], MtStore.ParseTags(form["tags"]), form["version"], form["notes"]), parsed, ct);
        ctx.Log($"Submission #{versionId} \"{form["title"]}\" by member {member.Id} ({parsed.Templates.Count} templates, {parsed.Removed.Count} values removed)");
        await MtMail.NotifyOperatorAsync(ctx, saved, versionId, form, member, parsed, ct);
        return PluginResult.Redirect(Base + "/mine?sent=" + versionId);
    }

    private static string Form(IPluginContext ctx, DataRow? entry, IReadOnlyDictionary<string, string> v, string? error)
    {
        string Field(string name, string labelKey, string input, string? helpKey = null) =>
            "<label class=\"mtpl-field\"><span>" + H(ctx.T(labelKey)) + "</span>" + input + (helpKey is null ? "" : "<small>" + H(ctx.T(helpKey)) + "</small>") + "</label>";
        string Input(string name, int max, bool required = false, string? ph = null) =>
            "<input name=\"" + name + "\" value=\"" + H(v[name]) + "\" maxlength=\"" + max + "\"" + (required ? " required" : "") + (ph is null ? "" : " placeholder=\"" + H(ph) + "\"") + ">";

        var sb = new StringBuilder();
        sb.Append("<nav class=\"mtpl-crumbs\"><a href=\"").Append(Base).Append("\">").Append(H(ctx.T("mt.title"))).Append("</a>");
        if (entry is not null) sb.Append(" / <a href=\"").Append(Base).Append('/').Append(H(entry.Text("slug"))).Append("\">").Append(H(entry.Text("title"))).Append("</a>");
        sb.Append("</nav><h1>").Append(H(ctx.T(entry is null ? "mt.submitTitle" : "mt.updateTitle"))).Append("</h1>");
        sb.Append("<div class=\"mtpl-lead\">").Append(Paragraphs(ctx.T("mt.submitIntro"))).Append("</div>");
        if (error is not null) sb.Append("<div class=\"mtpl-alert\" role=\"alert\">").Append(H(error)).Append("</div>");
        sb.Append("<form class=\"mtpl-form\" method=\"post\" enctype=\"multipart/form-data\" action=\"").Append(Base).Append("/submit\">").Append(ctx.Antiforgery);
        if (entry is not null) sb.Append("<input type=\"hidden\" name=\"entry\" value=\"").Append(H(entry.Text("slug"))).Append("\">");
        sb.Append(Field("file", "mt.f.file", "<input type=\"file\" name=\"file\" accept=\".json,application/json\" required>", "mt.f.fileHelp"));
        sb.Append(Field("title", "mt.f.title", Input("title", 80, true)));
        sb.Append(Field("summary", "mt.f.summary", Input("summary", 200), "mt.f.summaryHelp"));
        sb.Append(Field("description", "mt.f.description", "<textarea name=\"description\" rows=\"7\" maxlength=\"4000\">" + H(v["description"]) + "</textarea>", "mt.f.descriptionHelp"));
        sb.Append("<div class=\"mtpl-row\">");
        sb.Append(Field("tags", "mt.f.tags", Input("tags", 300, ph: ctx.T("mt.f.tagsPh")), "mt.f.tagsHelp"));
        sb.Append(Field("version", "mt.f.version", Input("version", 20, ph: "1.0")));
        sb.Append("</div>");
        if (entry is not null) sb.Append(Field("notes", "mt.f.notes", Input("notes", 500)));
        var terms = ctx.Settings.Get("terms");
        sb.Append("<label class=\"mtpl-check\"><input type=\"checkbox\" name=\"terms\" value=\"1\" required> <span>")
          .Append(H(string.IsNullOrWhiteSpace(terms) ? ctx.T("mt.f.termsDefault") : terms)).Append("</span></label>");
        sb.Append("<div class=\"mtpl-actions\"><button class=\"mtpl-btn\" type=\"submit\">").Append(H(ctx.T("mt.f.send"))).Append("</button>")
          .Append("<a class=\"mtpl-btn ghost\" href=\"").Append(Base).Append("/mine\">").Append(H(ctx.T("mt.mine"))).Append("</a></div></form>");
        return sb.ToString();
    }

    // ---- my submissions -----------------------------------------------------------------------------------------

    private static async Task<PluginResult> MineAsync(IPluginContext ctx, CancellationToken ct)
    {
        var member = ctx.Member!;
        var entries = await MtStore.E(ctx).QueryAsync(DataQuery.Everything.Eq("member_id", member.Id).OrderBy("updated", true), ct);
        var versions = await MtStore.V(ctx).QueryAsync(DataQuery.Everything.Eq("member_id", member.Id).OrderBy("created", true), ct);

        var sb = new StringBuilder();
        sb.Append("<nav class=\"mtpl-crumbs\"><a href=\"").Append(Base).Append("\">").Append(H(ctx.T("mt.title"))).Append("</a></nav>");
        sb.Append("<header class=\"mtpl-head\"><div><h1>").Append(H(ctx.T("mt.mine"))).Append("</h1></div><div class=\"mtpl-actions\"><a class=\"mtpl-btn\" href=\"")
          .Append(Base).Append("/submit\">").Append(H(ctx.T("mt.submit"))).Append("</a></div></header>");
        if (ctx.Request.Q("sent").Length > 0) sb.Append("<div class=\"mtpl-ok\" role=\"status\">").Append(H(ctx.T("mt.sent"))).Append("</div>");

        if (entries.Count == 0) sb.Append("<div class=\"mtpl-empty\">").Append(H(ctx.T("mt.mineEmpty"))).Append("</div>");
        else
        {
            sb.Append("<table class=\"mtpl-table\"><thead><tr><th>").Append(H(ctx.T("mt.colTemplate"))).Append("</th><th>").Append(H(ctx.T("mt.colState")))
              .Append("</th><th>").Append(H(ctx.T("mt.colDownloads"))).Append("</th><th></th></tr></thead><tbody>");
            foreach (var e in entries)
            {
                var latest = versions.FirstOrDefault(v => v.Int("entry_id") == e.Id);
                var state = latest is null ? "" : latest.Text("status") switch
                {
                    MtStore.Pending => "<span class=\"mtpl-badge warn\">" + H(ctx.T("mt.statePending", latest.Text("version"))) + "</span>",
                    MtStore.Rejected => "<span class=\"mtpl-badge err\">" + H(ctx.T("mt.stateRejected")) + "</span>" +
                        (latest.Text("reason").Length > 0 ? "<div class=\"mtpl-muted\">" + H(latest.Text("reason")) + "</div>" : ""),
                    _ => ""
                };
                if (MtStore.IsVisible(e)) state = "<span class=\"mtpl-badge ok\">" + H(ctx.T("mt.stateLive", e.Text("version"))) + "</span> " + state;
                else if (e.Bool("hidden")) state = "<span class=\"mtpl-badge\">" + H(ctx.T("mt.stateHidden")) + "</span> " + state;
                var title = MtStore.IsVisible(e) ? "<a href=\"" + Base + "/" + H(e.Text("slug")) + "\">" + H(e.Text("title")) + "</a>" : H(latest?.Text("title") ?? e.Text("title"));
                sb.Append("<tr><td>").Append(title).Append("</td><td>").Append(state).Append("</td><td>").Append(e.Int("downloads"))
                  .Append("</td><td><a href=\"").Append(Base).Append("/submit?entry=").Append(U(e.Text("slug"))).Append("\">").Append(H(ctx.T("mt.newVersion"))).Append("</a></td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        return Page(ctx, sb.ToString(), ctx.T("mt.mine"));
    }
}
