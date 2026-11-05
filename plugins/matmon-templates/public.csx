// The public side of the template store: the catalogue (/templates), one set's page with its download, the submission form and
// "my submissions" for signed-in members. The store itself is on Matmon Cloud (cloud.csx): this site draws its public catalogue and
// forwards submissions; review, mails and counting happen there. Everything from the cloud or a visitor is encoded on output.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

public static class MtPublic
{
    public const string Base = "/templates";

    public static void Configure(IPluginBuilder b)
    {
        b.Route(Base, CatalogAsync, new PublicRouteOptions { Methods = PluginMethod.Get });
        b.Route(Base + "/submit", SubmitAsync, new PublicRouteOptions { RequireMember = true, Antiforgery = true, PostsPerMinute = 10 });
        b.Route(Base + "/mine", MineAsync, new PublicRouteOptions { Methods = PluginMethod.Get, RequireMember = true });
        b.Route(Base + "/{slug}", DetailAsync, new PublicRouteOptions { Methods = PluginMethod.Get });
        b.Route(Base + "/{slug}/download", DownloadAsync, new PublicRouteOptions { Methods = PluginMethod.Get });

        b.Sitemap(async (ctx, ct) =>
        {
            var list = new List<SitemapEntry> { new SitemapEntry(Base) };
            try
            {
                // The sitemap lists the newest sets of the first pages; a cloud that does not answer leaves just the overview.
                for (var page = 1; page <= 10; page++)
                {
                    var data = await MtCloud.GetPublicAsync(ctx, "/api/store/templates?page=" + page, ct);
                    var items = data?["items"] as JsonArray;
                    if (items is null || items.Count == 0) break;
                    list.AddRange(items.Select(i => new SitemapEntry(Base + "/" + MtCloud.S(i, "slug"), MtCloud.D(i, "updated"))));
                    if (page * MtCloud.I(data, "pageSize") >= MtCloud.I(data, "total")) break;
                }
            }
            catch (MtCloudException) { }
            return list;
        });
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    public static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string U(string? s) => Uri.EscapeDataString(s ?? "");

    public static string Date(DateTime? d) => d is DateTime x ? x.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "";

    public static string Clip(string? text, int max)
    {
        var v = (text ?? "").Replace("\r\n", "\n").Trim();
        return v.Length > max ? v[..max].TrimEnd() : v;
    }

    /// <summary>Plain text with paragraphs and line breaks — no markup from the submitter survives.</summary>
    public static string Paragraphs(string text) =>
        string.Concat((text ?? "").Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => "<p>" + H(p.Trim()).Replace("\n", "<br>") + "</p>"));

    private static PluginResult Page(IPluginContext ctx, string body, string title, string? description = null) =>
        PluginResult.Page("<style>" + MtCss.Css + "</style><section class=\"section\"><div class=\"container\"><div class=\"mtpl\">" + body + "</div></div></section>",
            title, description);

    private static PluginResult Unavailable(IPluginContext ctx, string message) =>
        Page(ctx, "<nav class=\"mtpl-crumbs\"><a href=\"" + Base + "\">" + H(ctx.T("mt.title")) + "</a></nav><div class=\"mtpl-alert\" role=\"alert\">" + H(message) + "</div>", ctx.T("mt.title"));

    private static string Chips(IEnumerable<string> tags, bool links) =>
        string.Concat(tags.Select(t => links
            ? "<a class=\"mtpl-chip\" href=\"" + Base + "?tag=" + U(t) + "\">" + H(t) + "</a>"
            : "<span class=\"mtpl-chip\">" + H(t) + "</span>"));

    private static string Meta(IPluginContext ctx, JsonNode? e)
    {
        var author = MtCloud.S(e, "author");
        var count = MtCloud.I(e, "templateCount");
        return (author.Length > 0 ? "<span>" + H(ctx.T("mt.by", author)) + "</span>" : "") +
            (MtCloud.S(e, "version").Length > 0 ? "<span>v" + H(MtCloud.S(e, "version")) + "</span>" : "") +
            "<span>" + H(ctx.T(count == 1 ? "mt.count1" : "mt.countN", count)) + "</span>" +
            "<span>" + H(ctx.T("mt.downloads", MtCloud.I(e, "downloads"))) + "</span>";
    }

    // ---- catalogue ------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> CatalogAsync(IPluginContext ctx, CancellationToken ct)
    {
        var q = Clip(ctx.Request.Q("q"), 80);
        var tag = Clip(ctx.Request.Q("tag"), 30);
        var sort = ctx.Request.Q("sort") is "popular" or "name" ? ctx.Request.Q("sort") : "new";
        int.TryParse(ctx.Request.Q("page"), out var page);
        page = Math.Max(1, page);

        JsonNode? data;
        try
        {
            data = await MtCloud.GetPublicAsync(ctx, "/api/store/templates?" + string.Join("&", new[]
            {
                q.Length > 0 ? "q=" + U(q) : "", tag.Length > 0 ? "tag=" + U(tag) : "", sort != "new" ? "sort=" + sort : "", page > 1 ? "page=" + page : ""
            }.Where(x => x.Length > 0)), ct);
        }
        catch (MtCloudException ex) { return Unavailable(ctx, ex.Message); }

        var rows = (data?["items"] as JsonArray)?.ToList() ?? new List<JsonNode?>();
        var total = MtCloud.I(data, "total");
        var pageSize = Math.Max(1, MtCloud.I(data, "pageSize"));
        var pages = Math.Max(1, (total + pageSize - 1) / pageSize);

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

        var allTags = MtCloud.A(data, "allTags");
        if (tag.Length > 0)
            sb.Append("<p class=\"mtpl-active\">").Append(H(ctx.T("mt.taggedWith"))).Append(" <span class=\"mtpl-chip\">").Append(H(tag))
              .Append("</span> <a href=\"").Append(Base).Append(q.Length > 0 ? "?q=" + U(q) : "").Append("\">").Append(H(ctx.T("mt.clear"))).Append("</a></p>");
        else if (allTags.Length > 0)
            sb.Append("<div class=\"mtpl-chips mtpl-tagrow\">").Append(Chips(allTags, true)).Append("</div>");

        if (rows.Count == 0)
            sb.Append("<div class=\"mtpl-empty\">").Append(H(ctx.T(q.Length > 0 || tag.Length > 0 ? "mt.noHits" : "mt.noneYet"))).Append("</div>");
        else
        {
            sb.Append("<div class=\"mtpl-grid\">");
            foreach (var e in rows)
            {
                var href = Base + "/" + MtCloud.S(e, "slug");
                sb.Append("<article class=\"mtpl-card\"><h2><a href=\"").Append(H(href)).Append("\">").Append(H(MtCloud.S(e, "title"))).Append("</a></h2>");
                if (MtCloud.S(e, "summary").Length > 0) sb.Append("<p>").Append(H(MtCloud.S(e, "summary"))).Append("</p>");
                sb.Append("<div class=\"mtpl-meta\">").Append(Meta(ctx, e)).Append("</div>");
                var tags = MtCloud.A(e, "tags");
                if (tags.Length > 0) sb.Append("<div class=\"mtpl-chips\">").Append(Chips(tags, true)).Append("</div>");
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

    // ---- one set ------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> DetailAsync(IPluginContext ctx, CancellationToken ct)
    {
        var slug = ctx.Request.R("slug");
        JsonNode? e;
        try { e = await MtCloud.GetPublicAsync(ctx, "/api/store/templates/" + U(slug), ct); }
        catch (MtCloudException ex) { return Unavailable(ctx, ex.Message); }
        if (e is null) return PluginResult.NotFound();

        var href = Base + "/" + MtCloud.S(e, "slug");
        var sb = new StringBuilder();
        sb.Append("<nav class=\"mtpl-crumbs\"><a href=\"").Append(Base).Append("\">").Append(H(ctx.T("mt.title"))).Append("</a></nav>");
        sb.Append("<header class=\"mtpl-head\"><div><h1>").Append(H(MtCloud.S(e, "title"))).Append("</h1>");
        if (MtCloud.S(e, "summary").Length > 0) sb.Append("<p class=\"mtpl-lead\">").Append(H(MtCloud.S(e, "summary"))).Append("</p>");
        sb.Append("<div class=\"mtpl-meta\">").Append(Meta(ctx, e)).Append("<span>").Append(H(ctx.T("mt.updated", Date(MtCloud.D(e, "updated"))))).Append("</span></div>");
        var tags = MtCloud.A(e, "tags");
        if (tags.Length > 0) sb.Append("<div class=\"mtpl-chips\">").Append(Chips(tags, true)).Append("</div>");
        sb.Append("</div><div class=\"mtpl-actions\"><a class=\"mtpl-btn\" href=\"").Append(H(href)).Append("/download\" download>").Append(H(ctx.T("mt.download"))).Append("</a>");
        if (ctx.Member is not null) sb.Append("<a class=\"mtpl-btn ghost\" href=\"").Append(Base).Append("/submit?entry=").Append(U(MtCloud.S(e, "slug"))).Append("\" rel=\"nofollow\">").Append(H(ctx.T("mt.newVersion"))).Append("</a>");
        sb.Append("</div></header>");

        sb.Append("<div class=\"mtpl-cols\"><div>");
        if (MtCloud.S(e, "description").Length > 0) sb.Append("<div class=\"mtpl-text\">").Append(Paragraphs(MtCloud.S(e, "description"))).Append("</div>");
        if (e["templates"] is JsonArray templates && templates.Count > 0)
        {
            sb.Append("<h2>").Append(H(ctx.T("mt.inside"))).Append("</h2><table class=\"mtpl-table\"><thead><tr><th>").Append(H(ctx.T("mt.colName")))
              .Append("</th><th>").Append(H(ctx.T("mt.colSensor"))).Append("</th></tr></thead><tbody>");
            foreach (var t in templates)
            {
                var type = MtCloud.S(t, "sensorType");
                sb.Append("<tr><td>").Append(H(MtCloud.S(t, "name"))).Append("</td><td><code>").Append(H(type.Length > 0 ? type : MtCloud.S(t, "scope"))).Append("</code></td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</div><aside>");
        sb.Append("<div class=\"mtpl-box\"><h3>").Append(H(ctx.T("mt.howTitle"))).Append("</h3>").Append(Paragraphs(ctx.T("mt.howText"))).Append("</div>");
        if (e["versions"] is JsonArray history && history.Count > 0)
        {
            var current = MtCloud.S(e, "version");
            sb.Append("<div class=\"mtpl-box\"><h3>").Append(H(ctx.T("mt.versions"))).Append("</h3><ul class=\"mtpl-versions\">");
            foreach (var v in history)
            {
                var name = MtCloud.S(v, "version");
                sb.Append("<li><div><strong>v").Append(H(name)).Append("</strong> <span class=\"mtpl-muted\">").Append(H(Date(MtCloud.D(v, "date")))).Append("</span>");
                if (name != current) sb.Append(" <a href=\"").Append(H(href)).Append("/download?v=").Append(U(name)).Append("\" download>").Append(H(ctx.T("mt.downloadShort"))).Append("</a>");
                sb.Append("</div>");
                if (MtCloud.S(v, "notes").Length > 0) sb.Append("<div class=\"mtpl-muted\">").Append(H(MtCloud.S(v, "notes"))).Append("</div>");
                sb.Append("</li>");
            }
            sb.Append("</ul></div>");
        }
        sb.Append("</aside></div>");
        return Page(ctx, sb.ToString(), MtCloud.S(e, "title") + " – " + ctx.T("mt.title"), MtCloud.S(e, "summary"));
    }

    private static async Task<PluginResult> DownloadAsync(IPluginContext ctx, CancellationToken ct)
    {
        try
        {
            var file = await MtCloud.DownloadAsync(ctx, ctx.Request.R("slug"), Clip(ctx.Request.Q("v"), 40), ct);
            if (file is null) return PluginResult.NotFound();
            return PluginResult.File(file.Value.Bytes, "application/json", file.Value.FileName).WithHeader("X-Content-Type-Options", "nosniff");
        }
        catch (MtCloudException ex) { return Unavailable(ctx, ex.Message); }
    }

    // ---- submitting ---------------------------------------------------------------------------------------------

    private static async Task<PluginResult> SubmitAsync(IPluginContext ctx, CancellationToken ct)
    {
        var member = ctx.Member!;
        var authorRef = MtCloud.AuthorRef(ctx, member.Id);
        // An update of the member's own entry (?entry=slug), or a new one. The cloud decides what is "own".
        var entrySlug = Clip(ctx.Request.IsPost ? ctx.Request.F("entry") : ctx.Request.Q("entry"), 80);
        JsonObject? entry = null;
        if (entrySlug.Length > 0)
        {
            try { entry = await MtCloud.OwnEntryAsync(ctx, authorRef, entrySlug, ct); }
            catch (MtCloudException ex) { return Unavailable(ctx, ex.Message); }
            if (entry is null) return PluginResult.NotFound();
        }

        if (!ctx.Request.IsPost)
        {
            var values = new Dictionary<string, string>
            {
                ["title"] = MtCloud.S(entry, "title"), ["summary"] = MtCloud.S(entry, "summary"), ["description"] = MtCloud.S(entry, "description"),
                ["tags"] = MtCloud.S(entry, "tags"), ["version"] = "", ["notes"] = ""
            };
            return Page(ctx, Form(ctx, entry, values, null), ctx.T(entry is null ? "mt.submitTitle" : "mt.updateTitle"));
        }

        var form = new Dictionary<string, string>
        {
            ["title"] = Clip(ctx.Request.F("title"), 80), ["summary"] = Clip(ctx.Request.F("summary"), 200),
            ["description"] = Clip(ctx.Request.F("description"), 4000), ["tags"] = Clip(ctx.Request.F("tags"), 300),
            ["version"] = Clip(ctx.Request.F("version"), 20), ["notes"] = Clip(ctx.Request.F("notes"), 500)
        };
        PluginResult Fail(string message) => Page(ctx, Form(ctx, entry, form, message), ctx.T(entry is null ? "mt.submitTitle" : "mt.updateTitle"));

        if (form["title"].Length < 3) return Fail(ctx.T("mt.err.title"));
        if (ctx.Request.F("terms") != "1") return Fail(ctx.T("mt.err.terms"));
        var upload = ctx.Request.File("file");
        if (upload is null || upload.Length == 0) return Fail(ctx.T("mt.err.noFile"));
        if (upload.Length > 2 * 1024 * 1024) return Fail(ctx.T("mt.err.tooLarge"));

        string file;
        using (var s = upload.OpenRead()) using (var reader = new StreamReader(s, Encoding.UTF8)) file = await reader.ReadToEndAsync(ct);

        try
        {
            var answer = await MtCloud.SubmitAsync(ctx, new JsonObject
            {
                ["title"] = form["title"], ["summary"] = form["summary"], ["description"] = form["description"], ["tags"] = form["tags"],
                ["version"] = form["version"], ["notes"] = form["notes"],
                ["authorName"] = member.DisplayName, ["authorEmail"] = member.Email, ["authorRef"] = authorRef,
                ["entry"] = entry is null ? null : entrySlug, ["file"] = file
            }, ct);
            ctx.Log($"Submission \"{form["title"]}\" by member {member.Id} sent to the store ({answer.Removed.Length} values removed)");
            return PluginResult.Redirect(Base + "/mine?sent=1");
        }
        catch (MtCloudException ex) { return Fail(ex.Message); }
    }

    private static string Form(IPluginContext ctx, JsonObject? entry, IReadOnlyDictionary<string, string> v, string? error)
    {
        string Field(string labelKey, string input, string? helpKey = null) =>
            "<label class=\"mtpl-field\"><span>" + H(ctx.T(labelKey)) + "</span>" + input + (helpKey is null ? "" : "<small>" + H(ctx.T(helpKey)) + "</small>") + "</label>";
        string Input(string name, int max, bool required = false, string? ph = null) =>
            "<input name=\"" + name + "\" value=\"" + H(v[name]) + "\" maxlength=\"" + max + "\"" + (required ? " required" : "") + (ph is null ? "" : " placeholder=\"" + H(ph) + "\"") + ">";

        var slug = MtCloud.S(entry, "slug");
        var sb = new StringBuilder();
        sb.Append("<nav class=\"mtpl-crumbs\"><a href=\"").Append(Base).Append("\">").Append(H(ctx.T("mt.title"))).Append("</a>");
        if (entry is not null) sb.Append(" / <a href=\"").Append(Base).Append('/').Append(H(slug)).Append("\">").Append(H(MtCloud.S(entry, "title"))).Append("</a>");
        sb.Append("</nav><h1>").Append(H(ctx.T(entry is null ? "mt.submitTitle" : "mt.updateTitle"))).Append("</h1>");
        sb.Append("<div class=\"mtpl-lead\">").Append(Paragraphs(ctx.T("mt.submitIntro"))).Append("</div>");
        if (error is not null) sb.Append("<div class=\"mtpl-alert\" role=\"alert\">").Append(H(error)).Append("</div>");
        sb.Append("<form class=\"mtpl-form\" method=\"post\" enctype=\"multipart/form-data\" action=\"").Append(Base).Append("/submit\">").Append(ctx.Antiforgery);
        if (entry is not null) sb.Append("<input type=\"hidden\" name=\"entry\" value=\"").Append(H(slug)).Append("\">");
        sb.Append(Field("mt.f.file", "<input type=\"file\" name=\"file\" accept=\".json,application/json\" required>", "mt.f.fileHelp"));
        sb.Append(Field("mt.f.title", Input("title", 80, true)));
        sb.Append(Field("mt.f.summary", Input("summary", 200), "mt.f.summaryHelp"));
        sb.Append(Field("mt.f.description", "<textarea name=\"description\" rows=\"7\" maxlength=\"4000\">" + H(v["description"]) + "</textarea>", "mt.f.descriptionHelp"));
        sb.Append("<div class=\"mtpl-row\">");
        sb.Append(Field("mt.f.tags", Input("tags", 300, ph: ctx.T("mt.f.tagsPh")), "mt.f.tagsHelp"));
        sb.Append(Field("mt.f.version", Input("version", 20, ph: "1.0")));
        sb.Append("</div>");
        if (entry is not null) sb.Append(Field("mt.f.notes", Input("notes", 500)));
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
        JsonArray items;
        try { items = await MtCloud.MineAsync(ctx, MtCloud.AuthorRef(ctx, ctx.Member!.Id), ct); }
        catch (MtCloudException ex) { return Unavailable(ctx, ex.Message); }

        var sb = new StringBuilder();
        sb.Append("<nav class=\"mtpl-crumbs\"><a href=\"").Append(Base).Append("\">").Append(H(ctx.T("mt.title"))).Append("</a></nav>");
        sb.Append("<header class=\"mtpl-head\"><div><h1>").Append(H(ctx.T("mt.mine"))).Append("</h1></div><div class=\"mtpl-actions\"><a class=\"mtpl-btn\" href=\"")
          .Append(Base).Append("/submit\">").Append(H(ctx.T("mt.submit"))).Append("</a></div></header>");
        if (ctx.Request.Q("sent").Length > 0) sb.Append("<div class=\"mtpl-ok\" role=\"status\">").Append(H(ctx.T("mt.sent"))).Append("</div>");

        if (items.Count == 0) sb.Append("<div class=\"mtpl-empty\">").Append(H(ctx.T("mt.mineEmpty"))).Append("</div>");
        else
        {
            sb.Append("<table class=\"mtpl-table\"><thead><tr><th>").Append(H(ctx.T("mt.colTemplate"))).Append("</th><th>").Append(H(ctx.T("mt.f.version")))
              .Append("</th><th>").Append(H(ctx.T("mt.colState"))).Append("</th><th>").Append(H(ctx.T("mt.colSent"))).Append("</th><th></th></tr></thead><tbody>");
            foreach (var v in items)
            {
                var slug = MtCloud.S(v, "slug");
                var live = MtCloud.B(v, "live");
                var state = MtCloud.S(v, "status") switch
                {
                    "pending" => "<span class=\"mtpl-badge warn\">" + H(ctx.T("mt.statePending")) + "</span>",
                    "rejected" => "<span class=\"mtpl-badge err\">" + H(ctx.T("mt.stateRejected")) + "</span>" +
                        (MtCloud.S(v, "reason").Length > 0 ? "<div class=\"mtpl-muted\">" + H(MtCloud.S(v, "reason")) + "</div>" : ""),
                    _ => live ? "<span class=\"mtpl-badge ok\">" + H(ctx.T("mt.stateLive")) + "</span>" : "<span class=\"mtpl-badge\">" + H(ctx.T("mt.stateEarlier")) + "</span>"
                };
                var title = live ? "<a href=\"" + Base + "/" + H(slug) + "\">" + H(MtCloud.S(v, "title")) + "</a>" : H(MtCloud.S(v, "title"));
                sb.Append("<tr><td>").Append(title).Append("</td><td>").Append(H(MtCloud.S(v, "version"))).Append("</td><td>").Append(state)
                  .Append("</td><td>").Append(H(Date(MtCloud.D(v, "created")))).Append("</td><td><a href=\"").Append(Base).Append("/submit?entry=").Append(U(slug)).Append("\">")
                  .Append(H(ctx.T("mt.newVersion"))).Append("</a></td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        return Page(ctx, sb.ToString(), ctx.T("mt.mine"));
    }
}
