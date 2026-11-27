// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using Microsoft.AspNetCore.Http;

namespace MatPlatform;

/// <summary>
/// Describes the app for <c>GET /api</c>: who it is, its pages, tiles, settings, actions, roles and capabilities. The description is called once at startup
/// and then for each <c>GET /api</c> with the language of the caller (<see cref="Language"/>, <see cref="T"/>), so it must not depend on anything but the language.
/// <c>auth</c> and <c>endpoints</c> are filled in by the platform.
/// </summary>
public sealed class MatPlatformBuilder
{
    private readonly List<MatPage> _pages = [];
    private readonly List<MatStateItem> _state = [];
    private readonly List<MatField> _configure = [];
    private readonly List<MatAction> _actions = [];
    private readonly List<MatRole> _roles = [];
    private readonly List<MatPermission> _permissions = [];
    private readonly List<MatQuota> _quotas = [];
    private readonly List<MatCapability> _provides = [];
    private readonly List<MatCapability> _consumes = [];
    private readonly List<MatLauncher> _launchers = [];
    private readonly List<MatWidget> _widgets = [];
    private readonly List<MatQuickAction> _quickActions = [];
    private MatAppInfo? _app;

    public MatPlatformBuilder(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        Language = language;
    }

    /// <summary>The language of the texts: <c>de</c> or <c>en</c> (what the caller asked for, else the app's).</summary>
    public string Language { get; }

    /// <summary>The German or the English text, by <see cref="Language"/>.</summary>
    public string T(string de, string en) => Language == "en" ? en : de;

    /// <summary>Who the app is. <paramref name="id"/> is its id in MatDesign's registry, <paramref name="icon"/> and <paramref name="home"/> are relative to the app.</summary>
    public MatPlatformBuilder App(string id, string name, string description, string version, string? icon = null, string? accent = null, string home = "/")
    {
        _app = new MatAppInfo(id, name, description, version, icon, accent, home);
        return this;
    }

    /// <summary>A page (a menu entry in a consumer) with its sections, drawn top to bottom (<see cref="MatSection.Tiles"/>, <see cref="MatSection.Form"/>, ...).</summary>
    public MatPlatformBuilder Page(string id, string label, string icon, params MatSection[] sections)
    {
        _pages.Add(new MatPage(id, label, icon, sections));
        return this;
    }

    /// <summary>A tile; use the record's properties for unit, decimals, limits and the maximum of a meter.</summary>
    public MatPlatformBuilder Tile(MatStateItem tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        _state.Add(tile);
        return this;
    }

    /// <summary>A tile with a widget and an icon.</summary>
    public MatPlatformBuilder Tile(string key, string label, string widget = "number", string? icon = null, string? unit = null)
        => Tile(new MatStateItem(key, label, widget) { Icon = icon, Unit = unit });

    /// <summary>A setting; use the record's properties for limits, options, hints and <see cref="MatField.ShowIf"/> (<see cref="MatField.When"/>).</summary>
    public MatPlatformBuilder Field(MatField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        _configure.Add(field);
        return this;
    }

    /// <summary>A setting of a type, optionally required, with a hint.</summary>
    public MatPlatformBuilder Field(string key, string label, string type = "text", bool required = false, string? hint = null)
        => Field(new MatField(key, label, type) { Required = required ? true : null, Hint = hint });

    /// <summary>A button.</summary>
    public MatPlatformBuilder Action(MatAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _actions.Add(action);
        return this;
    }

    /// <summary>A button that opens <paramref name="url"/> (relative to the app).</summary>
    public MatPlatformBuilder LinkAction(string id, string label, string icon, string url)
        => Action(new MatAction(id, label, icon, "link") { Url = url });

    /// <summary>A button that runs <c>POST /api/actions/{id}</c>, maybe after a question and marked as dangerous.</summary>
    public MatPlatformBuilder PostAction(string id, string label, string icon, string? confirm = null, bool danger = false)
        => Action(new MatAction(id, label, icon, "post") { Confirm = confirm, Danger = danger ? true : null });

    /// <summary>A role of the app (the most powerful first): the value of the claim <c>role</c> and its name.</summary>
    public MatPlatformBuilder Role(string key, string label)
    {
        _roles.Add(new MatRole(key, label));
        return this;
    }

    /// <summary>
    /// 2.4.0: a right on top of a role that MatIdentity can give a person (docs/PLATFORM.md "People, permissions and quotas"); <paramref name="roles"/>: the roles that have it
    /// without anyone giving it.
    /// </summary>
    public MatPlatformBuilder Permission(string key, string label, string? hint = null, params string[] roles)
    {
        _permissions.Add(new MatPermission(key, label) { Hint = hint, Roles = roles is { Length: > 0 } ? roles : null });
        return this;
    }

    /// <summary>
    /// 2.4.0: a number per person that MatIdentity can set (<see cref="MatQuotaUnits"/>: <paramref name="unit"/> and <paramref name="period"/>). It has no default: the app's
    /// own setting applies when MatIdentity sets none (<see cref="MatAccess.LimitAsync"/>).
    /// </summary>
    public MatPlatformBuilder Quota(string key, string label, string unit, string? period = null, long? min = 0, long? max = null, string? hint = null)
    {
        _quotas.Add(new MatQuota(key, label, unit) { Period = period, Min = min, Max = max, Hint = hint });
        return this;
    }

    /// <summary>A capability the app offers to the other apps (docs/PLATFORM.md "Capabilities"); <paramref name="label"/>: what it is in words, for the Connections area.</summary>
    public MatPlatformBuilder Provides(string capability, int version = 1, string? label = null)
    {
        _provides.Add(new MatCapability(capability, version) { Label = label });
        return this;
    }

    /// <summary>
    /// A capability of another app this one uses; <paramref name="optional"/>: it works without it. <paramref name="label"/>: what the app does with it, in words
    /// ("Opens office files with"); <paramref name="parameters"/>: what it passes to the provider when it is bound.
    /// </summary>
    public MatPlatformBuilder Consumes(string capability, int version = 1, bool optional = false, string? label = null, IReadOnlyDictionary<string, string>? parameters = null)
    {
        _consumes.Add(new MatCapability(capability, version, optional ? true : null) { Label = label, Params = parameters });
        return this;
    }

    /// <summary>An icon on the home screen of a shell: <paramref name="icon"/> and <paramref name="url"/> relative to the app (docs/PLATFORM.md "Contributions").</summary>
    public MatPlatformBuilder Launcher(string id, string label, string icon, string url, string? audience = null, string? badge = null, string? accent = null, IReadOnlyList<string>? roles = null)
    {
        _launchers.Add(new MatLauncher(id, label, icon, url) { Audience = audience, Badge = badge, Accent = accent, Roles = roles is { Count: > 0 } ? roles : null });
        return this;
    }

    /// <summary>A widget on the home screen of a shell; use the record's properties for its items, address, refresh and audience.</summary>
    public MatPlatformBuilder Widget(MatWidget widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        _widgets.Add(widget);
        return this;
    }

    /// <summary>An entry of the menu of a launcher: an address of the app (relative), or the id of a post action with <paramref name="action"/>.</summary>
    public MatPlatformBuilder QuickAction(string launcher, string label, string icon, string? url = null, string? action = null)
    {
        _quickActions.Add(new MatQuickAction(launcher, label, icon) { Url = url, Action = action });
        return this;
    }

    /// <summary>The definition with the access the platform reports.</summary>
    public MatApiDefinition Build(MatAuthInfo auth)
    {
        ArgumentNullException.ThrowIfNull(auth);

        return new MatApiDefinition
        {
            App = _app ?? throw new InvalidOperationException("The description of the platform has no App(...)."),
            Auth = auth,
            Pages = [.. _pages],
            State = [.. _state],
            Configure = [.. _configure],
            Actions = [.. _actions],
            Provides = [.. _provides],
            Consumes = [.. _consumes],
            Contributes = _launchers.Count + _widgets.Count + _quickActions.Count == 0
                ? null
                : new MatContributions { Launchers = [.. _launchers], Widgets = [.. _widgets], QuickActions = [.. _quickActions] },
            Roles = _roles.Count == 0 ? null : [.. _roles],
            Permissions = _permissions.Count == 0 ? null : [.. _permissions],
            Quotas = _quotas.Count == 0 ? null : [.. _quotas],
            Endpoints = MatPlatformEndpoints.Paths,
        };
    }

    /// <summary>The definition that <paramref name="describe"/> makes in <paramref name="language"/> (for tests: <see cref="MatPlatformValidator"/>).</summary>
    public static MatApiDefinition Build(Action<MatPlatformBuilder> describe, string language = "de", MatAuthInfo? auth = null)
    {
        ArgumentNullException.ThrowIfNull(describe);
        var builder = new MatPlatformBuilder(language);
        describe(builder);
        return builder.Build(auth ?? new MatAuthInfo(false, null));
    }
}

/// <summary>How the platform behaves for this app; set with the second argument of <c>AddMatPlatform</c>.</summary>
public sealed class MatPlatformOptions
{
    /// <summary>The languages the description knows; a caller asking for another one gets <see cref="DefaultLanguage"/>.</summary>
    public static readonly IReadOnlyList<string> Languages = ["de", "en"];

    internal MatPlatformOptions(Action<MatPlatformBuilder> describe) => Describe = describe;

    /// <summary>The description of the app.</summary>
    public Action<MatPlatformBuilder> Describe { get; }

    /// <summary>The language of the app (<c>de</c> or <c>en</c>): texts when the caller asks for none of <see cref="Languages"/>.</summary>
    public string DefaultLanguage { get; set; } = "de";

    /// <summary>
    /// True (default): <c>PUT /api/configure</c> checks every value against the definition (unknown keys, types, <c>required</c>, <c>min</c>, <c>max</c>,
    /// <c>pattern</c>, <c>options</c>, <c>showIf</c>) before the app sees it (<see cref="MatValueChecker"/>). False: the app checks everything itself
    /// (for an app whose settings page has its own rules and messages).
    /// </summary>
    public bool CheckValues { get; set; } = true;

    /// <summary>
    /// The data folder of the app (<c>/data</c>): MatPlatform keeps the bindings there (<c>matplatform/bindings.json</c>), so an administrator can connect apps by hand in the
    /// Connections area and the app can provide key capabilities without code. Null: bindings come from the variables only.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Who may use the Connections area (<c>&lt;mat-connections /&gt;</c> and its forms): usually the administrators of the app, as the app knows them
    /// (<c>http =&gt; Task.FromResult(http.User.IsInRole("admin"))</c>). Null: nobody (the area is not shown).
    /// </summary>
    public Func<HttpContext, Task<bool>>? ConnectionsAccess { get; set; }

    /// <summary>
    /// The language of what MatPlatform draws in the app's own pages (the Connections area): `de` or `en` (`_ => "de"` for an app in German only). Null: the UI culture of the
    /// request when it is one of <see cref="Languages"/>, else `Accept-Language` (an app whose pages follow the person's language).
    /// </summary>
    public Func<HttpContext, string>? UiLanguage { get; set; }

    /// <summary>Requests per minute and caller address on the anonymous <c>/api</c> and <c>/api/health</c>.</summary>
    public int AnonymousPermitPerMinute { get; set; } = 120;

    /// <summary>The language of a request: the first entry of <c>Accept-Language</c> when it is one of <see cref="Languages"/>, else <see cref="DefaultLanguage"/>.</summary>
    public string LanguageOf(HttpContext? http)
    {
        var header = http?.Request.Headers.AcceptLanguage.ToString() ?? string.Empty;
        var first = header.Split(',', 2)[0].Split(';', 2)[0].Trim();
        var primary = first.Split('-', 2)[0].ToLowerInvariant();
        return Languages.Contains(primary) ? primary : DefaultLanguage;
    }

    /// <summary>The definition in <paramref name="language"/>.</summary>
    public MatApiDefinition Definition(string language, MatAuthInfo auth) => MatPlatformBuilder.Build(Describe, language, auth);
}
