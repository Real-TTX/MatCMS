using System.Text;
using System.Text.Json;
using MatCMS.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Machine translation behind the "Automatisch übersetzen" button in the page editor.
/// Two providers, chosen under Settings → Sprachen:
///   deepl          – DeepL API (free keys end in ":fx" → api-free.deepl.com, 500k chars/month)
///   libretranslate – a LibreTranslate instance (self-hosted container or public URL), key optional
/// Never throws to callers — failures come back as (ok:false, error).
/// </summary>
public class TranslationService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TranslationService> _log;

    public TranslationService(AppDbContext db, IHttpClientFactory http, ILogger<TranslationService> log)
    {
        _db = db; _http = http; _log = log;
    }

    public sealed record Config(string Provider, string ApiKey, string Url)
    {
        public bool IsConfigured => Provider is "deepl" or "libretranslate"
            && (Provider != "deepl" || !string.IsNullOrWhiteSpace(ApiKey))
            && (Provider != "libretranslate" || !string.IsNullOrWhiteSpace(Url));
    }

    public async Task<Config> GetConfigAsync()
    {
        var keys = SettingKeys.Translate;
        var map = await _db.SiteSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value);
        string G(string k) => map.TryGetValue(k, out var v) ? (v ?? "").Trim() : "";
        return new Config(G(SettingKeys.TranslateProvider).ToLowerInvariant(),
                          G(SettingKeys.TranslateApiKey), G(SettingKeys.TranslateUrl));
    }

    /// <summary>
    /// Translates a batch of texts (order preserved). <paramref name="html"/> switches the provider
    /// into HTML-aware mode so rich-text markup survives. Returns (ok, translations, error).
    /// </summary>
    public async Task<(bool ok, List<string> texts, string? error)> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLang, string targetLang, bool html, CancellationToken ct = default)
    {
        if (texts.Count == 0) return (true, new List<string>(), null);
        var cfg = await GetConfigAsync();
        if (!cfg.IsConfigured)
            return (false, new List<string>(), "Kein Übersetzungsdienst konfiguriert (Einstellungen → Sprachen).");

        try
        {
            return cfg.Provider switch
            {
                "deepl" => await DeepLAsync(cfg, texts, sourceLang, targetLang, html, ct),
                "libretranslate" => await LibreAsync(cfg, texts, sourceLang, targetLang, html, ct),
                _ => (false, new List<string>(), $"Unbekannter Anbieter „{cfg.Provider}“.")
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Translation failed ({Provider}, {Source}->{Target})", cfg.Provider, sourceLang, targetLang);
            return (false, new List<string>(), ex.Message);
        }
    }

    // ---- DeepL ------------------------------------------------------------

    private async Task<(bool, List<string>, string?)> DeepLAsync(
        Config cfg, IReadOnlyList<string> texts, string source, string target, bool html, CancellationToken ct)
    {
        // Free-tier keys are marked with a ":fx" suffix and MUST go to the free host.
        var host = cfg.ApiKey.TrimEnd().EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com" : "https://api.deepl.com";

        // DeepL wants a REGIONAL target for English; sources stay plain two-letter.
        static string Tgt(string l) => l.ToLowerInvariant() switch
        {
            "en" => "EN-GB",
            _ => l.ToUpperInvariant()
        };

        var results = new List<string>(texts.Count);
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        // DeepL accepts up to 50 text params per request.
        for (var offset = 0; offset < texts.Count; offset += 50)
        {
            var chunk = texts.Skip(offset).Take(50).ToList();
            var form = new List<KeyValuePair<string, string>>
            {
                new("source_lang", source.ToUpperInvariant()),
                new("target_lang", Tgt(target))
            };
            if (html) form.Add(new("tag_handling", "html"));
            form.AddRange(chunk.Select(t => new KeyValuePair<string, string>("text", t)));

            using var req = new HttpRequestMessage(HttpMethod.Post, host + "/v2/translate")
            { Content = new FormUrlEncodedContent(form) };
            req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + cfg.ApiKey.Trim());

            using var res = await client.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return (false, new List<string>(), $"DeepL HTTP {(int)res.StatusCode}: {Truncate(body)}");

            using var doc = JsonDocument.Parse(body);
            foreach (var t in doc.RootElement.GetProperty("translations").EnumerateArray())
                results.Add(t.GetProperty("text").GetString() ?? "");
        }
        if (results.Count != texts.Count)
            return (false, new List<string>(), "DeepL: unerwartete Antwortanzahl.");
        return (true, results, null);
    }

    // ---- LibreTranslate ----------------------------------------------------

    private async Task<(bool, List<string>, string?)> LibreAsync(
        Config cfg, IReadOnlyList<string> texts, string source, string target, bool html, CancellationToken ct)
    {
        var baseUrl = cfg.Url.TrimEnd('/');
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);

        var results = new List<string>(texts.Count);
        foreach (var text in texts)   // LT batching support varies per version → sequential is the safe path
        {
            var payload = new Dictionary<string, object?>
            {
                ["q"] = text,
                ["source"] = source.ToLowerInvariant(),
                ["target"] = target.ToLowerInvariant(),
                ["format"] = html ? "html" : "text"
            };
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) payload["api_key"] = cfg.ApiKey.Trim();

            using var res = await client.PostAsync(baseUrl + "/translate",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return (false, new List<string>(), $"LibreTranslate HTTP {(int)res.StatusCode}: {Truncate(body)}");

            using var doc = JsonDocument.Parse(body);
            results.Add(doc.RootElement.GetProperty("translatedText").GetString() ?? "");
        }
        return (true, results, null);
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
