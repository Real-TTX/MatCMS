using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatCMS.Data;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;

namespace MatCMS.Services;

/// <summary>
/// "Beitrag aus Link": takes a post over from a link (Instagram, Facebook, any page with preview data)
/// ONCE, while the post is being written — never live on the public site. Text, date and source are
/// handed back to the editor to fill its fields; every image is downloaded into the media library
/// right away, because the image URLs Meta hands out expire after a few days and a post linking them
/// would lose its pictures.
/// <para>Three ways in, tried in this order:</para>
/// <list type="number">
/// <item>Instagram through Meta's API with the site's own token (<see cref="SettingKeys.SocialInstagramToken"/>):
///   full caption, every carousel image in full size, the original date. Only the account the token
///   belongs to — Meta offers no lookup of other people's posts.</item>
/// <item>Facebook the same way with a page token (<see cref="SettingKeys.SocialFacebookToken"/>).</item>
/// <item>The public preview data (Open Graph) of any page. Meta mostly withholds it from servers, so
///   for Instagram/Facebook this yields at best one image and a shortened text — said so in the notes
///   instead of pretending the import was complete.</item>
/// </list>
/// </summary>
public sealed class PostImportService
{
    public const string HttpClientName = "postimport";
    private const string GraphInstagram = "https://graph.instagram.com/v23.0";
    private const string GraphFacebook = "https://graph.facebook.com/v23.0";
    private const int MaxImages = 20;
    private const long MaxImageBytes = 15 * 1024 * 1024;
    private const long MaxPageBytes = 3 * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PostImportService> _log;

    public PostImportService(AppDbContext db, IHttpClientFactory http, IWebHostEnvironment env, ILogger<PostImportService> log)
    {
        _db = db;
        _http = http;
        _env = env;
        _log = log;
    }

    /// <summary>What the editor receives. Images are already local media URLs.</summary>
    public sealed record Result(
        bool Ok, string? Error, string Title, string Excerpt, string Html, DateTime? PublishedAt,
        string SourceUrl, string SourceName, List<string> Images, List<string> Notes, string Via);

    /// <summary>A post as read from a source, before anything is downloaded.</summary>
    public sealed record Fetched(string Platform, string Caption, string? Title, DateTime? PublishedAt,
        string SourceUrl, string SourceName, List<string> ImageUrls, List<string> Notes, string Via);

    public async Task<Result> ImportAsync(string? rawUrl, CancellationToken ct)
    {
        var notes = new List<string>();
        var url = NormalizeUrl(rawUrl);
        if (url is null) return Fail("Bitte einen vollständigen Link angeben (https://…).");

        Fetched? f = null;
        try
        {
            var platform = PlatformOf(url);
            if (platform == "instagram")
            {
                var token = await SettingAsync(SettingKeys.SocialInstagramToken);
                if (token.Length > 0) f = await FromInstagramApiAsync(url, token, notes, ct);
                else notes.Add("Für Instagram ist kein Zugang hinterlegt (Einstellungen → Social Media) – es kommen nur die öffentlichen Vorschau-Daten.");
            }
            else if (platform == "facebook")
            {
                var token = await SettingAsync(SettingKeys.SocialFacebookToken);
                if (token.Length > 0) f = await FromFacebookApiAsync(url, token, notes, ct);
                else notes.Add("Für Facebook ist kein Zugang hinterlegt (Einstellungen → Social Media) – es kommen nur die öffentlichen Vorschau-Daten.");
            }
            f ??= await FromOpenGraphAsync(url, platform, notes, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("Die Seite hat nicht rechtzeitig geantwortet.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Post import from {Url} failed", url);
            return Fail("Der Link konnte nicht gelesen werden: " + ex.Message);
        }
        if (f is null) return Fail("Unter diesem Link wurde nichts gefunden, was sich übernehmen lässt.");
        f.Notes.InsertRange(0, notes);

        var (title, excerpt, html) = Compose(f.Caption, f.Title);
        var images = await DownloadImagesAsync(f, title, ct);
        if (images.Count < f.ImageUrls.Count)
            f.Notes.Add($"{f.ImageUrls.Count - images.Count} von {f.ImageUrls.Count} Bildern ließen sich nicht laden.");
        if (title.Length == 0 && html.Length == 0 && images.Count == 0)
            return Fail("Unter diesem Link wurde nichts gefunden, was sich übernehmen lässt." + (f.Notes.Count > 0 ? " " + string.Join(" ", f.Notes) : ""));

        return new Result(true, null, title, excerpt, html, f.PublishedAt, f.SourceUrl, f.SourceName, images, f.Notes, f.Via);

        Result Fail(string error) => new(false, error, "", "", "", null, "", "", new(), notes, "");
    }

    // ---- sources --------------------------------------------------------------------------------

    /// <summary>The site's own Instagram media, found by the shortcode in the link. Meta has no lookup
    /// by shortcode, so the account's media list is walked (newest first) until the permalink matches.</summary>
    private async Task<Fetched?> FromInstagramApiAsync(string url, string token, List<string> notes, CancellationToken ct)
    {
        var code = InstagramShortcode(url);
        if (code is null) { notes.Add("Der Link zeigt auf keinen einzelnen Instagram-Beitrag."); return null; }
        token = await RefreshInstagramTokenAsync(token, ct);

        var client = _http.CreateClient(HttpClientName);
        var next = $"{GraphInstagram}/me/media?fields=id,caption,media_type,media_url,thumbnail_url,permalink,timestamp,username," +
                   "children%7Bmedia_type,media_url,thumbnail_url%7D&limit=50&access_token=" + Uri.EscapeDataString(token);
        for (var page = 0; page < 8 && next is not null; page++)
        {
            using var doc = await GetJsonAsync(client, next, ct);
            if (GraphError(doc) is { } err) { notes.Add("Instagram-Zugang: " + err); return null; }
            foreach (var m in Arr(doc.RootElement, "data"))
            {
                if (InstagramShortcode(Str(m, "permalink")) != code) continue;
                return ParseInstagramMedia(m, url);
            }
            next = doc.RootElement.TryGetProperty("paging", out var pg) ? Str(pg, "next") : null;
            if (string.IsNullOrEmpty(next)) next = null;
        }
        notes.Add("Der Beitrag gehört nicht zu dem Instagram-Konto, für das der Zugang hinterlegt ist (oder ist älter als die letzten 400 Beiträge).");
        return null;
    }

    /// <summary>One Instagram media object (Graph JSON) → a fetched post. Public for testing.</summary>
    public static Fetched ParseInstagramMedia(JsonElement m, string linkUrl)
    {
        var images = new List<string>();
        void Add(JsonElement x)
        {
            // A video has no still under media_url (that is the video file) — its thumbnail stands in.
            var u = Str(x, "media_type") == "VIDEO" ? Str(x, "thumbnail_url") : Str(x, "media_url");
            if (!string.IsNullOrEmpty(u)) images.Add(u!);
        }
        if (m.TryGetProperty("children", out var ch) && Arr(ch, "data").Any())
            foreach (var c in Arr(ch, "data")) Add(c);
        else Add(m);

        var notes = new List<string>();
        if (Str(m, "media_type") == "VIDEO" || Str(m, "media_type") == "REELS")
            notes.Add("Videos werden nicht übernommen – nur ihr Vorschaubild.");
        var user = Str(m, "username");
        return new Fetched("instagram", Str(m, "caption") ?? "", null, ParseDate(Str(m, "timestamp")),
            Str(m, "permalink") ?? linkUrl, "Instagram" + (string.IsNullOrEmpty(user) ? "" : " · @" + user),
            images, notes, "Instagram (Zugang)");
    }

    /// <summary>The site's own Facebook page posts, matched by link. Facebook writes the same post under
    /// several URL shapes (pfbid…, story_fbid=…, /posts/&lt;id&gt;), so a post matches on its permalink OR
    /// on its numeric id appearing anywhere in the link.</summary>
    private async Task<Fetched?> FromFacebookApiAsync(string url, string token, List<string> notes, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        string pageName = "";
        using (var me = await GetJsonAsync(client, $"{GraphFacebook}/me?fields=name&access_token=" + Uri.EscapeDataString(token), ct))
        {
            if (GraphError(me) is { } err) { notes.Add("Facebook-Zugang: " + err); return null; }
            pageName = Str(me.RootElement, "name") ?? "";
        }
        var next = $"{GraphFacebook}/me/posts?fields=id,message,created_time,permalink_url,full_picture," +
                   "attachments%7Bmedia_type,media,subattachments.limit(50)%7Bmedia,media_type%7D%7D&limit=50&access_token=" + Uri.EscapeDataString(token);
        var wanted = FacebookKey(url);
        for (var page = 0; page < 8 && next is not null; page++)
        {
            using var doc = await GetJsonAsync(client, next, ct);
            if (GraphError(doc) is { } err) { notes.Add("Facebook-Zugang: " + err); return null; }
            foreach (var p in Arr(doc.RootElement, "data"))
            {
                var id = Str(p, "id") ?? "";
                var postId = id.Contains('_') ? id[(id.IndexOf('_') + 1)..] : id;
                var same = FacebookKey(Str(p, "permalink_url")) == wanted
                           || (postId.Length > 0 && url.Contains(postId, StringComparison.Ordinal));
                if (same) return ParseFacebookPost(p, url, pageName);
            }
            next = doc.RootElement.TryGetProperty("paging", out var pg) ? Str(pg, "next") : null;
            if (string.IsNullOrEmpty(next)) next = null;
        }
        notes.Add("Der Beitrag gehört nicht zu der Facebook-Seite, für die der Zugang hinterlegt ist (oder ist älter als die letzten 400 Beiträge).");
        return null;
    }

    /// <summary>One Facebook post (Graph JSON) → a fetched post. Public for testing.</summary>
    public static Fetched ParseFacebookPost(JsonElement p, string linkUrl, string pageName)
    {
        var images = new List<string>();
        foreach (var a in p.TryGetProperty("attachments", out var att) ? Arr(att, "data") : Enumerable.Empty<JsonElement>())
        {
            var subs = a.TryGetProperty("subattachments", out var sa) ? Arr(sa, "data").ToList() : new List<JsonElement>();
            foreach (var x in subs.Count > 0 ? subs : new List<JsonElement> { a })
                if (x.TryGetProperty("media", out var md) && md.TryGetProperty("image", out var im) && Str(im, "src") is { Length: > 0 } src)
                    images.Add(src);
        }
        if (images.Count == 0 && Str(p, "full_picture") is { Length: > 0 } full) images.Add(full);
        return new Fetched("facebook", Str(p, "message") ?? "", null, ParseDate(Str(p, "created_time")),
            Str(p, "permalink_url") ?? linkUrl, "Facebook" + (pageName.Length > 0 ? " · " + pageName : ""),
            images, new List<string>(), "Facebook (Zugang)");
    }

    /// <summary>Any page's public preview data (og:title/description/image…).</summary>
    private async Task<Fetched?> FromOpenGraphAsync(string url, string platform, List<string> notes, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) { notes.Add($"Die Seite antwortet mit {(int)resp.StatusCode}."); return null; }
        var html = await ReadLimitedStringAsync(resp, MaxPageBytes, ct);
        var f = ParseOpenGraph(html, resp.RequestMessage?.RequestUri?.ToString() ?? url, platform);
        if (platform is "instagram" or "facebook")
        {
            if (f.Caption.Length == 0 && f.ImageUrls.Count == 0)
                f.Notes.Add("Meta gibt diesen Beitrag ohne Zugang nicht heraus.");
            else
                f.Notes.Add("Ohne Zugang liefert Meta höchstens ein Bild und einen gekürzten Text – mehrere Bilder (Karussell) kommen nur über den Zugang.");
        }
        return f;
    }

    /// <summary>Open Graph tags of an HTML page → a fetched post. Public for testing.</summary>
    public static Fetched ParseOpenGraph(string html, string pageUrl, string platform)
    {
        var meta = new List<(string Key, string Value)>();
        foreach (Match m in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = m.Value;
            var key = Attr(tag, "property") ?? Attr(tag, "name");
            var val = Attr(tag, "content");
            if (key is not null && val is not null) meta.Add((key.ToLowerInvariant(), WebUtility.HtmlDecode(val).Trim()));
        }
        string? First(params string[] keys) => keys.Select(k => meta.FirstOrDefault(x => x.Key == k).Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var title = First("og:title", "twitter:title");
        if (title is null)
        {
            var t = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (t.Success) title = WebUtility.HtmlDecode(t.Groups[1].Value).Trim();
        }
        var desc = First("og:description", "twitter:description", "description") ?? "";
        // Several og:image tags = several pictures (a page may list a whole carousel this way).
        var images = meta.Where(x => x.Key is "og:image" or "og:image:secure_url" or "og:image:url" or "twitter:image")
            .Select(x => AbsoluteUrl(pageUrl, x.Value)).Where(x => x is not null).Select(x => x!)
            .Distinct().Take(MaxImages).ToList();
        var site = First("og:site_name") ?? HostOf(pageUrl);
        var source = AbsoluteUrl(pageUrl, First("og:url") ?? "") ?? pageUrl;

        // Instagram's preview text reads 'N likes, M comments - name on 1. Oktober 2026: "caption"'.
        // Only the caption belongs into the post; the title of such a preview is just "name on Instagram".
        if (platform == "instagram")
        {
            var q = Regex.Match(desc, "^.*?:\\s*[\"“](.*)[\"”]\\.?\\s*$", RegexOptions.Singleline);
            if (q.Success) desc = q.Groups[1].Value.Trim();
            title = null;
        }
        return new Fetched(platform, desc, title, ParseDate(First("article:published_time", "og:updated_time")),
            source, platform switch { "instagram" => "Instagram", "facebook" => "Facebook", _ => site }, images,
            new List<string>(), "Öffentliche Vorschau-Daten");
    }

    // ---- text → title / teaser / body ------------------------------------------------------------

    /// <summary>Turns a caption (plain text with line breaks) into the editor's three fields. The first
    /// line is the title (Instagram has none — the first line is what people read as one); when it fits
    /// whole it is not repeated in the body. Public for testing.</summary>
    public static (string Title, string Excerpt, string Html) Compose(string caption, string? title)
    {
        caption = (caption ?? "").Replace("\r\n", "\n").Trim();
        var lines = caption.Split('\n').ToList();
        var body = caption;
        if (string.IsNullOrWhiteSpace(title))
        {
            var first = lines.FirstOrDefault(l => StripTags(l).Length > 0) ?? "";
            var clean = StripTags(first);
            title = Shorten(clean, 90);
            // The whole first line became the title: the body starts after it.
            if (title == clean && clean.Length > 0)
            {
                var i = lines.IndexOf(first);
                body = string.Join("\n", lines.Skip(i + 1)).Trim();
            }
        }
        var teaserText = Regex.Replace(StripTags(Regex.Replace(caption, @"https?://\S+", "")), @"\s+", " ").Trim();
        var excerpt = Shorten(teaserText, 200);
        // A short caption fits the teaser whole. The post page shows the teaser as its lead paragraph,
        // so the same text again as the body would stand twice in a row — the teaser alone carries it.
        var bodyText = Regex.Replace(StripTags(Regex.Replace(body, @"https?://\S+", "")), @"\s+", " ").Trim();
        if (bodyText.Length > 0 && bodyText == excerpt && !Regex.IsMatch(body, @"https?://")) body = "";
        return (title!.Trim(), excerpt, ToHtml(body));
    }

    /// <summary>Plain text → paragraphs; links become clickable, everything else is encoded.</summary>
    public static string ToHtml(string text)
    {
        var sb = new StringBuilder();
        foreach (var para in Regex.Split(text ?? "", @"\n\s*\n"))
        {
            var p = para.Trim();
            if (p.Length == 0) continue;
            // Only the four characters that matter: HtmlEncode would also turn every umlaut into &#252;,
            // which then stands in the editor's source and in every later edit.
            var enc = p.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
            enc = Regex.Replace(enc, @"https?://[^\s<]+", m =>
                $"<a href=\"{m.Value}\" target=\"_blank\" rel=\"nofollow noopener\">{m.Value}</a>");
            sb.Append("<p>").Append(enc.Replace("\n", "<br>")).Append("</p>");
        }
        return sb.ToString();
    }

    // Hashtags and @mentions are the caption's metadata, not words for a title or teaser.
    private static string StripTags(string s) => Regex.Replace(s ?? "", @"(^|\s)[#@][\p{L}\p{N}_.]+", " ").Trim();

    private static string Shorten(string s, int max)
    {
        s = (s ?? "").Trim();
        if (s.Length <= max) return s;
        var cut = s[..max];
        var sp = cut.LastIndexOf(' ');
        return (sp > max / 2 ? cut[..sp] : cut).TrimEnd(',', '.', ';', ':', '-', ' ') + " …";
    }

    // ---- images ---------------------------------------------------------------------------------

    /// <summary>Downloads every image into the media library. Each file is DECODED and re-encoded, so
    /// only a real image lands under /uploads (whatever a server claims a file is), without its
    /// metadata (location, camera).</summary>
    private async Task<List<string>> DownloadImagesAsync(Fetched f, string title, CancellationToken ct)
    {
        var urls = new List<string>();
        var client = _http.CreateClient(HttpClientName);
        var uploads = StoragePaths.Uploads(_env);
        Directory.CreateDirectory(uploads);
        var order = (await _db.Media.MaxAsync(m => (int?)m.SortOrder, ct) ?? 0) + 1;
        var n = 0;
        foreach (var src in f.ImageUrls.Take(MaxImages))
        {
            n++;
            try
            {
                using var resp = await client.GetAsync(src, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await ReadLimitedBytesAsync(resp, MaxImageBytes, ct);
                var format = Image.DetectFormat(bytes);
                using var image = Image.Load(bytes);
                var ext = format?.Name switch { "PNG" => ".png", "WEBP" => ".webp", "GIF" => ".gif", _ => ".jpg" };
                image.Metadata.ExifProfile = null;
                image.Metadata.IptcProfile = null;
                image.Metadata.XmpProfile = null;
                var name = $"{Guid.NewGuid():N}{ext}";
                var path = Path.Combine(uploads, name);
                await image.SaveAsync(path, ct);
                var url = "/uploads/" + name;
                _db.Media.Add(new Models.Media
                {
                    Url = url,
                    FileName = $"{f.Platform}-{DateTime.UtcNow:yyyyMMdd}-{n}{ext}",
                    Alt = title.Length > 0 ? title : null,
                    Tags = "import, " + f.Platform,
                    ContentType = ext switch { ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg" },
                    SizeBytes = new FileInfo(path).Length,
                    SortOrder = order++,
                });
                urls.Add(url);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogInformation(ex, "Post import: image {Src} skipped", src);
            }
        }
        await _db.SaveChangesAsync(ct);
        return urls;
    }

    // ---- Instagram token upkeep ------------------------------------------------------------------

    /// <summary>A long-lived Instagram token lasts 60 days and can be extended while it is valid. The
    /// import does that at most once a week, so a site that imports now and then never loses access.</summary>
    private async Task<string> RefreshInstagramTokenAsync(string token, CancellationToken ct)
    {
        var last = await SettingAsync(SettingKeys.SocialInstagramRefreshedAt);
        if (DateTime.TryParse(last, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) && DateTime.UtcNow - at < TimeSpan.FromDays(7))
            return token;
        try
        {
            using var doc = await GetJsonAsync(_http.CreateClient(HttpClientName),
                "https://graph.instagram.com/refresh_access_token?grant_type=ig_refresh_token&access_token=" + Uri.EscapeDataString(token), ct);
            var fresh = Str(doc.RootElement, "access_token");
            if (!string.IsNullOrEmpty(fresh))
            {
                await SetSettingAsync(SettingKeys.SocialInstagramToken, fresh!);
                token = fresh!;
            }
            await SetSettingAsync(SettingKeys.SocialInstagramRefreshedAt, DateTime.UtcNow.ToString("o"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation(ex, "Instagram token refresh failed; using the stored token");
        }
        return token;
    }

    /// <summary>Asks Meta who a token belongs to — for the "Verbindung prüfen" button.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(string platform, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient(HttpClientName);
            if (platform == "instagram")
            {
                var token = await SettingAsync(SettingKeys.SocialInstagramToken);
                if (token.Length == 0) return (false, "Kein Instagram-Zugang hinterlegt.");
                using var doc = await GetJsonAsync(client, $"{GraphInstagram}/me?fields=username,account_type&access_token=" + Uri.EscapeDataString(token), ct);
                return GraphError(doc) is { } e ? (false, e) : (true, "Verbunden mit Instagram @" + Str(doc.RootElement, "username"));
            }
            else
            {
                var token = await SettingAsync(SettingKeys.SocialFacebookToken);
                if (token.Length == 0) return (false, "Kein Facebook-Zugang hinterlegt.");
                using var doc = await GetJsonAsync(client, $"{GraphFacebook}/me?fields=name&access_token=" + Uri.EscapeDataString(token), ct);
                return GraphError(doc) is { } e ? (false, e) : (true, "Verbunden mit der Facebook-Seite „" + Str(doc.RootElement, "name") + "“");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    public static string? NormalizeUrl(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0 || s.Length > 2000) return null;
        if (!s.Contains("://")) s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") && u.Host.Contains('.')
            ? u.ToString() : null;
    }

    public static string PlatformOf(string url)
    {
        var h = HostOf(url);
        if (h == "instagram.com" || h.EndsWith(".instagram.com") || h == "instagr.am") return "instagram";
        if (h == "facebook.com" || h.EndsWith(".facebook.com") || h == "fb.com" || h == "fb.watch") return "facebook";
        return "web";
    }

    /// <summary>"/p/ABC/", "/reel/ABC", "/username/p/ABC/" → "ABC".</summary>
    public static string? InstagramShortcode(string? url)
    {
        var m = Regex.Match(url ?? "", @"instagram\.com/(?:[^/?#]+/)?(?:p|reel|reels|tv)/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>A comparable form of a Facebook post link: host-less path plus the ids that matter.</summary>
    private static string FacebookKey(string? url)
    {
        if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u)) return "";
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        var ids = string.Join("|", new[] { q["story_fbid"], q["fbid"], q["id"] }.Where(x => !string.IsNullOrEmpty(x)));
        return u.AbsolutePath.TrimEnd('/').ToLowerInvariant() + "?" + ids;
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Host.StartsWith("www.") ? u.Host[4..] : u.Host).ToLowerInvariant() : "";

    private static string? AbsoluteUrl(string baseUrl, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(new Uri(baseUrl), value.Trim(), out var u) && (u.Scheme == "https" || u.Scheme == "http") ? u.ToString() : null;
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
    }

    private static DateTime? ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d.UtcDateTime : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string? GraphError(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("error", out var e)
            ? (Str(e, "message") ?? "unbekannter Fehler") + (e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() == 190 ? " (Zugang abgelaufen oder ungültig)" : "")
            : null;

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        // Graph answers errors with a JSON body and a 4xx status — the body is what explains it.
        var text = await ReadLimitedStringAsync(resp, MaxPageBytes, ct);
        return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(HttpResponseMessage resp, long max, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength > max) throw new InvalidOperationException("Datei zu groß.");
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int read;
        while ((read = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, read);
            if (ms.Length > max) throw new InvalidOperationException("Datei zu groß.");
        }
        return ms.ToArray();
    }

    private static async Task<string> ReadLimitedStringAsync(HttpResponseMessage resp, long max, CancellationToken ct)
    {
        var bytes = await ReadLimitedBytesAsync(resp, max, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<string> SettingAsync(string key) =>
        (await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key))?.Value?.Trim() ?? "";

    private async Task SetSettingAsync(string key, string value)
    {
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) _db.SiteSettings.Add(new Models.SiteSetting { Key = key, Value = value });
        else row.Value = value;
        await _db.SaveChangesAsync();
    }

    // ---- outbound guard ---------------------------------------------------------------------------

    /// <summary>The handler behind <see cref="HttpClientName"/>. The link is typed by an admin, but the
    /// request is made BY THE SERVER — without a guard, "http://localhost:…", the Docker network or a
    /// cloud metadata address would be one paste away. The check sits in the connect step, so it also
    /// holds for every redirect and cannot be dodged by a DNS name that resolves differently later.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ip = addresses.FirstOrDefault(a => !IsPrivate(a))
                     ?? throw new HttpRequestException("Diese Adresse ist nicht erlaubt (internes Netz).");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || b[0] == 0 || b[0] == 127
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                   || b[0] >= 224;
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
               || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;   // fc00::/7 unique local
    }
}
