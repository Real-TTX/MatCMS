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
        // Per-action operator guidance (rolled out from the profile, keyed by purpose): augments the
        // built-in action prompt below WITHOUT replacing it, so "how it builds pages" can be steered per
        // action without touching code. Empty / unknown purpose = nothing added.
        var guide = ActionGuide(purpose);
        if (guide.Length > 0)
            msgs.Add(new AiMessage { Role = "system", Content = "Zusätzliche Vorgabe für diese Aktion (immer beachten):\n" + guide });
        msgs.AddRange(messages.Select(m => new AiMessage { Role = m.role, Content = m.content }));
        var req = new AiRequest { Purpose = purpose, MaxTokens = maxTokens, Messages = msgs };
        return await _cloud.CallAiAsync(req, ct);
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

    /// <summary>Generates a whole page as a list of blocks. The model is given the AVAILABLE block types
    /// with their text fields (<paramref name="blocksSpec"/>) and a brief, and must answer with a JSON
    /// array of <c>{type, data}</c> using only those types/fields. The caller validates every block
    /// against the registry — nothing here is trusted. The global site instruction (via RunAsync) rides
    /// along, so the page comes out on-brand.</summary>
    public Task<(bool ok, string? text, string? error)> GeneratePageAsync(
        string instruction, string blocksSpec, CancellationToken ct = default)
    {
        var system =
            "Du bist Web-Redakteur und Designer und baust eine Website-Seite aus VORGEGEBENEN Bausteinen. "
          + "Unten die verfügbaren Blocktypen mit ihren Textfeldern (feldId [Beschreibung]). Wähle eine "
          + "sinnvolle Abfolge (z. B. Hero am Anfang, dann Inhalt, am Ende ein Aufruf zur Handlung) und "
          + "fülle die Textfelder mit echten, zum Auftrag passenden Inhalten — KEIN Lorem Ipsum, keine "
          + "Platzhalter. Verwende AUSSCHLIESSLICH die aufgelisteten type-Werte und feldId-Werte; erfinde "
          + "keine. Antworte AUSSCHLIESSLICH mit einem JSON-Array "
          + "[{\"type\":\"hero\",\"data\":{\"heading\":\"…\"}}, …] — ohne Erklärungen, ohne Markdown.";
        var body = $"Auftrag: {instruction}\n\nVerfügbare Blöcke:\n{blocksSpec}";
        return RunAsync("pagegen", new[] { ("system", system), ("user", body) }, maxTokens: 2000, ct: ct);
    }

    /// <summary>Generates a whole WEBSITE — several pages, each with a title, slug, whether it belongs in
    /// the menu, and a block list — from a briefing + the available blocks. Answers with a JSON array of
    /// pages. The caller validates every page and every block against the registry; nothing is trusted.
    /// The global site instruction rides along (via RunAsync), so the whole site comes out on-brand.</summary>
    public Task<(bool ok, string? text, string? error)> GenerateSiteAsync(
        string briefing, string blocksSpec, CancellationToken ct = default)
    {
        var system =
            "Du bist Web-Redakteur und Designer und baust eine ganze WEBSITE (mehrere Seiten) aus "
          + "VORGEGEBENEN Bausteinen. Unten die verfügbaren Blocktypen mit ihren Textfeldern. Erstelle "
          + "eine sinnvolle, kompakte Seitenstruktur (z. B. Start, Details/Über uns, Angebot/Preise, "
          + "Kontakt) — höchstens 6 Seiten. Für JEDE Seite: ein kurzer Titel, ein URL-Slug (klein, nur "
          + "a-z, 0-9, Bindestrich), ob sie ins Hauptmenü gehört (nav true/false), und eine Blockliste "
          + "(nur aufgelistete type/feldId, echte Inhalte, KEIN Lorem Ipsum). Antworte AUSSCHLIESSLICH "
          + "mit einem JSON-Array von Seiten: [{\"title\":\"Start\",\"slug\":\"home\",\"nav\":true,"
          + "\"blocks\":[{\"type\":\"hero\",\"data\":{\"heading\":\"…\"}}]}, …] — ohne Erklärungen, ohne Markdown.";
        var body = $"Auftrag: {briefing}\n\nVerfügbare Blöcke:\n{blocksSpec}";
        return RunAsync("sitegen", new[] { ("system", system), ("user", body) }, maxTokens: 4000, ct: ct);
    }
}
