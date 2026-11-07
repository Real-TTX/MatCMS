namespace MatCMS.Plugin.Verein;

/// <summary>
/// The club's mails. Their wording lives in Admin → Mailvorlagen once the plugin has loaded (the texts here are only the starting point). A mail that cannot
/// go out never stops what triggered it; the failure is written to the plugin's log and counted in the notice the back office shows.
/// </summary>
public static class VereinMail
{
    // "verein.welcome", not the first version's "verein.invite": that one sent people the long way round (forgot password, then sign in); a stored
    // template is never overwritten, so the new one-link wording needs a key of its own.
    public const string Invite = "verein.welcome", Document = "verein.document", Reminder = "verein.reminder", Meeting = "verein.meeting", Minutes = "verein.minutes";

    private static MailPlaceholder Ph(string t, string d) => new(t, d);
    private static readonly MailPlaceholder[] Common = { Ph("name", "Vorname des Mitglieds"), Ph("verein", "Name des Vereins"), Ph("login_url", "Adresse des Mitgliederbereichs") };

    public static IEnumerable<MailTemplateSpec> Specs => new[]
    {
        new MailTemplateSpec(Invite, "Verein: Einladung in den Mitgliederbereich", "Geht an ein Mitglied, das im Admin angelegt wurde (oder dem die Einladung erneut geschickt wird). Ein Klick auf den Link, Passwort festlegen – fertig und angemeldet.",
            "Willkommen im Mitgliederbereich – {{verein}}",
            "Hallo {{name}},\n\nfür dich wurde ein Zugang zum Mitgliederbereich von {{verein}} eingerichtet.\n\n{{schritt}}\n{{link}}\n\nDort findest du Termine und Sitzungen, Dokumente zum Bestätigen und kannst Bilder und Material hochladen. Dein Benutzername ist deine E-Mail-Adresse: {{email}}\n\nViele Grüße\n{{verein}}\n")
        {
            Placeholders = Common.Concat(new[] { Ph("email", "E-Mail-Adresse = Benutzername"), Ph("link", "Der Link: Passwort festlegen (neues Konto) oder Anmeldung (Konto gab es schon)"),
                Ph("schritt", "Der Satz vor dem Link – passend dazu, ob ein Passwort festgelegt oder nur angemeldet wird") }).ToArray()
        },

        new MailTemplateSpec(Document, "Verein: neues Dokument", "Geht an alle Mitglieder, für die ein neues Dokument bestimmt ist.",
            "{{verein}}: {{titel}} – bitte {{aufgabe}}",
            "Hallo {{name}},\n\nim Mitgliederbereich liegt ein neues Dokument für dich: „{{titel}}“.\nBitte {{aufgabe}}{{frist}}.\n\nHier geht es direkt dorthin:\n{{doc_url}}\n\nViele Grüße\n{{verein}}\n")
        { Placeholders = Common.Concat(new[] { Ph("titel", "Titel des Dokuments"), Ph("aufgabe", "„bestätigen“, „unterschreiben“ oder „lesen“"), Ph("frist", "„ bis zum …“ oder leer"), Ph("doc_url", "Link zum Dokument") }).ToArray() },

        new MailTemplateSpec(Reminder, "Verein: Erinnerung an ein Dokument", "Geht an Mitglieder, die ein Dokument noch nicht beantwortet haben, wenn im Admin „Erinnern“ geklickt wird.",
            "Erinnerung: {{titel}} – {{verein}}",
            "Hallo {{name}},\n\nkurze Erinnerung: „{{titel}}“ wartet noch auf dich. Bitte {{aufgabe}}{{frist}}.\n\n{{doc_url}}\n\nDanke und viele Grüße\n{{verein}}\n")
        { Placeholders = Common.Concat(new[] { Ph("titel", "Titel des Dokuments"), Ph("aufgabe", "„bestätigen“, „unterschreiben“ oder „lesen“"), Ph("frist", "„ bis zum …“ oder leer"), Ph("doc_url", "Link zum Dokument") }).ToArray() },

        new MailTemplateSpec(Meeting, "Verein: Einladung zur Sitzung", "Geht an alle, für die eine Sitzung bestimmt ist, wenn im Admin „Einladung senden“ geklickt wird.",
            "Einladung: {{titel}} am {{termin}}",
            "Hallo {{name}},\n\nhiermit laden wir dich ein zu: {{titel}}\nWann: {{termin}}\nWo: {{ort}}\n\nTagesordnung:\n{{tagesordnung}}\n\nBitte sag uns hier kurz, ob du kommst:\n{{meeting_url}}\n\nViele Grüße\n{{verein}}\n")
        { Placeholders = Common.Concat(new[] { Ph("titel", "Titel der Sitzung"), Ph("termin", "Datum und Uhrzeit"), Ph("ort", "Ort"), Ph("tagesordnung", "Tagesordnung"), Ph("meeting_url", "Link zur Sitzung") }).ToArray() },

        new MailTemplateSpec(Minutes, "Verein: Protokoll veröffentlicht", "Geht an die Eingeladenen einer Sitzung, wenn ihr Protokoll freigegeben wird.",
            "Protokoll: {{titel}}",
            "Hallo {{name}},\n\ndas Protokoll von „{{titel}}“ ({{termin}}) steht jetzt im Mitgliederbereich:\n{{meeting_url}}\n\nViele Grüße\n{{verein}}\n")
        { Placeholders = Common.Concat(new[] { Ph("titel", "Titel der Sitzung"), Ph("termin", "Datum und Uhrzeit"), Ph("meeting_url", "Link zur Sitzung") }).ToArray() },
    };

    public static string Origin(IPluginContext ctx) => (ctx.Site.Origin ?? "").TrimEnd('/');

    /// <summary>Sends one template to each person (one mail each, so nobody sees the others' addresses). Returns how many went out.</summary>
    public static async Task<(int Sent, int Failed)> SendAsync(IPluginContext ctx, string template, IEnumerable<Person> people,
        Func<Person, Dictionary<string, string>> values, CancellationToken ct)
    {
        var replyTo = ctx.Settings.Get(VereinSettings.ReplyTo);
        int sent = 0, failed = 0;
        foreach (var p in people)
        {
            if (string.IsNullOrWhiteSpace(p.Email)) { failed++; continue; }
            var v = values(p);
            v.TryAdd("name", p.First.Length > 0 ? p.First : p.Name);
            v.TryAdd("verein", VereinSettings.Club(ctx));
            v.TryAdd("login_url", Origin(ctx) + VereinPublic.Base);
            var r = await ctx.Mail.SendAsync(template, new[] { p.Email.Trim() }, v, null, string.IsNullOrWhiteSpace(replyTo) ? null : replyTo.Trim(), ct);
            if (r.Ok) sent++;
            else { failed++; ctx.Log($"Mail {template} an {p.Email} nicht gesendet: {r.Error}"); }
        }
        return (sent, failed);
    }

    public static string Task(string kind) => kind switch { VereinStore.Sign => "unterschreiben", VereinStore.Read => "lesen", _ => "bestätigen" };
    public static string Due(string due) => due.Length > 0 ? " bis zum " + V.Date(due) : "";
}
