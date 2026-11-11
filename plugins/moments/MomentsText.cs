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

    // Labels, help and option texts are keys of the plugin's string tables (MomentsStrings); the host looks them up in the admin's language.
    // The defaults of the text settings stay German: they are the operator's own content once saved, not texts of the plugin.
    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(EventName, "moments.set.eventName") { Group = "moments.set.groupGeneral", Help = "moments.set.eventNameHelp" })
        .Text(Title, "moments.set.title", "Unsere Momente")
        .Text(HostName, "moments.set.hostName", "", "moments.set.hostNameHelp")
        .TextArea(Welcome, "moments.set.welcome", "Schön, dass ihr da seid! Hier findet ihr die Fotos von unserem Tag – und hier könnt ihr eure eigenen Bilder hochladen.")
        .TextArea(LoginIntro, "moments.set.loginIntro", "Gebt den persönlichen Zugang ein, den ihr von uns bekommen habt.", "moments.set.loginIntroHelp")
        .Text(MenuLabel, "moments.set.menuLabel", "Fotos", "moments.set.menuLabelHelp")
        .Add(new SettingSpec(LoginMode, "moments.set.loginMode", SettingKind.Choice)
        {
            Group = "moments.set.groupLogin", Default = ModeCode,
            Options = new[]
            {
                new FieldOption(ModeCode, "moments.set.modeCode"),
                new FieldOption(ModePassword, "moments.set.modePassword"),
                new FieldOption(ModeBoth, "moments.set.modeBoth")
            },
            Help = "moments.set.loginModeHelp"
        })
        .Choice(CodeChars, "moments.set.codeChars", Codes.Mixed, new[]
        {
            new FieldOption(Codes.Mixed, "moments.set.charsMixed"),
            new FieldOption(Codes.Digits, "moments.set.charsDigits"),
            new FieldOption(Codes.Letters, "moments.set.charsLetters")
        }, "moments.set.codeCharsHelp")
        // the help names the failure limit (Throttle.Limit = 12) in its text
        .Number(CodeLength, "moments.set.codeLength", 8, Codes.MinLength, 12, "moments.set.codeLengthHelp")
        .Add(new SettingSpec(UploadApproval, "moments.set.uploadApproval", SettingKind.Bool)
        {
            Group = "moments.set.groupModeration", Default = "true", Help = "moments.set.uploadApprovalHelp"
        })
        .Bool(CommentApproval, "moments.set.commentApproval", false, "moments.set.commentApprovalHelp")
        .Bool(GuestTags, "moments.set.guestTags", true, "moments.set.guestTagsHelp")
        .Bool(NewTags, "moments.set.newTags", true, "moments.set.newTagsHelp")
        .Bool(ShowTable, "moments.set.showTable", false, "moments.set.showTableHelp")
        .Add(new SettingSpec(GuestUpload, "moments.set.guestUpload", SettingKind.Bool) { Group = "moments.set.groupFiles", Default = "true", Help = "moments.set.guestUploadHelp" })
        .Number(MaxUploadMb, "moments.set.maxUpload", 25, 1, 25, "moments.set.maxUploadHelp")
        .Choice(Downloads, "moments.set.downloads", DownloadOriginal, new[]
        {
            new FieldOption(DownloadOriginal, "moments.set.dlOriginal"),
            new FieldOption(DownloadView, "moments.set.dlView"),
            new FieldOption(DownloadOff, "moments.set.dlOff")
        }, "moments.set.downloadsHelp");

    public static string Event(IPluginContext ctx) => ctx.Settings.Get(EventName) is { Length: > 0 } n ? n.Trim() : ctx.Site.Name;
    public static string Hosts(IPluginContext ctx) => ctx.Settings.Get(HostName) is { Length: > 0 } n ? n.Trim() : Event(ctx);
    public static string GalleryTitle(IPluginContext ctx) => ctx.Settings.Get(Title) is { Length: > 0 } n ? n.Trim() : ctx.T("moments.galleryTitle");
    public static string Menu(IPluginContext ctx) => ctx.Settings.Get(MenuLabel) is { Length: > 0 } n ? n.Trim() : ctx.T("moments.photos");
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
    public static string? Problem(IPluginContext ctx, string? typed)
    {
        var raw = (typed ?? "").Trim();
        if (raw.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-'))) return ctx.T("moments.code.onlyLetters");
        var s = Normalize(raw);
        if (s.Length < MinLength) return ctx.T("moments.code.tooShort", M.N(MinLength));
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

/// <summary>Small helpers: encoding, dates in the request's language (the host runs with invariant globalization).</summary>
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

    // Month and day names come from the string tables (moments.month.1–12, moments.day.0–6, moments.dayShort.0–6): the host runs with invariant
    // globalization, so a culture's own names are not available, and the language is the request's.
    private static string Month(IPluginContext ctx, int month) => ctx.T("moments.month." + M.N(month));
    private static string Day(IPluginContext ctx, DayOfWeek d) => ctx.T("moments.day." + M.N((int)d));
    private static string ShortDay(IPluginContext ctx, DayOfWeek d) => ctx.T("moments.dayShort." + M.N((int)d));

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

    /// <summary>When a photo was taken, as "Fr, 9. Oktober 2026, 16:42 Uhr" ("Fri, October 9, 2026, 16:42").</summary>
    public static string Taken(IPluginContext ctx, string? s) => TryTaken(s, out var d)
        ? ctx.T("moments.date.taken", ShortDay(ctx, d.DayOfWeek), N(d.Day), Month(ctx, d.Month), N(d.Year), d.ToString("HH:mm", CultureInfo.InvariantCulture))
        : "";

    /// <summary>The heading of an hour in the chronological gallery: "Freitag, 9. Oktober · 15 Uhr" (the day only when the photos span several).</summary>
    public static string Hour(IPluginContext ctx, DateTime d, bool withDay) => withDay
        ? ctx.T("moments.date.dayHour", Day(ctx, d.DayOfWeek), N(d.Day), Month(ctx, d.Month), N(d.Hour))
        : ctx.T("moments.date.hour", N(d.Hour));

    /// <summary>"vor 5 Minuten" for a comment.</summary>
    public static string Ago(IPluginContext ctx, DateTime? utc)
    {
        if (utc is not DateTime d) return "";
        var span = DateTime.UtcNow - d;
        if (span.TotalMinutes < 1) return ctx.T("moments.ago.now");
        if (span.TotalMinutes < 60) return ctx.T(span.TotalMinutes < 2 ? "moments.ago.minute" : "moments.ago.minutes", N((int)span.TotalMinutes));
        if (span.TotalHours < 24) return ctx.T(span.TotalHours < 2 ? "moments.ago.hour" : "moments.ago.hours", N((int)span.TotalHours));
        if (span.TotalDays < 7) return ctx.T(span.TotalDays < 2 ? "moments.ago.day" : "moments.ago.days", N((int)span.TotalDays));
        return Stamp(d)[..10];
    }

    /// <summary>A file size, "2,5 MB" (the decimal mark is the language's: moments.decimalMark).</summary>
    public static string Size(IPluginContext ctx, long bytes) => bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0 ? (bytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + " MB"
        : bytes >= 1024 * 1024
        ? (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture).Replace(".", ctx.T("moments.decimalMark")) + " MB"
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

    /// <summary>"1 Foto" / "3 Fotos": the two keys <paramref name="prefix"/> + "1" and + "n" of the string tables, with the number as {0}.</summary>
    public static string Count(IPluginContext ctx, long n, string prefix) => ctx.T(prefix + (n == 1 ? "1" : "n"), N(n));
}
