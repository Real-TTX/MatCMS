using System.Globalization;
using System.Net;
using System.Text;

namespace MatCMS.Plugin.Verein;

/// <summary>Settings of the plugin (keys are stored — never renamed).</summary>
public static class VereinSettings
{
    public const string ClubName = "clubName", Roles = "roles", BoardRoles = "boardRoles", LoginIntro = "loginIntro", ReplyTo = "replyTo",
        MaxUploadMb = "maxUploadMb", DirectoryForAll = "directoryForAll", EventsTitle = "eventsTitle",
        CustomFields = "customFields", BirthdayForAll = "birthdayForAll", AddressForAll = "addressForAll", UploadApproval = "uploadApproval",
        TwoFactor = "twoFactor", AskDefault = "askDefault";

    /// <summary>The roles as stored since 1.8: one row per role, "| vorstand" marks the board. (Up to 1.7 roles and board were two plain lists.)</summary>
    public const string DefaultRoles = "Vorsitzende/r | vorstand\nStellv. Vorsitzende/r | vorstand\nKassierer/in | vorstand\nSchriftführer/in | vorstand\nBeisitzer/in | vorstand\nJugendleiter/in | \nMitglied | ";
    public const string DefaultBoard = "Vorsitzende/r\nStellv. Vorsitzende/r\nKassierer/in\nSchriftführer/in\nBeisitzer/in";
    private const string BoardMark = "vorstand";

    /// <summary>
    /// The settings in groups (each a page of its own under Einstellungen). Lists — roles, own fields, the letterhead's lines — are edited row by row
    /// (<see cref="SettingKind.List"/>) and stored one row per line, as the text areas were: an older value reads unchanged. The board used to be a
    /// second list of role names (<see cref="BoardRoles"/>, no longer offered); it is now a tick per role and the old list is read until roles are saved.
    /// </summary>
    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(ClubName, "verein.set.clubName") { Group = "verein.set.groupGeneral", Help = "verein.set.clubNameHelp" })
        .Text(ReplyTo, "verein.set.replyTo", "", "verein.set.replyToHelp")
        .Text(EventsTitle, "verein.set.eventsTitle", "Kommende Termine", "verein.set.eventsTitleHelp")
        .Bool(AskDefault, "verein.set.askDefault", false, "verein.set.askDefaultHelp")
        .Add(new SettingSpec(Roles, "verein.set.roles", SettingKind.List)
        {
            Group = "verein.set.groupRoles", Default = DefaultRoles, AddLabel = "verein.set.role",
            Help = "verein.set.rolesHelp",
            Columns = new[] { new SettingColumn("name", "verein.set.role") { Placeholder = "verein.set.rolePlaceholder" }, new SettingColumn("board", "verein.set.board") { Kind = UiRowCell.Check, On = BoardMark, Off = "", Width = "7em" } }
        })
        .Add(new SettingSpec(CustomFields, "verein.set.fields", SettingKind.List)
        {
            AddLabel = "verein.set.field",
            Help = "verein.set.fieldsHelp",
            Columns = new[] { new SettingColumn("name", "verein.set.field") { Placeholder = "verein.set.fieldPlaceholder" }, new SettingColumn("all", "verein.set.fieldAll") { Kind = UiRowCell.Check, On = "alle", Off = "vorstand", Width = "9em" } }
        })
        .Add(new SettingSpec(DirectoryForAll, "verein.set.directoryForAll", SettingKind.Bool) { Group = "verein.set.groupArea", Default = "false", Help = "verein.set.directoryForAllHelp" })
        .Bool(BirthdayForAll, "verein.set.birthdayForAll", false, "verein.set.birthdayForAllHelp")
        .Bool(AddressForAll, "verein.set.addressForAll", false, "verein.set.addressForAllHelp")
        .TextArea(LoginIntro, "verein.set.loginIntro", "Hier geht es zum internen Bereich unseres Vereins. Die Zugangsdaten bekommst du vom Vorstand.")
        .Choice(TwoFactor, "verein.set.twoFactor", "", new[] { new FieldOption("", "verein.set.twoFactorOptional"), new FieldOption("vorstand", "verein.set.twoFactorBoard"), new FieldOption("alle", "verein.set.twoFactorAll") },
            "verein.set.twoFactorHelp")
        .Add(new SettingSpec(UploadApproval, "verein.set.uploadApproval", SettingKind.Bool) { Group = "verein.set.groupUploads", Default = "true", Help = "verein.set.uploadApprovalHelp" })
        .Number(MaxUploadMb, "verein.set.maxUpload", 15, 1, 25);

    /// <summary>One of the club's own fields and who sees it in the member area.</summary>
    public sealed record Field(string Label, bool ForAll);

    /// <summary>The declared own fields, in their order. A label is the key its value is stored under – renaming one starts it empty.</summary>
    public static IReadOnlyList<Field> Fields(IPluginContext ctx) =>
        ctx.Settings.Get(CustomFields).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|', 2)).Select(p => new Field(p[0].Trim(), p.Length > 1 && p[1].Trim().ToLowerInvariant() is "alle" or "all" or "öffentlich" or "mitglieder"))
            .Where(f => f.Label.Length > 0 && f.Label.Length <= 60).DistinctBy(f => f.Label.ToLowerInvariant()).Take(30).ToList();

    /// <summary>The roles' rows: name and whether it is a board role. A value without any "|" is the plain list of 1.7 and before — its board comes from
    /// the old second setting.</summary>
    private static IReadOnlyList<(string Name, bool Board)> RoleRows(IPluginContext ctx)
    {
        var raw = ctx.Settings.Get(Roles) is { Length: > 0 } r ? r : DefaultRoles;
        var rows = raw.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('|')).Select(p => (Name: p[0].Trim().Replace(",", " "), Mark: p.Length > 1 ? p[1].Trim() : ""))
            .Where(x => x.Name.Length > 0).DistinctBy(x => x.Name.ToLowerInvariant()).ToList();
        if (raw.Contains('|')) return rows.Select(x => (x.Name, string.Equals(x.Mark, BoardMark, StringComparison.OrdinalIgnoreCase))).ToList();
        var legacy = Lines(ctx.Settings.Get(BoardRoles) is { Length: > 0 } b ? b : DefaultBoard);
        return rows.Select(x => (x.Name, legacy.Contains(x.Name, StringComparer.OrdinalIgnoreCase))).ToList();
    }

    public static IReadOnlyList<string> RoleList(IPluginContext ctx) => RoleRows(ctx).Select(r => r.Name).ToList();
    public static IReadOnlyList<string> BoardList(IPluginContext ctx) => RoleRows(ctx).Where(r => r.Board).Select(r => r.Name).ToList();
    public static string Club(IPluginContext ctx) => ctx.Settings.Get(ClubName) is { Length: > 0 } n ? n.Trim() : ctx.Site.Name;
    /// <summary>Whether this person must sign in with a second factor before entering the member area.</summary>
    public static bool TwoFactorRequired(IPluginContext ctx, bool board) => ctx.Settings.Get(TwoFactor) switch { "alle" => true, "vorstand" => board, _ => false };

    public static long MaxBytes(IPluginContext ctx) => (long)Math.Clamp(ctx.Settings.GetNumber(MaxUploadMb, 15), 1, 25) * 1024 * 1024;

    private static IReadOnlyList<string> Lines(string s) =>
        s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Replace(",", " ")).Distinct().ToList();
}

/// <summary>Small helpers: encoding, German dates (the host runs with invariant globalization), file kinds, template placeholders.</summary>
public static class V
{
    public static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>An e-mail address that may wrap only after the "@" and before a dot — "overflow-wrap:anywhere" cut it into "markus@schalksteinnarr|en.de".</summary>
    public static string Mail(string? s) => H(s).Replace("@", "@<wbr>").Replace(".", "<wbr>.");

    public static string Clip(string? s, int max)
    {
        var t = (s ?? "").Replace("\r\n", "\n").Trim();
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>Plain text as paragraphs: nothing a person typed ever becomes markup.</summary>
    public static string Paragraphs(string? text) =>
        string.Concat((text ?? "").Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => "<p>" + H(p.Trim()).Replace("\n", "<br>") + "</p>"));

    private static readonly string[] Months = { "Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" };
    private static readonly string[] Short = { "Jan", "Feb", "Mär", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez" };
    private static readonly string[] Days = { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" };

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
    public static string Today => Local(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A stored moment (UTC) as "07.11.2026, 20:35".</summary>
    public static string Stamp(DateTime? utc) => utc is DateTime d ? Local(d).ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture) : "";

    /// <summary>Reads "yyyy-MM-dd" or "yyyy-MM-ddTHH:mm" (local time as typed).</summary>
    public static bool TryParse(string? s, out DateTime value, out bool hasTime)
    {
        hasTime = (s ?? "").Length > 10;
        return DateTime.TryParseExact((s ?? "").Trim(), new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    /// <summary>A typed date (and time) as "Sa, 14. Februar 2027, 14:00 Uhr".</summary>
    public static string When(string? s, bool weekday = true)
    {
        if (!TryParse(s, out var d, out var hasTime)) return s ?? "";
        var text = (weekday ? Days[(int)d.DayOfWeek] + ", " : "") + d.Day + ". " + Months[d.Month - 1] + " " + d.Year;
        return hasTime && (d.Hour != 0 || d.Minute != 0) ? text + ", " + d.ToString("HH:mm", CultureInfo.InvariantCulture) + " Uhr" : text;
    }

    /// <summary>A short date "14.02.2027".</summary>
    public static string Date(string? s) => TryParse(s, out var d, out _) ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : s ?? "";

    public static (string Day, string Month, string Weekday) Badge(string? s) =>
        TryParse(s, out var d, out _) ? (d.Day.ToString(CultureInfo.InvariantCulture), Short[d.Month - 1], Days[(int)d.DayOfWeek]) : ("", "", "");

    /// <summary>A value posted from a date or datetime-local field, normalised; "" when it is not one.</summary>
    public static string CleanDate(string? s) => TryParse(s, out var d, out var hasTime)
        ? d.ToString(hasTime ? "yyyy-MM-ddTHH:mm" : "yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

    // ---- the same in the request's language ----------------------------------------------------------------------
    // The overloads above write German and stay for what is German by design (mail placeholders, the operator-editable mail templates).
    // Everything a person sees on a page goes through these: they follow ctx.T, so a date is in the same language as the words around it.

    private static readonly string[] MonthsEn = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
    private static readonly string[] ShortEn = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    private static readonly string[] DaysEn = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    /// <summary>Whether the request's texts are English (asked through the string table, so it always agrees with <c>ctx.T</c>).</summary>
    public static bool En(IPluginContext ctx) => ctx.T("verein.lang") == "en";

    /// <summary>The month names of the request's language (January … / Januar …).</summary>
    public static IReadOnlyList<string> MonthNames(IPluginContext ctx) => En(ctx) ? MonthsEn : Months;

    /// <summary>A typed date (and time): "Sa, 14. Februar 2027, 14:00 Uhr" / "Sat, 14 February 2027, 14:00".</summary>
    public static string When(IPluginContext ctx, string? s, bool weekday = true)
    {
        if (!En(ctx)) return When(s, weekday);
        if (!TryParse(s, out var d, out var hasTime)) return s ?? "";
        var text = (weekday ? DaysEn[(int)d.DayOfWeek] + ", " : "") + d.Day + " " + MonthsEn[d.Month - 1] + " " + d.Year;
        return hasTime && (d.Hour != 0 || d.Minute != 0) ? text + ", " + d.ToString("HH:mm", CultureInfo.InvariantCulture) : text;
    }

    /// <summary>A short date: "14.02.2027" / "14 Feb 2027".</summary>
    public static string Date(IPluginContext ctx, string? s) =>
        !En(ctx) ? Date(s) : TryParse(s, out var d, out _) ? d.Day + " " + ShortEn[d.Month - 1] + " " + d.Year : s ?? "";

    /// <summary>A stored moment (UTC): "07.11.2026, 20:35" / "7 Nov 2026, 20:35".</summary>
    public static string Stamp(IPluginContext ctx, DateTime? utc)
    {
        if (!En(ctx) || utc is not DateTime u) return Stamp(utc);
        var d = Local(u);
        return d.Day + " " + ShortEn[d.Month - 1] + " " + d.Year + ", " + d.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Only the day of a stored moment: "07.11.2026" / "7 Nov 2026" (never cut a <see cref="Stamp(IPluginContext, DateTime?)"/> by length).</summary>
    public static string Day(IPluginContext ctx, DateTime? utc) =>
        utc is DateTime u ? Date(ctx, Local(u).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : "";

    public static (string Day, string Month, string Weekday) Badge(IPluginContext ctx, string? s) =>
        !En(ctx) ? Badge(s) : TryParse(s, out var d, out _) ? (d.Day.ToString(CultureInfo.InvariantCulture), ShortEn[d.Month - 1], DaysEn[(int)d.DayOfWeek]) : ("", "", "");

    public static string Size(IPluginContext ctx, long bytes) => En(ctx) ? Size(bytes).Replace(',', '.') : Size(bytes);

    public static string Size(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " MB"
        : Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";

    // ---- files ------------------------------------------------------------------------------------------------------

    /// <summary>What may be uploaded, by extension, with the type the file is served as. Never HTML, SVG or scripts: a file is shown in the browser.</summary>
    public static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp", [".gif"] = "image/gif",
        [".pdf"] = "application/pdf", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [".xls"] = "application/vnd.ms-excel",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation", [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet", [".txt"] = "text/plain; charset=utf-8", [".csv"] = "text/csv; charset=utf-8",
        [".zip"] = "application/zip", [".mp3"] = "audio/mpeg", [".mp4"] = "video/mp4"
    };

    public static bool IsImage(string ext) => ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif";

    /// <summary>The extension of an uploaded file's name, lower case, or "" when it is not allowed.</summary>
    public static string AllowedExt(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return Types.ContainsKey(ext) ? ext : "";
    }

    /// <summary>Checks the first bytes of an image against what its name claims (a renamed script is not a picture).</summary>
    public static bool LooksLike(string ext, byte[] b) => ext switch
    {
        ".jpg" or ".jpeg" => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        ".png" => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47,
        ".gif" => b.Length > 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46,
        ".webp" => b.Length > 12 && b[0] == 0x52 && b[1] == 0x49 && b[8] == 0x57 && b[9] == 0x45,
        ".pdf" => b.Length > 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46,
        _ => true
    };

    /// <summary>A file name safe to put into a Content-Disposition header.</summary>
    public static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in Path.GetFileName(name ?? "datei"))
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or ' ' ? ch : '_');
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "datei" : s.Length > 100 ? s[^100..] : s;
    }

    // ---- templates ----------------------------------------------------------------------------------------------

    /// <summary>Fills the placeholders a template may use: {{verein}}, {{datum}}, {{jahr}}, and for a meeting {{titel}}, {{termin}}, {{ort}}, {{tagesordnung}}.</summary>
    public static string Fill(string text, IPluginContext ctx, IReadOnlyDictionary<string, string>? extra = null)
    {
        var now = Local(DateTime.UtcNow);
        var values = new Dictionary<string, string>
        {
            ["verein"] = VereinSettings.Club(ctx), ["datum"] = now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), ["jahr"] = now.Year.ToString(CultureInfo.InvariantCulture)
        };
        if (extra is not null) foreach (var (k, v) in extra) values[k] = v;
        var s = text ?? "";
        foreach (var (k, v) in values) s = s.Replace("{{" + k + "}}", v);
        return s;
    }

    public static bool Checked(string v) => v is "true" or "on" or "1";
}
