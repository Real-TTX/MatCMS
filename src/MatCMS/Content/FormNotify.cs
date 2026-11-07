using System.Text.Json;

namespace MatCMS.Content;

/// <summary>Notification-recipient config for a form (stored in <c>Form.NotifyJson</c>):
/// selected user ids plus free-form extra e-mail addresses.</summary>
public class FormNotify
{
    public List<int> UserIds { get; set; } = new();
    public List<string> Emails { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static FormNotify Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<FormNotify>(json, Opts) ?? new(); }
        catch { return new(); }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Opts);

    /// <summary>The default for a form whose notification nobody has set up yet (nothing saved: no
    /// <c>NotifyJson</c>): every submission is mailed to the site's admins. Forms used to stay silent until someone
    /// ticked the box, so an inquiry sat unseen in the back office. Saving the form's settings once — the page
    /// shows this default ticked — makes the choice explicit, including switching it off.</summary>
    public static bool IsUnset(bool notifyEnabled, string? notifyJson) => !notifyEnabled && string.IsNullOrWhiteSpace(notifyJson);

    /// <summary>Whether a submission of this form is mailed at all.</summary>
    public static bool Sends(bool notifyEnabled, string? notifyJson) => notifyEnabled || IsUnset(notifyEnabled, notifyJson);

    /// <summary>The admin role's name on <c>User.Role</c>; admins are who gets a form's mail when no recipient is chosen.</summary>
    public const string AdminRole = "Admin";

    /// <summary>Who a submission goes to: the chosen users and extra addresses; when that leaves nobody (the default,
    /// or switched on without picking anyone) the admins — never silence. Duplicates and blanks removed.</summary>
    public List<string> Recipients(IEnumerable<string> chosenUserEmails, Func<IEnumerable<string>> adminEmails)
    {
        var list = Emails.Concat(chosenUserEmails).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
        if (list.Count == 0) list = adminEmails().Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Parses a free-text list of e-mail addresses (comma / semicolon / newline separated).</summary>
    public static List<string> ParseEmails(string? text) =>
        (text ?? "")
            .Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
