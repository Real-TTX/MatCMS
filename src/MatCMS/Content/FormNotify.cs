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

    /// <summary>Parses a free-text list of e-mail addresses (comma / semicolon / newline separated).</summary>
    public static List<string> ParseEmails(string? text) =>
        (text ?? "")
            .Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
