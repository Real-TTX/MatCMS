using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MatCMS.Services.Assistant;

/// <summary>
/// Reads the cells of an Excel workbook (.xlsx) into text the assistant reads as a table: one line per row, cells joined by " | ",
/// each sheet under its own heading. An .xlsx is a zip of XML parts (the same as a .docx), so this needs no library — and with that no
/// licence to check (a spreadsheet package would be a third-party dependency under the rules of LICENSE.md).
/// <para>What it reads: shared strings, inline strings, formula RESULTS (the cached value Excel stored), numbers, booleans, and dates
/// where the cell's number format is a date. Formatting, merged cells, pictures and formulas themselves are left out. Bounded: at most
/// <see cref="MaxRows"/> rows and <see cref="MaxColumns"/> columns per sheet, <see cref="MaxSheets"/> sheets — a product list, not a data
/// warehouse. External entities are off (<see cref="XmlReaderSettings.DtdProcessing"/> prohibit): the file is from outside.</para>
/// </summary>
public static class SpreadsheetText
{
    public const int MaxRows = 20000, MaxColumns = 80, MaxSheets = 12;
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The workbook as text. Throws <see cref="InvalidDataException"/> on a file that is not an .xlsx.</summary>
    public static string ReadXlsx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var workbook = Load(zip, "xl/workbook.xml") ?? throw new InvalidDataException("Keine Excel-Arbeitsmappe (.xlsx).");
        var rels = Load(zip, "xl/_rels/workbook.xml.rels");
        var targets = rels?.Root?.Elements(Pkg + "Relationship")
            .ToDictionary(r => (string?)r.Attribute("Id") ?? "", r => (string?)r.Attribute("Target") ?? "") ?? new();
        var shared = SharedStrings(Load(zip, "xl/sharedStrings.xml"));
        var dateStyles = DateStyles(Load(zip, "xl/styles.xml"));
        var date1904 = (string?)workbook.Root?.Element(S + "workbookPr")?.Attribute("date1904") is "1" or "true";

        var sb = new StringBuilder();
        var sheets = workbook.Root?.Element(S + "sheets")?.Elements(S + "sheet").Take(MaxSheets).ToList() ?? new();
        foreach (var sheet in sheets)
        {
            var name = (string?)sheet.Attribute("name") ?? "Tabelle";
            var id = (string?)sheet.Attribute(R + "id") ?? "";
            if (!targets.TryGetValue(id, out var target) || target.Length == 0) continue;
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            var xml = Load(zip, path);
            if (xml is null) continue;
            var rows = Rows(xml, shared, dateStyles, date1904);
            if (rows.Count == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("## Tabelle „").Append(name).Append("“ (").Append(rows.Count).Append(" Zeilen)\n");
            foreach (var (number, cells) in rows)
                sb.Append("Zeile ").Append(number).Append(": ").Append(string.Join(" | ", cells)).Append('\n');
        }
        return sb.ToString();
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        using var s = entry.Open();
        using var reader = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static List<string> SharedStrings(XDocument? doc) =>
        doc?.Root?.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList() ?? new();

    /// <summary>The cell styles (index into cellXfs) whose number format shows a date: the built-in date formats 14–22 and 45–47, and any
    /// custom format with a day/month/year letter outside quotes.</summary>
    private static HashSet<int> DateStyles(XDocument? styles)
    {
        var result = new HashSet<int>();
        if (styles?.Root is not { } root) return result;
        var custom = root.Element(S + "numFmts")?.Elements(S + "numFmt")
            .Where(f => LooksLikeDate((string?)f.Attribute("formatCode") ?? ""))
            .Select(f => (int?)f.Attribute("numFmtId") ?? -1).ToHashSet() ?? new();
        var xfs = root.Element(S + "cellXfs")?.Elements(S + "xf").ToList() ?? new();
        for (var i = 0; i < xfs.Count; i++)
        {
            var fmt = (int?)xfs[i].Attribute("numFmtId") ?? 0;
            if (fmt is >= 14 and <= 22 or >= 45 and <= 47 || custom.Contains(fmt)) result.Add(i);
        }
        return result;
    }

    private static bool LooksLikeDate(string code)
    {
        var plain = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]", "").ToLowerInvariant();
        return plain.Contains('d') || plain.Contains('y') || (plain.Contains('m') && !plain.Contains('h') && !plain.Contains('s'));
    }

    private static List<(int Number, List<string> Cells)> Rows(XDocument sheet, List<string> shared, HashSet<int> dateStyles, bool date1904)
    {
        var rows = new List<(int, List<string>)>();
        foreach (var row in sheet.Descendants(S + "row"))
        {
            if (rows.Count >= MaxRows) break;
            var cells = new List<string>();
            var next = 0;
            foreach (var c in row.Elements(S + "c"))
            {
                var col = Column((string?)c.Attribute("r")) ?? next;
                if (col >= MaxColumns) break;
                while (cells.Count < col) cells.Add("");
                cells.Add(Value(c, shared, dateStyles, date1904).Replace("|", "/").Replace('\n', ' ').Replace('\r', ' ').Trim());
                next = col + 1;
            }
            while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
            if (cells.Count == 0) continue;
            rows.Add(((int?)row.Attribute("r") ?? rows.Count + 1, cells));
        }
        return rows;
    }

    /// <summary>The zero-based column of a cell reference ("C7" → 2), or null.</summary>
    public static int? Column(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var n = 0;
        var i = 0;
        for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++) n = n * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        return i == 0 ? null : n - 1;
    }

    private static string Value(XElement c, List<string> shared, HashSet<int> dateStyles, bool date1904)
    {
        var type = (string?)c.Attribute("t") ?? "";
        var v = c.Element(S + "v")?.Value ?? "";
        switch (type)
        {
            case "s": return int.TryParse(v, out var i) && i >= 0 && i < shared.Count ? shared[i] : "";
            case "inlineStr": return string.Concat(c.Descendants(S + "t").Select(t => t.Value));
            case "b": return v == "1" ? "WAHR" : "FALSCH";
            case "str" or "e": return v;
        }
        if (v.Length == 0) return "";
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return v;
        var style = (int?)c.Attribute("s") ?? -1;
        if (style >= 0 && dateStyles.Contains(style) && number is > 0 and < 2958466)
        {
            var day = (date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30)).AddDays(number);
            return day.TimeOfDay == TimeSpan.Zero ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : day.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        // Invariant, short: "12.9" not "12,899999999999999" — what a price column holds once Excel's binary rounding is taken off.
        return Math.Round(number, 10).ToString("0.##########", CultureInfo.InvariantCulture);
    }
}
