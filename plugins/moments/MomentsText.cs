using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MatCMS.Plugin.Moments;

/// <summary>Settings of the plugin (keys are stored — never renamed).</summary>
public static class MomentsSettings
{
    public const string Title = "title", EventName = "eventName", HostName = "hostName", Welcome = "welcome", LoginIntro = "loginIntro", MenuLabel = "menuLabel",
        UploadApproval = "uploadApproval", CommentApproval = "commentApproval", GuestTags = "guestTags", NewTags = "newTags", GuestUpload = "guestUpload",
        MaxUploadMb = "maxUploadMb", Downloads = "downloads", ShowTable = "showTable";

    public const string DownloadOriginal = "original", DownloadView = "view", DownloadOff = "off";

    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(EventName, "Name des Anlasses") { Group = "Allgemein", Help = "Z. B. „Hochzeit von Grit & Steffen“. Erscheint über der Galerie und auf den Zugangskarten. Leer = Name der Website." })
        .Text(Title, "Überschrift der Galerie", "Unsere Momente")
        .Text(HostName, "Name der Gastgeber", "", "So erscheinen Fotos und Kommentare, die ihr im Admin hochladet oder schreibt (z. B. „Grit & Steffen“). Leer = Name des Anlasses.")
        .TextArea(Welcome, "Begrüßung in der Galerie", "Schön, dass ihr da seid! Hier findet ihr die Fotos von unserem Tag – und hier könnt ihr eure eigenen Bilder hochladen.")
        .TextArea(LoginIntro, "Text auf der Anmeldeseite", "Gebt den persönlichen Zugangscode ein, den ihr von uns bekommen habt – oder scannt den QR-Code auf eurer Karte.")
        .Text(MenuLabel, "Menüpunkt für angemeldete Gäste", "Fotos", "Erscheint im Hauptmenü der Website, sobald ein Gast angemeldet ist (mit Hochladen, Meine Fotos und Abmelden darunter).")
        .Add(new SettingSpec(UploadApproval, "Fotos der Gäste erst nach Freigabe zeigen", SettingKind.Bool)
        {
            Group = "Freigabe & Moderation", Default = "true",
            Help = "An: Ein hochgeladenes Foto sehen erst ihr (Admin → Moments → Freigabe) und der Gast selbst; alle anderen erst, wenn ihr es freigegeben habt."
        })
        .Bool(CommentApproval, "Kommentare erst nach Freigabe zeigen", false, "Aus: Kommentare erscheinen sofort; ihr könnt sie jederzeit ausblenden oder löschen.")
        .Bool(GuestTags, "Gäste dürfen Fotos markieren", true, "Personen und Stichworte an Fotos setzen. Ihr könnt jede Markierung wieder entfernen.")
        .Bool(NewTags, "Gäste dürfen neue Stichworte anlegen", true, "Aus: Gäste wählen nur aus den Personen der Gästeliste und den Stichworten, die es schon gibt.")
        .Bool(ShowTable, "Tisch der Gäste im Gästebereich zeigen", false, "An: Bei der Begrüßung steht, an welchem Tisch der Haushalt sitzt (Feld „Tisch“ in der Gästeliste).")
        .Add(new SettingSpec(GuestUpload, "Gäste dürfen Fotos hochladen", SettingKind.Bool) { Group = "Hochladen & Herunterladen", Default = "true", Help = "Aus: Die Galerie ist nur noch zum Ansehen (z. B. nach dem Fest)." })
        .Number(MaxUploadMb, "Größtes Foto beim Hochladen (MB)", 25, 1, 25, "Das Original. Die Ansicht und die Kachel macht das Handy des Gastes vor dem Hochladen selbst (klein und schnell).")
        .Choice(Downloads, "Herunterladen", DownloadOriginal, new[]
        {
            new FieldOption(DownloadOriginal, "Originalfotos (Standortdaten entfernt)"),
            new FieldOption(DownloadView, "nur die große Ansicht (max. 2048 Pixel)"),
            new FieldOption(DownloadOff, "kein Herunterladen")
        }, "Was Gäste über „Herunterladen“ bekommen.");

    public static string Event(IPluginContext ctx) => ctx.Settings.Get(EventName) is { Length: > 0 } n ? n.Trim() : ctx.Site.Name;
    public static string Hosts(IPluginContext ctx) => ctx.Settings.Get(HostName) is { Length: > 0 } n ? n.Trim() : Event(ctx);
    public static string GalleryTitle(IPluginContext ctx) => ctx.Settings.Get(Title) is { Length: > 0 } n ? n.Trim() : "Unsere Momente";
    public static string Menu(IPluginContext ctx) => ctx.Settings.Get(MenuLabel) is { Length: > 0 } n ? n.Trim() : "Fotos";
    public static long MaxBytes(IPluginContext ctx) => (long)Math.Clamp(ctx.Settings.GetNumber(MaxUploadMb, 25), 1, 25) * 1024 * 1024;
    public static string DownloadMode(IPluginContext ctx) => ctx.Settings.Get(Downloads) is DownloadView or DownloadOff ? ctx.Settings.Get(Downloads) : DownloadOriginal;
}

/// <summary>
/// Access codes: eight characters from an alphabet without the ones people mix up (0/O, 1/I/L), printed in two groups of four. 31^8 ≈ 8.5·10^11 —
/// with the failure limit in <see cref="Throttle"/> nobody guesses one.
/// </summary>
public static class Codes
{
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string New()
    {
        var sb = new StringBuilder(8);
        for (var i = 0; i < 8; i++) sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        return sb.ToString();
    }

    /// <summary>What a person typed (lower case, spaces, a dash) as the stored form; "" when it cannot be a code.</summary>
    public static string Normalize(string? typed)
    {
        var s = new string((typed ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return s.Length == 8 && s.All(c => Alphabet.Contains(c)) ? s : "";
    }
}

/// <summary>
/// Wrong codes per address: after <see cref="Limit"/> misses in ten minutes the address is refused until the window is over. In memory (a restart
/// forgets — fine for a guess limit) and bounded, like the host's own POST limiter.
/// </summary>
public static class Throttle
{
    public const int Limit = 12;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Window, int Misses)> Misses = new();

    private static long Window => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 600;

    public static bool Blocked(string ip) => Misses.TryGetValue(ip ?? "", out var e) && e.Window == Window && e.Misses >= Limit;

    public static void Miss(string ip)
    {
        var w = Window;
        if (Misses.Count > 5000) foreach (var kv in Misses.Where(kv => kv.Value.Window != w).ToList()) Misses.TryRemove(kv.Key, out _);
        Misses.AddOrUpdate(ip ?? "", (w, 1), (_, cur) => cur.Window == w ? (w, cur.Misses + 1) : (w, 1));
    }

    /// <summary>Forgets everything (tests).</summary>
    public static void Reset() => Misses.Clear();
}

/// <summary>Small helpers: encoding, German dates (the host runs with invariant globalization).</summary>
public static class M
{
    public static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    public static string U(string? s) => Uri.EscapeDataString(s ?? "");

    public static string Clip(string? s, int max)
    {
        var t = (s ?? "").Replace("\r\n", "\n").Trim();
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>One line: no line breaks, no commas (a tag name is never a list).</summary>
    public static string Line(string? s, int max) => Clip(string.Join(' ', (s ?? "").Replace(",", " ").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim(), max);

    /// <summary>Plain text with its line breaks: nothing a person typed ever becomes markup.</summary>
    public static string Text(string? text) => H(text).Replace("\r\n", "\n").Replace("\n", "<br>");

    public static readonly string[] Months = { "Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" };
    private static readonly string[] Days = { "Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag" };
    private static readonly string[] ShortDays = { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" };

    private static TimeZoneInfo? _tz;
    /// <summary>Germany's time; UTC if the zone is missing on the machine.</summary>
    public static TimeZoneInfo Zone
    {
        get
        {
            if (_tz is not null) return _tz;
            try { _tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); } catch { _tz = TimeZoneInfo.Utc; }
            return _tz;
        }
    }

    public static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
    public static string NowLocal => Local(DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A stored moment (UTC) as "07.11.2026, 20:35".</summary>
    public static string Stamp(DateTime? utc) => utc is DateTime d ? Local(d).ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture) : "";

    public static bool TryTaken(string? s, out DateTime d) =>
        DateTime.TryParseExact((s ?? "").Trim(), new[] { "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    /// <summary>When a photo was taken, as "Fr, 9. Okt. 2026, 16:42".</summary>
    public static string Taken(string? s) => TryTaken(s, out var d)
        ? ShortDays[(int)d.DayOfWeek] + ", " + d.Day + ". " + Months[d.Month - 1] + " " + d.Year + ", " + d.ToString("HH:mm", CultureInfo.InvariantCulture) + " Uhr"
        : "";

    /// <summary>The heading of an hour in the chronological gallery: "Freitag, 9. Oktober · 15 Uhr" (the day only when the photos span several).</summary>
    public static string Hour(DateTime d, bool withDay) =>
        (withDay ? Days[(int)d.DayOfWeek] + ", " + d.Day + ". " + Months[d.Month - 1] + " · " : "") + d.Hour.ToString(CultureInfo.InvariantCulture) + " Uhr";

    /// <summary>"vor 5 Minuten" for a comment.</summary>
    public static string Ago(DateTime? utc)
    {
        if (utc is not DateTime d) return "";
        var span = DateTime.UtcNow - d;
        if (span.TotalMinutes < 1) return "gerade eben";
        if (span.TotalMinutes < 60) return "vor " + (int)span.TotalMinutes + (span.TotalMinutes < 2 ? " Minute" : " Minuten");
        if (span.TotalHours < 24) return "vor " + (int)span.TotalHours + (span.TotalHours < 2 ? " Stunde" : " Stunden");
        if (span.TotalDays < 7) return "vor " + (int)span.TotalDays + (span.TotalDays < 2 ? " Tag" : " Tagen");
        return Stamp(d)[..10];
    }

    public static string Size(long bytes) => bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0 ? (bytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + " MB"
        : bytes >= 1024 * 1024
        ? (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " MB"
        : Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";

    public static string Initials(string name)
    {
        var parts = MomentsStore.SplitPeople(name);
        var letters = (parts.Count > 1 ? parts.Take(2).Select(p => p[0]) : name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => p[0]))
            .Where(char.IsLetter).Select(char.ToUpperInvariant);
        var s = string.Concat(letters);
        return s.Length > 0 ? s : "?";
    }

    public static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    public static string Plural(long n, string one, string many) => N(n) + " " + (n == 1 ? one : many);
}
