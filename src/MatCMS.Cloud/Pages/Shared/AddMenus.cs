using MatCMS.Cloud.Services;
using MatCMS.Shared.Web;

namespace MatCMS.Cloud.Pages.Shared;

/// <summary>
/// Builds the shared "Hinzufügen" dialogs with the cloud's own wording. The partial itself lives in
/// <c>MatCMS.Shared.Web</c> and takes plain strings, because a shared view cannot reference either
/// application's <see cref="Localizer"/>; this is where the keys are resolved.
/// </summary>
public static class AddMenus
{
    /// <summary>The standard three ways to add a payload to a profile.</summary>
    /// <param name="storePickerKind">Payload key of the store picker, or null where there is nothing
    /// global to take from — which is the case throughout the store itself.</param>
    /// <param name="importTargetId">Element id of the page's own import form, or null where the
    /// payload has no separate import (a plugin's create page IS the bundle upload).</param>
    public static AddMenu Payload(Localizer t, string id, string createUrl, string createLabelKey,
        string? storePickerKind = null, string? importTargetId = null)
    {
        var options = new List<AddOption>();
        if (storePickerKind is { Length: > 0 })
            options.Add(new AddOption(t["add.fromGlobal"], t["add.fromGlobalHint"],
                Data: new Dictionary<string, string> { ["store-picker"] = storePickerKind }));

        options.Add(new AddOption(t[createLabelKey], t["add.createHint"], createUrl));

        if (importTargetId is { Length: > 0 })
            options.Add(new AddOption(t["add.import"], t["add.importHint"],
                Data: new Dictionary<string, string> { ["add-import"] = importTargetId }));

        return new AddMenu(id, t["add.button"], t["add.button"], t["action.close"], options);
    }

    /// <summary>A step of a question that has more than one. Same dialog, no button — only the step
    /// before it may open this.</summary>
    public static AddMenu Step(Localizer t, string id, string title, IReadOnlyList<AddOption> options)
        => new(id, "", title, t["action.close"], options, WithButton: false);

    /// <summary>An option that opens another step instead of going anywhere.</summary>
    public static AddOption StepTo(string title, string sub, string stepId)
        => new(title, sub, Data: new Dictionary<string, string> { ["add-menu"] = stepId });

    /// <summary>Step one for settings: WHICH setting.</summary>
    /// <param name="hasSmtp">True hides the SMTP branch — it is already in the profile, and offering
    /// to add it again would be an option that does nothing.</param>
    public static AddMenu Settings(Localizer t, string customUrl, bool hasSmtp)
    {
        var options = new List<AddOption>();
        if (!hasSmtp)
            options.Add(StepTo(t["settings.smtp"], t["add.smtpHint"], "settings-smtp"));
        options.Add(new AddOption(t["profiles.addSetting"], t["add.customSettingHint"], customUrl));
        return new AddMenu("settings", t["add.button"], t["add.whichSetting"], t["action.close"], options);
    }

    /// <summary>Step two for SMTP: WHERE FROM. Both answers land on the mail page — one with the
    /// global values filled in, one with the profile's own — because a configuration that is about to
    /// be rolled out to live sites should be seen before it is saved, not applied by a menu click.</summary>
    public static AddMenu SmtpSource(Localizer t, string globalUrl, string ownUrl) =>
        Step(t, "settings-smtp", t["add.smtpSource"],
        [
            new AddOption(t["add.fromGlobal"], t["add.smtpGlobalHint"], globalUrl),
            new AddOption(t["add.smtpOwn"], t["add.smtpOwnHint"], ownUrl),
        ]);
}
