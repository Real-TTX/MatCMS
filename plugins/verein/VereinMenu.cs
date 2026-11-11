namespace MatCMS.Plugin.Verein;

/// <summary>
/// For a signed-in club member the site's main menu gets "Mitglieder" with the member area's pages under it (asked per page view, with the visitor's
/// own context — a visitor who is not a member sees nothing of it). Open documents show as a counter.
/// </summary>
public static class VereinMenu
{
    public static void Configure(IPluginBuilder b) => b.Menu(async (ctx, menu, ct) =>
    {
        if (menu != "header" || ctx.Member is null) return Array.Empty<MenuEntry>();
        var store = new VereinStore(ctx.Data);
        var p = await store.PersonOfMemberAsync(ctx.Member.Id, ct);
        if (p is null || !p.Active) return Array.Empty<MenuEntry>();
        var boardRoles = VereinSettings.BoardList(ctx);
        var board = p.Roles.Any(r => boardRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
        var answered = (await store.AnswersOfMemberAsync(p.MemberId, ct)).Select(a => a.DocId).ToHashSet();
        var open = (await store.DocsAsync(false, ct)).Count(d => VereinStore.InAudience(p, d.Audience, boardRoles) && !answered.Contains(d.Id));
        const string B = VereinPublic.Base;
        var children = new List<MenuEntry>
        {
            new(ctx.T("verein.menu.overview"), B) { Icon = "home", Order = 1 },
            new(ctx.T("verein.menu.docs"), B + "/dokumente") { Icon = "file-check", Order = 2, Badge = open > 0 ? open.ToString() : null },
            new(ctx.T("verein.menu.events"), B + "/termine") { Icon = "calendar", Order = 3 },
            new(ctx.T("verein.menu.calendar"), B + "/kalender") { Icon = "calendar-month", Order = 4 },
            new(ctx.T("verein.menu.files"), B + "/dateien") { Icon = "photo", Order = 5 },
            new(ctx.T("verein.menu.directory"), B + "/mitglieder") { Icon = "users", Order = 6 },
        };
        if (board) children.Add(new MenuEntry(ctx.T("verein.menu.board"), B + "/vorstand") { Icon = "clipboard-list", Order = 7 });
        children.Add(new MenuEntry(ctx.T("verein.menu.profile"), B + "/profil") { Icon = "user", Order = 8 });
        children.Add(new MenuEntry(ctx.T("verein.menu.signOut"), B + "/abmelden") { Icon = "logout", Order = 99 });
        return new[] { new MenuEntry(ctx.T("verein.menu.members"), B) { Icon = "users-group", Order = 900, Badge = open > 0 ? open.ToString() : null, Children = children } };
    });
}
