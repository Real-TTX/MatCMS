using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MatCMS.Plugin.Booking;

/// <summary>A portal's calendar for one unit.</summary>
public sealed record Feed(long Id, long UnitId, string Name, string Url, string ExportToken, DateTime? LastRun, DateTime? LastOk, string LastError, int Events, string Conflicts);

/// <summary>One busy period read from a calendar: nights from <see cref="From"/> up to the day before <see cref="To"/>.</summary>
public sealed record IcsEvent(DateOnly From, DateOnly To, string Uid, string Summary);

/// <summary>
/// The calendar exchange with Airbnb, Booking.com, Vrbo, Rentlio … (phase B4). Both ways are iCal: every portal can read and write it, no partner contract
/// needed. What iCal cannot carry — price, guest — stays where it is; only busy nights travel.
/// <list type="bullet">
/// <item>OUT: <c>/buchung/ical/{token}.ics</c>. The unit's token gives all its busy nights (bookings, requests that hold, blocks, other portals', the nights of
/// the apartments a whole house contains, plus the cleaning buffer). A FEED's token gives the same without the nights that came from that feed — a portal
/// must not get its own reservations back as foreign blocks.</item>
/// <item>IN: a job fetches every feed every 15 minutes (https only, public addresses only — checked when the connection is made, so a name that resolves to
/// an inner address is refused too —, 2 MB, 20 s, three redirects) and REPLACES the feed's blocks in one transaction: a reservation the portal cancelled
/// disappears here. A night already taken here is not overwritten — the block is kept with a conflict note, the host is told (attention + mail).</item>
/// </list>
/// </summary>
public static class BookingIcal
{
    public const string ExportPath = "/buchung/ical/";
    public const int MaxBytes = 2 * 1024 * 1024, MaxEvents = 3000;

    public static void Configure(IPluginBuilder b)
    {
        b.Route(ExportPath + "{token}", ExportAsync, new PublicRouteOptions { Methods = PluginMethod.Get });
        b.Job("ical-feeds", JobSchedule.EveryInterval(TimeSpan.FromMinutes(15)), async (ctx, ct) =>
        {
            foreach (var f in await FeedsAsync(ctx.Data, ct))
                if (f.Url.Length > 0) await RunAsync(ctx, f, ct);
        });
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    // ---- reading and keeping feeds ----------------------------------------------------------------------------------

    private static Feed F(DataRow r) => new(r.Id, r.Int("unit_id"), r.Text("name"), r.Text("url"), r.Text("export_token"), r.Timestamp("last_run"), r.Timestamp("last_ok"),
        r.Text("last_error"), (int)r.Int("events"), r.Text("conflicts"));

    public static async Task<List<Feed>> FeedsAsync(IPluginData data, CancellationToken ct = default) =>
        (await data.Table(BookingStore.TFeeds).QueryAsync(DataQuery.Everything.OrderBy("unit_id").OrderBy("name"), ct)).Select(F).ToList();

    public static async Task<Feed?> FeedAsync(IPluginData data, long id, CancellationToken ct = default) =>
        await data.Table(BookingStore.TFeeds).GetAsync(id, ct) is { } r ? F(r) : null;

    /// <summary>The unit's own calendar token — made the first time it is asked for.</summary>
    public static async Task<string> UnitTokenAsync(IPluginData data, long unitId, CancellationToken ct = default)
    {
        var row = await data.Table(BookingStore.TUnits).GetAsync(unitId, ct);
        if (row is null) return "";
        if (row.Text("ical_token") is { Length: >= 20 } t) return t;
        var token = NewToken();
        await data.Table(BookingStore.TUnits).UpdateAsync(unitId, new Dictionary<string, object?> { ["ical_token"] = token }, ct);
        return token;
    }

    /// <summary>New tokens for a unit and all its feeds: every address handed out before stops working (a leaked one is dead).</summary>
    public static async Task RenewTokensAsync(IPluginData data, long unitId, CancellationToken ct = default)
    {
        await data.Table(BookingStore.TUnits).UpdateAsync(unitId, new Dictionary<string, object?> { ["ical_token"] = NewToken() }, ct);
        foreach (var f in (await FeedsAsync(data, ct)).Where(f => f.UnitId == unitId))
            await data.Table(BookingStore.TFeeds).UpdateAsync(f.Id, new Dictionary<string, object?> { ["export_token"] = NewToken() }, ct);
    }

    /// <summary>Removes a feed and every block it brought.</summary>
    public static async Task RemoveFeedAsync(IPluginData data, long feedId, CancellationToken ct = default) =>
        await data.InTransactionAsync(async tx =>
        {
            await ClearAsync(tx, feedId, ct);
            await tx.Table(BookingStore.TFeeds).DeleteAsync(feedId, ct);
        }, ct);

    private static async Task ClearAsync(IPluginData tx, long feedId, CancellationToken ct)
    {
        foreach (var blk in await tx.Table(BookingStore.TBlocks).QueryAsync(DataQuery.Everything.Eq("source", "feed:" + feedId), ct))
            await tx.Table(BookingStore.TNights).DeleteWhereAsync(DataQuery.Everything.Eq("block_id", blk.Id), ct);
        await tx.Table(BookingStore.TBlocks).DeleteWhereAsync(DataQuery.Everything.Eq("source", "feed:" + feedId), ct);
    }

    // ---- OUT ----------------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> ExportAsync(IPluginContext ctx, CancellationToken ct)
    {
        var token = ctx.Request.R("token");
        if (token.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) token = token[..^4];
        if (token.Length < 20 || token.Length > 80) return PluginResult.NotFound();
        var store = new BookingStore(ctx.Data);
        long unitId = 0, without = 0;
        if (await ctx.Data.Table(BookingStore.TUnits).FindAsync(DataQuery.Everything.Eq("ical_token", token), ct) is { } u) unitId = u.Id;
        else if (await ctx.Data.Table(BookingStore.TFeeds).FindAsync(DataQuery.Everything.Eq("export_token", token), ct) is { } f) { unitId = f.Int("unit_id"); without = f.Id; }
        var unit = unitId > 0 ? await store.UnitAsync(unitId, ct) : null;
        if (unit is null) return PluginResult.NotFound();
        var ics = await BuildAsync(ctx, unit, without, ct);
        return PluginResult.File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8")
            .WithHeader("Cache-Control", "no-store").WithHeader("X-Robots-Tag", "noindex");
    }

    /// <summary>The calendar of a unit: busy periods, merged, widened by the buffer days, from a month back to three years ahead (Airbnb looks as far). No names, no prices.</summary>
    public static async Task<string> BuildAsync(IPluginContext ctx, Unit unit, long withoutFeed, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var today = B.Today;
        var units = await store.UnitsAsync(ct);
        var nights = await store.NightsAsync(new[] { unit.Id }.Concat(unit.Contains), today.AddDays(-31), today.AddYears(3), ct);
        if (withoutFeed > 0)
        {
            var skip = (await ctx.Data.Table(BookingStore.TBlocks).QueryAsync(DataQuery.Everything.Eq("source", "feed:" + withoutFeed), ct)).Select(r => r.Id).ToHashSet();
            nights = nights.Where(n => n.BlockId == 0 || !skip.Contains(n.BlockId)).ToList();
        }
        var days = nights.Select(n => n.Day).ToHashSet();
        if (unit.BufferDays > 0)
            foreach (var d in days.ToList())
                for (var i = 1; i <= unit.BufferDays; i++) { days.Add(d.AddDays(i)); days.Add(d.AddDays(-i)); }
        var host = Uri.TryCreate(ctx.Site.Origin, UriKind.Absolute, out var o) ? o.Host : "matcms";
        var sb = new StringBuilder();
        void L(string line) => sb.Append(Fold(line)).Append("\r\n");
        L("BEGIN:VCALENDAR"); L("VERSION:2.0"); L("PRODID:-//MatCMS//Buchungen//DE"); L("CALSCALE:GREGORIAN"); L("METHOD:PUBLISH");
        L("X-WR-CALNAME:" + Esc(unit.Name));
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var (from, to) in Runs(days))
        {
            L("BEGIN:VEVENT");
            L("UID:" + unit.Id + "-" + from.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "@" + host);
            L("DTSTAMP:" + stamp);
            L("DTSTART;VALUE=DATE:" + from.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            L("DTEND;VALUE=DATE:" + to.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            L("SUMMARY:Belegt");
            L("TRANSP:OPAQUE");
            L("END:VEVENT");
        }
        L("END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>Busy nights → periods [first night, day after the last night).</summary>
    public static IEnumerable<(DateOnly From, DateOnly To)> Runs(IEnumerable<DateOnly> nights)
    {
        DateOnly? start = null, last = null;
        foreach (var d in nights.Distinct().OrderBy(d => d))
        {
            if (start is null) { start = last = d; continue; }
            if (d == last!.Value.AddDays(1)) { last = d; continue; }
            yield return (start.Value, last!.Value.AddDays(1));
            start = last = d;
        }
        if (start is not null) yield return (start.Value, last!.Value.AddDays(1));
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");

    /// <summary>RFC 5545 line folding at 75 octets (a continuation line starts with a space).</summary>
    private static string Fold(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= 75) return line;
        var sb = new StringBuilder();
        var count = 0;
        foreach (var ch in line)
        {
            var n = Encoding.UTF8.GetByteCount(ch.ToString());
            if (count + n > 75) { sb.Append("\r\n "); count = 1; }
            sb.Append(ch); count += n;
        }
        return sb.ToString();
    }

    // ---- reading a calendar -------------------------------------------------------------------------------------------

    /// <summary>
    /// The busy periods of an iCal text, leniently: folded lines, DATE and DATE-TIME (the date part is the day; a DTEND with a time on a later day is the
    /// departure), no DTEND = one night, cancelled events and events that end before they start are skipped. At most <see cref="MaxEvents"/>.
    /// </summary>
    public static List<IcsEvent> Parse(string text)
    {
        var lines = new List<string>();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && lines.Count > 0) lines[^1] += raw[1..];
            else lines.Add(raw);
        }
        var events = new List<IcsEvent>();
        Dictionary<string, string>? cur = null;
        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new(StringComparer.OrdinalIgnoreCase); continue; }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (cur is not null && Make(cur) is { } e) events.Add(e);
                cur = null;
                if (events.Count >= MaxEvents) break;
                continue;
            }
            if (cur is null) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Split(';')[0].Trim();
            cur.TryAdd(name, line[(colon + 1)..].Trim());
        }
        return events;
    }

    private static IcsEvent? Make(Dictionary<string, string> v)
    {
        if (v.TryGetValue("STATUS", out var st) && st.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) return null;
        if (!v.TryGetValue("DTSTART", out var s) || Date(s) is not { } from) return null;
        var to = v.TryGetValue("DTEND", out var e) && Date(e) is { } end ? end : from.AddDays(1);
        if (to <= from) to = from.AddDays(1);
        if (to.DayNumber - from.DayNumber > 400) return null;                     // a "blocked forever" placeholder, not a stay
        string Unesc(string x) => x.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
        return new IcsEvent(from, to, B.Clip(v.GetValueOrDefault("UID") ?? "", 200), B.Clip(Unesc(v.GetValueOrDefault("SUMMARY") ?? ""), 120));
    }

    private static DateOnly? Date(string value) =>
        value.Length >= 8 && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    // ---- IN: fetching -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Fetches a calendar from the outside. The address must be https; every connection — the first and each redirect — goes only to a PUBLIC address,
    /// checked on the resolved IP inside the connect itself (a host name that resolves to 127.0.0.1, 10.x, 169.254.x … or an IPv6 private range is refused,
    /// also when it changes between two lookups). At most 2 MB and 20 s.
    /// </summary>
    public static async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = async (context, token) =>
            {
                var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                var allowed = addresses.Where(IsPublic).ToList();
                if (allowed.Count == 0) throw new HttpRequestException("Die Adresse führt nicht ins öffentliche Internet.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(allowed[0], context.DnsEndPoint.Port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Buchungen/1.0");
        var uri = Check(url);
        for (var hop = 0; hop < 4; hop++)
        {
            using var res = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } loc)
            {
                uri = Check(new Uri(uri, loc).ToString());
                continue;
            }
            if (!res.IsSuccessStatusCode) throw new HttpRequestException("Antwort " + (int)res.StatusCode + " " + res.ReasonPhrase);
            if (res.Content.Headers.ContentLength > MaxBytes) throw new HttpRequestException("Der Kalender ist größer als 2 MB.");
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buf = new byte[16384];
            int n;
            while ((n = await stream.ReadAsync(buf, ct)) > 0)
            {
                if (ms.Length + n > MaxBytes) throw new HttpRequestException("Der Kalender ist größer als 2 MB.");
                ms.Write(buf, 0, n);
            }
            var text = Encoding.UTF8.GetString(ms.ToArray());
            if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) throw new HttpRequestException("Unter der Adresse liegt kein Kalender (kein BEGIN:VCALENDAR).");
            return text;
        }
        throw new HttpRequestException("Zu viele Weiterleitungen.");
    }

    /// <summary>The address as given: absolute, https, no user/password in it. Throws with a reason a host understands.</summary>
    public static Uri Check(string url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u)) throw new HttpRequestException("Das ist keine vollständige Adresse.");
        if (u.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException("Nur https-Adressen (die Portale liefern alle https).");
        if (u.UserInfo.Length > 0) throw new HttpRequestException("Die Adresse darf keinen Benutzernamen enthalten.");
        if (IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && !IsPublic(ip)) throw new HttpRequestException("Die Adresse führt nicht ins öffentliche Internet.");
        if (u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Die Adresse führt nicht ins öffentliche Internet.");
        return u;
    }

    /// <summary>Only addresses of the public internet: no loopback, private, link-local, carrier-grade NAT, benchmark, multicast or reserved ranges.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC || ip.Equals(IPAddress.IPv6Loopback));
        }
        return false;
    }

    // ---- IN: applying -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Replaces a feed's blocks with what its calendar says now — one transaction, so a reader never sees the unit half-free. Only periods that have not
    /// ended and start within three years. A night already taken here (a booking, a block, another portal) is NOT overwritten: the block is still written,
    /// with the free nights only and a note naming what it collides with. Returns the conflicts in words.
    /// </summary>
    public static async Task<List<string>> ApplyAsync(IPluginData data, Feed feed, IReadOnlyList<IcsEvent> events, CancellationToken ct = default)
    {
        var store = new BookingStore(data);
        var unit = await store.UnitAsync(feed.UnitId, ct) ?? throw new BookingException("booking.ical.unitGone");
        var units = BookingStore.Occupies(unit, await store.UnitsAsync(ct));
        var today = B.Today;
        var keep = events.Where(e => e.To > today && e.From < today.AddYears(3)).ToList();
        var bookings = (await BookingFlow.ListAsync(data, null, ct)).ToDictionary(b => b.Id, b => b.Number);
        var conflicts = new List<string>();
        await data.InTransactionAsync(async tx =>
        {
            await tx.Table(BookingStore.TMeta).UpdateWhereAsync(DataQuery.Everything.Eq("name", "__lock"), new Dictionary<string, object?> { ["value"] = "" }, ct);
            await ClearAsync(tx, feed.Id, ct);
            var mine = new HashSet<long>();                                     // blocks written in THIS run: a calendar cannot collide with itself
            foreach (var e in keep)
            {
                var from = e.From < today ? today : e.From;
                var all = await BookingStore.NightsIn(tx, units, from, e.To, ct);
                var taken = all.Where(n => !mine.Contains(n.BlockId)).ToList();
                var clash = taken.Select(n => n.BookingId > 0 ? "Buchung " + (bookings.TryGetValue(n.BookingId, out var no) ? no : "#" + n.BookingId) : "Sperre").Distinct().ToList();
                var note = clash.Count == 0 ? "" : feed.Name + " " + B.Date(e.From) + "–" + B.Date(e.To) + " überschneidet sich mit " + string.Join(", ", clash);
                if (note.Length > 0) conflicts.Add(note);
                var blockId = await tx.Table(BookingStore.TBlocks).InsertAsync(new Dictionary<string, object?>
                {
                    ["unit_id"] = unit.Id, ["from_day"] = B.Iso(e.From), ["to_day"] = B.Iso(e.To), ["reason"] = B.Clip(feed.Name + (e.Summary.Length > 0 ? ": " + e.Summary : ""), 200),
                    ["source"] = "feed:" + feed.Id, ["uid"] = e.Uid, ["conflict"] = note, ["created"] = DateTime.UtcNow
                }, ct);
                mine.Add(blockId);
                var busy = all.Select(n => (n.UnitId, n.Day)).ToHashSet();
                for (var d = from; d < e.To; d = d.AddDays(1))
                    foreach (var u in units)
                        if (!busy.Contains((u, d)))
                        {
                            busy.Add((u, d));                                   // two overlapping events of the same feed: the first one has the night
                            await tx.Table(BookingStore.TNights).InsertAsync(new Dictionary<string, object?>
                            {
                                ["unit_id"] = u, ["night"] = B.Iso(d), ["booking_id"] = 0, ["block_id"] = blockId, ["origin"] = unit.Id
                            }, ct);
                        }
            }
        }, ct);
        return conflicts;
    }

    /// <summary>
    /// Fetches and applies one feed and writes down how it went. A failed fetch keeps the blocks it brought last time (an outage at Airbnb must not open
    /// the calendar). A NEW conflict (one that was not there on the last run) is mailed to the host once.
    /// </summary>
    public static async Task RunAsync(IPluginContext ctx, Feed feed, CancellationToken ct, string? text = null)
    {
        var values = new Dictionary<string, object?> { ["last_run"] = DateTime.UtcNow };
        try
        {
            var events = Parse(text ?? await FetchAsync(feed.Url, ct));
            var conflicts = await ApplyAsync(ctx.Data, feed, events, ct);
            values["last_ok"] = DateTime.UtcNow; values["last_error"] = ""; values["events"] = events.Count; values["conflicts"] = string.Join("\n", conflicts);
            var fresh = conflicts.Except(feed.Conflicts.Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToList();
            if (fresh.Count > 0 && BookingPlugin.NotifyAddress(ctx) is { Length: > 0 } host)
            {
                var unit = await new BookingStore(ctx.Data).UnitAsync(feed.UnitId, ct);
                await BookingMail.SendAsync(ctx, BookingMail.Conflict, host, new Dictionary<string, string>
                {
                    ["einheit"] = unit?.Name ?? "", ["kanal"] = feed.Name, ["konflikte"] = string.Join("\n", fresh), ["website"] = ctx.Site.Name,
                    ["admin_link"] = (ctx.Site.Origin ?? "").TrimEnd('/') + BookingAdmin.Root + "channels"
                }, ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BookingException or SocketException or IOException)
        {
            values["last_error"] = B.Clip(ex is TaskCanceledException ? "Keine Antwort innerhalb von 20 Sekunden." : ex.Message, 300);
        }
        await ctx.Data.Table(BookingStore.TFeeds).UpdateAsync(feed.Id, values, ct);
    }
}
