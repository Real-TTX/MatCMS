using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>Per-request helper exposing site settings and navigation to layout/partials.</summary>
public class SiteContext
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _http;
    private Dictionary<string, string>? _settings;
    private List<Page>? _navPages;
    private List<Page>? _footerPages;

    public SiteContext(AppDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
    }

    // ------------------------------------------------------------------
    // Content locale (driven by the route, NOT the UI-culture cookie)
    // ------------------------------------------------------------------

    private string? _currentLocale;

    /// <summary>
    /// The content locale of the current request. Comes from the "{culture}" route value on the
    /// prefixed content route (/en/…); absent on the default routes → the default locale ("de").
    /// </summary>
    public string CurrentLocale
    {
        get
        {
            if (_currentLocale is not null) return _currentLocale;
            var routeCulture = _http.HttpContext?.Request.RouteValues.TryGetValue("culture", out var c) == true
                ? c as string
                : null;
            _currentLocale = Localizer.IsSupported(routeCulture) ? routeCulture! : Localizer.DefaultCulture;
            return _currentLocale;
        }
    }

    public bool IsDefaultLocale => CurrentLocale == Localizer.DefaultCulture;

    public string Get(string key, string fallback = "")
    {
        _settings ??= _db.SiteSettings.AsNoTracking().ToDictionary(s => s.Key, s => s.Value);
        return _settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
    }

    private readonly Dictionary<string, IReadOnlyList<MenuItem>> _menuCache = new();
    private List<Menu>? _allMenus;

    /// <summary>All defined menus (built-in + user-created), ordered.</summary>
    public IReadOnlyList<Menu> AllMenus => _allMenus ??= _db.Menus.AsNoTracking()
        .OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToList();

    /// <summary>Items of the menu with the given key, for the current content locale.</summary>
    public IReadOnlyList<MenuItem> MenuItems(string key)
    {
        if (_menuCache.TryGetValue(key, out var cached)) return cached;
        var items = _db.MenuItems.AsNoTracking()
            .Where(m => m.Menu == key && m.Locale == CurrentLocale)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToList();
        _menuCache[key] = items;
        return items;
    }

    // Convenience accessors for the built-in menus (served per content locale).
    public IReadOnlyList<MenuItem> HeaderMenu => MenuItems("header");
    public IReadOnlyList<MenuItem> FooterMenu => MenuItems("footer");
    /// <summary>Top-bar icon strip ("Obere Leiste").</summary>
    public IReadOnlyList<MenuItem> ToolbarMenu => MenuItems("toolbar");

    public IReadOnlyList<Page> NavPages => _navPages ??= _db.Pages.AsNoTracking()
        .Where(p => p.IsPublished && p.ShowInNav && p.Locale == CurrentLocale)
        .OrderBy(p => p.NavOrder).ThenBy(p => p.Title)
        .ToList();

    public IReadOnlyList<Page> FooterPages => _footerPages ??= _db.Pages.AsNoTracking()
        .Where(p => p.IsPublished && p.ShowInFooter && p.Locale == CurrentLocale)
        .OrderBy(p => p.FooterOrder).ThenBy(p => p.Title)
        .ToList();

    // ------------------------------------------------------------------
    // Language switcher
    // ------------------------------------------------------------------

    private List<string>? _availableLocales;

    /// <summary>
    /// Content locales that actually have at least one published page (default locale first, then
    /// the remaining supported cultures in their configured order). With a single-locale site this
    /// is just ["de"] and the switcher renders as a single/hidden entry.
    /// </summary>
    public IReadOnlyList<string> AvailableLocales
    {
        get
        {
            if (_availableLocales is not null) return _availableLocales;
            var present = _db.Pages.AsNoTracking().Where(p => p.IsPublished)
                .Select(p => p.Locale).Distinct().ToHashSet();
            present.Add(Localizer.DefaultCulture);
            _availableLocales = Localizer.SupportedCultures.Where(present.Contains).ToList();
            return _availableLocales;
        }
    }

    /// <summary>A single language-switcher target.</summary>
    public sealed record LocaleLink(string Locale, string Url, bool IsCurrent);

    private List<LocaleLink>? _languageLinks;

    /// <summary>
    /// Targets for the header language switcher: for each available locale, the translation of the
    /// current page (same TranslationGroup) if one exists, otherwise that locale's home page.
    /// </summary>
    public IReadOnlyList<LocaleLink> LanguageLinks()
    {
        if (_languageLinks is not null) return _languageLinks;

        var locales = AvailableLocales;
        var links = new List<LocaleLink>(locales.Count);

        // Single-locale site: no cross-locale lookups needed (switcher renders hidden anyway).
        if (locales.Count <= 1)
        {
            foreach (var loc in locales)
                links.Add(new LocaleLink(loc, LocalizedUrl(loc, "home"), loc == CurrentLocale));
            _languageLinks = links;
            return _languageLinks;
        }

        // Resolve the current page from the route (slug + current locale) to find its siblings.
        var slug = NormalizeSlug(_http.HttpContext?.Request.RouteValues.TryGetValue("slug", out var s) == true
            ? s as string
            : null);
        List<Page> siblings = new();
        var group = _db.Pages.AsNoTracking()
            .Where(p => p.Slug == slug && p.Locale == CurrentLocale)
            .Select(p => p.TranslationGroup)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(group))
        {
            siblings = _db.Pages.AsNoTracking()
                .Where(p => p.TranslationGroup == group && p.IsPublished)
                .ToList();
        }

        foreach (var loc in locales)
        {
            var sib = siblings.FirstOrDefault(p => p.Locale == loc);
            var url = sib is not null ? LocalizedUrl(sib.Locale, sib.Slug) : LocalizedUrl(loc, "home");
            links.Add(new LocaleLink(loc, url, loc == CurrentLocale));
        }

        _languageLinks = links;
        return _languageLinks;
    }

    private Template? _activeTemplate;
    private bool _templateLoaded;

    /// <summary>The active template, or a sensible FeuSys default if none is configured.</summary>
    public Template ActiveTemplate
    {
        get
        {
            if (!_templateLoaded)
            {
                // Admin-only live preview: render the public page with a specific template so the
                // template gallery can show a real, scaled <iframe> thumbnail (?previewTemplate=ID).
                var http = _http.HttpContext;
                if (http?.User?.IsInRole("Admin") == true &&
                    int.TryParse(http.Request.Query["previewTemplate"], out var pvId) && pvId > 0)
                {
                    _activeTemplate = _db.Templates.AsNoTracking().FirstOrDefault(t => t.Id == pvId);
                }
                _activeTemplate ??= _db.Templates.AsNoTracking()
                    .OrderByDescending(t => t.IsActive).ThenBy(t => t.Id)
                    .FirstOrDefault();
                _templateLoaded = true;
            }
            return _activeTemplate ?? new Template
            {
                Name = "FeuSys",
                IsActive = true,
                AccentColor = "#de7e11",
                HeadingFont = "Geologica",
                BodyFont = "Inter",
                ButtonStyle = "solid"
            };
        }
    }

    public string SiteName => Get(SettingKeys.SiteName, "FEUSYS");
    public string LogoUrl => Get(SettingKeys.LogoUrl, "/img/logo.svg");
    /// <summary>Favicon URL; falls back to the site logo when no separate favicon is configured.</summary>
    public string FaviconUrl => Get(SettingKeys.FaviconUrl, LogoUrl);
    public string FooterText => Get(SettingKeys.FooterText, "© FEUSYS");

    // --- Custom code / tracking (Settings → Code) ---
    /// <summary>Raw HTML injected right before &lt;/head&gt;.</summary>
    public string HeadCode => Get(SettingKeys.CodeHead);
    /// <summary>Raw HTML injected right after &lt;body&gt;.</summary>
    public string BodyStartCode => Get(SettingKeys.CodeBodyStart);
    /// <summary>Raw HTML injected right before &lt;/body&gt;.</summary>
    public string BodyEndCode => Get(SettingKeys.CodeBodyEnd);
    /// <summary>GA4 Measurement-ID (e.g. "G-XXXXXXX"); empty = no analytics.</summary>
    public string AnalyticsGa4 => Get(SettingKeys.AnalyticsGa4);

    /// <summary>Filenames (under /plugin-assets) that are auto-included site-wide, in order.</summary>
    public IReadOnlyList<string> PluginAutoIncludes =>
        Get(SettingKeys.PluginAutoInclude)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList();

    public string TopBarLink1Text => Get(SettingKeys.TopBarLink1Text);
    public string TopBarLink1Url => Get(SettingKeys.TopBarLink1Url);
    public string TopBarLink2Text => Get(SettingKeys.TopBarLink2Text);
    public string TopBarLink2Url => Get(SettingKeys.TopBarLink2Url);

    /// <summary>Public URL of a page including its locale prefix (default locale = no prefix).</summary>
    public static string PageUrl(Page p) => LocalizedUrl(p.Locale, p.Slug);

    /// <summary>
    /// Builds a public URL for (locale, slug): the default locale keeps root URLs (/, /kontakt);
    /// every other locale is served under a "/{locale}" prefix (/en, /en/about).
    /// </summary>
    public static string LocalizedUrl(string? locale, string? slug)
    {
        var isHome = string.IsNullOrEmpty(slug) || slug == "home";
        var prefix = string.IsNullOrEmpty(locale) || locale == Localizer.DefaultCulture ? "" : "/" + locale;
        if (isHome) return prefix.Length == 0 ? "/" : prefix;
        return prefix + "/" + slug;
    }

    private static string NormalizeSlug(string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? "home" : slug.Trim().ToLowerInvariant();
}
