using MatCMS.Services;
using MatCMS.Shared.Web;

namespace MatCMS.Pages.Shared;

/// <summary>
/// Builds the shared "Hinzufügen" dialogs with this application's wording. The partial itself lives
/// in <c>MatCMS.Shared.Web</c> and takes plain strings, because a shared view cannot reference either
/// application's <see cref="Localizer"/>; this is where the keys are resolved.
/// </summary>
public static class AddMenus
{
    /// <summary>The ways to add one of the catalogue payloads — templates, components, plugins.</summary>
    /// <param name="browseUrl">Opens the connected cloud's catalogue as a store dialog. Null when no
    /// cloud is connected: there is nothing to browse, and an option that answers "not connected" is
    /// worse than no option.</param>
    /// <param name="importTargetId">Element id of the page's own import form. The formats differ per
    /// payload — a template and a component arrive as JSON, a plugin as its bundle ZIP — which is why
    /// the dialog reveals the page's form rather than carrying one.</param>
    public static AddMenu Payload(Localizer t, string id, string createUrl, string createLabelKey,
        string? browseUrl = null, string? importTargetId = null)
    {
        var options = new List<AddOption>();
        if (browseUrl is { Length: > 0 })
            options.Add(new AddOption(t["add.fromCloud"], t["add.fromCloudHint"], browseUrl));

        options.Add(new AddOption(t[createLabelKey], t["add.createHint"], createUrl));

        if (importTargetId is { Length: > 0 })
            options.Add(new AddOption(t["add.import"], t["add.importHint"],
                Data: new Dictionary<string, string> { ["add-import"] = importTargetId }));

        return new AddMenu(id, t["add.button"], t["add.button"], t["action.close"], options);
    }
}
