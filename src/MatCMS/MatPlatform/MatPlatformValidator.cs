// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatPlatform;

/// <summary>
/// Checks a MatApiDefinition against the contract (docs/PLATFORM.md): ids and keys unique and camelCase, every key a section names exists, every
/// <c>showIf</c> names another field, the kinds, widgets and types are known, a link has its address, and, with the icon names of the MatUi sprite,
/// every icon is in it. An app calls it in its tests: <c>Assert.Empty(MatPlatformValidator.Validate(definition, MatPlatformValidator.ReadSprite(path)))</c>.
/// </summary>
public static partial class MatPlatformValidator
{
    public static readonly IReadOnlySet<string> SectionKinds = new HashSet<string>(StringComparer.Ordinal) { "tiles", "form", "actions", "link" };

    public static readonly IReadOnlySet<string> Widgets = new HashSet<string>(StringComparer.Ordinal) { "number", "text", "bool", "time", "meter", "status" };

    public static readonly IReadOnlySet<string> FieldTypes = new HashSet<string>(StringComparer.Ordinal) { "text", "number", "bool", "select", "url", "email", "secret" };

    public static readonly IReadOnlySet<string> ActionKinds = new HashSet<string>(StringComparer.Ordinal) { "link", "post" };

    /// <summary>The problems of <paramref name="definition"/>; empty when it is valid. <paramref name="icons"/>: the names of the sprite (null: icons are not checked).</summary>
    public static IReadOnlyList<string> Validate(MatApiDefinition definition, IEnumerable<string>? icons = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var problems = new List<string>();
        var sprite = icons?.ToHashSet(StringComparer.Ordinal);

        if (definition.Contract != MatApiDefinition.ContractVersion)
        {
            problems.Add($"contract is {definition.Contract}, not {MatApiDefinition.ContractVersion}.");
        }

        var app = definition.App;
        if (app is null || !AppIdRegex().IsMatch(app.Id ?? string.Empty))
        {
            problems.Add($"app.id \"{app?.Id}\" is not an id (small letters and digits, a letter first).");
        }

        if (app is not null)
        {
            Required(problems, "app.name", app.Name);
            Required(problems, "app.version", app.Version);
            if (app.Accent is not null && !AccentRegex().IsMatch(app.Accent))
            {
                problems.Add($"app.accent \"{app.Accent}\" is not a colour like #2f6feb.");
            }
        }

        var state = Keys(problems, "state", definition.State.Select(s => s.Key));
        var fields = Keys(problems, "configure", definition.Configure.Select(f => f.Key));
        var actions = Keys(problems, "actions", definition.Actions.Select(a => a.Id));
        Unique(problems, "pages", definition.Pages.Select(p => p.Id));

        foreach (var page in definition.Pages)
        {
            Required(problems, "the label of page " + page.Id, page.Label);
            Icon(problems, sprite, "page " + page.Id, page.Icon);

            foreach (var section in page.Sections)
            {
                if (!SectionKinds.Contains(section.Kind))
                {
                    problems.Add($"page {page.Id}: the section kind \"{section.Kind}\" is not one of {string.Join(", ", SectionKinds)}.");
                    continue;
                }

                if (section.Kind == "link")
                {
                    Required(problems, $"page {page.Id}: the url of a link section", section.Url);
                    continue;
                }

                var (known, what) = section.Kind switch
                {
                    "tiles" => (state, "state"),
                    "form" => (fields, "configure"),
                    _ => (actions, "actions"),
                };
                foreach (var item in section.Items ?? [])
                {
                    if (!known.Contains(item))
                    {
                        problems.Add($"page {page.Id}: the {section.Kind} section names \"{item}\", which is not in {what}.");
                    }
                }
            }
        }

        foreach (var tile in definition.State)
        {
            Required(problems, "the label of state " + tile.Key, tile.Label);
            if (!Widgets.Contains(tile.Widget))
            {
                problems.Add($"state {tile.Key}: the widget \"{tile.Widget}\" is not one of {string.Join(", ", Widgets)}.");
            }

            if (tile.Max is { } max && max.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
            {
                problems.Add($"state {tile.Key}: max is a number or the key of another value.");
            }

            if (tile.Icon is not null)
            {
                Icon(problems, sprite, "state " + tile.Key, tile.Icon);
            }
        }

        foreach (var field in definition.Configure)
        {
            Required(problems, "the label of configure " + field.Key, field.Label);
            if (!FieldTypes.Contains(field.Type))
            {
                problems.Add($"configure {field.Key}: the type \"{field.Type}\" is not one of {string.Join(", ", FieldTypes)}.");
            }

            if (field.Type == "select" && field.Options is not { Count: > 0 })
            {
                problems.Add($"configure {field.Key}: a select has options.");
            }

            if (field.Min is { } min && field.Max is { } max && min > max)
            {
                problems.Add($"configure {field.Key}: min is above max.");
            }

            if (field.Pattern is { } pattern && !IsRegex(pattern))
            {
                problems.Add($"configure {field.Key}: the pattern is not a regular expression.");
            }

            foreach (var named in field.ShowIf?.Keys ?? Enumerable.Empty<string>())
            {
                if (named == field.Key || !fields.Contains(named))
                {
                    problems.Add($"configure {field.Key}: showIf names \"{named}\", which is not another field.");
                }
            }
        }

        foreach (var action in definition.Actions)
        {
            Required(problems, "the label of action " + action.Id, action.Label);
            Icon(problems, sprite, "action " + action.Id, action.Icon);
            if (!ActionKinds.Contains(action.Kind))
            {
                problems.Add($"action {action.Id}: the kind \"{action.Kind}\" is not link or post.");
            }
            else if (action.Kind == "link")
            {
                Required(problems, $"action {action.Id}: the url of a link", action.Url);
            }
        }

        foreach (var role in definition.Roles ?? [])
        {
            if (!RoleRegex().IsMatch(role.Key ?? string.Empty))
            {
                problems.Add($"role \"{role.Key}\": small letters, digits, - and _ only.");
            }

            Required(problems, "the label of role " + role.Key, role.Label);
        }

        Unique(problems, "roles", (definition.Roles ?? []).Select(r => r.Key));
        PeopleChecks(problems, definition);

        foreach (var capability in definition.Provides.Concat(definition.Consumes))
        {
            if (!CapabilityRegex().IsMatch(capability.Capability ?? string.Empty) || capability.Version < 1)
            {
                problems.Add($"capability \"{capability.Capability}\" version {capability.Version}: a name like mail.send and a version from 1.");
            }

            foreach (var key in (capability.Params ?? new Dictionary<string, string>()).Keys.Where(k => !KeyRegex().IsMatch(k ?? string.Empty)))
            {
                problems.Add($"capability {capability.Capability}: the parameter \"{key}\" is not camelCase.");
            }
        }

        Unique(problems, "provides", definition.Provides.Select(c => c.Capability));
        Unique(problems, "consumes", definition.Consumes.Select(c => c.Capability));

        if (definition.Contributes is { } contributes)
        {
            Contributions(problems, sprite, contributes, definition, state);
        }

        return problems;
    }

    /// <summary>The checks of <c>contributes</c>: ids, addresses relative to the app, the kinds and sizes of the widgets and the keys of <c>state</c> they show.</summary>
    private static void Contributions(List<string> problems, HashSet<string>? sprite, MatContributions contributes, MatApiDefinition definition, HashSet<string> state)
    {
        var launchers = Keys(problems, "contributes.launchers", contributes.Launchers.Select(l => l.Id));
        foreach (var launcher in contributes.Launchers)
        {
            var what = "launcher " + launcher.Id;
            Required(problems, what + ": the label", launcher.Label);
            Relative(problems, what + ": the icon", launcher.Icon);
            Relative(problems, what + ": the url", launcher.Url);
            Audience(problems, what, launcher.Audience);
            RolesOf(problems, what, launcher.Roles, definition);
            if (launcher.Badge is not null && !state.Contains(launcher.Badge))
            {
                problems.Add($"{what}: the badge \"{launcher.Badge}\" is not a key of state.");
            }

            if (launcher.Accent is not null && !AccentRegex().IsMatch(launcher.Accent))
            {
                problems.Add($"{what}: the accent \"{launcher.Accent}\" is not a colour like #2f6feb.");
            }
        }

        Keys(problems, "contributes.widgets", contributes.Widgets.Select(w => w.Id));
        foreach (var widget in contributes.Widgets)
        {
            var what = "widget " + widget.Id;
            Required(problems, what + ": the label", widget.Label);
            if (!MatWidgetKinds.All.Contains(widget.Kind ?? string.Empty))
            {
                problems.Add($"{what}: the kind \"{widget.Kind}\" is not one of {string.Join(", ", MatWidgetKinds.All)}.");
            }

            if (!MatWidgetKinds.Sizes.Contains(widget.Size ?? string.Empty))
            {
                problems.Add($"{what}: the size \"{widget.Size}\" is not one of {string.Join(", ", MatWidgetKinds.Sizes)}.");
            }

            var items = widget.Items ?? [];
            var (least, most) = widget.Kind switch
            {
                MatWidgetKinds.Stat or MatWidgetKinds.Meter => (1, 1),
                MatWidgetKinds.Tiles => (1, 4),
                _ => (0, 0),
            };
            if (items.Count < least || items.Count > most)
            {
                problems.Add($"{what}: a {widget.Kind} widget shows {(least == most ? least.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{least} to {most}")} keys of state, not {items.Count}.");
            }

            foreach (var item in items.Where(i => !state.Contains(i)))
            {
                problems.Add($"{what}: \"{item}\" is not a key of state.");
            }

            if (widget.Kind == MatWidgetKinds.Meter && items.Count == 1 && definition.State.FirstOrDefault(s => s.Key == items[0]) is { } meter && meter.Widget != "meter")
            {
                problems.Add($"{what}: \"{items[0]}\" is not a meter.");
            }

            if (widget.Icon is not null)
            {
                Icon(problems, sprite, what, widget.Icon);
            }

            if (widget.Url is not null)
            {
                Relative(problems, what + ": the url", widget.Url);
            }

            if (widget.Refresh is < MatWidgetKinds.MinRefresh)
            {
                problems.Add($"{what}: refresh is {widget.Refresh} seconds, at least {MatWidgetKinds.MinRefresh}.");
            }

            Audience(problems, what, widget.Audience);
            RolesOf(problems, what, widget.Roles, definition);
        }

        var posts = definition.Actions.Where(a => a.Kind == "post").Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var quick in contributes.QuickActions)
        {
            var what = $"quick action \"{quick.Label}\"";
            if (!launchers.Contains(quick.Launcher ?? string.Empty))
            {
                problems.Add($"{what}: the launcher \"{quick.Launcher}\" does not exist.");
            }

            Required(problems, what + ": the label", quick.Label);
            Icon(problems, sprite, what, quick.Icon);
            if ((quick.Url is null) == (quick.Action is null))
            {
                problems.Add(what + ": needs either a url or a post action.");
            }
            else if (quick.Url is not null)
            {
                Relative(problems, what + ": the url", quick.Url);
            }
            else if (!posts.Contains(quick.Action!))
            {
                problems.Add($"{what}: \"{quick.Action}\" is not a post action.");
            }
        }
    }

    /// <summary>2.4.0: the permissions and quotas: keys like a role key (unique), labels, the roles a permission names, the unit, period and limits of a quota.</summary>
    private static void PeopleChecks(List<string> problems, MatApiDefinition definition)
    {
        foreach (var permission in definition.Permissions ?? [])
        {
            var what = $"permission \"{permission.Key}\"";
            if (!AccessKeyRegex().IsMatch(permission.Key ?? string.Empty))
            {
                problems.Add($"{what}: small letters and digits first, then also - and _, at most 64.");
            }

            Required(problems, what + ": the label", permission.Label);
            RolesOf(problems, what, permission.Roles, definition);
        }

        Unique(problems, "permissions", (definition.Permissions ?? []).Select(p => p.Key));

        foreach (var quota in definition.Quotas ?? [])
        {
            var what = $"quota \"{quota.Key}\"";
            if (!AccessKeyRegex().IsMatch(quota.Key ?? string.Empty))
            {
                problems.Add($"{what}: small letters and digits first, then also - and _, at most 64.");
            }

            Required(problems, what + ": the label", quota.Label);
            if (!MatQuotaUnits.All.Contains(quota.Unit ?? string.Empty))
            {
                problems.Add($"{what}: the unit \"{quota.Unit}\" is not one of {string.Join(", ", MatQuotaUnits.All)}.");
            }

            if (quota.Period is not null && !MatQuotaUnits.Periods.Contains(quota.Period))
            {
                problems.Add($"{what}: the period \"{quota.Period}\" is not one of {string.Join(", ", MatQuotaUnits.Periods)}.");
            }

            if (quota.Min is < 0 || (quota.Min is { } min && quota.Max is { } max && min > max))
            {
                problems.Add($"{what}: min is from 0 and not above max.");
            }
        }

        Unique(problems, "quotas", (definition.Quotas ?? []).Select(q => q.Key));
    }

    /// <summary>Every key of <paramref name="roles"/> is a role of the definition.</summary>
    private static void RolesOf(List<string> problems, string what, IReadOnlyList<string>? roles, MatApiDefinition definition)
    {
        if (roles is null)
        {
            return;
        }

        var known = (definition.Roles ?? []).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var role in roles.Where(r => !known.Contains(r ?? string.Empty)))
        {
            problems.Add($"{what}: \"{role}\" is not a role of the app.");
        }
    }

    private static void Relative(List<string> problems, string what, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal))
        {
            problems.Add($"{what} \"{url}\" is not an address of the app (starting with one /).");
        }
    }

    private static void Audience(List<string> problems, string what, string? audience)
    {
        if (audience is not null && !MatAudience.All.Contains(audience))
        {
            problems.Add($"{what}: the audience \"{audience}\" is not one of {string.Join(", ", MatAudience.All)}.");
        }
    }

    /// <summary>The problems of a definition as JSON (the body of <c>GET /api</c>).</summary>
    public static IReadOnlyList<string> Validate(string json, IEnumerable<string>? icons = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        MatApiDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<MatApiDefinition>(json, MatApiDefinition.Json);
        }
        catch (JsonException ex)
        {
            return ["The definition cannot be read: " + ex.Message];
        }

        return definition is null ? ["The definition is empty."] : Validate(definition, icons);
    }

    /// <summary>The icon names of a MatUi sprite file (<c>&lt;symbol id="mat-i-name"&gt;</c>).</summary>
    public static IReadOnlySet<string> ReadSprite(string path)
        => SymbolRegex().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Keys(List<string> problems, string list, IEnumerable<string> keys)
    {
        var all = keys.ToList();
        foreach (var key in all.Where(k => !KeyRegex().IsMatch(k ?? string.Empty)))
        {
            problems.Add($"{list}: \"{key}\" is not camelCase (a small letter first, then letters and digits).");
        }

        Unique(problems, list, all);
        return all.ToHashSet(StringComparer.Ordinal);
    }

    private static void Unique(List<string> problems, string list, IEnumerable<string> keys)
    {
        foreach (var twice in keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"{list}: \"{twice.Key}\" is there {twice.Count()} times.");
        }
    }

    private static void Required(List<string> problems, string what, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add(what + " is empty.");
        }
    }

    private static void Icon(List<string> problems, HashSet<string>? sprite, string what, string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            problems.Add(what + " has no icon.");
        }
        else if (sprite is not null && !sprite.Contains(icon))
        {
            problems.Add($"{what}: the icon \"{icon}\" is not in the sprite.");
        }
    }

    private static bool IsRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*$")]
    private static partial Regex AppIdRegex();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex AccentRegex();

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex KeyRegex();

    [GeneratedRegex("^[a-z0-9_-]+$")]
    private static partial Regex RoleRegex();

    /// <summary>The key of a permission or a quota: the form MatIdentity keeps role keys in.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex AccessKeyRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+$")]
    private static partial Regex CapabilityRegex();

    [GeneratedRegex(@"<symbol id=""mat-i-([a-z0-9-]+)""")]
    private static partial Regex SymbolRegex();
}
