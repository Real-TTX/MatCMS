// Matmon live — "What's new": the changelog of Matmon, read from the public commit history of the repository on GitHub
// (Real-TTX/Matmon, branch main). Matmon's commits are written as release notes ("feat(windows): an online setup …"), so they ARE
// the changelog: new things and fixes, by day. Fetched by the server (never by the visitor's browser), at most once an hour,
// the last good list kept when GitHub does not answer. docs/chore/refactor/test/ci/build commits and merges are left out.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

public sealed record MmChange(DateTime Day, bool Fix, string Scope, string Text);

public static class MmChangelog
{
    public const string BlockType = "matmon_changelog";
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(1);
    private static readonly string[] Skip = { "docs", "chore", "refactor", "test", "tests", "ci", "build", "style", "perf", "wip" };
    private static IReadOnlyList<MmChange>? _list;
    private static DateTime _fetched = DateTime.MinValue;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Configure(IPluginBuilder b)
    {
        b.Strings("de", MmChangeText.De);
        b.Strings("en", MmChangeText.En);
        b.Settings.Text("repo", "mmc.set.repo", "Real-TTX/Matmon", "mmc.set.repoHelp")
            .Text("githubApi", "mmc.set.api", "https://api.github.com", "mmc.set.apiHelp");
        b.Block(new BlockSpec(BlockType, "mmc.block.name", "mmc.block.desc")
        {
            Category = "design",
            IconSvg = "<path d=\"M12 8v4l3 2\"/><circle cx=\"12\" cy=\"12\" r=\"9\"/>",
            Fields = new[]
            {
                new FieldSpec("heading", "mmc.f.heading") { Default = "What's new" },
                new FieldSpec("intro", "mmc.f.intro", FieldKind.Textarea),
                new FieldSpec("mode", "mmc.f.mode", FieldKind.Select)
                {
                    Default = "full", Options = new[] { new FieldOption("full", "mmc.o.full"), new FieldOption("teaser", "mmc.o.teaser") }
                },
                new FieldSpec("days", "mmc.f.days") { Default = "21", Help = "mmc.f.daysHelp" },
                new FieldSpec("fixes", "mmc.f.fixes", FieldKind.Select)
                {
                    Default = "yes", ShowWhenField = "mode", ShowWhenValue = "full",
                    Options = new[] { new FieldOption("yes", "mmc.o.yes"), new FieldOption("no", "mmc.o.no") }
                },
                new FieldSpec("moreText", "mmc.f.moreText"),
                new FieldSpec("moreUrl", "mmc.f.moreUrl", FieldKind.Url),
            }
        }, RenderAsync);
        b.Job("changelog", JobSchedule.EveryInterval(TimeSpan.FromMinutes(15)), ctx => RefreshAsync(ctx, CancellationToken.None));
    }

    private static string Repo(IPluginContext ctx)
    {
        var r = (ctx.Settings.Get("repo") ?? "").Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(r, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ? r : "Real-TTX/Matmon";
    }

    // The GitHub API address: a setting only so a test (or a mirror) can stand in; anything but http(s) falls back.
    private static string Api(IPluginContext ctx)
    {
        var a = (ctx.Settings.Get("githubApi") ?? "").Trim().TrimEnd('/');
        return Uri.TryCreate(a, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? a : "https://api.github.com";
    }

    /// <summary>One commit's first line as a change, or null when it is not one a reader cares about.</summary>
    public static MmChange? Parse(DateTime day, string message)
    {
        var line = (message ?? "").Split('\n')[0].Trim();
        if (line.Length == 0 || line.StartsWith("Merge ", StringComparison.Ordinal)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(line, @"^(?<type>[a-z]+)(\((?<scope>[^)]{1,40})\))?!?:\s*(?<text>.+)$");
        string type = "", scope = "", text = line;
        if (m.Success) { type = m.Groups["type"].Value; scope = m.Groups["scope"].Value; text = m.Groups["text"].Value; }
        if (Skip.Contains(type)) return null;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\(#\d+\)\s*$", "").Trim();
        if (text.Length == 0) return null;
        text = char.ToUpperInvariant(text[0]) + text.Substring(1);
        if (text.Length > 180) { var cut = text.LastIndexOf(' ', 176); text = text.Substring(0, cut > 100 ? cut : 176) + " …"; }
        return new MmChange(day.Date, type == "fix", scope, text);
    }

    public static async Task RefreshAsync(IPluginContext ctx, CancellationToken ct, bool force = false)
    {
        if (!force && DateTime.UtcNow - _fetched < Fresh) return;
        if (!await Gate.WaitAsync(0, ct)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Matmon-Live/1.4");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync(Api(ctx) + "/repos/" + Repo(ctx) + "/commits?sha=main&per_page=100", ct);
            if (JsonNode.Parse(json) is JsonArray arr)
            {
                TimeZoneInfo? tz = null;
                try { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); } catch { /* no zone data: UTC days */ }
                var list = new List<MmChange>();
                foreach (var c in arr.OfType<JsonObject>())
                {
                    var msg = c["commit"]?["message"]?.GetValue<string>() ?? "";
                    var when = c["commit"]?["committer"]?["date"]?.GetValue<string>() ?? "";
                    if (!DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
                    var local = tz is null ? at.UtcDateTime : TimeZoneInfo.ConvertTime(at, tz).DateTime;
                    if (Parse(local, msg) is { } ch) list.Add(ch);
                }
                if (list.Count > 0) _list = list;
            }
            _fetched = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ctx.Log("Matmon changelog: " + ex.Message);
            _fetched = DateTime.UtcNow - Fresh + TimeSpan.FromMinutes(10);      // again in ten minutes
        }
        finally { Gate.Release(); }
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static readonly string[] MonthsDe = { "Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" };
    private static string Day(DateTime d, bool de) =>
        de ? d.Day + ". " + MonthsDe[d.Month - 1] + " " + d.Year : d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    public static async Task<PluginResult> RenderAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (_list is null) await RefreshAsync(ctx, ct, force: true);
        else _ = RefreshAsync(ctx, CancellationToken.None);
        string F(string id) => ctx.Block?.Get(id) ?? "";
        var de = ctx.Locale.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        var teaser = F("mode") == "teaser";
        var list = _list ?? Array.Empty<MmChange>();
        var sb = new StringBuilder("<section class=\"section mm-live mmc" + (teaser ? " mmc-teaser" : "") + "\"><div class=\"container\">");
        if (F("heading") is { Length: > 0 } h) sb.Append("<h2 class=\"mm-title\">").Append(E(h)).Append("</h2>");
        if (F("intro") is { Length: > 0 } i) sb.Append("<p class=\"mm-intro\">").Append(E(i)).Append("</p>");
        if (list.Count == 0)
            sb.Append("<p class=\"mmc-empty\">").Append(E(ctx.T("mmc.none"))).Append(" <a href=\"https://github.com/").Append(E(Repo(ctx)))
              .Append("/commits/main\" rel=\"noopener\">GitHub</a></p>");
        else if (teaser)
        {
            var n = int.TryParse(F("days"), out var d0) && d0 > 0 && d0 < 30 ? d0 : 4;
            sb.Append("<ul class=\"mmc-list\">");
            foreach (var c in list.Where(c => !c.Fix).Take(n))
                sb.Append("<li><time>").Append(E(Day(c.Day, de))).Append("</time><span>")
                  .Append(c.Scope.Length > 0 ? "<em>" + E(c.Scope) + "</em> " : "").Append(E(c.Text)).Append("</span></li>");
            sb.Append("</ul>");
        }
        else
        {
            var days = int.TryParse(F("days"), out var d1) && d1 > 0 && d1 < 200 ? d1 : 21;
            var fixes = F("fixes") != "no";
            foreach (var g in list.GroupBy(c => c.Day).OrderByDescending(g => g.Key).Take(days))
            {
                var news = g.Where(c => !c.Fix).ToList();
                var fx = g.Where(c => c.Fix).ToList();
                if (news.Count == 0 && (!fixes || fx.Count == 0)) continue;
                sb.Append("<article class=\"mmc-day\"><h3><time>").Append(E(Day(g.Key, de))).Append("</time></h3>");
                if (news.Count > 0)
                {
                    sb.Append("<ul class=\"mmc-list\">");
                    foreach (var c in news)
                        sb.Append("<li><span class=\"mmc-tag mmc-new\">").Append(E(ctx.T("mmc.new"))).Append("</span><span>")
                          .Append(c.Scope.Length > 0 ? "<em>" + E(c.Scope) + "</em> " : "").Append(E(c.Text)).Append("</span></li>");
                    sb.Append("</ul>");
                }
                if (fixes && fx.Count > 0)
                {
                    sb.Append("<details class=\"mmc-fixes\"><summary>").Append(E(ctx.T("mmc.fixes", fx.Count))).Append("</summary><ul class=\"mmc-list\">");
                    foreach (var c in fx)
                        sb.Append("<li><span class=\"mmc-tag\">").Append(E(ctx.T("mmc.fix"))).Append("</span><span>")
                          .Append(c.Scope.Length > 0 ? "<em>" + E(c.Scope) + "</em> " : "").Append(E(c.Text)).Append("</span></li>");
                    sb.Append("</ul></details>");
                }
                sb.Append("</article>");
            }
        }
        var more = F("moreUrl");
        if (F("moreText") is { Length: > 0 } mt && (more.StartsWith('/') && !more.StartsWith("//") || more.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            sb.Append("<p class=\"mmc-more\"><a class=\"btn mmp-ghost\" href=\"").Append(E(more)).Append("\">").Append(E(mt)).Append("</a></p>");
        sb.Append("</div></section>").Append(MmLive.Css).Append(Css);
        return PluginResult.Fragment(sb.ToString());
    }

    private const string Css = "<style>.mmc .mmc-day{border-left:2px solid var(--line,#e5e7eb);padding:0 0 18px 18px;margin:0 0 6px;position:relative}"
        + ".mmc .mmc-day::before{content:'';position:absolute;left:-6px;top:6px;width:10px;height:10px;border-radius:50%;background:var(--accent,#295cff)}"
        + ".mmc .mmc-day h3{margin:0 0 10px;font-size:16px}.mmc .mmc-list{list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:8px}"
        + ".mmc .mmc-list li{display:flex;gap:10px;align-items:baseline;line-height:1.5;font-size:15px}"
        + ".mmc .mmc-list em{font-style:normal;font-weight:600;font-size:12px;padding:1px 7px;border-radius:6px;background:color-mix(in srgb,var(--accent,#295cff) 10%,transparent);color:var(--accent,#295cff);margin-right:4px;white-space:nowrap}"
        + ".mmc .mmc-tag{flex:0 0 auto;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.05em;padding:2px 7px;border-radius:6px;background:color-mix(in srgb,var(--ink,#111827) 7%,transparent)}"
        + ".mmc .mmc-tag.mmc-new{background:color-mix(in srgb,var(--accent,#295cff) 14%,transparent);color:var(--accent,#295cff)}"
        + ".mmc .mmc-fixes{margin-top:10px}.mmc .mmc-fixes summary{cursor:pointer;font-size:14px;color:var(--muted,#6b7280)}.mmc .mmc-fixes .mmc-list{margin-top:8px}"
        + ".mmc .mmc-fixes li{font-size:14px;color:var(--muted,#4b5563)}"
        + ".mmc-teaser .mmc-list li{display:grid;grid-template-columns:9.5rem 1fr;gap:12px;padding:10px 0;border-bottom:1px solid var(--line,#e5e7eb)}"
        + ".mmc-teaser .mmc-list time{font-size:13px;color:var(--muted,#6b7280)}.mmc .mmc-more{margin-top:18px}.mmc .mmc-empty{color:var(--muted,#6b7280)}"
        + "@media (max-width:600px){.mmc-teaser .mmc-list li{grid-template-columns:1fr;gap:2px}}</style>";
}

public static class MmChangeText
{
    public static readonly Dictionary<string, string> En = new()
    {
        ["mmc.set.api"] = "Changelog: GitHub API address", ["mmc.set.apiHelp"] = "Leave as it is – only for a mirror or a test.",
        ["mmc.set.repo"] = "Changelog: GitHub repository", ["mmc.set.repoHelp"] = "owner/name of the public repository whose commits are the changelog (main branch).",
        ["mmc.block.name"] = "Matmon: what's new", ["mmc.block.desc"] = "The changelog of Matmon by day, from the public commit history – or the latest few as a teaser.",
        ["mmc.f.heading"] = "Heading", ["mmc.f.intro"] = "Introduction", ["mmc.f.mode"] = "Show",
        ["mmc.o.full"] = "Changelog by day", ["mmc.o.teaser"] = "The latest few (teaser)",
        ["mmc.f.days"] = "How many", ["mmc.f.daysHelp"] = "Days for the changelog, entries for the teaser.",
        ["mmc.f.fixes"] = "Show fixes", ["mmc.o.yes"] = "Yes", ["mmc.o.no"] = "No",
        ["mmc.f.moreText"] = "Button below: text", ["mmc.f.moreUrl"] = "Button below: link",
        ["mmc.new"] = "New", ["mmc.fix"] = "Fix", ["mmc.fixes"] = "{0} fixes", ["mmc.none"] = "The changelog cannot be loaded right now – see the commits on",
    };

    public static readonly Dictionary<string, string> De = new()
    {
        ["mmc.set.api"] = "Changelog: Adresse der GitHub-API", ["mmc.set.apiHelp"] = "So lassen – nur für einen Spiegel oder einen Test.",
        ["mmc.set.repo"] = "Changelog: GitHub-Repository", ["mmc.set.repoHelp"] = "besitzer/name des öffentlichen Repositorys, dessen Commits der Changelog sind (Zweig main).",
        ["mmc.block.name"] = "Matmon: Neuigkeiten", ["mmc.block.desc"] = "Der Changelog von Matmon nach Tagen, aus der öffentlichen Commit-Historie – oder die neuesten Einträge als Kurzfassung.",
        ["mmc.f.heading"] = "Überschrift", ["mmc.f.intro"] = "Einleitung", ["mmc.f.mode"] = "Anzeige",
        ["mmc.o.full"] = "Changelog nach Tagen", ["mmc.o.teaser"] = "Die neuesten (Kurzfassung)",
        ["mmc.f.days"] = "Wie viele", ["mmc.f.daysHelp"] = "Tage beim Changelog, Einträge bei der Kurzfassung.",
        ["mmc.f.fixes"] = "Fehlerbehebungen zeigen", ["mmc.o.yes"] = "Ja", ["mmc.o.no"] = "Nein",
        ["mmc.f.moreText"] = "Knopf darunter: Text", ["mmc.f.moreUrl"] = "Knopf darunter: Link",
        ["mmc.new"] = "Neu", ["mmc.fix"] = "Fix", ["mmc.fixes"] = "{0} Fehlerbehebungen", ["mmc.none"] = "Der Changelog lässt sich gerade nicht laden – die Commits stehen auf",
    };
}
