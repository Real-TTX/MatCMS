using System.Text;

namespace MatCMS.Services;

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

    /// <param name="Key">Identity. Never renamed once shipped.</param>
    public sealed record Definition(
        string Key, string Name, string Description,
        string Subject, string Body, IReadOnlyList<Placeholder> Placeholders);

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
            ]),
    ];

    public static Definition? Find(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Replaces <c>{{token}}</c> with the given values.
    /// <para>Unknown tokens are left standing rather than blanked: an operator who mistypes one sees
    /// it in the mail and can fix it, whereas a silently emptied line just looks like missing data.</para>
    /// </summary>
    public static string Render(string text, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text);
        foreach (var (token, value) in values)
            sb.Replace("{{" + token + "}}", value ?? "");
        return sb.ToString();
    }
}
