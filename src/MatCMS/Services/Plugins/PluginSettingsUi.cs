using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using Microsoft.AspNetCore.DataProtection;

namespace MatCMS.Services.Plugins;

/// <summary>
/// Draws a plugin's settings as a form and validates what comes back. The plugin only DECLARES its settings; the form, the
/// validation, the encryption of secrets and the storage are the host's — so every plugin's settings page looks the same, a
/// secret is never sent back to the browser, and an out-of-range number cannot reach the plugin.
/// </summary>
internal static class PluginSettingsUi
{
    /// <summary>The field name of a setting in the posted form.</summary>
    public static string FieldName(string key) => "s_" + key;
    /// <summary>The name of the "remove the stored secret" box.</summary>
    public static string ClearName(string key) => "clear_" + key;
    /// <summary>The name of one box of a multiple choice (by the option's position — a value may hold characters a field name should not).</summary>
    public static string OptionName(string key, int index) => FieldName(key) + "~" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The values of a stored multiple choice that are still options (in the declared order).</summary>
    public static List<string> Chosen(SettingSpec s, string? value)
    {
        var have = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return s.Options.Select(o => o.Value).Where(v => have.Contains(v, StringComparer.Ordinal)).ToList();
    }

    private const int MaxText = 4000;

    /// <summary>
    /// The settings with the group each belongs to. A declaration names a group on the first setting of it; the settings after it (without a name) belong to it until the next one is named.
    /// </summary>
    /// <summary>The group of the settings declared before a plugin's first named group, once it has groups at all.</summary>
    public const string GeneralGroup = "Allgemein";

    public static IReadOnlyList<(SettingSpec Spec, string? Group)> WithGroups(PluginRegistry.Slice slice)
    {
        // Settings before the first named group used to have none — and with groups, the settings page opens the first group and shows only
        // groups: the booking plugin's notify address and currency could not be reached at all. They form a first group of their own.
        string? current = slice.Settings.Any(x => !string.IsNullOrWhiteSpace(x.Group)) ? GeneralGroup : null;
        var list = new List<(SettingSpec, string?)>();
        foreach (var s in slice.Settings)
        {
            if (!string.IsNullOrWhiteSpace(s.Group)) current = s.Group;
            list.Add((s, current));
        }
        return list;
    }

    /// <summary>The groups a plugin's settings fall into, in declaration order, with how many settings each has.</summary>
    public static IReadOnlyList<(string Group, int Count)> Groups(PluginRegistry.Slice slice) =>
        WithGroups(slice).Where(x => x.Group is not null).GroupBy(x => x.Group!).Select(g => (g.Key, g.Count())).ToList();

    /// <summary>The settings form for a plugin — or, with <paramref name="onlyGroup"/>, for one group of its settings.</summary>
    /// <param name="slice">The plugin's declaration.</param>
    /// <param name="stored">What is saved (<c>ConfigJson</c> parsed).</param>
    /// <param name="locale">The admin language, for translating labels that are keys of the plugin's strings.</param>
    /// <param name="saveLabel">The text of the save button.</param>
    /// <param name="clearLabel">The text of the "remove secret" box.</param>
    /// <param name="secretSet">The placeholder shown in an already stored secret.</param>
    public static UiNode Build(PluginRegistry.Slice slice, IReadOnlyDictionary<string, string> stored, string locale,
        string saveLabel, string clearLabel, string secretSet, IReadOnlyDictionary<string, string>? errors = null,
        IReadOnlyDictionary<string, string>? posted = null, string? onlyGroup = null)
    {
        string L(string s) => PluginStrings.Label(slice, locale, s);
        var children = new List<UiNode?>();
        string? group = null;
        foreach (var (s, effective) in WithGroups(slice))
        {
            if (onlyGroup is not null && effective != onlyGroup) continue;
            if (!string.Equals(effective, group, StringComparison.Ordinal))
            {
                group = effective;
                // On a page of one group the page's own title already says it.
                if (!string.IsNullOrWhiteSpace(group) && onlyGroup is null) children.Add(Ui.Heading(L(group), 3));
            }
            var name = FieldName(s.Key);
            var value = posted is not null && posted.TryGetValue(name, out var pv) ? pv
                      : stored.TryGetValue(s.Key, out var sv) ? sv : s.Default;
            var help = s.Help is null ? null : L(s.Help);
            if (errors is not null && errors.TryGetValue(s.Key, out var err)) help = err + (help is null ? "" : " — " + help);
            UiNode field = s.Kind switch
            {
                SettingKind.TextArea => new UiTextArea(name, L(s.Label)) { Value = value, Rows = 5, Help = help, Required = s.Required },
                SettingKind.Number => new UiInput(name, L(s.Label)) { Value = value, Type = UiInputType.Number, Help = help, Required = s.Required, Step = "any",
                    Min = s.Min?.ToString(CultureInfo.InvariantCulture), Max = s.Max?.ToString(CultureInfo.InvariantCulture) },
                SettingKind.Bool => new UiCheckbox(name, L(s.Label)) { Checked = IsTrue(value), Help = help },
                SettingKind.Choice => new UiSelect(name, L(s.Label)) { Value = value, Help = help, Options = s.Options.Select(o => new UiOption(o.Value, L(o.Label))).ToList() },
                SettingKind.Image => new UiImage(name, L(s.Label)) { Value = value, Help = help },
                SettingKind.Color => new UiInput(name, L(s.Label)) { Value = IsColor(value) ? value : (IsColor(s.Default) ? s.Default : "#000000"), Type = UiInputType.Color, Help = help },
                SettingKind.Secret => new UiInput(name, L(s.Label)) { Type = UiInputType.Password, Help = help, Placeholder = stored.ContainsKey(s.Key) ? secretSet : null },
                // The boxes post as {name}~{index} — OptionName below reads them the same way.
                SettingKind.MultiChoice => new UiCheckList(name, L(s.Label)) { Help = help, Options = s.Options.Select(o => new UiOption(o.Value, L(o.Label))).ToList(), Checked = Chosen(s, value) },
                SettingKind.List => ListEditor(s, name, L(s.Label), help, value, posted, L),
                _ => new UiInput(name, L(s.Label)) { Value = value, Help = help, Required = s.Required }
            };
            children.Add(field);
            if (s.Kind == SettingKind.Secret && stored.ContainsKey(s.Key))
                children.Add(new UiCheckbox(ClearName(s.Key), clearLabel));
        }
        children.Add(Ui.FormActions(Ui.Submit(saveLabel, name: "action", value: "save-settings")));
        return new UiForm(children.Where(c => c is not null).Select(c => c!).ToList()) { Narrow = true };
    }

    /// <summary>
    /// Validates a posted settings form and returns the new stored configuration (existing keys the plugin does not declare are kept).
    /// A blank secret keeps the stored one; a ticked "remove" box deletes it. Errors are keyed by setting key; with any error nothing is saved.
    /// </summary>
    public static (Dictionary<string, string> Config, Dictionary<string, string> Errors) Apply(
        PluginRegistry.Slice slice, IReadOnlyDictionary<string, string> stored, IReadOnlyDictionary<string, string> form,
        IDataProtectionProvider? protection, Func<string, string> t, string? onlyGroup = null)
    {
        var config = new Dictionary<string, string>(stored, StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        string F(string name) => form.TryGetValue(name, out var v) ? v : "";

        foreach (var (s, effective) in WithGroups(slice))
        {
            // A page of one group posts only that group's fields: the others keep what is stored (a missing switch must not read as "off").
            if (onlyGroup is not null && effective != onlyGroup) continue;
            var raw = F(FieldName(s.Key));
            switch (s.Kind)
            {
                case SettingKind.Bool:
                    config[s.Key] = IsTrue(raw) ? "true" : "false";
                    break;
                case SettingKind.Secret:
                    if (IsTrue(F(ClearName(s.Key)))) config.Remove(s.Key);
                    else if (raw.Length > 0)
                    {
                        if (raw.Length > MaxText) { errors[s.Key] = t("plugins.settings.tooLong"); break; }
                        if (protection is null) { errors[s.Key] = t("plugins.settings.noKeys"); break; }
                        config[s.Key] = PluginSettingsAccess.SecretPrefix + protection.CreateProtector(PluginSettingsAccess.SecretPurpose).Protect(raw);
                    }
                    else if (s.Required && !stored.ContainsKey(s.Key)) errors[s.Key] = t("plugins.settings.required");
                    break;
                case SettingKind.Number:
                {
                    raw = raw.Trim();
                    if (raw.Length == 0) { if (s.Required) errors[s.Key] = t("plugins.settings.required"); else config[s.Key] = s.Default; break; }
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && !double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out n))
                    { errors[s.Key] = t("plugins.settings.notNumber"); break; }
                    if (double.IsNaN(n) || double.IsInfinity(n) || (s.Min is { } min && n < min) || (s.Max is { } max && n > max))
                    { errors[s.Key] = t("plugins.settings.range"); break; }
                    config[s.Key] = n.ToString("R", CultureInfo.InvariantCulture);
                    break;
                }
                case SettingKind.Color:
                    raw = raw.Trim();
                    if (raw.Length == 0) config[s.Key] = IsColor(s.Default) ? s.Default : "";          // a form that does not carry the field keeps the default
                    else if (!IsColor(raw)) errors[s.Key] = t("plugins.settings.notColor");
                    else config[s.Key] = raw.ToLowerInvariant();
                    break;
                case SettingKind.Image:
                    raw = raw.Trim();
                    // An address that is safe in a src and a CSS url(): a local path or https, nothing else.
                    if (raw.Length > 0 && (raw.Length > 500 || raw.IndexOfAny(new[] { '\'', '"', '(', ')', '\\', '<', '>', ' ' }) >= 0 || !((raw.StartsWith('/') && !raw.StartsWith("//")) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))))
                        errors[s.Key] = t("plugins.settings.notImage");
                    else config[s.Key] = raw;
                    break;
                case SettingKind.MultiChoice:
                    // Only declared values can be stored — the boxes post by position, never a value of their own.
                    config[s.Key] = string.Join(",", s.Options.Where((o, i) => IsTrue(F(OptionName(s.Key, i)))).Select(o => o.Value));
                    break;
                case SettingKind.Choice:
                    if (s.Options.All(o => o.Value != raw)) errors[s.Key] = t("plugins.settings.choice");
                    else config[s.Key] = raw;
                    break;
                case SettingKind.List:
                {
                    // a form without the editor (another group's page, an old client) keeps what is stored
                    if (!form.ContainsKey(FieldName(s.Key) + ".count")) break;
                    var rows = ReadRows(s, FieldName(s.Key), form);
                    var text = string.Join("\n", rows.Select(r => string.Join(CellSeparator, r.Cells)));
                    if (s.Required && rows.Count == 0) { errors[s.Key] = t("plugins.settings.required"); break; }
                    config[s.Key] = text;
                    break;
                }
                default:
                    if (raw.Length > MaxText) { errors[s.Key] = t("plugins.settings.tooLong"); break; }
                    if (s.Required && string.IsNullOrWhiteSpace(raw)) { errors[s.Key] = t("plugins.settings.required"); break; }
                    config[s.Key] = raw.Trim();
                    break;
            }
        }
        return (config, errors);
    }

    // ---- list settings: rows in a row editor, stored one row per line, cells joined by " | " ------------------------------------------------------

    private const string CellSeparator = " | ";
    private const int MaxRows = 200, MaxCell = 300;

    /// <summary>The columns of a list setting (one text column when it declares none).</summary>
    private static IReadOnlyList<SettingColumn> ColumnsOf(SettingSpec s) => s.Columns.Count > 0 ? s.Columns : new[] { new SettingColumn("v", s.Label) };

    /// <summary>A stored value as rows of cells (a row with fewer cells is filled with "").</summary>
    internal static List<string[]> ParseRows(SettingSpec s, string value)
    {
        var cols = ColumnsOf(s);
        return (value ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.Trim().Length > 0)
            .Select(l => cols.Count == 1 ? new[] { l.Trim() } : Enumerable.Range(0, cols.Count).Select(i => l.Split('|') is var p && i < p.Length ? p[i].Trim() : "").ToArray())
            .Take(MaxRows).ToList();
    }

    private static UiNode ListEditor(SettingSpec s, string name, string label, string? help, string value, IReadOnlyDictionary<string, string>? posted, Func<string, string> L)
    {
        var cols = ColumnsOf(s);
        // after a failed save the rows come back as they were posted, else as stored
        var rows = posted is not null && posted.ContainsKey(name + ".count") ? ReadRows(s, name, posted).Select(r => r.Cells).ToList() : ParseRows(s, value);
        return new UiRowEditor(name, label)
        {
            Help = help,
            AddLabel = s.AddLabel is { Length: > 0 } a ? L(a) : "+",
            MaxRows = MaxRows,
            Columns = cols.Select(c => new UiRowColumn(c.Key, cols.Count == 1 && s.Columns.Count == 0 ? "" : L(c.Label)) { Kind = c.Kind, Placeholder = c.Placeholder is null ? null : L(c.Placeholder), Width = c.Width }).ToList(),
            Rows = rows.Select((r, i) => new UiRowValue(i.ToString(CultureInfo.InvariantCulture),
                cols.Select((c, k) => c.Kind == UiRowCell.Check ? (string.Equals(r[k], c.On, StringComparison.OrdinalIgnoreCase) ? "true" : "") : r[k]).ToList())).ToList()
        };
    }

    /// <summary>The posted rows of a list setting, in screen order; empty rows are dropped, a cell loses "|" and line breaks.</summary>
    private static List<(string[] Cells, bool Empty)> ReadRows(SettingSpec s, string name, IReadOnlyDictionary<string, string> form)
    {
        var cols = ColumnsOf(s);
        string F(string n) => form.TryGetValue(n, out var v) ? v : "";
        if (!int.TryParse(F(name + ".count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) count = 0;
        var rows = new List<(string[], bool)>();
        for (var i = 0; i < Math.Min(count, MaxRows); i++)
        {
            var cells = cols.Select(c =>
            {
                var raw = F($"{name}.{i}.{c.Key}");
                if (c.Kind == UiRowCell.Check) return IsTrue(raw) ? c.On : c.Off;
                var v = raw.Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();
                return v.Length > MaxCell ? v[..MaxCell] : v;
            }).ToArray();
            var empty = cols.Select((c, k) => c.Kind == UiRowCell.Check || cells[k].Length == 0).All(x => x);
            rows.Add((cells, empty));
        }
        return rows.Where(r => !r.Item2).ToList();
    }

    /// <summary>The configuration as the JSON object that is stored.</summary>
    public static string ToJson(IReadOnlyDictionary<string, string> config)
    {
        var o = new JsonObject();
        foreach (var kv in config) o[kv.Key] = kv.Value;
        return o.ToJsonString();
    }

    private static bool IsColor(string? v) => v is { Length: 7 } && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit);

    private static bool IsTrue(string? v) => (v ?? "").Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes";
}
