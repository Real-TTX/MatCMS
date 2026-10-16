using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// Reading and writing CSV the way a shop owner's spreadsheet and bookkeeping expect it. Two formats: German (semicolon, decimal comma — what Excel opens correctly in a German setup) and international
/// (comma, decimal point). A file starts with a byte-order mark so Excel reads the umlauts as UTF-8. Text that a spreadsheet would run as a formula (it starts with =, +, -, @ or a tab) is written with
/// a quote in front, so a product named "=HYPERLINK(…)" or a customer who typed a formula as a name cannot make the export a weapon.
/// </summary>
internal static class ShopCsv
{
    public sealed record Format(char Sep, bool DecimalComma);
    public static readonly Format German = new(';', true);
    public static readonly Format International = new(',', false);
    public static Format Of(string? key) => string.Equals(key, "intl", StringComparison.OrdinalIgnoreCase) ? International : German;

    // ---- writing ---------------------------------------------------------------------------------------

    private static string Quote(string s, Format f) => s.IndexOfAny(new[] { f.Sep, '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>A text cell: no formula, quoted where it needs to be.</summary>
    public static string Text(string? s, Format f)
    {
        var t = s ?? "";
        if (t.Length > 0 && (t[0] is '=' or '+' or '-' or '@' or '\t' or '\r')) t = "'" + t;
        return Quote(t, f);
    }

    /// <summary>An amount in whole units with two decimals ("12,50" or "12.50"); a number, so it is never given the formula quote.</summary>
    public static string Money(long cents, Format f)
    {
        var s = (Math.Abs(cents) / 100).ToString(CultureInfo.InvariantCulture) + (f.DecimalComma ? "," : ".") + (Math.Abs(cents) % 100).ToString("00", CultureInfo.InvariantCulture);
        return cents < 0 ? "-" + s : s;
    }

    public static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);
    public static string Percent(int bp, Format f) => f.DecimalComma ? ShopPercent(bp) : ShopPercent(bp).Replace(',', '.');
    private static string ShopPercent(int bp) => MatCMS.Plugin.Shop.Money.Percent(bp);
    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public static string Yes(bool b) => b ? "1" : "0";

    /// <summary>The file: a byte-order mark and the rows, each ending in CRLF. Cells are already written (<see cref="Text"/>, <see cref="Money"/> …).</summary>
    public static byte[] Document(IEnumerable<IEnumerable<string>> rows, Format f)
    {
        var sb = new StringBuilder();
        foreach (var r in rows) sb.Append(string.Join(f.Sep, r)).Append("\r\n");
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    // ---- reading ---------------------------------------------------------------------------------------

    /// <summary>The cells of a file (quotes, doubled quotes, line breaks inside quotes), blank lines left out. The separator is the one of ; , tab that the first line has most of outside quotes.</summary>
    public static List<string[]> Parse(string? text, out char sep)
    {
        var s = (text ?? "").TrimStart('﻿');
        sep = DetectSeparator(s);
        var rows = new List<string[]>();
        var row = new List<string>(); var cell = new StringBuilder(); var inQuotes = false; var any = false;
        void EndCell() { row.Add(cell.ToString()); cell.Clear(); }
        void EndRow() { EndCell(); if (row.Any(c => c.Trim().Length > 0)) rows.Add(row.ToArray()); row = new List<string>(); any = false; }
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inQuotes)
            {
                if (c == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { cell.Append('"'); i++; } else inQuotes = false; }
                else cell.Append(c);
                continue;
            }
            if (c == '"' && cell.Length == 0) { inQuotes = true; any = true; }
            else if (c == sep) { EndCell(); any = true; }
            else if (c == '\r') { if (i + 1 < s.Length && s[i + 1] == '\n') i++; EndRow(); }
            else if (c == '\n') EndRow();
            else { cell.Append(c); any = true; }
        }
        if (cell.Length > 0 || row.Count > 0 || any) EndRow();
        return rows;
    }

    internal static char DetectSeparator(string s)
    {
        var inQuotes = false; var counts = new Dictionary<char, int> { [';'] = 0, [','] = 0, ['\t'] = 0 };
        foreach (var c in s)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && (c == '\n' || c == '\r')) break;
            else if (!inQuotes && counts.ContainsKey(c)) counts[c]++;
        }
        var best = counts.OrderByDescending(kv => kv.Value).First();
        return best.Value == 0 ? ';' : best.Key;
    }
}
