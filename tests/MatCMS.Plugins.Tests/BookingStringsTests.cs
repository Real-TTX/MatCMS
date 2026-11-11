using System.Text.RegularExpressions;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// Every text key the booking plugin's code uses has its German and English text (BookingStrings*.cs) — a missing one would show "booking.xyz" on a
/// live page — and no key is in the tables twice (the plugin would not load: its string tables are built with ToDictionary).
/// </summary>
public class BookingStringsTests
{
    private static string Dir()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MatCMS.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "plugins", "booking");
    }

    private static List<string> TableKeys(string dir) =>
        Directory.GetFiles(dir, "BookingStrings*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\(""(booking\.[a-zA-Z0-9._]+)"", ""[^""]+"", ""[^""]+""\)").Select(m => m.Groups[1].Value))
            .ToList();

    [Fact]
    public void Every_key_the_code_uses_has_both_languages()
    {
        var dir = Dir();
        var rows = TableKeys(dir).ToHashSet();
        var code = Directory.GetFiles(dir, "*.cs").Where(f => !Path.GetFileName(f).StartsWith("BookingStrings", StringComparison.Ordinal)).ToList();
        // the mail templates' own keys (operator-editable rows in Admin → Mailvorlagen) are names, not texts
        var mailKeys = code.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"const string[^;]*;").Select(m => m.Value))
            .SelectMany(l =>Regex.Matches(l, @"""(booking\.[a-z][a-zA-Z0-9._]*)""").Select(m => m.Groups[1].Value)).ToHashSet();
        var missing = code
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"""(booking\.[a-z][a-zA-Z0-9._]*[a-zA-Z0-9])""").Select(m => m.Groups[1].Value))
            .Where(k => !mailKeys.Contains(k) && !rows.Contains(k))
            .Distinct().ToList();
        Assert.True(missing.Count == 0, "without text: " + string.Join(", ", missing));
        // keys built at run time from a fixed list
        foreach (var k in Enumerable.Range(1, 7).Select(i => "booking.day." + i).Concat(Enumerable.Range(1, 12).Select(i => "booking.month." + i))
                     .Concat(new[] { "request", "offer", "confirmed", "declined", "expired", "cancelled" }.Select(s => "booking.status." + s))
                     .Concat(new[] { "stay", "night", "person", "person_night" }.Select(p => "booking.per." + p)))
            Assert.True(rows.Contains(k), "without text: " + k);
    }

    [Fact]
    public void No_key_is_in_the_tables_twice()
    {
        var twice = TableKeys(Dir()).GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(twice.Count == 0, "twice: " + string.Join(", ", twice));
    }
}
