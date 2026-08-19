using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace MatCMS.Services;

/// <summary>
/// Lightweight, git-friendly localizer. Strings live in <c>Resources/&lt;culture&gt;.json</c>
/// (flat key → text maps). The current UI culture is set by RequestLocalization (cookie/default).
/// Missing keys fall back to the default culture (de) and finally to the key itself.
/// Usage in views: <c>@T["nav.pages"]</c>.
/// </summary>
public class Localizer
{
    /// <summary>The site's DEFAULT (root) content language — served at the prefix-less URLs (/ , /kontakt);
    /// every other active language is served under a "/{culture}" prefix. Defaults to German but is
    /// configurable per site via the <c>i18n.default</c> setting (applied at startup by
    /// <see cref="SetDefaultCulture"/>). This is the CONTENT root language, independent of the admin UI
    /// language (whose fallback is <see cref="ResourceFallbackCulture"/>).</summary>
    public static string DefaultCulture { get; private set; } = "de";

    /// <summary>The language the UI resource files (<c>Resources/*.json</c>) are authored in and the
    /// ultimate fallback for missing <c>@T</c> keys. Fixed (not the per-site content default), so changing
    /// a site's root content language never leaves admin strings untranslated.</summary>
    public const string ResourceFallbackCulture = "de";

    /// <summary>Sets the site's default (root) content language from the <c>i18n.default</c> setting.
    /// Must be one of <see cref="SupportedCultures"/> (else ignored). Call ONCE at startup, before
    /// routing and RequestLocalization are built.</summary>
    public static void SetDefaultCulture(string? code)
    {
        code = (code ?? "").Trim().ToLowerInvariant();
        if (SupportedCultures.Contains(code)) DefaultCulture = code;
    }

    /// <summary>
    /// The ROUTABLE culture universe: languages the build ships routes/RequestLocalization for. Which
    /// of these are actually ACTIVE on a site is an admin setting (<c>i18n.languages</c>, see
    /// <see cref="ParseActive"/>); which one is the ROOT (prefix-less) language is <c>i18n.default</c>
    /// (see <see cref="DefaultCulture"/>). Adding a truly new language = add its code here
    /// (+ optional Resources/&lt;c&gt;.json for the admin UI).
    /// </summary>
    public static readonly string[] SupportedCultures = ["de", "en", "fr", "it", "es", "hr", "sk", "nl", "pl"];

    /// <summary>
    /// The ADMIN-UI languages actually OFFERED in the back-office (the switcher in the admin foot and
    /// what <c>/set-language</c> accepts). Deliberately a SEPARATE list from
    /// <see cref="SupportedCultures"/>: that one is the CONTENT universe — it drives the public
    /// "/{culture}/…" routes, <see cref="ParseActive"/> and every page/menu locale. Deriving the
    /// back-office switcher from it (which is what the presence of <c>Resources/&lt;c&gt;.json</c> used
    /// to do) ties the two together, and then "offer fewer admin languages" silently means "take
    /// content languages away" — on a site with Croatian and Slovak pages that unpublishes them.
    /// Only de/en are complete translations; hr/sk ship 19 of ~1180 keys, i.e. a German UI with a
    /// Croatian label on it. The resource files stay on disk, so re-offering a language is this list
    /// plus a finished translation, nothing else.
    /// </summary>
    public static readonly string[] AdminUiCultures = ["de", "en"];

    /// <summary>True if <paramref name="culture"/> is offered as an ADMIN-UI language
    /// (<see cref="AdminUiCultures"/>) — not to be confused with <see cref="IsSupported"/>, which
    /// answers the same question for CONTENT locales.</summary>
    public static bool IsAdminUiSupported(string? culture) =>
        !string.IsNullOrEmpty(culture) && AdminUiCultures.Contains(culture);

    /// <summary>Routable cultures other than the current default (served under a URL prefix). Computed
    /// (not cached) so it reflects the configured <see cref="DefaultCulture"/>.</summary>
    public static IReadOnlyList<string> NonDefaultCultures =>
        SupportedCultures.Where(c => c != DefaultCulture).ToArray();

    /// <summary>Human names for the language picker.</summary>
    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        ["de"] = "Deutsch", ["en"] = "English", ["fr"] = "Français", ["it"] = "Italiano",
        ["es"] = "Español", ["hr"] = "Hrvatski", ["sk"] = "Slovenčina", ["nl"] = "Nederlands", ["pl"] = "Polski"
    };

    public static string DisplayName(string code) =>
        DisplayNames.TryGetValue((code ?? "").ToLowerInvariant(), out var n) ? n : (code ?? "").ToUpperInvariant();

    /// <summary>True if <paramref name="culture"/> is one of the routable cultures.</summary>
    public static bool IsSupported(string? culture) =>
        !string.IsNullOrEmpty(culture) && SupportedCultures.Contains(culture);

    /// <summary>The ACTIVE content languages, parsed from the admin <c>i18n.languages</c> setting
    /// (a comma list). The default culture is always active; order follows <see cref="SupportedCultures"/>;
    /// unknown/non-routable codes are ignored. An empty/unset setting → only the default language.</summary>
    public static IReadOnlyList<string> ParseActive(string? csv)
    {
        var chosen = (csv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        chosen.Add(DefaultCulture); // the original language is always active
        return SupportedCultures.Where(chosen.Contains).ToList();
    }

    private readonly IWebHostEnvironment _env;
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> _cache = new();

    public Localizer(IWebHostEnvironment env) => _env = env;

    public string this[string key] => Get(key) ?? key;

    public string this[string key, params object[] args]
    {
        get
        {
            var v = Get(key);
            return v is null ? key : string.Format(v, args);
        }
    }

    private string? Get(string key)
    {
        // Use Name (e.g. "de", "de-DE", "en") and take the language part. NB: under
        // InvariantGlobalization, TwoLetterISOLanguageName collapses to "iv", so we must
        // rely on Name here — otherwise a non-default culture could never be matched.
        var culture = CultureInfo.CurrentUICulture.Name.Split('-', 2)[0];
        if (string.IsNullOrEmpty(culture)) culture = ResourceFallbackCulture;
        // Resolve against the current UI culture, then fall back to the resource-authoring language
        // (NOT the per-site content default) so admin strings stay translated whatever the root language.
        foreach (var c in culture == ResourceFallbackCulture ? new[] { culture } : new[] { culture, ResourceFallbackCulture })
        {
            if (Load(c).TryGetValue(key, out var v))
                return v;
        }
        return null;
    }

    private Dictionary<string, string> Load(string culture) =>
        _cache.GetOrAdd(culture, c =>
        {
            var path = Path.Combine(_env.ContentRootPath, "Resources", c + ".json");
            if (!File.Exists(path)) return new();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                       ?? new();
            }
            catch
            {
                return new();
            }
        });
}
