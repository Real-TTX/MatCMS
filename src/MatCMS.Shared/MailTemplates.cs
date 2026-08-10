using System.Text;
using System.Text.RegularExpressions;

namespace MatCMS.Shared;

/// <summary>
/// The mails MatCMS knows how to send, and what may be put into each of them.
/// <para>This is the single list the code, the seeder and the editor all read. A mail that is not
/// declared here has no placeholder documentation and no default text, so it cannot be sent by key —
/// which is the point: the editor must be able to tell an operator what they may write, and the only
/// honest source for that is the code that fills it in.</para>
/// </summary>
public static class MailTemplates
{
    /// <summary>A placeholder the editor offers and the sender fills.</summary>
    /// <param name="Token">Written as <c>{{token}}</c> in subject and body.</param>
    /// <param name="Description">What it will contain, shown next to the insert button.</param>
    public sealed record Placeholder(string Token, string Description);

    /// <summary>
    /// A repeatable block: <c>{{#name}} … {{/name}}</c> is written once and rendered once per item.
    /// <para>Same syntax as the template engine's menu loops, on purpose — an operator who has laid
    /// out a navigation already knows this one.</para>
    /// </summary>
    /// <param name="Name">Loop name, used as <c>{{#name}}</c>.</param>
    /// <param name="Description">What one pass represents.</param>
    /// <param name="Fields">The placeholders available INSIDE the block.</param>
    public sealed record Loop(string Name, string Description, IReadOnlyList<Placeholder> Fields);

    /// <param name="Key">Identity. Never renamed once shipped.</param>
    public sealed record Definition(
        string Key, string Name, string Description,
        string Subject, string Body, IReadOnlyList<Placeholder> Placeholders,
        IReadOnlyList<Loop> Loops, string HtmlBody);

    public const string FormSubmission = "form.submission";

    /// <summary>
    /// Every mail the product can send. Today that is one; the shape exists so the next one is a
    /// list entry rather than another hard-coded string in a page handler.
    /// </summary>
    public static readonly IReadOnlyList<Definition> All =
    [
        new Definition(
            FormSubmission,
            "Formular-Benachrichtigung",
            "Geht an die im Formular hinterlegten Empfänger, sobald jemand es absendet.",
            "Neue Einsendung: {{form_name}}",
            """
            Neue Einsendung über das Formular „{{form_name}}“:
            --------------------------------------------
            {{fields}}

            Gesendet am {{date}} über {{site_name}}.
            """,
            [
                new Placeholder("form_name", "Name des Formulars"),
                new Placeholder("fields", "Alle ausgefüllten Felder, eines je Zeile"),
                new Placeholder("site_name", "Name der Website"),
                new Placeholder("date", "Zeitpunkt der Einsendung"),
            ],
            [
                new Loop("fields", "Einmal je ausgefülltem Feld",
                [
                    new Placeholder("label", "Beschriftung des Feldes"),
                    new Placeholder("value", "Was der Besucher eingetragen hat"),
                ]),
            ],
            // The starting point when somebody switches a template to HTML. A table rather than divs
            // because that is what mail clients still lay out reliably, and inline styles because
            // most of them drop a <style> block.
            """
            <div style="font-family:Arial,Helvetica,sans-serif;font-size:15px;color:#1a1a1a;">
              <p>Neue Einsendung über das Formular <strong>{{form_name}}</strong>:</p>
              <table cellpadding="6" cellspacing="0" border="0" style="border-collapse:collapse;">
                {{#fields}}
                <tr>
                  <td style="border-bottom:1px solid #e5e5e5;color:#666;vertical-align:top;">{{label}}</td>
                  <td style="border-bottom:1px solid #e5e5e5;"><strong>{{value}}</strong></td>
                </tr>
                {{/fields}}
              </table>
              <p style="color:#888;font-size:13px;">Gesendet am {{date}} über {{site_name}}.</p>
            </div>
            """),
    ];

    public static Definition? Find(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>One pass of a loop: the placeholders available inside the block, filled.</summary>
    public sealed class Item : Dictionary<string, string>
    {
        public Item() : base(StringComparer.OrdinalIgnoreCase) { }
    }

    private static readonly Regex LoopPattern = new(
        @"\{\{#(?<name>[a-zA-Z0-9_]+)\}\}(?<body>.*?)\{\{/\k<name>\}\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Fills a template: repeatable blocks first, then the plain placeholders.
    /// <para>Loops before scalars, because a block's contents are written by the loop and only then
    /// have their own <c>{{label}}</c>-style tokens replaced — the other way round, a scalar named
    /// like a field would be substituted into the block before it was ever repeated.</para>
    /// <para>Unknown tokens are left standing rather than blanked: an operator who mistypes one sees
    /// it in the mail and can fix it, whereas a silently emptied line just looks like missing data.</para>
    /// </summary>
    /// <param name="html">Escapes every value that is put in. Form values come from the public, so in
    /// an HTML mail an unescaped one would let a visitor put markup into somebody's inbox.</param>
    public static string Render(
        string text,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyList<Item>>? lists = null,
        bool html = false)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var body = LoopPattern.Replace(text, m =>
        {
            var name = m.Groups["name"].Value;
            // A block whose loop nothing supplies is left exactly as written. Removing it would be
            // the other option, and it would hide a typo in the loop name instead of showing it.
            if (lists is null || !lists.TryGetValue(name, out var items)) return m.Value;

            var inner = m.Groups["body"].Value;
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                var pass = inner;
                foreach (var (k, v) in item)
                    pass = pass.Replace("{{" + k + "}}", Encode(v, html));
                sb.Append(pass);
            }
            return sb.ToString();
        });

        var result = new StringBuilder(body);
        foreach (var (token, value) in values)
            result.Replace("{{" + token + "}}", Encode(value, html));
        return result.ToString();
    }

    private static string Encode(string? value, bool html) =>
        html ? System.Net.WebUtility.HtmlEncode(value ?? "") : value ?? "";

    /// <summary>
    /// A plain-text version of an HTML body, for the alternative part of the message.
    /// <para>Sent alongside rather than instead: a mail with only an HTML part is treated worse by
    /// spam filters and unreadable in a client that shows text only. Derived rather than asked for,
    /// because nobody wants to write every notification twice.</para>
    /// </summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var s = Regex.Replace(html, @"<(script|style)\b.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Block-level ends become line breaks, or the whole mail arrives as one paragraph.
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|tr|h[1-6]|li)>", "\n", RegexOptions.IgnoreCase);
        // A table row's cells are one line with its parts separated, which is what the text version
        // of a two-column "label / value" table should look like.
        s = Regex.Replace(s, @"</t[dh]>", "  ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);

        var lines = s.Split('\n').Select(l => l.Trim()).ToList();
        // Collapse runs of blank lines that the tag stripping leaves behind.
        var outp = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && (outp.Count == 0 || outp[^1].Length == 0)) continue;
            outp.Add(line);
        }
        return string.Join("\n", outp).Trim();
    }
}
