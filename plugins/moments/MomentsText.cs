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
        MaxUploadMb = "maxUploadMb", Downloads = "downloads", ShowTable = "showTable", LoginMode = "loginMode", CodeChars = "codeChars", CodeLength = "codeLength";

    public const string DownloadOriginal = "original", DownloadView = "view", DownloadOff = "off";
    public const string ModeCode = "code", ModePassword = "password", ModeBoth = "both";

    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(EventName, "Name des Anlasses") { Group = "Allgemein", Help = "Z. B. „Hochzeit von Grit & Steffen“. Erscheint über der Galerie und auf den Zugangskarten. Leer = Name der Website." })
        .Text(Title, "Überschrift der Galerie", "Unsere Momente")
        .Text(HostName, "Name der Gastgeber", "", "So erscheinen Fotos und Kommentare, die ihr im Admin hochladet oder schreibt (z. B. „Grit & Steffen“). Leer = Name des Anlasses.")
        .TextArea(Welcome, "Begrüßung in der Galerie", "Schön, dass ihr da seid! Hier findet ihr die Fotos von unserem Tag – und hier könnt ihr eure eigenen Bilder hochladen.")
        .TextArea(LoginIntro, "Text auf der Anmeldeseite", "Gebt den persönlichen Zugang ein, den ihr von uns bekommen habt.",
            "Steht über dem Anmeldefeld. Wie der QR-Code auf der Karte funktioniert, erklärt die Seite selbst darunter.")
        .Text(MenuLabel, "Menüpunkt für angemeldete Gäste", "Fotos", "Erscheint im Hauptmenü der Website, sobald ein Gast angemeldet ist (mit Alle Fotos, Hochladen und Meine Fotos darunter; „Abmelden“ steht ganz rechts daneben).")
        .Add(new SettingSpec(LoginMode, "Anmeldung mit", SettingKind.Choice)
        {
            Group = "Anmeldung", Default = ModeCode,
            Options = new[]
            {
                new FieldOption(ModeCode, "persönlichem Code (mit Link und QR-Code)"),
                new FieldOption(ModePassword, "Benutzername und Passwort"),
                new FieldOption(ModeBoth, "beidem – Code oder Benutzername und Passwort")
            },
            Help = "Code: Jeder Haushalt bekommt einen Code, die Karte trägt ihn mit QR-Code (scannen = angemeldet). Benutzername und Passwort vergebt ihr je Haushalt unter Gäste; " +
                   "das Passwort wird nur verschlüsselt gespeichert – ihr könnt es neu setzen, aber nicht mehr ablesen."
        })
        .Choice(CodeChars, "Neue Codes bestehen aus", Codes.Mixed, new[]
        {
            new FieldOption(Codes.Mixed, "Buchstaben und Ziffern (z. B. K7XM-4QPA)"),
            new FieldOption(Codes.Digits, "nur Ziffern (z. B. 4829-1736)"),
            new FieldOption(Codes.Letters, "nur Buchstaben (z. B. KXMQ-PARW)")
        }, "Mit Buchstaben ohne die Zeichen, die man verwechselt (0/O, 1/I/L). Gilt für neu erzeugte Codes – vorhandene bleiben gültig. Einen eigenen Code könnt ihr je Haushalt unter Gäste eintippen.")
        .Number(CodeLength, "Länge neuer Codes", 8, Codes.MinLength, 12,
            "6 bis 12 Zeichen. Kürzer ist leichter zu tippen, aber auch leichter zu erraten – nach " + Throttle.Limit + " Fehlversuchen ist eine Adresse 10 Minuten gesperrt.")
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

    /// <summary>How households sign in: <see cref="ModeCode"/> (the default, and what every site had before 1.1), <see cref="ModePassword"/> or <see cref="ModeBoth"/>.</summary>
    public static string Mode(IPluginContext ctx) => ctx.Settings.Get(LoginMode) is ModePassword or ModeBoth ? ctx.Settings.Get(LoginMode) : ModeCode;
    public static bool CodeLogin(IPluginContext ctx) => Mode(ctx) != ModePassword;
    public static bool PasswordLogin(IPluginContext ctx) => Mode(ctx) != ModeCode;

    /// <summary>The shape of newly made codes.</summary>
    public static (int Length, string Chars) CodeFormat(IPluginContext ctx) =>
        ((int)Math.Clamp(ctx.Settings.GetNumber(CodeLength, 8), Codes.MinLength, 12), ctx.Settings.Get(CodeChars) is Codes.Digits or Codes.Letters ? ctx.Settings.Get(CodeChars) : Codes.Mixed);
}

/// <summary>
/// Access codes. A made one comes from an alphabet without the characters people mix up (0/O, 1/I/L): eight of them by default (31^8 ≈ 8.5·10^11),
/// shorter or digits only if the hosts want it easier to type. A household may also get a code the hosts typed themselves ("START12345"). Stored without
/// spaces and dashes in upper case; what a guest types is brought into that form, so "k7xm 4qpa" finds "K7XM4QPA". With the failure limit in
/// <see cref="Throttle"/> nobody guesses a long one.
/// </summary>
public static class Codes
{
    public const string Mixed = "mixed", Digits = "digits", Letters = "letters";
    public const int MinLength = 6, MaxLength = 32;

    private const string MixedAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ", LetterAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string New(int length = 8, string chars = Mixed)
    {
        // digits only: 0 and 1 are fine without letters around (nothing to mix them up with)
        var alphabet = chars switch { Digits => "0123456789", Letters => LetterAlphabet, _ => MixedAlphabet };
        var n = Math.Clamp(length, MinLength, 12);
        var sb = new StringBuilder(n);
        for (var i = 0; i < n; i++) sb.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
        return sb.ToString();
    }

    /// <summary>What a person typed (lower case, spaces, dashes) as the stored form; "" when it cannot be a code.</summary>
    public static string Normalize(string? typed)
    {
        var s = new string((typed ?? "").ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray());
        return s.Length is >= 4 and <= MaxLength ? s : "";
    }

    /// <summary>Why a code the hosts typed cannot be used (null = fine): only letters A–Z and digits, at least <see cref="MinLength"/>.</summary>
    public static string? Problem(string? typed)
    {
        var raw = (typed ?? "").Trim();
        if (raw.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-'))) return "Ein Code besteht nur aus Buchstaben A–Z und Ziffern (Leerzeichen und Bindestriche werden ignoriert).";
        var s = Normalize(raw);
        if (s.Length < MinLength) return "Ein Code braucht mindestens " + MinLength + " Zeichen.";
        return null;
    }

    /// <summary>The code as printed: groups of four ("K7XM-4QPA", "K7XM-4QPA-Z2RT") or of three ("482-913"), a length that fits neither as it is.</summary>
    public static string Shown(string code)
    {
        var size = code.Length % 4 == 0 && code.Length > 4 ? 4 : code.Length % 3 == 0 && code.Length > 3 ? 3 : 0;
        if (size == 0) return code;
        return string.Join("-", Enumerable.Range(0, code.Length / size).Select(i => code.Substring(i * size, size)));
    }
}

/// <summary>
/// Passwords the hosts give a household (Benutzername + Passwort). Stored as PBKDF2-SHA256 with a salt of its own — the plugin can check one, never
/// show one; a forgotten password is set anew in the back office.
/// </summary>
public static class Passwords
{
    public const int MinLength = 6, MaxLength = 128;
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return "v1$" + Iterations.ToString(CultureInfo.InvariantCulture) + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
    }

    public static bool Verify(string password, string stored)
    {
        var parts = (stored ?? "").Split('$');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var it) || it is < 1000 or > 10_000_000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var want = Convert.FromBase64String(parts[3]);
            var got = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, it, HashAlgorithmName.SHA256, want.Length);
            return CryptographicOperations.FixedTimeEquals(got, want);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>The same work as a check, for a name nobody has — so the answer's timing does not tell which names exist.</summary>
    public static void Waste(string password) => Rfc2898DeriveBytes.Pbkdf2(password ?? "", new byte[16], Iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>A user name: letters, digits, space, dot, dash, underscore, @; 3–40 characters. "" when empty, null when it does not fit.</summary>
    public static string? CleanLogin(string? typed)
    {
        var s = string.Join(' ', (typed ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (s.Length == 0) return "";
        return s.Length is >= 3 and <= 40 && s.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_' or '@') ? s : null;
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
