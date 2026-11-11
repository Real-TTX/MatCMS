using System.Runtime.CompilerServices;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// The member area's pages in the BLOCK EDITOR, the way the shop's pages are (<see cref="IPluginBuilder.PageRole"/>): two roles —
/// <c>area</c>, the frame of every page after signing in (/mitglied, Dokumente, Termine, Kalender, Bilder, Mitgliederliste, Vorstand, Profil), and
/// <c>signin</c>, the sign-in page /mitglied shows a visitor. The routes compute what they always did and offer it as variables (<c>{{area.title}}</c>,
/// <c>{{area.first_name}}</c> …); the pieces the plugin builds — the whole page as before, the head with the member's chip, the page's content, the sign-in
/// form — are HTML variables that the block <c>verein_slot</c> places. The forms inside them stay whole on purpose: layout and words around them are free,
/// the form is not.
/// <para>The starting layout is ONE slot with <c>page_html</c> — exactly the markup the page had before — so a site that changes nothing looks as it did.
/// An editor adds blocks above or below it, or replaces it with <c>head_html</c> and <c>content_html</c> inside a section of their own. A page that is
/// switched off (or a host without plugin pages) is not used: the route draws its own default (<see cref="RenderAsync"/> answers null).</para>
/// </summary>
internal static class VereinPages
{
    public const string Area = "area", SignIn = "signin", SlotBlock = "verein_slot";

    private sealed record Var(string Role, string Name, bool Html, string Label);

    // the labels are string keys (VereinStrings.Public); the names are what an editor types between the braces
    private static readonly Var[] Vars =
    {
        new(Area, "title", false, "verein.page.var.title"), new(Area, "page", false, "verein.page.var.page"), new(Area, "club", false, "verein.page.var.club"),
        new(Area, "name", false, "verein.page.var.name"), new(Area, "first_name", false, "verein.page.var.firstName"), new(Area, "initials", false, "verein.page.var.initials"),
        new(Area, "board", false, "verein.page.var.board"),
        new(Area, "page_html", true, "verein.page.var.areaPage"), new(Area, "head_html", true, "verein.page.var.head"), new(Area, "content_html", true, "verein.page.var.content"),
        new(Area, "me_html", true, "verein.page.var.me"),
        new(SignIn, "title", false, "verein.page.var.title"), new(SignIn, "club", false, "verein.page.var.club"), new(SignIn, "intro", false, "verein.page.var.intro"),
        new(SignIn, "failed", false, "verein.page.var.failed"),
        new(SignIn, "page_html", true, "verein.page.var.signinPage"), new(SignIn, "form_html", true, "verein.page.var.form"),
    };

    /// <summary>The names (without the role) of a role's variables that hold HTML.</summary>
    public static IReadOnlyCollection<string> HtmlNames(string role) => Vars.Where(v => v.Role == role && v.Html).Select(v => v.Name).ToList();

    /// <summary>True when the complete name ("area.content_html") is one of the plugin's HTML pieces — the only kind the slot draws as it is (a name in the stored data is never trusted).</summary>
    public static bool IsHtml(string complete) => Vars.Any(v => v.Html && v.Role + "." + v.Name == complete);

    // ---- registering ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Declares the two pages and the slot block. In a frame of its own: on a host older than plugin pages the API's types are missing, and the plugin
    /// still loads — its routes then draw what they always drew.
    /// </summary>
    public static void Register(IPluginBuilder b)
    {
        try { RegisterCore(b); }
        catch (MissingMethodException) { }
        catch (TypeLoadException) { }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterCore(IPluginBuilder b)
    {
        b.PageRole(new PageRoleSpec(Area, "verein.page.area")
        {
            Description = "verein.page.areaDesc",
            Variables = Vars.Where(v => v.Role == Area).Select(v => new PageVariable(v.Name, v.Label) { Html = v.Html }).ToList(),
            Layout = _ => new[] { Slot(Area + ".page_html") },
            Scope = (ctx, ct) => Task.FromResult<PageScope?>(new PageScope(SampleArea(ctx), HtmlNames(Area)))
        });
        b.PageRole(new PageRoleSpec(SignIn, "verein.page.signin")
        {
            Description = "verein.page.signinDesc",
            Variables = Vars.Where(v => v.Role == SignIn).Select(v => new PageVariable(v.Name, v.Label) { Html = v.Html }).ToList(),
            Layout = _ => new[] { Slot(SignIn + ".page_html") },
            // a back-office user is no club member: /mitglied shows them the sign-in page with real data
            Preview = (ctx, ct) => Task.FromResult<string?>(VereinPublic.Base),
            Scope = (ctx, ct) => Task.FromResult<PageScope?>(new PageScope(VereinPublic.SignInValues(ctx, false), HtmlNames(SignIn)))
        });
        b.Block(new BlockSpec(SlotBlock, "verein.page.slot", "verein.page.slotDesc")
        {
            Category = "design",
            IconSvg = "<rect x=\"3\" y=\"4\" width=\"18\" height=\"16\" rx=\"2\"/><path d=\"M7 9h10M7 13h6\"/>",
            Fields = new[]
            {
                new FieldSpec("slot", "verein.page.slotField", FieldKind.Select)
                {
                    Options = Vars.Where(v => v.Html).Select(v => new FieldOption(v.Role + "." + v.Name, v.Label)).ToList(), Help = "verein.page.slotHelp"
                }
            }
        }, SlotAsync);
    }

    private static PageBlockSeed Slot(string name) => new(SlotBlock) { Data = new Dictionary<string, string> { ["slot"] = name } };

    /// <summary>Draws the piece the block names from the page's variables: the whole page as it is (it carries its own frame), a smaller piece inside the member area's look.</summary>
    private static Task<PluginResult> SlotAsync(IPluginContext ctx, CancellationToken ct)
    {
        var name = (ctx.Block?.Get("slot") ?? "").Trim();
        if (!IsHtml(name) || !ctx.PageScope.TryGetValue(name, out var html) || string.IsNullOrWhiteSpace(html)) return Task.FromResult(PluginResult.Fragment(""));
        if (name.EndsWith(".page_html", StringComparison.Ordinal)) return Task.FromResult(PluginResult.Fragment(html));
        return Task.FromResult(PluginResult.Fragment("<style>" + VereinCss.Css + "</style><div class=\"vm verein-slot\">" + html + "</div>"));
    }

    // ---- drawing ----------------------------------------------------------------------------------------------------

    /// <summary>The page the editors laid out for <paramref name="role"/>, drawn with <paramref name="values"/>; null = none (switched off, older host) — the route draws its default.</summary>
    public static async Task<string?> RenderAsync(IPluginContext ctx, string role, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        try { return await RenderCoreAsync(ctx, role, values, ct); }
        catch (MissingMethodException) { return null; }
        catch (TypeLoadException) { return null; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<string?> RenderCoreAsync(IPluginContext ctx, string role, IReadOnlyDictionary<string, string> values, CancellationToken ct) =>
        ctx.RenderPageAsync(role, new PageScope(values, HtmlNames(role)), ct);

    /// <summary>What the editor's live preview shows on the member area's page: a sample member and a sample card, drawn with the area's own classes.</summary>
    private static Dictionary<string, string> SampleArea(IPluginContext ctx)
    {
        var name = ctx.T("verein.page.sampleName");
        var title = ctx.T("verein.pub.area");
        var content = "<section class=\"vm-card\"><h2><i class=\"ti ti-calendar-event\"></i> " + V.H(ctx.T("verein.pub.upcoming")) + "</h2><p class=\"vm-muted\">" + V.H(ctx.T("verein.page.sampleText")) + "</p></section>";
        return VereinPublic.AreaValues(ctx, title, "home", name, name.Split(' ')[0], "EM", false, "<span class=\"vm-avatar\">EM</span>", content);
    }
}
