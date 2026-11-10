namespace MatCMS.Plugin.Booking;

/// <summary>
/// Admin → Buchungen → Kundenverbund: the role of this site, and per role what there is to know — the attached sites that called (leading site),
/// the state of the last run, what waits in the queue, "Verbindung prüfen" and "Jetzt abgleichen" (attached site).
/// </summary>
public static class BookingAdminFederation
{
    private static string R => BookingAdmin.Root;

    public static IAdminSection Register(IAdminSection s) => s.Page("federation", null, PageAsync);

    private static string When(DateTime d) => d == DateTime.MinValue ? "noch nie" : BookingMail.Local(d);

    private static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var role = BookingFederation.Role(ctx);
        var self = R + "federation";
        if (ctx.Request.IsPost && role == BookingFederation.Member)
        {
            if (ctx.Request.Action == "probe")
                return await BookingFederation.ProbeAsync(ctx, ct) is { } why
                    ? PluginResult.Redirect(self, "Keine Verbindung: " + why, true)
                    : PluginResult.Redirect(self, "Verbindung steht – Adresse und Schlüssel werden angenommen.");
            var r = await BookingFederation.SyncAsync(ctx, ct);
            return r.Ok
                ? PluginResult.Redirect(self, "Abgeglichen: " + r.Pushed + " Gäste und " + r.Stays + " Aufenthalte gesendet, " + r.Pulled + " Gäste aus der Kartei übernommen.")
                : PluginResult.Redirect(self, "Abgleich nicht möglich: " + r.Error + " Die Änderungen bleiben in der Warteschlange.", true);
        }
        var settings = Ui.Button("Einstellungen", R + "_settings?group=" + Uri.EscapeDataString(BookingFederation.Group), UiButtonKind.Ghost, "ti-settings", small: true);
        var privacy = Ui.Muted("Ein Verbund ist eine gemeinsame Verarbeitung der Gästedaten durch die beteiligten Häuser – die Datenschutzerklärung jeder Seite muss ihn nennen " +
            "(wer beteiligt ist, welche Daten geteilt werden). Das ist ein Hinweis, keine Rechtsberatung. Meldedaten (Ausweise) werden nie geteilt – außer dem, was ein Gast " +
            "beim Check-in ausdrücklich „für den nächsten Aufenthalt“ merken lässt.");

        UiNode body;
        if (role == BookingFederation.Lead)
        {
            var seen = await BookingFederation.SeenAsync(ctx.Data, ct);
            var guests = (await ctx.Data.Table(BookingStore.TGuests).QueryAsync(DataQuery.Everything, ct)).Count;
            body = Ui.Stack(
                Ui.Card("Diese Seite führt die Gästekartei", Ui.Stack(
                    Ui.Text(guests + (guests == 1 ? " Gast" : " Gäste") + " in der Kartei. Andere Seiten lesen und schreiben sie über die Plugin-API."),
                    Ui.Text("So hängt eine weitere Seite an:"),
                    Ui.Html("<ol><li>Hier unter <b>Admin → API-Schlüssel</b> einen Schlüssel anlegen – Name z. B. der Name der anderen Seite, Recht <b>„Buchungen: Personendaten“</b>.</li>" +
                            "<li>Auf der anderen Seite unter Buchungen → Einstellungen → Kundenverbund: Rolle „hängt an“, die Adresse dieser Seite (" + System.Net.WebUtility.HtmlEncode(ctx.Site.Origin) + ") und den Schlüssel eintragen.</li>" +
                            "<li>Dort „Verbindung prüfen“ – ab dann gleicht sie alle 10 Minuten ab.</li></ol>"))),
                Ui.Card("Angehängte Seiten", Ui.Table(new[] { Ui.Column("Schlüssel"), Ui.Column("Zuletzt gemeldet") },
                    seen.Select(x => Ui.Row(x.Site, x.At is { } at ? BookingMail.Local(at) : "–")), emptyText: "Noch keine – sobald eine Seite anhängt, steht sie hier.")));
        }
        else if (role == BookingFederation.Member)
        {
            var (g, s) = await BookingFederation.PendingAsync(ctx.Data, ct);
            var error = await BookingFederation.MetaAsync(ctx.Data, "fed.last_error", ct);
            var ok = await BookingFederation.MetaTimeAsync(ctx.Data, "fed.last_ok", ct);
            body = Ui.Stack(
                error.Length > 0 ? Ui.Alert("Der letzte Abgleich ging nicht: " + error + " Buchungen gehen trotzdem – die Änderungen werden nachgereicht.", UiTone.Warning) : null,
                Ui.Card("Diese Seite hängt an", Ui.Stack(
                    Ui.Text("Führende Seite: " + (ctx.Settings.Get(BookingFederation.UrlKey) is { Length: > 0 } u ? u : "(keine Adresse eingetragen)")),
                    Ui.Text("Zuletzt abgeglichen: " + When(ok) + " · warten auf Abgleich: " + g + (g == 1 ? " Gast" : " Gäste") + ", " + s + (s == 1 ? " Aufenthalt" : " Aufenthalte")),
                    Ui.Muted("Die Seite hält eine Kopie der Gäste, die bei ihr angefragt oder gebucht haben – eine Anfrage geht auch, wenn die führende Seite nicht erreichbar ist. " +
                             "Ändert sich ein Feld auf beiden Seiten, gilt die spätere Änderung."),
                    Ui.Form(Ui.FormActions(Ui.Submit("Jetzt abgleichen", UiButtonKind.Primary, "action", "sync"), Ui.Submit("Verbindung prüfen", UiButtonKind.Ghost, "action", "probe"))))));
        }
        else
            body = Ui.Card("Diese Seite führt ihre Gäste für sich", Ui.Stack(
                Ui.Text("Mehrere Häuser mit denselben Gästen (z. B. zwei Villen eines Gastgebers) können eine Kartei teilen: eine Seite führt sie, die anderen hängen an. " +
                        "Ein Gast, der schon in einem Haus war, ist im anderen bekannt – mit Kontakt, Notizen und seinen Aufenthalten. Eine Cloud braucht es dafür nicht."),
                Ui.Text("Die Rolle stellst du in den Einstellungen ein.")));
        return PluginResult.Page(Ui.Stack(Ui.PageHead("Gäste mit anderen Seiten teilen", "Kundenverbund", settings), body, privacy), "Kundenverbund");
    }

    /// <summary>The federation's card on a guest's page: on the leading site the stays the others reported, on an attached site what the card index knows.</summary>
    public static async Task<UiNode?> GuestCardAsync(IPluginContext ctx, string email, CancellationToken ct)
    {
        var role = BookingFederation.Role(ctx);
        if (role == BookingFederation.Solo || email.Length == 0) return null;
        List<BookingFederation.RemoteStay> stays;
        string? note = null;
        if (role == BookingFederation.Lead) stays = await BookingFederation.RemoteStaysAsync(ctx.Data, email, ct);
        else
        {
            var (g, error) = await BookingFederation.LookupAsync(ctx, email, ct);
            stays = (g?["stays"] as System.Text.Json.Nodes.JsonArray ?? new()).OfType<System.Text.Json.Nodes.JsonObject>().Select(BookingFederation.RemoteStay.From).ToList();
            note = error is not null ? "Die Kartei ist gerade nicht erreichbar: " + error : g is null ? "In der Kartei noch nicht bekannt – kommt mit dem nächsten Abgleich dazu." : null;
        }
        return Ui.Card("Auf anderen Seiten", Ui.Stack(
            note is null ? null : Ui.Muted(note),
            Ui.Table(new[] { Ui.Column("Seite"), Ui.Column("Unterkunft"), Ui.Column("Zeitraum"), Ui.Column("Betrag", right: true), Ui.Column("Status") },
                stays.Select(x => Ui.Row(x.Site, (x.Property.Length > 0 ? x.Property + " · " : "") + x.Unit,
                    x.Arrival is { } a && x.Departure is { } d ? B.Date(a) + " – " + B.Date(d) : "–", B.Money(x.Total, x.Currency.Length == 3 ? x.Currency : "EUR"),
                    Ui.Badge(BookingStatus.Label(x.Status), BookingStatus.Tone(x.Status)))),
                emptyText: "Keine Aufenthalte auf anderen Seiten.")));
    }
}
