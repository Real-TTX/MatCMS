// Matmon live — the partner list: GET {cloud}/api/partners (only partners that opted in on their Branding page; name, description,
// website, city, country, logo, vanity page), as a block for matmon.eu's "Find a partner" page. Read in the background like the status.
// The LOGOS are passed through this site (/matmon-live/partner-logo/{id}), never linked to the cloud directly: a visitor's browser
// then talks to matmon.eu alone, which is what the privacy statement promises ("no third-party resources on a page view").
// The API carries no contact details on purpose (the partner decides what is public), so "contact" is the partner's own page on the
// cloud when it has one, else its website.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

public sealed record MmPartner(string Id, string Name, string Description, string Website, string City, string Country, bool HasLogo, string PageUrl,
    string Email = "", string Phone = "");

public static class MmPartners
{
    public const string BlockType = "matmon_partners";
    private const int MaxLogoBytes = 512 * 1024;
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    private static IReadOnlyList<MmPartner>? _list;
    private static DateTime _fetched = DateTime.MinValue;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private sealed record Logo(byte[] Bytes, string Type, string ETag, DateTime At);
    private static readonly ConcurrentDictionary<string, Logo> Logos = new();

    public static void Configure(IPluginBuilder b)
    {
        b.Strings("de", MmPartnerText.De);
        b.Strings("en", MmPartnerText.En);
        b.Block(new BlockSpec(BlockType, "mmp.block.name", "mmp.block.desc")
        {
            Category = "design",
            IconSvg = "<circle cx=\"9\" cy=\"8\" r=\"3\"/><circle cx=\"17\" cy=\"10\" r=\"2.5\"/><path d=\"M3 20c0-3.3 2.7-6 6-6s6 2.7 6 6\"/><path d=\"M14.5 15.2c.8-.7 1.7-1.2 2.5-1.2 2.2 0 4 1.8 4 4v2\"/>",
            Fields = new[]
            {
                new FieldSpec("eyebrow", "mmp.f.eyebrow") { Default = "Partners" },
                new FieldSpec("heading", "mmp.f.heading") { Default = "Find a partner" },
                new FieldSpec("intro", "mmp.f.intro", FieldKind.Textarea),
                new FieldSpec("empty", "mmp.f.empty") { Default = "No partners are listed yet." },
                new FieldSpec("becomeText", "mmp.f.becomeText"),
                new FieldSpec("becomeUrl", "mmp.f.becomeUrl", FieldKind.Url),
            }
        }, RenderAsync);
        b.Job("partners", JobSchedule.EveryInterval(TimeSpan.FromMinutes(10)), ctx => RefreshAsync(ctx, CancellationToken.None));
        b.Route("/matmon-live/partner-logo/{id}", LogoAsync, new PublicRouteOptions { Methods = PluginMethod.Get });
    }

    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
    private static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>An address a visitor may be sent to: absolute http(s), nothing else (a javascript: "website" stays text).</summary>
    public static string SafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.AbsoluteUri : "";

    /// <summary>A public e-mail address the partner chose to show, or "" - only plain name@domain, so it can stand in a mailto: link.</summary>
    public static string SafeEmail(string email) =>
        email.Length is > 3 and <= 200 && System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^\s@<>""'()\\,;:]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$") ? email : "";

    /// <summary>A phone number, or "" - digits, spaces and + ( ) - / . only, so it can stand in a tel: link.</summary>
    public static string SafePhone(string phone) =>
        phone.Length is >= 5 and <= 40 && phone.All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '(' or ')' or '-' or '/' or '.') ? phone : "";

    public static IReadOnlyList<MmPartner>? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o["partners"] is not JsonArray arr) return null;
            return arr.OfType<JsonObject>()
                .Select(p => new MmPartner(S(p["id"]), S(p["name"]), S(p["description"]), SafeUrl(S(p["website"])), S(p["city"]), S(p["country"]),
                    S(p["logoUrl"]).Length > 0, SafeUrl(S(p["pageUrl"])), SafeEmail(S(p["email"])), SafePhone(S(p["phone"]))))
                .Where(p => Guid.TryParse(p.Id, out _) && p.Name.Length > 0)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return null; }
    }

    /// <summary>Fetches the list when it is older than ten minutes. A failed fetch keeps the last list (a page that goes blank because
    /// the cloud restarts is worse than one a few minutes old).</summary>
    public static async Task RefreshAsync(IPluginContext ctx, CancellationToken ct, bool force = false)
    {
        if (!force && DateTime.UtcNow - _fetched < Fresh) return;
        if (!await Gate.WaitAsync(0, ct)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Matmon-Live/1.3");
            var list = Parse(await http.GetStringAsync(MmLive.Cloud(ctx) + "/api/partners", ct));
            if (list is not null) { _list = list; foreach (var gone in Logos.Keys.Where(k => list.All(p => p.Id != k)).ToList()) Logos.TryRemove(gone, out _); }
            _fetched = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ctx.Log("Matmon partners: " + ex.Message);
            _fetched = DateTime.UtcNow - Fresh + TimeSpan.FromMinutes(2);         // try again in two minutes
        }
        finally { Gate.Release(); }
    }

    // ---- the logo, passed through ---------------------------------------------------------------------------------------

    private static async Task<PluginResult> LogoAsync(IPluginContext ctx, CancellationToken ct)
    {
        var id = ctx.Request.R("id");
        // Only a partner of the current list, with a logo: the id goes into a request to the cloud, so it is checked, not trusted.
        if (!Guid.TryParse(id, out var g) || _list?.FirstOrDefault(p => p.Id == id) is not { HasLogo: true }) return PluginResult.NotFound();
        id = g.ToString();
        if (!Logos.TryGetValue(id, out var logo) || DateTime.UtcNow - logo.At > TimeSpan.FromHours(6))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Matmon-Live/1.3");
                using var res = await http.GetAsync(MmLive.Cloud(ctx) + "/api/partners/" + id + "/logo", HttpCompletionOption.ResponseHeadersRead, ct);
                if (!res.IsSuccessStatusCode) return logo is null ? PluginResult.NotFound() : Answer(logo);
                var bytes = await res.Content.ReadAsByteArrayAsync(ct);
                var type = Sniff(bytes);
                if (bytes.Length > MaxLogoBytes || type is null) return PluginResult.NotFound();
                logo = new Logo(bytes, type, "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + "\"", DateTime.UtcNow);
                Logos[id] = logo;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                ctx.Log("Matmon partner logo: " + ex.Message);
                if (logo is null) return PluginResult.NotFound();
            }
        }
        return Answer(logo!);
    }

    private static PluginResult Answer(Logo logo) =>
        PluginResult.File(logo.Bytes, logo.Type).WithHeader("Cache-Control", "public, max-age=3600").WithHeader("ETag", logo.ETag)
            .WithHeader("X-Content-Type-Options", "nosniff");

    /// <summary>PNG, JPEG or WebP by their first bytes — whatever the other side calls it. Anything else (SVG with script, HTML) is refused.</summary>
    public static string? Sniff(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return null;
    }

    // ---- the block --------------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> RenderAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (_list is null) await RefreshAsync(ctx, ct, force: true);          // the first view after a start
        else _ = RefreshAsync(ctx, CancellationToken.None);
        string F(string id) => ctx.Block?.Get(id) ?? "";
        var list = _list ?? Array.Empty<MmPartner>();
        var id = "mmp" + Guid.NewGuid().ToString("N")[..8];

        var sb = new StringBuilder($"<section class=\"section mm-live mmp\" id=\"{id}\"><div class=\"container\">");
        if (F("eyebrow") is { Length: > 0 } eb) sb.Append("<span class=\"mm-eyebrow\"><i class=\"ti ti-users-group\" aria-hidden=\"true\"></i>").Append(E(eb)).Append("</span>");
        if (F("heading") is { Length: > 0 } h) sb.Append("<h2 class=\"mm-title\">").Append(E(h)).Append("</h2>");
        if (F("intro") is { Length: > 0 } i) sb.Append("<p class=\"mm-intro\">").Append(E(i)).Append("</p>");

        if (list.Count == 0)
            sb.Append("<p class=\"mmp-empty\">").Append(E(F("empty") is { Length: > 0 } em ? em : ctx.T("mmp.none"))).Append("</p>");
        else
        {
            var countries = list.Select(p => p.Country).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
            // A search box (and a country choice) once there is something to search through; filtered in the browser, the list is small.
            if (list.Count >= 6)
            {
                sb.Append("<div class=\"mmp-tools\"><input type=\"search\" class=\"mmp-q\" placeholder=\"").Append(E(ctx.T("mmp.search"))).Append("\" aria-label=\"").Append(E(ctx.T("mmp.search"))).Append("\">");
                if (countries.Count > 1)
                {
                    sb.Append("<select class=\"mmp-c\" aria-label=\"").Append(E(ctx.T("mmp.country"))).Append("\"><option value=\"\">").Append(E(ctx.T("mmp.allCountries"))).Append("</option>");
                    foreach (var c in countries) sb.Append("<option>").Append(E(c)).Append("</option>");
                    sb.Append("</select>");
                }
                sb.Append("</div>");
            }
            sb.Append("<div class=\"mmp-grid\">");
            foreach (var p in list)
            {
                var place = string.Join(", ", new[] { p.City, p.Country }.Where(x => x.Length > 0));
                var contact = p.PageUrl.Length > 0 ? p.PageUrl : p.Website.Length > 0 ? p.Website : p.Email.Length > 0 ? "mailto:" + p.Email : "";
                sb.Append($"<article class=\"mm-card mmp-card\" data-country=\"{E(p.Country)}\" data-text=\"{E((p.Name + " " + p.Description + " " + place).ToLowerInvariant())}\">");
                sb.Append("<div class=\"mmp-logo\">");
                if (p.HasLogo) sb.Append($"<img src=\"/matmon-live/partner-logo/{E(p.Id)}\" alt=\"{E(p.Name)}\" loading=\"lazy\">");
                else sb.Append("<span>").Append(E(p.Name.Substring(0, 1).ToUpperInvariant())).Append("</span>");
                sb.Append("</div><h3>").Append(E(p.Name)).Append("</h3>");
                if (place.Length > 0) sb.Append("<p class=\"mmp-place\"><i class=\"ti ti-map-pin\" aria-hidden=\"true\"></i>").Append(E(place)).Append("</p>");
                if (p.Description.Length > 0) sb.Append("<p>").Append(E(p.Description)).Append("</p>");
                if (p.Email.Length > 0)
                    sb.Append("<p class=\"mmp-place\"><i class=\"ti ti-mail\" aria-hidden=\"true\"></i><a href=\"mailto:").Append(E(p.Email)).Append("\">").Append(E(p.Email)).Append("</a></p>");
                if (p.Phone.Length > 0)
                    sb.Append("<p class=\"mmp-place\"><i class=\"ti ti-phone\" aria-hidden=\"true\"></i><a href=\"tel:")
                        .Append(E(new string(p.Phone.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray()))).Append("\">").Append(E(p.Phone)).Append("</a></p>");
                sb.Append("<div class=\"mmp-actions\">");
                if (contact.Length > 0)
                    sb.Append($"<a class=\"btn\" href=\"{E(contact)}\" target=\"_blank\" rel=\"noopener\">").Append(E(ctx.T("mmp.contact"))).Append("</a>");
                if (p.Website.Length > 0 && p.Website != contact)
                    sb.Append($"<a class=\"btn mmp-ghost\" href=\"{E(p.Website)}\" target=\"_blank\" rel=\"noopener\">").Append(E(ctx.T("mmp.website"))).Append("</a>");
                sb.Append("</div></article>");
            }
            sb.Append("</div><p class=\"mmp-empty mmp-nomatch\" hidden>").Append(E(ctx.T("mmp.noMatch"))).Append("</p>");
        }
        if (F("becomeText") is { Length: > 0 } bt && F("becomeUrl") is { Length: > 0 } bu
            && (bu.StartsWith('/') && !bu.StartsWith("//") || SafeUrl(bu).Length > 0))
            sb.Append("<p class=\"mmp-become\"><a class=\"btn mmp-ghost\" href=\"").Append(E(bu)).Append("\">").Append(E(bt)).Append("</a></p>");
        sb.Append("</div></section>").Append(MmLive.Css).Append(Css).Append(Script(id));
        return PluginResult.Fragment(sb.ToString());
    }

    private static string Script(string id) => "<script>(function(){var r=document.getElementById('" + id + "');if(!r)return;"
        + "var q=r.querySelector('.mmp-q'),c=r.querySelector('.mmp-c'),none=r.querySelector('.mmp-nomatch');"
        + "function f(){var t=(q&&q.value||'').trim().toLowerCase(),k=c&&c.value||'',n=0;r.querySelectorAll('.mmp-card').forEach(function(a){"
        + "var ok=(!t||a.getAttribute('data-text').indexOf(t)>=0)&&(!k||a.getAttribute('data-country')===k);a.hidden=!ok;if(ok)n++;});"
        + "if(none)none.hidden=n>0;}"
        + "if(q)q.addEventListener('input',f);if(c)c.addEventListener('change',f);})();</script>";

    private const string Css = "<style>.mmp .mmp-tools{display:flex;flex-wrap:wrap;gap:10px;margin:0 0 22px}"
        + ".mmp .mmp-q{flex:1 1 260px;padding:10px 14px;border:1px solid var(--line,#e5e7eb);border-radius:10px;font:inherit;background:var(--bg,#fff);color:inherit}"
        + ".mmp .mmp-c{padding:10px 12px;border:1px solid var(--line,#e5e7eb);border-radius:10px;font:inherit;background:var(--bg,#fff);color:inherit}"
        + ".mmp .mmp-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:18px}"
        + ".mmp .mmp-card{gap:10px}.mmp .mmp-card[hidden]{display:none}"
        + ".mmp .mmp-logo{height:64px;display:flex;align-items:center;justify-content:flex-start}"
        + ".mmp .mmp-logo img{max-height:56px;max-width:180px;width:auto;height:auto;object-fit:contain}"
        + ".mmp .mmp-logo span{width:56px;height:56px;border-radius:12px;display:inline-flex;align-items:center;justify-content:center;font-weight:700;font-size:22px;"
        + "background:color-mix(in srgb,var(--accent,#295cff) 12%,transparent);color:var(--accent,#295cff)}"
        + ".mmp .mmp-card h3{margin:0;font-size:18px}.mmp .mmp-place{display:flex;align-items:center;gap:6px;font-size:14px;margin:0}.mmp .mmp-place a{color:inherit;overflow-wrap:anywhere}"
        + ".mmp .mmp-actions{display:flex;flex-wrap:wrap;gap:8px;margin-top:auto;padding-top:6px}"
        + ".mmp .mmp-actions .btn{padding:8px 14px;font-size:14px}"
        + ".mmp .mmp-ghost{background:transparent!important;color:var(--accent,#295cff)!important;border:1px solid var(--line,#e5e7eb)!important}"
        + ".mmp .mmp-empty{color:var(--muted,#6b7280)}.mmp .mmp-become{margin-top:26px;text-align:center}"
        + "@media (max-width:900px){.mmp .mmp-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media (max-width:600px){.mmp .mmp-grid{grid-template-columns:minmax(0,1fr)}}</style>";
}

public static class MmPartnerText
{
    public static readonly Dictionary<string, string> En = new()
    {
        ["mmp.block.name"] = "Matmon: partners", ["mmp.block.desc"] = "The Matmon partners who chose to be listed, with logo, place and contact – from Matmon Cloud.",
        ["mmp.f.eyebrow"] = "Line above the heading", ["mmp.f.heading"] = "Heading", ["mmp.f.intro"] = "Introduction",
        ["mmp.f.empty"] = "Text when nobody is listed", ["mmp.f.becomeText"] = "Button below: text (e.g. Become a partner)", ["mmp.f.becomeUrl"] = "Button below: link",
        ["mmp.none"] = "No partners are listed yet.", ["mmp.search"] = "Search partners", ["mmp.country"] = "Country", ["mmp.allCountries"] = "All countries",
        ["mmp.contact"] = "Contact", ["mmp.website"] = "Website", ["mmp.noMatch"] = "No partner matches your search.",
    };

    public static readonly Dictionary<string, string> De = new()
    {
        ["mmp.block.name"] = "Matmon: Partner", ["mmp.block.desc"] = "Die Matmon-Partner, die gelistet sein wollen, mit Logo, Ort und Kontakt – aus der Matmon Cloud.",
        ["mmp.f.eyebrow"] = "Zeile über der Überschrift", ["mmp.f.heading"] = "Überschrift", ["mmp.f.intro"] = "Einleitung",
        ["mmp.f.empty"] = "Text, wenn niemand gelistet ist", ["mmp.f.becomeText"] = "Knopf darunter: Text (z. B. Partner werden)", ["mmp.f.becomeUrl"] = "Knopf darunter: Link",
        ["mmp.none"] = "Noch keine Partner gelistet.", ["mmp.search"] = "Partner suchen", ["mmp.country"] = "Land", ["mmp.allCountries"] = "Alle Länder",
        ["mmp.contact"] = "Kontakt", ["mmp.website"] = "Website", ["mmp.noMatch"] = "Kein Partner passt zur Suche.",
    };
}
