using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Docs;

/// <summary>What a page file's front matter says; a field that is not there is null and leaves the page's value alone on import.</summary>
internal sealed class FrontMatter
{
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? Icon { get; set; }
    public long? Sort { get; set; }
    public string? TranslationOf { get; set; }
    public bool? Draft { get; set; }
    public string? Format { get; set; }
}

/// <summary>One page file of an import: where it goes (language, version name or "" for the default, path), its front matter and its text.</summary>
internal sealed record DocFile(string Entry, string Locale, string Version, string Path, FrontMatter Meta, string Body);

/// <summary>What the import would do with one file.</summary>
internal sealed record PlanItem(DocFile File, string Status, string Title, IReadOnlyList<string> Changes, string? Error);

/// <summary>
/// Export and import of the whole documentation as a ZIP of Markdown files — the way to keep docs in a folder, a repository or another site without
/// the API. Layout: <c>&lt;locale&gt;/&lt;path&gt;.md</c> for the default version (or a site without versions), <c>&lt;locale&gt;/v/&lt;version&gt;/&lt;path&gt;.md</c>
/// for the others. Each file starts with YAML front matter (title, summary, icon, sort, translationOf, draft; format only for rich text).
/// <para>The front matter is OPTIONAL, so a plain folder of Markdown files (<c>content/de/…</c> of a docs project, written for the API) imports too:
/// a missing title is the page's current one, or made from the file name. An import is shown as a dry run first and writes only after confirmation;
/// it writes through <see cref="DocsOps.UpsertAsync"/>, the API's own upsert, so it is checked and cleaned exactly like the API.</para>
/// </summary>
internal static class DocsTransfer
{
    private const string Stash = "import.zip";
    public const int MaxFiles = 5000;
    public const long MaxBytes = 50L * 1024 * 1024;
    private static string E(string? s) => DocsMarkdown.E(s);
    private static string Url => DocsAdmin.Section + "/transfer";

    // ---- front matter ------------------------------------------------------------------------------------------------

    /// <summary>Front matter and text of a file. Without a leading <c>---</c> block the whole file is the text and every field is null.</summary>
    public static (FrontMatter Meta, string Body) Split(string text)
    {
        var meta = new FrontMatter();
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        if (!(text.StartsWith("---\n", StringComparison.Ordinal) || text.StartsWith("---\r\n", StringComparison.Ordinal))) return (meta, text);
        var pos = text.IndexOf('\n') + 1;
        var lines = new List<string>();
        while (pos < text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            var line = (nl < 0 ? text[pos..] : text[pos..nl]).TrimEnd('\r');
            var next = nl < 0 ? text.Length : nl + 1;
            if (line.Trim() is "---" or "...")
            {
                foreach (var l in lines) Read(meta, l);
                return (meta, text[next..]);
            }
            lines.Add(line);
            pos = next;
        }
        return (new FrontMatter(), text);                      // no closing line: not front matter after all
    }

    private static void Read(FrontMatter m, string line)
    {
        if (line.TrimStart().StartsWith('#')) return;
        var colon = line.IndexOf(':');
        if (colon <= 0) return;
        var key = line[..colon].Trim().ToLowerInvariant();
        var value = Scalar(line[(colon + 1)..].Trim());
        switch (key)
        {
            case "title": m.Title = value; break;
            case "summary": m.Summary = value; break;
            case "icon": m.Icon = value; break;
            case "sort": if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) m.Sort = s; break;
            case "translationof": case "translation_of": m.TranslationOf = value; break;
            case "draft": if (Bool(value) is { } d) m.Draft = d; break;
            case "published": if (Bool(value) is { } p) m.Draft = !p; break;
            case "format": m.Format = value.Trim().ToLowerInvariant() == "html" ? "html" : "md"; break;
        }
    }

    private static bool? Bool(string v) => v.Trim().ToLowerInvariant() switch { "true" or "yes" or "on" or "1" => true, "false" or "no" or "off" or "0" => false, _ => null };

    /// <summary>A YAML scalar of one line: "double quoted" (with \" \\ \n \t \uXXXX), 'single quoted' ('' = '), or plain (a " #" starts a comment).</summary>
    private static string Scalar(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            var sb = new StringBuilder();
            for (var k = 1; k < v.Length - 1; k++)
            {
                if (v[k] != '\\' || k + 1 >= v.Length - 1) { sb.Append(v[k]); continue; }
                var c = v[++k];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': break;
                    case 'u' when k + 4 < v.Length && int.TryParse(v.AsSpan(k + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                        sb.Append((char)code); k += 4; break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v[1..^1].Replace("''", "'");
        var hash = v.IndexOf(" #", StringComparison.Ordinal);
        return (hash >= 0 ? v[..hash] : v).Trim();
    }

    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    /// <summary>A page as a file: front matter, then the text as it is stored.</summary>
    public static string Write(FrontMatter m, string body)
    {
        var sb = new StringBuilder("---\n");
        if (m.Title is not null) sb.Append("title: ").Append(Quote(m.Title)).Append('\n');
        if (!string.IsNullOrEmpty(m.Summary)) sb.Append("summary: ").Append(Quote(m.Summary)).Append('\n');
        if (!string.IsNullOrEmpty(m.Icon)) sb.Append("icon: ").Append(Quote(m.Icon)).Append('\n');
        if (m.Sort is { } s) sb.Append("sort: ").Append(s.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (!string.IsNullOrEmpty(m.TranslationOf)) sb.Append("translationOf: ").Append(Quote(m.TranslationOf)).Append('\n');
        if (m.Draft is { } d) sb.Append("draft: ").Append(d ? "true" : "false").Append('\n');
        if (m.Format == "html") sb.Append("format: html\n");
        return sb.Append("---\n").Append(body).ToString();
    }

    // ---- export ------------------------------------------------------------------------------------------------------

    /// <summary>Every page of every language and version as a ZIP, parents before children.</summary>
    public static async Task<byte[]> ExportAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new DocsStore(ctx.Data);
        var versions = await store.VersionsAsync(ct);
        var defVersion = DocsStore.DefaultVersion(versions);
        var defLocale = DocsUrls.Default(ctx);
        var all = await store.AllPagesAsync(ct);
        var trees = all.Select(p => (p.Locale, p.Version)).Distinct().OrderBy(s => s.Locale == defLocale ? 0 : 1).ThenBy(s => s.Locale).ThenBy(s => s.Version)
            .ToDictionary(s => s, s => DocTree.Build(all.Where(p => p.Locale == s.Locale && p.Version == s.Version)));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var ((locale, version), tree) in trees)
                foreach (var n in tree.Flat)
                {
                    var p = n.Page;
                    string? of = null;
                    if (locale != defLocale && p.GroupKey.Length > 0
                        && all.FirstOrDefault(o => o.GroupKey == p.GroupKey && o.Locale == defLocale && o.Version == version) is { } source
                        && trees.TryGetValue((defLocale, version), out var defTree))
                        of = defTree.Find(source.Id)?.Path;
                    var meta = new FrontMatter
                    {
                        Title = p.Title, Summary = p.Summary, Icon = p.Icon, Sort = p.Sort, TranslationOf = of, Draft = !p.Published,
                        Format = p.Format == "html" ? "html" : null
                    };
                    var entry = zip.CreateEntry(EntryName(locale, version, defVersion, n.Path), CompressionLevel.Optimal);
                    await using var s = entry.Open();
                    var bytes = new UTF8Encoding(false).GetBytes(Write(meta, p.Body));
                    await s.WriteAsync(bytes, ct);
                }
        return ms.ToArray();
    }

    public static string EntryName(string locale, string version, string defVersion, string path) =>
        locale + (version.Length > 0 && version != defVersion ? "/v/" + version : "") + "/" + path + ".md";

    // ---- reading an import -------------------------------------------------------------------------------------------

    /// <summary>
    /// The page files of a ZIP. Folders in front of the language folder (<c>content/de/…</c>, a ZIP of a whole folder) are skipped; files that are not
    /// <c>.md</c> are ignored. Sizes are counted while reading, never taken from the ZIP's own length fields.
    /// </summary>
    public static (List<DocFile> Files, List<string> Problems) Read(Stream zipStream, IReadOnlyCollection<string> languages)
    {
        var files = new List<DocFile>();
        var problems = new List<string>();
        ZipArchive zip;
        try { zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { problems.Add("zip"); return (files, problems); }
        using (zip)
        {
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (name.EndsWith('/') || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                if (files.Count >= MaxFiles) { problems.Add("tooMany"); break; }
                var segs = name[..^3].Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (segs.Any(s => s is "." or "..")) { problems.Add(name); continue; }
                var at = segs.FindIndex(s => languages.Contains(s.ToLowerInvariant()));
                if (at < 0 || at > 2 || segs.Count - at < 2) { problems.Add(name); continue; }
                var locale = segs[at].ToLowerInvariant();
                var rest = segs.Skip(at + 1).ToList();
                var version = "";
                if (rest.Count >= 3 && rest[0] == "v") { version = rest[1]; rest = rest.Skip(2).ToList(); }
                var path = string.Join("/", rest.Select(s => PluginText.Slugify(s)));

                using var s = entry.Open();
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > MaxBytes) { problems.Add("tooBig"); return (files, problems); }
                    buffer.Write(chunk, 0, read);
                }
                var (meta, body) = Split(new UTF8Encoding(false).GetString(buffer.ToArray()));
                files.Add(new DocFile(name, locale, version, path, meta, body));
            }
        }
        return (files, problems);
    }

    /// <summary>The text as stored: line ends as written, trailing blank lines down to one line end (what the API's writers send).</summary>
    public static string CleanBody(string body)
    {
        var t = body.TrimEnd();
        return t.Length == 0 ? "" : t + "\n";
    }

    private static string Same(string body) => body.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

    /// <summary>A title from a file name when the page has none yet: "first-steps" → "First steps".</summary>
    public static string TitleFromSlug(string slug)
    {
        var t = slug.Replace('-', ' ').Replace('_', ' ').Trim();
        return t.Length == 0 ? slug : char.ToUpperInvariant(t[0]) + t[1..];
    }

    // ---- the plan ----------------------------------------------------------------------------------------------------

    /// <summary>What an import would do, in the order it would do it: the default language first (originals before their translations), then by
    /// version and depth (parents before children).</summary>
    public static async Task<List<PlanItem>> PlanAsync(IPluginContext ctx, IReadOnlyList<DocFile> files, CancellationToken ct)
    {
        var store = new DocsStore(ctx.Data);
        var versions = await store.VersionsAsync(ct);
        var defVersion = DocsStore.DefaultVersion(versions);
        var defLocale = DocsUrls.Default(ctx);
        var all = await store.AllPagesAsync(ct);
        var trees = new Dictionary<(string, string), DocTree>();
        DocTree Tree(string l, string v) => trees.TryGetValue((l, v), out var t) ? t : trees[(l, v)] = DocTree.Build(all.Where(p => p.Locale == l && p.Version == v));
        string V(DocFile f) => f.Version.Length == 0 ? defVersion : f.Version;
        var incoming = files.Select(f => (f.Locale, V(f), f.Path)).ToHashSet();

        var plan = new List<PlanItem>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var f in files.OrderBy(f => f.Locale == defLocale ? 0 : 1).ThenBy(f => f.Locale).ThenBy(f => f.Version)
                     .ThenBy(f => f.Path.Count(c => c == '/')).ThenBy(f => f.Meta.Sort ?? long.MaxValue).ThenBy(f => f.Path, StringComparer.Ordinal))
        {
            var version = V(f);
            var segs = f.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            var existing = f.Version.Length > 0 && !versions.Any(v => v.Name == f.Version) ? null : Tree(f.Locale, version).ByPath(segs);
            var title = (f.Meta.Title ?? "").Trim();
            if (title.Length == 0) title = existing?.Page.Title ?? TitleFromSlug(segs.LastOrDefault() ?? f.Path);
            PlanItem Error(string key, params object[] args) => new(f, "error", title, Array.Empty<string>(), ctx.T(key, args));

            if (!seen.Add((f.Locale, version, f.Path))) { plan.Add(Error("docs.io.errTwice")); continue; }
            if (!DocsUrls.Publishes(ctx, f.Locale)) { plan.Add(Error("docs.io.errLocale", f.Locale)); continue; }
            if (f.Version.Length > 0 && !versions.Any(v => v.Name == f.Version)) { plan.Add(Error("docs.io.errVersion", f.Version)); continue; }
            if (segs.Count == 0 || segs.Any(s => s.Length == 0)) { plan.Add(Error("docs.io.errPath")); continue; }
            if (segs.Count > DocsStore.MaxDepth) { plan.Add(Error("docs.io.errDepth", DocsStore.MaxDepth)); continue; }
            if (DocsStore.ReservedSlugs.Contains(segs[0])) { plan.Add(Error("docs.io.errReserved", segs[0])); continue; }
            if (segs.Count > 1)
            {
                var parent = segs.Take(segs.Count - 1).ToList();
                if (Tree(f.Locale, version).ByPath(parent) is null && !incoming.Contains((f.Locale, version, string.Join("/", parent))))
                { plan.Add(Error("docs.io.errParent", string.Join("/", parent))); continue; }
            }
            var of = f.Meta.TranslationOf?.Trim().Trim('/') ?? "";
            if (of.Length > 0 && f.Locale != defLocale)
            {
                var ofSegs = of.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => PluginText.Slugify(s)).ToList();
                if (Tree(defLocale, version).ByPath(ofSegs) is null && !incoming.Contains((defLocale, version, string.Join("/", ofSegs))))
                { plan.Add(Error("docs.io.errTranslation", of, defLocale.ToUpperInvariant())); continue; }
            }
            if (existing is null) { plan.Add(new PlanItem(f, "new", title, Array.Empty<string>(), null)); continue; }

            var p = existing.Page;
            var changes = new List<string>();
            if (f.Meta.Title is not null && title != p.Title) changes.Add(ctx.T("docs.io.fTitle"));
            if (f.Meta.Summary is not null && f.Meta.Summary.Trim() != p.Summary) changes.Add(ctx.T("docs.io.fSummary"));
            if (f.Meta.Icon is not null && DocsHtml.Icon(f.Meta.Icon) != p.Icon) changes.Add(ctx.T("docs.io.fIcon"));
            if (f.Meta.Sort is { } sort && sort != p.Sort) changes.Add(ctx.T("docs.io.fSort"));
            if (f.Meta.Draft is { } draft && draft == p.Published) changes.Add(ctx.T(draft ? "docs.io.fDraft" : "docs.io.fPublished"));
            var format = f.Meta.Format ?? p.Format;
            if (format != p.Format) changes.Add(ctx.T("docs.io.fFormat"));
            if (Same(f.Body) != Same(p.Body)) changes.Add(ctx.T("docs.io.fBody"));
            if (of.Length > 0 && f.Locale != defLocale && p.GroupKey.Length == 0) changes.Add(ctx.T("docs.io.fTranslation"));
            plan.Add(new PlanItem(f, changes.Count > 0 ? "changed" : "unchanged", title, changes, null));
        }
        return plan;
    }

    /// <summary>Writes the new and changed pages of a plan, in its order, through the API's upsert. Returns (written, failed with reasons).</summary>
    public static async Task<(int Written, List<string> Failed)> ApplyAsync(IPluginContext ctx, IReadOnlyList<PlanItem> plan, CancellationToken ct)
    {
        var written = 0;
        var failed = new List<string>();
        foreach (var item in plan.Where(i => i.Status is "new" or "changed"))
        {
            var f = item.File;
            var a = new JsonObject
            {
                ["path"] = f.Path, ["locale"] = f.Locale, ["version"] = f.Version, ["title"] = item.Title,
                ["body"] = item.Changes.Count == 0 || item.Changes.Contains(ctx.T("docs.io.fBody")) ? CleanBody(f.Body) : null
            };
            if (a["body"] is null) a.Remove("body");
            if (f.Meta.Summary is not null) a["summary"] = f.Meta.Summary;
            if (f.Meta.Icon is not null) a["icon"] = f.Meta.Icon;
            if (f.Meta.Sort is { } sort) a["sort"] = sort;
            if (f.Meta.Draft is { } draft) a["published"] = !draft;
            if (f.Meta.Format is { } format) a["format"] = format;
            if (!string.IsNullOrWhiteSpace(f.Meta.TranslationOf)) a["translationOf"] = f.Meta.TranslationOf;
            var r = await DocsOps.UpsertAsync(ctx, a, ct, by: ctx.User.Name.Length > 0 ? ctx.User.Name : "import");
            if (r["error"] is not null) failed.Add(f.Entry + ": " + r["error"]);
            else written++;
        }
        return (written, failed);
    }

    // ---- the back-office page -----------------------------------------------------------------------------------------

    public static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var f = ctx.Request;
        if (!f.IsPost && f.Q("export") == "1")
        {
            var bytes = await ExportAsync(ctx, ct);
            return PluginResult.File(bytes, "application/zip", "docs-" + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".zip");
        }
        if (f.IsPost)
        {
            switch (f.Action)
            {
                case "check":
                {
                    var file = f.File("zip");
                    if (file is null || file.Length == 0) return PluginResult.Redirect(Url, ctx.T("docs.io.noFile"), true);
                    if (file.Length > MaxBytes) return PluginResult.Redirect(Url, ctx.T("docs.io.tooBig"), true);
                    using var ms = new MemoryStream();
                    await using (var s = file.OpenRead()) await s.CopyToAsync(ms, ct);
                    ms.Position = 0;
                    await ctx.Files.SaveAsync(Stash, ms, ct);
                    return await ShowPlanAsync(ctx, ms.ToArray(), ct);
                }
                case "apply":
                {
                    var bytes = await StashedAsync(ctx, ct);
                    if (bytes is null || Hash(bytes) != f.F("hash")) return PluginResult.Redirect(Url, ctx.T("docs.io.expired"), true);
                    var (files, _) = Read(new MemoryStream(bytes), Languages(ctx));
                    var plan = await PlanAsync(ctx, files, ct);
                    var (written, failed) = await ApplyAsync(ctx, plan, ct);
                    await ctx.Files.DeleteAsync(Stash, ct);
                    var unchanged = plan.Count(i => i.Status == "unchanged");
                    var errors = plan.Count(i => i.Status == "error") + failed.Count;
                    var notice = ctx.T("docs.io.done", written, unchanged, errors) + (failed.Count > 0 ? " " + string.Join(" · ", failed.Take(5)) : "");
                    return PluginResult.Redirect(Url, notice, errors > 0);
                }
                case "cancel":
                    await ctx.Files.DeleteAsync(Stash, ct);
                    return PluginResult.Redirect(Url);
            }
        }

        var store = new DocsStore(ctx.Data);
        var count = (await store.AllPagesAsync(ct)).Count;
        return PluginResult.Page(Ui.Stack(
            Ui.Card(ctx.T("docs.io.exportTitle"),
                Ui.Muted(ctx.T("docs.io.exportHelp", count)),
                Ui.Button(ctx.T("docs.io.exportButton"), Url + "?export=1", UiButtonKind.Primary, "ti-download")),
            Ui.Card(ctx.T("docs.io.importTitle"),
                Ui.Muted(ctx.T("docs.io.importHelp")),
                Ui.Html("<pre style=\"margin:0;white-space:pre-wrap;font-size:.86em\">" + E(Example) + "</pre>"),
                Ui.Form(
                    Ui.File("zip", ctx.T("docs.io.file"), ".zip,application/zip") with { Required = true },
                    Ui.FormActions(Ui.Submit(ctx.T("docs.io.check"), name: "action", value: "check", icon: "ti-list-check"))) with { Upload = true, Narrow = true })),
            ctx.T("docs.admin.transfer"));
    }

    private const string Example = "de/erste-schritte.md\nde/erste-schritte/installation.md\nen/getting-started.md\nde/v/1.0/erste-schritte.md\n\n---\ntitle: \"Installation\"\nsummary: \"…\"\nicon: \"download\"\nsort: 20\ntranslationOf: \"getting-started/installation\"\ndraft: false\n---\n## …";

    private static IReadOnlyCollection<string> Languages(IPluginContext ctx)
    {
        var set = new HashSet<string>(DocsUrls.Languages(ctx), StringComparer.OrdinalIgnoreCase) { DocsUrls.Default(ctx) };
        return set;
    }

    private static async Task<byte[]?> StashedAsync(IPluginContext ctx, CancellationToken ct)
    {
        await using var s = await ctx.Files.OpenAsync(Stash, ct);
        if (s is null) return null;
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<PluginResult> ShowPlanAsync(IPluginContext ctx, byte[] bytes, CancellationToken ct)
    {
        var (files, problems) = Read(new MemoryStream(bytes), Languages(ctx));
        var plan = await PlanAsync(ctx, files, ct);
        var counts = new[] { "new", "changed", "unchanged", "error" }.ToDictionary(s => s, s => plan.Count(i => i.Status == s));
        var nodes = new List<UiNode?>();
        if (problems.Contains("zip")) nodes.Add(Ui.Alert(ctx.T("docs.io.notZip"), UiTone.Error));
        if (problems.Contains("tooBig")) nodes.Add(Ui.Alert(ctx.T("docs.io.tooBig"), UiTone.Error));
        if (problems.Contains("tooMany")) nodes.Add(Ui.Alert(ctx.T("docs.io.tooMany", MaxFiles), UiTone.Error));
        var skipped = problems.Where(p => p is not ("zip" or "tooBig" or "tooMany")).ToList();
        if (skipped.Count > 0) nodes.Add(Ui.Alert(ctx.T("docs.io.skipped", skipped.Count, string.Join(", ", skipped.Take(8))), UiTone.Warning));
        nodes.Add(Ui.Alert(ctx.T("docs.io.summary", counts["new"], counts["changed"], counts["unchanged"], counts["error"]),
            counts["new"] + counts["changed"] > 0 ? UiTone.Info : UiTone.Success));

        UiNode Status(string s) => s switch
        {
            "new" => Ui.Badge(ctx.T("docs.io.sNew"), UiTone.Success),
            "changed" => Ui.Badge(ctx.T("docs.io.sChanged"), UiTone.Warning),
            "unchanged" => Ui.Badge(ctx.T("docs.io.sUnchanged"), UiTone.Muted),
            _ => Ui.Badge(ctx.T("docs.io.sError"), UiTone.Error)
        };
        var rows = plan.Select(i => Ui.RowFor(i.File.Entry + " " + i.Title,
            Status(i.Status),
            i.File.Locale.ToUpperInvariant() + (i.File.Version.Length > 0 ? " · " + i.File.Version : ""),
            Ui.Html("<code>" + E(i.File.Path) + "</code>"),
            i.Title,
            i.Error ?? string.Join(", ", i.Changes)));
        nodes.Add(Ui.Table(
            new[] { Ui.Column(ctx.T("docs.io.colStatus")), Ui.Column(ctx.T("docs.io.colLang")), Ui.Column(ctx.T("docs.io.colPath")), Ui.Column(ctx.T("docs.admin.colTitle")), Ui.Column(ctx.T("docs.io.colWhat")) },
            rows, searchPlaceholder: ctx.T("docs.admin.search"), emptyText: ctx.T("docs.io.empty")));
        var todo = counts["new"] + counts["changed"];
        nodes.Add(Ui.Form(
            Ui.Hidden("hash", Hash(bytes)),
            Ui.FormActions(
                todo > 0 ? Ui.Submit(ctx.T("docs.io.apply", todo), name: "action", value: "apply", icon: "ti-check", confirm: ctx.T("docs.io.confirm", todo)) : null,
                Ui.Submit(ctx.T("docs.io.cancel"), UiButtonKind.Ghost, "action", "cancel"))));
        return PluginResult.Page(Ui.Stack(nodes.ToArray()), ctx.T("docs.io.planTitle"));
    }
}
