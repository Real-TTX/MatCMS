using MatCMS.Shared;

namespace MatCMS.Services;

/// <summary>
/// Instance-side AI. Gated on the rolled-out <c>ai.transport</c> setting (a profile switches AI on for
/// this site); every model call is relayed through the connected cloud, which holds the provider key —
/// the key never lives here. Never throws: failures come back as (false, null, error) so an editor
/// action shows a reason instead of breaking. High-level helpers build the messages per action.
/// </summary>
public class AiService
{
    private readonly SiteContext _site;
    private readonly CloudService _cloud;

    public AiService(SiteContext site, CloudService cloud)
    {
        _site = site;
        _cloud = cloud;
    }

    /// <summary>True when AI is switched on for this site (rolled out from the cloud profile).</summary>
    public bool Enabled => _site.Get(SettingKeys.AiTransport) is "cloud";

    /// <summary>Runs a chat completion through the cloud relay. Returns (ok, text, error).</summary>
    public async Task<(bool ok, string? text, string? error)> RunAsync(
        string purpose, IReadOnlyList<(string role, string content)> messages, int? maxTokens = null,
        CancellationToken ct = default)
    {
        if (!Enabled) return (false, null, "KI ist für diese Website nicht aktiviert.");
        var req = new AiRequest
        {
            Purpose = purpose,
            MaxTokens = maxTokens,
            Messages = messages.Select(m => new AiMessage { Role = m.role, Content = m.content }).ToList(),
        };
        return await _cloud.CallAiAsync(req, ct);
    }

    /// <summary>Rewrites a single piece of prose to a given instruction, returning ONLY the rewritten
    /// text (no quotes, no commentary). The site's content language is passed so the model keeps it.</summary>
    public Task<(bool ok, string? text, string? error)> RewriteAsync(
        string instruction, string text, string? language = null, CancellationToken ct = default)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "" : $" Antworte auf {language}.";
        var system = "Du bist ein Redakteur für Website-Texte. Verbessere den gegebenen Text gemäß der "
                   + "Anweisung. Gib AUSSCHLIESSLICH den überarbeiteten Text zurück — ohne Anführungszeichen, "
                   + "ohne Erklärungen, ohne Markdown-Codeblöcke. Behalte Sinn und Sprache bei." + lang;
        return RunAsync("rewrite", new[]
        {
            ("system", system),
            ("user", $"Anweisung: {instruction}\n\nText:\n{text}"),
        }, maxTokens: 800, ct: ct);
    }
}
