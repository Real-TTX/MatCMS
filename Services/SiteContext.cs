using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>Per-request helper exposing site settings and navigation to layout/partials.</summary>
public class SiteContext
{
    private readonly AppDbContext _db;
    private Dictionary<string, string>? _settings;
    private List<Page>? _navPages;
    private List<Page>? _footerPages;

    public SiteContext(AppDbContext db) => _db = db;

    public string Get(string key, string fallback = "")
    {
        _settings ??= _db.SiteSettings.AsNoTracking().ToDictionary(s => s.Key, s => s.Value);
        return _settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
    }

    private List<MenuItem>? _headerMenu;
    private List<MenuItem>? _footerMenu;

    public IReadOnlyList<MenuItem> HeaderMenu => _headerMenu ??= _db.MenuItems.AsNoTracking()
        .Where(m => m.Menu == "header").OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToList();

    public IReadOnlyList<MenuItem> FooterMenu => _footerMenu ??= _db.MenuItems.AsNoTracking()
        .Where(m => m.Menu == "footer").OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToList();

    public IReadOnlyList<Page> NavPages => _navPages ??= _db.Pages.AsNoTracking()
        .Where(p => p.IsPublished && p.ShowInNav)
        .OrderBy(p => p.NavOrder).ThenBy(p => p.Title)
        .ToList();

    public IReadOnlyList<Page> FooterPages => _footerPages ??= _db.Pages.AsNoTracking()
        .Where(p => p.IsPublished && p.ShowInFooter)
        .OrderBy(p => p.FooterOrder).ThenBy(p => p.Title)
        .ToList();

    private Template? _activeTemplate;
    private bool _templateLoaded;

    /// <summary>The active template, or a sensible FeuSys default if none is configured.</summary>
    public Template ActiveTemplate
    {
        get
        {
            if (!_templateLoaded)
            {
                _activeTemplate = _db.Templates.AsNoTracking()
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
    public string FooterText => Get(SettingKeys.FooterText, "© FEUSYS");

    public string TopBarLink1Text => Get(SettingKeys.TopBarLink1Text);
    public string TopBarLink1Url => Get(SettingKeys.TopBarLink1Url);
    public string TopBarLink2Text => Get(SettingKeys.TopBarLink2Text);
    public string TopBarLink2Url => Get(SettingKeys.TopBarLink2Url);

    public static string PageUrl(Page p) => p.Slug == "home" ? "/" : "/" + p.Slug;
}
