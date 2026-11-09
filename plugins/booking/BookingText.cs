using System.Globalization;
using System.Net;

namespace MatCMS.Plugin.Booking;

/// <summary>Small helpers shared by every part: encoding, money in whole cents, dates as yyyy-MM-dd.</summary>
public static class B
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
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
    public static string MoneyField(long cents) => cents % 100 == 0 ? Num(cents / 100) : (cents / 100m).ToString("0.00", De);

    /// <summary>Cents for reading: "1.200,50 €".</summary>
    public static string Money(long cents, string currency = "EUR") =>
        (cents / 100m).ToString(cents % 100 == 0 ? "#,##0" : "#,##0.00", De) + " " + Symbol(currency);

    public static string Symbol(string currency) => currency switch { "EUR" or "" => "€", "CHF" => "CHF", "GBP" => "£", "USD" => "$", _ => currency };

    public static DateOnly? Day(string? s) =>
        DateOnly.TryParseExact((s ?? "").Trim().Length >= 10 ? s!.Trim()[..10] : s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Date(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    public static string Date(string s) => Day(s) is { } d ? Date(d) : s;
    public static string Weekday(DateOnly d) => De.DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek);

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
