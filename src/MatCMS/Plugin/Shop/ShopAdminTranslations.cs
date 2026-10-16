using System.Globalization;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>The translations of a product or a category in the back office: one block of fields per language other than the main one, inside the same form, saved with it.</summary>
internal static class ShopAdminTranslations
{
    /// <summary>The site's languages except the main (first) one — the main language is the form itself.</summary>
    public static IReadOnlyList<string> Locales(IPluginContext ctx) => ctx.Site.Locales.Skip(1).ToList();

    private static string NameOf(string locale)
    {
        try { var n = CultureInfo.GetCultureInfo(locale).NativeName; return char.ToUpperInvariant(n[0]) + n[1..]; }
        catch (CultureNotFoundException) { return locale.ToUpperInvariant(); }
    }

    /// <summary>What the form posted, by language (empty ones are kept so a translation can be cleared).</summary>
    public static Dictionary<string, Translation> Read(RequestData f, IEnumerable<string> locales)
    {
        var map = new Dictionary<string, Translation>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in locales)
            map[l] = new Translation(f.F($"tr_{l}_title").Trim(), f.F($"tr_{l}_summary").Trim(), MatCMS.Services.SafeHtml.Sanitize(f.F($"tr_{l}_description")), f.F($"tr_{l}_seo_title").Trim(), f.F($"tr_{l}_seo_description").Trim());
        return map;
    }

    /// <summary>The same for a category: its name and its plain text.</summary>
    public static Dictionary<string, Translation> ReadCategory(RequestData f, IEnumerable<string> locales)
    {
        var map = new Dictionary<string, Translation>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in locales)
        {
            var d = f.F($"tr_{l}_description").Trim();
            map[l] = new Translation(f.F($"tr_{l}_title").Trim(), "", d.Length > 2000 ? d[..2000] : d);
        }
        return map;
    }

    public static async Task SaveAsync(ShopStore store, string kind, long id, IEnumerable<string> locales, IReadOnlyDictionary<string, Translation>? posted, CancellationToken ct)
    {
        if (posted is null) return;
        foreach (var l in locales)
            if (posted.TryGetValue(l, out var t)) await store.SaveTranslationAsync(kind, id, l, t, ct);
    }

    /// <summary>The card of a product's words in the other languages; null when the site has only one language.</summary>
    public static UiNode? ProductCard(IPluginContext ctx, IReadOnlyDictionary<string, Translation> have)
    {
        var locales = Locales(ctx);
        if (locales.Count == 0) return null;
        var nodes = new List<UiNode?> { Ui.Muted(ctx.T("shop.adm.tr.help")) };
        foreach (var l in locales)
        {
            have.TryGetValue(l, out var t); t ??= new Translation();
            nodes.Add(Ui.Text(NameOf(l)) with { Strong = true });
            nodes.Add(Ui.Input($"tr_{l}_title", ctx.T("shop.adm.title"), t.Title));
            nodes.Add(Ui.TextArea($"tr_{l}_summary", ctx.T("shop.adm.summary"), t.Summary, 2));
            nodes.Add(Ui.RichText($"tr_{l}_description", ctx.T("shop.description"), t.Description));
            nodes.Add(Ui.Input($"tr_{l}_seo_title", ctx.T("shop.adm.seoTitle"), t.SeoTitle));
            nodes.Add(Ui.TextArea($"tr_{l}_seo_description", ctx.T("shop.adm.seoDescription"), t.SeoDescription, 2));
        }
        return Ui.Card(ctx.T("shop.adm.tr.title"), nodes.ToArray());
    }

    /// <summary>The card of a category's name and text in the other languages; null when the site has only one language.</summary>
    public static UiNode? CategoryCard(IPluginContext ctx, IReadOnlyDictionary<string, Translation> have)
    {
        var locales = Locales(ctx);
        if (locales.Count == 0) return null;
        var nodes = new List<UiNode?> { Ui.Muted(ctx.T("shop.adm.tr.help")) };
        foreach (var l in locales)
        {
            have.TryGetValue(l, out var t); t ??= new Translation();
            nodes.Add(Ui.Text(NameOf(l)) with { Strong = true });
            nodes.Add(Ui.Input($"tr_{l}_title", ctx.T("shop.adm.categoryName"), t.Title));
            nodes.Add(Ui.TextArea($"tr_{l}_description", ctx.T("shop.adm.categoryDescription"), t.Description, 3));
        }
        return Ui.Card(ctx.T("shop.adm.tr.title"), nodes.ToArray());
    }
}
