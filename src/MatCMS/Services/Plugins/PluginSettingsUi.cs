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

    private const int MaxText = 4000;

    /// <summary>The settings form for a plugin.</summary>
    /// <param name="slice">The plugin's declaration.</param>
    /// <param name="stored">What is saved (<c>ConfigJson</c> parsed).</param>
    /// <param name="locale">The admin language, for translating labels that are keys of the plugin's strings.</param>
    /// <param name="saveLabel">The text of the save button.</param>
    /// <param name="clearLabel">The text of the "remove secret" box.</param>
    /// <param name="secretSet">The placeholder shown in an already stored secret.</param>
    public static UiNode Build(PluginRegistry.Slice slice, IReadOnlyDictionary<string, string> stored, string locale,
        string saveLabel, string clearLabel, string secretSet, IReadOnlyDictionary<string, string>? errors = null,
        IReadOnlyDictionary<string, string>? posted = null)
    {
        string L(string s) => PluginStrings.Label(slice, locale, s);
        var children = new List<UiNode?>();
        string? group = null;
        foreach (var s in slice.Settings)
        {
            if (!string.Equals(s.Group, group, StringComparison.Ordinal))
            {
                group = s.Group;
                if (!string.IsNullOrWhiteSpace(group)) children.Add(Ui.Heading(L(group), 3));
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
                SettingKind.Secret => new UiInput(name, L(s.Label)) { Type = UiInputType.Password, Help = help, Placeholder = stored.ContainsKey(s.Key) ? secretSet : null },
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
        IDataProtectionProvider? protection, Func<string, string> t)
    {
        var config = new Dictionary<string, string>(stored, StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        string F(string name) => form.TryGetValue(name, out var v) ? v : "";

        foreach (var s in slice.Settings)
        {
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
                case SettingKind.Choice:
                    if (s.Options.All(o => o.Value != raw)) errors[s.Key] = t("plugins.settings.choice");
                    else config[s.Key] = raw;
                    break;
                default:
                    if (raw.Length > MaxText) { errors[s.Key] = t("plugins.settings.tooLong"); break; }
                    if (s.Required && string.IsNullOrWhiteSpace(raw)) { errors[s.Key] = t("plugins.settings.required"); break; }
                    config[s.Key] = raw.Trim();
                    break;
            }
        }
        return (config, errors);
    }

    /// <summary>The configuration as the JSON object that is stored.</summary>
    public static string ToJson(IReadOnlyDictionary<string, string> config)
    {
        var o = new JsonObject();
        foreach (var kv in config) o[kv.Key] = kv.Value;
        return o.ToJsonString();
    }

    private static bool IsTrue(string? v) => (v ?? "").Trim().ToLowerInvariant() is "true" or "1" or "on" or "yes";
}
