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
        TwoFactor = "twoFactor";

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
        .Add(new SettingSpec(ClubName, "Name des Vereins") { Group = "Allgemein", Help = "Erscheint im Mitgliederbereich, in den E-Mails und auf dem Briefkopf. Leer = Name der Website." })
        .Text(ReplyTo, "Antwortadresse der E-Mails", "", "Wer auf eine E-Mail des Vereins antwortet, schreibt an diese Adresse (z. B. die des Vorstands).")
        .Text(EventsTitle, "Überschrift der Termine", "Kommende Termine", "Über dem Block „Vereinstermine“ auf der Website, wenn der Block keine eigene hat.")
        .Add(new SettingSpec(Roles, "Rollen", SettingKind.List)
        {
            Group = "Rollen & Felder", Default = DefaultRoles, AddLabel = "Rolle",
            Help = "Ein Mitglied kann mehrere Rollen haben. Wer eine Vorstandsrolle hat, sieht im Mitgliederbereich die Übersichten (wer hat bestätigt, wer kommt) und alle Mitglieder.",
            Columns = new[] { new SettingColumn("name", "Rolle") { Placeholder = "z. B. Kassierer/in" }, new SettingColumn("board", "Vorstand") { Kind = UiRowCell.Check, On = BoardMark, Off = "", Width = "7em" } }
        })
        .Add(new SettingSpec(CustomFields, "Eigene Felder", SettingKind.List)
        {
            AddLabel = "Feld",
            Help = "Zusätzliche Angaben je Mitglied (z. B. Mitgliedsnummer, Instrument, Spielklasse, Häs-Nummer). Ohne Haken sieht ein Feld nur der Vorstand; auf der Website erscheint es nie. Umbenennen beginnt das Feld leer.",
            Columns = new[] { new SettingColumn("name", "Feld") { Placeholder = "z. B. Mitgliedsnummer" }, new SettingColumn("all", "Alle sehen es") { Kind = UiRowCell.Check, On = "alle", Off = "vorstand", Width = "9em" } }
        })
        .Add(new SettingSpec(DirectoryForAll, "Mitgliederliste für alle Mitglieder", SettingKind.Bool) { Group = "Mitgliederbereich", Default = "false", Help = "Aus: Mitglieder sehen nur den Vorstand. An: alle sehen alle Mitglieder (Name, Rolle, Telefon)." })
        .Bool(BirthdayForAll, "Geburtsdatum für alle Mitglieder sichtbar", false, "Aus: nur der Vorstand (und die Person selbst) sieht es.")
        .Bool(AddressForAll, "Adresse für alle Mitglieder sichtbar", false, "Aus: nur der Vorstand (und die Person selbst) sieht sie.")
        .TextArea(LoginIntro, "Text auf der Anmeldeseite", "Hier geht es zum internen Bereich unseres Vereins. Die Zugangsdaten bekommst du vom Vorstand.")
        .Choice(TwoFactor, "Zwei-Faktor-Anmeldung", "", new[] { new FieldOption("", "freiwillig (jedes Mitglied kann sie im Profil einschalten)"), new FieldOption("vorstand", "Pflicht für den Vorstand"), new FieldOption("alle", "Pflicht für alle Mitglieder") },
            "Pflicht: Wer sie noch nicht eingerichtet hat, wird beim Öffnen des Mitgliederbereichs zur Einrichtung geschickt (Code aus einer Authenticator-App, z. B. Google oder Microsoft Authenticator).")
        .Add(new SettingSpec(UploadApproval, "Uploads vor dem Veröffentlichen freigeben", SettingKind.Bool) { Group = "Bilder & Material", Default = "true", Help = "An: Was ein Mitglied hochlädt, sehen erst der Vorstand und die Person selbst; die anderen Mitglieder erst nach der Freigabe." })
        .Number(MaxUploadMb, "Größte Datei beim Hochladen (MB)", 15, 1, 25);

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
