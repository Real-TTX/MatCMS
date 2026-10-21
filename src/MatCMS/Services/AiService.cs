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
    private readonly IAiTransport _transport;

    public AiService(SiteContext site, IAiTransport transport)
    {
        _site = site;
        _transport = transport;
    }

    /// <summary>True when this site has AI: its own connection (Einstellungen → KI) is set up, or the cloud profile switched the
    /// relay on. Read from the settings only — whether a key is still valid, the first call says.</summary>
    public bool Enabled => OwnConfigured || _site.Get(SettingKeys.AiTransport) is "cloud";

    private bool OwnConfigured => _site.Get(SettingKeys.AiOwnProvider) switch
    {
        "openai" => _site.Get(SettingKeys.AiOwnApiKey).Length > 0 && _site.Get(SettingKeys.AiOwnModel).Length > 0,
        "chatgpt" => _site.Get(SettingKeys.AiOwnChatGptTokens).Length > 0 && _site.Get(SettingKeys.AiOwnChatGptModel).Length > 0,
        _ => false,
    };

    /// <summary>One call as it is, with the whole answer (model, tokens, the cloud's note). The assistant builds its own
    /// messages, including the cloud's instruction, so nothing is added here.</summary>
    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken ct = default)
    {
        if (!Enabled) return new AiResponse { Ok = false, Error = "KI ist für diese Website nicht aktiviert." };
        return await _transport.SendAsync(request, ct);
    }

    /// <summary>Runs a chat completion through the cloud relay. Returns (ok, text, error).</summary>
    public async Task<(bool ok, string? text, string? error)> RunAsync(
        string purpose, IReadOnlyList<(string role, string content)> messages, int? maxTokens = null,
        CancellationToken ct = default)
    {
        if (!Enabled) return (false, null, "KI ist für diese Website nicht aktiviert.");
        var msgs = new List<AiMessage>();
        // Global, cloud-rolled-out context first — brand/tone/language/facts the model must honour on
        // EVERY action. Its own system message, ahead of the per-action prompt, so it colours the result
        // without replacing the action's own instructions. Empty = nothing added.
        var context = (_site.Get(SettingKeys.AiInstruction) ?? "").Trim();
        if (context.Length > 0)
            msgs.Add(new AiMessage { Role = "system", Content = "Kontext dieser Website (immer beachten):\n" + context });
        // Per-action operator guidance (rolled out from the profile, keyed by purpose): augments the
        // built-in action prompt below WITHOUT replacing it, so "how it builds pages" can be steered per
        // action without touching code. Empty / unknown purpose = nothing added.
        var guide = ActionGuide(purpose);
        if (guide.Length > 0)
            msgs.Add(new AiMessage { Role = "system", Content = "Zusätzliche Vorgabe für diese Aktion (immer beachten):\n" + guide });
        msgs.AddRange(messages.Select(m => new AiMessage { Role = m.role, Content = m.content }));
        var req = new AiRequest { Purpose = purpose, MaxTokens = maxTokens, Messages = msgs };
        var answer = await _transport.SendAsync(req, ct);
        return (answer.Ok, answer.Text, answer.Ok ? null : answer.Error ?? "Unbekannter Grund.");
    }

    /// <summary>The operator's extra guidance for one action ("pagegen"/"sitegen"/"theme"/"seo"), from the
    /// rolled-out <c>ai.actionGuides</c> JSON map (purpose → text). Empty when unset or malformed.</summary>
    private string ActionGuide(string purpose)
    {
        var json = (_site.Get(SettingKeys.AiActionGuides) ?? "").Trim();
        if (json.Length == 0) return "";
        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map != null && map.TryGetValue(purpose, out var g) ? (g ?? "").Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>Proposes new values for a template's design parameters to match a design instruction.
    /// Returns the model's raw text — asked for as a bare JSON object <c>{ "paramId": "newValue", … }</c>
    /// containing ONLY the parameters it chooses to change. The caller validates every proposed value
    /// against its parameter's declared type before offering it; nothing here is trusted or applied.</summary>
    public Task<(bool ok, string? text, string? error)> SuggestThemeAsync(
        string instruction,
        IReadOnlyList<(string id, string label, string type, string options, string value)> parameters,
        CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in parameters)
        {
            sb.Append("- id=").Append(p.id).Append(" | Label: ").Append(p.label).Append(" | Typ: ").Append(p.type);
            if (!string.IsNullOrWhiteSpace(p.options)) sb.Append(" | Optionen: ").Append(p.options);
            sb.Append(" | aktuell: ").Append(string.IsNullOrEmpty(p.value) ? "(leer)" : p.value).Append('\n');
        }
        var system =
            "Du bist ein erfahrener Web-Designer. Du bekommst die Design-Parameter eines Website-Themes "
          + "(je Zeile: id, Label, Typ, ggf. Optionen, aktueller Wert) und eine Gestaltungs-Anweisung. "
          + "Schlage neue Werte vor, die die Anweisung stimmig umsetzen — achte auf Farbharmonie, "
          + "ausreichenden Kontrast/Lesbarkeit und Konsistenz. Werteregeln: color = Hex #rrggbb; "
          + "select = EXAKT eine der genannten Optionen; number = nur eine Zahl; bool = true oder false; "
          + "text = kurzer Text. Ändere NUR die Parameter, die für die Anweisung sinnvoll sind — die "
          + "übrigen lässt du weg. Antworte AUSSCHLIESSLICH mit einem JSON-Objekt "
          + "{ \"id\": \"neuerWert\", … } ohne Erklärungen und ohne Markdown-Codeblock.";
        return RunAsync("theme", new[]
        {
            ("system", system),
            ("user", $"Anweisung: {instruction}\n\nParameter:\n{sb}"),
        }, maxTokens: 3000, ct: ct);
    }

    /// <summary>Summarises page/post content into a short SEO text: a meta description
    /// (<paramref name="kind"/> = "meta") or a blog teaser ("excerpt"). Returns ONLY the text — no
    /// quotes, label or markdown — bounded in length, in the content language. Nothing is invented; it
    /// condenses the given content. An optional <paramref name="instruction"/> steers focus/tone.</summary>
    public Task<(bool ok, string? text, string? error)> SummarizeForSeoAsync(
        string kind, string title, string content, string? instruction, CancellationToken ct = default)
    {
        var (what, limit) = kind == "excerpt"
            ? ("einen kurzen, einladenden Teaser für diesen Blog-Beitrag (erscheint auf Karten und in Listen)", 200)
            : ("eine prägnante SEO-Meta-Beschreibung für diese Seite (das Snippet in Suchmaschinen)", 160);
        var extra = string.IsNullOrWhiteSpace(instruction) ? "" : $"\n\nZusätzliche Anweisung: {instruction!.Trim()}";
        var system =
            $"Du bist SEO-Redakteur. Schreibe {what}. Höchstens {limit} Zeichen, ein bis zwei Sätze, aktiv "
          + "und konkret. Fasse NUR den gegebenen Inhalt zusammen — nichts erfinden, keine Platzhalter. Gib "
          + "AUSSCHLIESSLICH den Text zurück: ohne Anführungszeichen, ohne Label, ohne Markdown.";
        var trimmed = content.Length > 6000 ? content[..6000] : content;
        var body = $"Titel: {title}\n\nInhalt:\n{trimmed}{extra}";
        return RunAsync("seo", new[] { ("system", system), ("user", body) }, maxTokens: 2000, ct: ct);
    }
}

/// <summary>How a model call leaves this site: <see cref="SiteAiTransport"/> (the site's own key or ChatGPT account, else the
/// cloud relay). The seam exists so the tests can answer in its place without a cloud or a provider.</summary>
public interface IAiTransport
{
    Task<AiResponse> SendAsync(AiRequest request, CancellationToken ct = default);
}
