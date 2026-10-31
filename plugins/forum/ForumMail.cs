namespace MatCMS.Plugin.Forum;

/// <summary>
/// The forum's mails. Their wording lives in Admin → Mailvorlagen (declared here once, then the operator's to change). A mail that cannot go out never
/// stops a post: <see cref="IPluginMail.SendAsync"/> does not throw, and a failure is only written to the plugin's log.
/// </summary>
internal static class ForumMail
{
    public const string Reply = "forum.reply", NewThread = "forum.newthread", Report = "forum.report";

    public static IEnumerable<MailTemplateSpec> Specs { get; } =
    [
        new(Reply, "Forum: neue Antwort (Abo)", "An Mitglieder, die ein Thema abonniert haben, wenn jemand anderes antwortet.",
            "Neue Antwort: {{thread}}",
            "Hallo {{name}},\n\n{{author}} hat im Thema „{{thread}}“ geantwortet:\n\n{{excerpt}}\n\nZum Beitrag: {{url}}\n\nDu bekommst diese E-Mail, weil du das Thema abonniert hast. Abbestellen kannst du hier: {{unsubscribe_url}}")
        {
            Placeholders =
            [
                new("name", "Name des Empfängers"), new("author", "Wer geantwortet hat"), new("thread", "Titel des Themas"),
                new("excerpt", "Anfang der Antwort (ohne Zitate)"), new("url", "Adresse der Antwort"), new("unsubscribe_url", "Seite der Abos"), new("forum", "Name des Forums")
            ]
        },
        new(NewThread, "Forum: neues Thema (an die Moderation)", "An die Adresse aus den Forum-Einstellungen, wenn jemand ein neues Thema eröffnet.",
            "Neues Thema im Forum: {{thread}}",
            "{{author}} hat im Bereich „{{board}}“ ein neues Thema eröffnet:\n\n{{thread}}\n\n{{excerpt}}\n\nZum Thema: {{url}}")
        {
            Placeholders = [new("author", "Wer es eröffnet hat"), new("board", "Forum"), new("thread", "Titel"), new("excerpt", "Anfang des Beitrags"), new("url", "Adresse des Themas")]
        },
        new(Report, "Forum: Beitrag gemeldet", "An die Adresse aus den Forum-Einstellungen, wenn ein Mitglied einen Beitrag meldet.",
            "Gemeldeter Beitrag: {{thread}}",
            "{{reporter}} hat einen Beitrag von {{author}} im Thema „{{thread}}“ gemeldet.\n\nGrund: {{reason}}\n\nBeitrag: {{excerpt}}\n\nAnsehen: {{url}}\nAlle Meldungen: {{admin_url}}")
        {
            Placeholders =
            [
                new("reporter", "Wer gemeldet hat"), new("author", "Verfasser des Beitrags"), new("thread", "Titel des Themas"), new("reason", "Begründung"),
                new("excerpt", "Text des Beitrags"), new("url", "Adresse des Beitrags"), new("admin_url", "Meldungen im Admin")
            ]
        }
    ];

    private static string Abs(IPluginContext ctx, string path) => ctx.Site.Origin.TrimEnd('/') + path;

    private static IEnumerable<string> NotifyAddresses(IPluginContext ctx) =>
        ctx.Settings.Get(ForumSettings.NotifyEmail).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => a.Contains('@')).Take(10);

    /// <summary>Tells every subscriber of the thread but the author. One mail per person (nobody sees the others' addresses), at most 300 per reply.</summary>
    public static async Task ReplyAsync(IPluginContext ctx, ForumStore store, ForumThread thread, long postId, PluginMember author, string body, CancellationToken ct)
    {
        if (!ctx.Settings.GetBool(ForumSettings.Subscriptions)) return;
        var subscribers = (await store.SubscribersAsync(thread.Id, ct)).Where(id => id != author.Id).Take(300).ToList();
        if (subscribers.Count == 0 || !await ctx.Mail.IsConfiguredAsync(ct)) return;
        var board = await store.BoardAsync(thread.BoardId, ct);
        foreach (var id in subscribers)
        {
            var account = await ctx.Members.FindAsync(id, ct);
            if (account is not { IsActive: true } || account.Email.Length == 0) continue;
            // A subscriber who lost the right to read the board (a role was taken away) hears nothing more from it.
            if (board is not null && !ForumStore.CanRead(board, new Viewer(new PluginMember(account.Id, account.Username, account.DisplayName, account.Email, account.Roles),
                    account.Roles.Contains(ForumSettings.Moderators(ctx), StringComparer.OrdinalIgnoreCase), false, false))) continue;
            var result = await ctx.Mail.SendAsync(Reply, new[] { account.Email }, new Dictionary<string, string>
            {
                ["name"] = account.DisplayName.Length > 0 ? account.DisplayName : account.Username, ["author"] = ForumStore.AuthorName(author),
                ["thread"] = thread.Title, ["excerpt"] = ForumMarkup.Plain(body, 600), ["url"] = Abs(ctx, ForumUrls.Post(postId)),
                ["unsubscribe_url"] = Abs(ctx, ForumUrls.Subscriptions), ["forum"] = ForumSettings.Title(ctx)
            }, ct: ct);
            if (!result.Ok) ctx.Log("Abo-Mail an Mitglied " + id + " nicht gesendet: " + result.Error);
        }
    }

    public static async Task NewThreadAsync(IPluginContext ctx, Board board, long threadId, string title, PluginMember author, string body, CancellationToken ct)
    {
        var to = NotifyAddresses(ctx).ToList();
        if (to.Count == 0) return;
        var result = await ctx.Mail.SendAsync(NewThread, to, new Dictionary<string, string>
        {
            ["author"] = ForumStore.AuthorName(author), ["board"] = board.Title, ["thread"] = title, ["excerpt"] = ForumMarkup.Plain(body, 600),
            ["url"] = Abs(ctx, ForumUrls.Thread(threadId, title))
        }, ct: ct);
        if (!result.Ok) ctx.Log("Mail „neues Thema“ nicht gesendet: " + result.Error);
    }

    public static async Task ReportAsync(IPluginContext ctx, ForumThread thread, ForumPost post, PluginMember reporter, string reason, CancellationToken ct)
    {
        var to = NotifyAddresses(ctx).ToList();
        if (to.Count == 0) return;
        var result = await ctx.Mail.SendAsync(Report, to, new Dictionary<string, string>
        {
            ["reporter"] = ForumStore.AuthorName(reporter), ["author"] = post.AuthorName, ["thread"] = thread.Title, ["reason"] = reason,
            ["excerpt"] = ForumMarkup.Plain(post.Body, 600), ["url"] = Abs(ctx, ForumUrls.Post(post.Id)), ["admin_url"] = Abs(ctx, "/admin/p/forum/reports")
        }, ct: ct);
        if (!result.Ok) ctx.Log("Mail „Beitrag gemeldet“ nicht gesendet: " + result.Error);
    }
}
