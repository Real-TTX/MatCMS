using System.Runtime.CompilerServices;

namespace MatCMS.Plugin.Docs;

/// <summary>
/// The docs' addresses and languages — in ONE place, never assembled by hand elsewhere. The docs live at <c>{prefix}/docs</c>, a page at
/// <c>{prefix}/docs/{section}/{page}/…</c> (its path of slugs), a version other than the default one at <c>{prefix}/docs/v/{version}/…</c>, the search at
/// <c>/docs/suche</c> (German) or <c>/docs/search</c> (any other language; both are understood everywhere). The prefix is the site's own for the
/// language (<see cref="IPluginSite.LocalePrefix"/>): "" for the default language while it lives at the plain addresses, <c>/en</c> … for the others.
/// </summary>
internal static class DocsUrls
{
    public const string Root = "/docs";

    public static string Default(IPluginContext ctx) => ctx.Site.DefaultCulture;

    /// <summary>The language of the request, the default one when none is known.</summary>
    public static string Current(IPluginContext ctx) => string.IsNullOrEmpty(ctx.Locale) ? Default(ctx) : ctx.Locale;

    /// <summary>The languages the site publishes in, the default first (only the default one outside a request with a site behind it).</summary>
    public static IReadOnlyList<string> Languages(IPluginContext ctx)
    {
        try
        {
            var all = ctx.Site.Locales;
            if (all.Count > 0) return all;
        }
        catch (InvalidOperationException) { /* no site in this context */ }
        return new[] { Default(ctx) };
    }

    public static bool Publishes(IPluginContext ctx, string locale) =>
        locale == Default(ctx) || Languages(ctx).Contains(locale, StringComparer.OrdinalIgnoreCase);

    public static string Prefix(IPluginContext ctx, string? locale)
    {
        try { return PrefixOf(ctx.Site, locale); }
        catch (MissingMethodException) { return string.IsNullOrEmpty(locale) || locale == Default(ctx) ? "" : "/" + locale; }   // a host before API 2.2
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string PrefixOf(IPluginSite site, string? locale) => site.LocalePrefix(locale);

    /// <summary>The start page of the docs in a language and version.</summary>
    public static string Home(IPluginContext ctx, string locale, string version, string defaultVersion) =>
        Prefix(ctx, locale) + Root + (version.Length > 0 && version != defaultVersion ? "/v/" + Uri.EscapeDataString(version) : "");

    public static string Page(IPluginContext ctx, string locale, string version, string defaultVersion, string path) =>
        Home(ctx, locale, version, defaultVersion) + "/" + path;

    public static string SearchSegment(string locale) => locale == "de" ? "suche" : "search";

    public static string Search(IPluginContext ctx, string locale, string version, string defaultVersion) =>
        Home(ctx, locale, version, defaultVersion) + "/" + SearchSegment(locale);

    /// <summary>The same page with its translations named for the site's language switcher — on a host before API 2.2 the page as it is.</summary>
    public static PluginResult WithTranslations(PluginResult page, IReadOnlyList<KeyValuePair<string, string>> alternates)
    {
        if (alternates.Count == 0) return page;
        try { return Alternates(page, alternates); }
        catch (MissingMethodException) { return page; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PluginResult Alternates(PluginResult page, IReadOnlyList<KeyValuePair<string, string>> alternates) => page.WithAlternates(alternates);

    /// <summary>The host's cleaner for rich text (API 2.2); an older host encodes instead.</summary>
    public static string Sanitize(IPluginContext ctx, string html)
    {
        try { return SanitizeOn(ctx.Site, html); }
        catch (MissingMethodException) { return System.Net.WebUtility.HtmlEncode(html); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string SanitizeOn(IPluginSite site, string html) => site.SanitizeHtml(html);
}

/// <summary>The plugin's settings.</summary>
internal static class DocsSettings
{
    public const string Title = "title", Intro = "intro", Toc = "toc", PrevNext = "prevNext", LastUpdated = "lastUpdated", HomeLayout = "homeLayout",
        Indexing = "indexing", CustomCss = "customCss";

    public static void Declare(ISettingsBuilder s)
    {
        s.Add(new SettingSpec(Title, "docs.set.title") { Help = "docs.set.titleHelp", Group = "docs.set.groupLook" });
        s.TextArea(Intro, "docs.set.intro", "", "docs.set.introHelp");
        s.Choice(HomeLayout, "docs.set.homeLayout", "cards", new[] { new FieldOption("cards", "docs.set.homeCards"), new FieldOption("list", "docs.set.homeList") });
        s.Bool(Toc, "docs.set.toc", true, "docs.set.tocHelp");
        s.Bool(PrevNext, "docs.set.prevNext", true);
        s.Bool(LastUpdated, "docs.set.lastUpdated", true);
        s.Bool(Indexing, "docs.set.indexing", true, "docs.set.indexingHelp");
        s.TextArea(CustomCss, "docs.set.css", "", "docs.set.cssHelp");
    }

    /// <summary>The docs' name: what the operator typed, else "Dokumentation" / "Documentation" in the page's language.</summary>
    public static string TitleOf(IPluginContext ctx) => ctx.Settings.Get(Title) is { Length: > 0 } t ? t.Trim() : ctx.T("docs.defaultTitle");

    public static string IntroOf(IPluginContext ctx) => ctx.Settings.Get(Intro) is { Length: > 0 } t ? t.Trim() : ctx.T("docs.defaultIntro");
}
