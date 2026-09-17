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
        var msgs = new List<AiMessage>();
        // Global, cloud-rolled-out context first — brand/tone/language/facts the model must honour on
        // EVERY action. Its own system message, ahead of the per-action prompt, so it colours the result
        // without replacing the action's own instructions. Empty = nothing added.
        var context = (_site.Get(SettingKeys.AiInstruction) ?? "").Trim();
        if (context.Length > 0)
            msgs.Add(new AiMessage { Role = "system", Content = "Kontext dieser Website (immer beachten):\n" + context });
        msgs.AddRange(messages.Select(m => new AiMessage { Role = m.role, Content = m.content }));
        var req = new AiRequest { Purpose = purpose, MaxTokens = maxTokens, Messages = msgs };
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
        }, maxTokens: 500, ct: ct);
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
        return RunAsync("seo", new[] { ("system", system), ("user", body) }, maxTokens: 220, ct: ct);
    }
}
