using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// The club's dates as iCalendar feeds to subscribe to in a phone or Outlook calendar:
/// <c>/verein/termine.ics</c> — everybody, the PUBLIC events only;
/// <c>/verein/kalender/{token}.ics</c> — one member's own feed (a calendar app cannot sign in, so the secret link is the key): every event that is not
/// a draft, plus the meetings the person is invited to. A member can replace the link, which ends the old one.
/// </summary>
public static class VereinIcal
{
    public const string PublicFeed = "/verein/termine.ics";
    public static string PersonalFeed(string token) => "/verein/kalender/" + token + ".ics";

    public static void Configure(IPluginBuilder b)
    {
        var read = new PublicRouteOptions { Methods = PluginMethod.Get, DefaultLanguageOnly = true };
        b.Route(PublicFeed, PublicAsync, read);
        b.Route("/verein/kalender/{token}", PersonalAsync, read);
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    private static async Task<PluginResult> PublicAsync(IPluginContext ctx, CancellationToken ct)
    {
        var events = (await new VereinStore(ctx.Data).EventsAsync(ct)).Where(e => !e.Draft && e.Public);
        return Calendar(ctx, VereinSettings.Club(ctx) + " – Termine", events, Array.Empty<Meeting>());
    }

    private static async Task<PluginResult> PersonalAsync(IPluginContext ctx, CancellationToken ct)
    {
        var token = ctx.Request.R("token");
        if (token.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) token = token[..^4];
        if (token.Length != 40 || !token.All(Uri.IsHexDigit)) return PluginResult.NotFound();
        var store = new VereinStore(ctx.Data);
        var p = (await store.PeopleAsync(ct)).FirstOrDefault(x => !x.Child && x.Active && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(x.CalToken), Encoding.ASCII.GetBytes(token)));
        if (p is null) return PluginResult.NotFound();
        var boardRoles = VereinSettings.BoardList(ctx);
        var board = p.Roles.Any(r => boardRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
        var events = (await store.EventsAsync(ct)).Where(e => !e.Draft);
        var meetings = (await store.MeetingsAsync(ct)).Where(m => board || VereinStore.InAudience(p, m.Audience, boardRoles));
        return Calendar(ctx, VereinSettings.Club(ctx) + " – meine Termine", events, meetings);
    }

    /// <summary>The calendar; with <paramref name="download"/> as a file to open (one event: "add to calendar"), else as a feed to subscribe to.</summary>
    internal static PluginResult Calendar(IPluginContext ctx, string name, IEnumerable<Event> events, IEnumerable<Meeting> meetings, string? download = null)
    {
        var origin = VereinMail.Origin(ctx);
        var host = new Uri(origin.Length > 0 ? origin : "https://localhost").Host;
        var sb = new StringBuilder();
        void L(string line) => sb.Append(Fold(line)).Append("\r\n");
        L("BEGIN:VCALENDAR"); L("VERSION:2.0"); L("PRODID:-//MatCMS//Verein//DE"); L("CALSCALE:GREGORIAN"); L("METHOD:PUBLISH");
        L("X-WR-CALNAME:" + Esc(name)); L("X-WR-TIMEZONE:Europe/Berlin"); L("REFRESH-INTERVAL;VALUE=DURATION:PT6H"); L("X-PUBLISHED-TTL:PT6H");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var e in events)
        {
            if (!V.TryParse(e.Starts, out var start, out var hasTime)) continue;
            L("BEGIN:VEVENT"); L("UID:event-" + e.Id + "@" + host); L("DTSTAMP:" + stamp);
            Times(L, start, hasTime, e.Ends, 2);
            L("SUMMARY:" + Esc(e.Title + (e.Cancelled ? " (abgesagt)" : "")));
            if (e.Place.Length > 0) L("LOCATION:" + Esc(e.Place));
            var desc = new List<string>();
            if (e.Description.Length > 0) desc.Add(e.Description);
            if (e.Meeting.Length > 0 && !e.Public) desc.Add("Treffpunkt: " + e.Meeting);
            if (desc.Count > 0) L("DESCRIPTION:" + Esc(string.Join("\n\n", desc)));
            if (origin.Length > 0) L("URL:" + origin + (e.Public ? "/" : VereinPublic.Base + "/termine/" + e.Id));
            if (e.Cancelled) L("STATUS:CANCELLED");
            L("END:VEVENT");
        }
        foreach (var m in meetings)
        {
            if (!V.TryParse(m.Starts, out var start, out var hasTime)) continue;
            L("BEGIN:VEVENT"); L("UID:meeting-" + m.Id + "@" + host); L("DTSTAMP:" + stamp);
            Times(L, start, hasTime, "", 2);
            L("SUMMARY:" + Esc(m.Title + (m.Status == VereinStore.Cancelled ? " (abgesagt)" : "")));
            if (m.Place.Length > 0) L("LOCATION:" + Esc(m.Place));
            if (m.Agenda.Length > 0) L("DESCRIPTION:" + Esc("Tagesordnung:\n" + m.Agenda));
            if (origin.Length > 0) L("URL:" + origin + VereinPublic.Base + "/sitzungen/" + m.Id);
            if (m.Status == VereinStore.Cancelled) L("STATUS:CANCELLED");
            L("END:VEVENT");
        }
        L("END:VCALENDAR");
        return download is null
            ? PluginResult.Text(sb.ToString(), "text/calendar; charset=utf-8").WithHeader("Cache-Control", "private, max-age=900")
            : PluginResult.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/calendar; charset=utf-8", download);
    }

    /// <summary>A date with a time is written in UTC (no time-zone block needed); a date alone as an all-day entry.</summary>
    private static void Times(Action<string> L, DateTime start, bool hasTime, string ends, int defaultHours)
    {
        if (!hasTime || (start.Hour == 0 && start.Minute == 0 && !ends.Contains('T')))
        {
            var endDay = V.TryParse(ends, out var e, out _) && e.Date >= start.Date ? e.Date.AddDays(1) : start.Date.AddDays(1);
            L("DTSTART;VALUE=DATE:" + start.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            L("DTEND;VALUE=DATE:" + endDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            return;
        }
        var s = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), V.Zone);
        var end = V.TryParse(ends, out var en, out var endHasTime) && endHasTime && en > start ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(en, DateTimeKind.Unspecified), V.Zone) : s.AddHours(defaultHours);
        L("DTSTART:" + s.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        L("DTEND:" + end.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
    }

    private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\n").Replace("\n", "\\n");

    /// <summary>Lines longer than 75 octets are folded (RFC 5545) — never inside a UTF-8 character.</summary>
    private static string Fold(string line)
    {
        var bytes = Encoding.UTF8.GetByteCount(line);
        if (bytes <= 75) return line;
        var sb = new StringBuilder();
        var count = 0;
        foreach (var ch in line)
        {
            var n = Encoding.UTF8.GetByteCount(ch.ToString());
            if (count + n > 74) { sb.Append("\r\n "); count = 1; }
            sb.Append(ch);
            count += n;
        }
        return sb.ToString();
    }
}
