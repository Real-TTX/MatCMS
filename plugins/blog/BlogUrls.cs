using System.Runtime.CompilerServices;

namespace MatCMS.Plugin.Blog;

/// <summary>
/// The blog's addresses and languages. A post lives at <c>{prefix}/blog/{slug}</c>, the overview at <c>{prefix}/blog</c>, where the prefix is
/// the site's own for the post's language (<see cref="IPluginSite.LocalePrefix"/>): "" for the default language while it lives at the plain
/// addresses — exactly the addresses the blog always had — and <c>/en</c>, <c>/fr</c> … for the others. Never build one by hand.
/// </summary>
internal static class BlogUrls
{
    /// <summary>The site's default content language.</summary>
    public static string Default(IPluginContext ctx) => ctx.Site.DefaultCulture;

    /// <summary>The language of the request (the page being drawn), the default one when none is known.</summary>
    public static string Current(IPluginContext ctx) => string.IsNullOrEmpty(ctx.Locale) ? Default(ctx) : ctx.Locale;

    /// <summary>The languages the site publishes in, the default first. Outside a request with a site behind it (a test harness) only the
    /// default one.</summary>
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

    /// <summary>True when the site publishes in this language (a post in a language that was switched off is not shown).</summary>
    public static bool Publishes(IPluginContext ctx, string locale) =>
        locale == Default(ctx) || Languages(ctx).Contains(locale, StringComparer.OrdinalIgnoreCase);

    /// <summary>The address prefix of a language on this site.</summary>
    public static string Prefix(IPluginContext ctx, string? locale)
    {
        try { return PrefixOf(ctx.Site, locale); }
        catch (MissingMethodException) { return string.IsNullOrEmpty(locale) || locale == Default(ctx) ? "" : "/" + locale; }  // a host before API 2.2
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string PrefixOf(IPluginSite site, string? locale) => site.LocalePrefix(locale);

    /// <summary>The overview of a language.</summary>
    public static string Overview(IPluginContext ctx, string locale) => Prefix(ctx, locale) + "/blog";

    /// <summary>A post's own address.</summary>
    public static string Post(IPluginContext ctx, BlogPost p) => Prefix(ctx, p.Locale) + "/blog/" + p.Slug;

    /// <summary>
    /// The forum's "source" of a post (<c>content.discussion</c> / <c>content.item</c>): <c>blog:{slug}</c> for a post in the default language —
    /// the form every existing discussion has — and <c>blog:{locale}_{slug}</c> for another language (a slug never holds "_", Slugify turns it
    /// into "-"). By slug, never by id: a restore gives the rows new ids.
    /// </summary>
    public static string Source(IPluginContext ctx, BlogPost p) => "blog:" + (p.Locale == Default(ctx) ? "" : p.Locale + "_") + p.Slug;

    /// <summary>Reads a source back into (locale, slug); null when it is not the blog's.</summary>
    public static (string Locale, string Slug)? ParseSource(IPluginContext ctx, string? source)
    {
        if (source is null || !source.StartsWith("blog:", StringComparison.Ordinal) || source.Length < 6) return null;
        var id = source[5..];
        var cut = id.IndexOf('_');
        return cut > 0 ? (id[..cut], id[(cut + 1)..]) : (Default(ctx), id);
    }

    /// <summary>The same page with its translations named for the site's language switcher — on a host before API 2.2 the page as it is.</summary>
    public static PluginResult WithTranslations(PluginResult page, IReadOnlyList<KeyValuePair<string, string>> alternates)
    {
        if (alternates.Count == 0) return page;
        try { return Alternates(page, alternates); }
        catch (MissingMethodException) { return page; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PluginResult Alternates(PluginResult page, IReadOnlyList<KeyValuePair<string, string>> alternates) => page.WithAlternates(alternates);
}
