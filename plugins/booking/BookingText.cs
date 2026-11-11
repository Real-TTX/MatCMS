using System.Globalization;
using System.Net;

namespace MatCMS.Plugin.Booking;

/// <summary>Small helpers shared by every part: encoding, money in whole cents, dates as yyyy-MM-dd.</summary>
public static class B
{
    // NO culture data: the image runs with invariant globalization, where "de-DE" silently formats like English ("523.50", "January").
    // Every German number, month and weekday is written here by hand.
    public static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    public static string Clip(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }
    public static bool Checked(string v) => v is "true" or "on" or "1";
    public static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A typed amount → whole cents. German and English spellings both: "120", "120,50", "1.200,50", "1,200.50", "120 €". Null = not an amount.
    /// The LAST separator followed by one or two digits is the decimal one; every other "." "," or space is a thousands separator.
    /// </summary>
    public static long? ParseMoney(string? s)
    {
        s = new string((s ?? "").Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (s.Length == 0) return null;
        var neg = s.StartsWith('-');
        s = s.Replace("-", "");
        var cut = s.LastIndexOfAny(new[] { '.', ',' });
        string whole = s, frac = "";
        if (cut >= 0 && s.Length - cut - 1 is 1 or 2) { whole = s[..cut]; frac = s[(cut + 1)..]; }
        whole = whole.Replace(".", "").Replace(",", "");
        if (whole.Length == 0) whole = "0";
        if (whole.Length > 12 || !long.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var w)) return null;
        var cents = w * 100 + (frac.Length == 0 ? 0 : int.Parse(frac.PadRight(2, '0'), CultureInfo.InvariantCulture));
        return neg ? -cents : cents;
    }

    /// <summary>Cents for an input field: "120" or "120,50" (no thousands separator — it is read back).</summary>
    public static string MoneyField(long cents) => cents % 100 == 0 ? Num(cents / 100) : Num(cents / 100) + "," + Math.Abs(cents % 100).ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Cents for reading: "1.200,50 €".</summary>
    public static string Money(long cents, string currency = "EUR") =>
        Grouped(cents) + " " + Symbol(currency);

    /// <summary>
    /// Cents for reading in the language of <paramref name="t"/>: German "1.200,50 €" (exactly <see cref="Money(long, string)"/>), English "€1,200.50"
    /// (a one-character symbol in front; a code with a space: "CHF 1,200.50"). The language comes from the string table itself (<c>booking.fmt.lang</c>),
    /// so the amount is always written the way the words around it are.
    /// </summary>
    public static string Money(Tr t, long cents, string currency = "EUR")
    {
        if (!English(t)) return Money(cents, currency);
        var symbol = Symbol(currency);
        var amount = Grouped(Math.Abs(cents), ',', '.');
        return (cents < 0 ? "−" : "") + (symbol.Length == 1 ? symbol + amount : symbol + " " + amount);
    }

    /// <summary>Whether <paramref name="t"/> speaks English — anything else is written the German way, as it always was.</summary>
    public static bool English(Tr t) => t("booking.fmt.lang") == "en";

    /// <summary>"1.200" or "1.200,50" — German grouping and decimal comma, without culture data.</summary>
    private static string Grouped(long cents) => Grouped(cents, '.', ',');

    private static string Grouped(long cents, char thousands, char decimals)
    {
        var whole = Math.Abs(cents) / 100;
        var digits = whole.ToString(CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) sb.Append(thousands);
            sb.Append(digits[i]);
        }
        if (cents % 100 != 0) sb.Append(decimals).Append(Math.Abs(cents % 100).ToString("00", CultureInfo.InvariantCulture));
        return (cents < 0 ? "−" : "") + sb;
    }

    public static string Symbol(string currency) => currency switch { "EUR" or "" => "€", "CHF" => "CHF", "GBP" => "£", "USD" => "$", _ => currency };

    public static DateOnly? Day(string? s) =>
        DateOnly.TryParseExact((s ?? "").Trim().Length >= 10 ? s!.Trim()[..10] : s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Today where the houses are (Central European time) — in UTC a late evening is already "tomorrow" or still "yesterday".</summary>
    public static DateOnly Today
    {
        get
        {
            try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"))); }
            catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(DateTime.UtcNow); }
        }
    }

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Date(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    public static string Date(string s) => Day(s) is { } d ? Date(d) : s;
    /// <summary>A day for reading in the language of <paramref name="t"/>: German "12.01.2027", English "12/01/2027" (day first, as in Britain).</summary>
    public static string Date(Tr t, DateOnly d) => English(t) ? d.ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture) : Date(d);
    public static string Date(Tr t, string s) => Day(s) is { } d ? Date(t, d) : s;
    public static string Weekday(DateOnly d) => DayNames[IsoDay(d.DayOfWeek) - 1];
    public static readonly string[] MonthNames = { "Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" };
    public static string Month(DateOnly d) => MonthNames[d.Month - 1] + " " + d.Year.ToString(CultureInfo.InvariantCulture);

    // the same in the language of a request
    public static string Weekday(Tr t, DateOnly d) => DayName(t, IsoDay(d.DayOfWeek));
    public static string DayName(Tr t, int iso) => t("booking.day." + iso.ToString(CultureInfo.InvariantCulture));
    public static string MonthName(Tr t, int month) => t("booking.month." + month.ToString(CultureInfo.InvariantCulture));
    public static string Month(Tr t, DateOnly d) => MonthName(t, d.Month) + " " + d.Year.ToString(CultureInfo.InvariantCulture);

    /// <summary>A slug: lower case, a–z, 0–9 and dashes; umlauts spelled out.</summary>
    public static string Slug(string? s)
    {
        var map = new Dictionary<char, string> { ['ä'] = "ae", ['ö'] = "oe", ['ü'] = "ue", ['ß'] = "ss" };
        var sb = new System.Text.StringBuilder();
        foreach (var ch in (s ?? "").Trim().ToLowerInvariant())
            if (map.TryGetValue(ch, out var r)) sb.Append(r);
            else if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        var slug = sb.ToString().Trim('-');
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }

    /// <summary>ISO weekday numbers (1 = Monday … 7 = Sunday) as stored in "allowed arrival days".</summary>
    public static int IsoDay(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;
    public static readonly string[] DayNames = { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" };
}
