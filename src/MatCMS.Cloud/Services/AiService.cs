using System.Text;
using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Calls the central AI provider (OpenAI by default, or any OpenAI-compatible endpoint via a base URL)
/// for a relayed <see cref="AiRequest"/>. The API key lives ONLY here — CloudSettings, SecretProtector-
/// encrypted — and never reaches an instance, exactly like the SMTP credential. The MODEL is fixed here
/// too, so a connected site cannot escalate to a costlier one through a request field. Never throws to
/// the caller: failures come back as <c>Ok=false</c> with a reason the instance can show.
/// </summary>
public class AiService
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AiService> _log;

    /// <summary>Hard ceiling on completion length regardless of what an instance asks for — bounds the
    /// cost of a single call.</summary>
    private const int MaxCompletionTokens = 4000;

    public AiService(AppDbContext db, SecretProtector secrets, IHttpClientFactory http, ILogger<AiService> log)
    {
        _db = db;
        _secrets = secrets;
        _http = http;
        _log = log;
    }

    public sealed record AiConfig(string Provider, string Model, string BaseUrl, string ApiKey)
    {
        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model);
    }

    public async Task<AiConfig> GetConfigAsync()
    {
        var keys = new[] { SettingKeys.AiProvider, SettingKeys.AiModel, SettingKeys.AiBaseUrl, SettingKeys.AiApiKey };
        var map = await _db.CloudSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value);
        string G(string k) => map.TryGetValue(k, out var v) ? (v ?? "") : "";

        var provider = G(SettingKeys.AiProvider).Trim().ToLowerInvariant();
        if (provider.Length == 0) provider = "openai";
        var model = G(SettingKeys.AiModel).Trim();
        if (model.Length == 0) model = "gpt-4o-mini";
        var baseUrl = G(SettingKeys.AiBaseUrl).Trim().TrimEnd('/');
        if (baseUrl.Length == 0) baseUrl = "https://api.openai.com/v1";
        // Stored encrypted (SecretProtector); an unmarked legacy value passes through unchanged.
        var key = _secrets.Unprotect(G(SettingKeys.AiApiKey)) ?? "";
        return new AiConfig(provider, model, baseUrl, key);
    }

    public async Task<bool> IsConfiguredAsync() => (await GetConfigAsync()).IsConfigured;

    /// <summary>Runs a chat completion with the saved config. Never throws.</summary>
    public async Task<AiResponse> CompleteAsync(AiRequest req, CancellationToken ct = default)
    {
        var cfg = await GetConfigAsync();
        if (!cfg.IsConfigured)
            return new AiResponse { Ok = false, Error = "KI ist nicht konfiguriert (Anbieter-Schlüssel und Modell erforderlich)." };
        if (req.Messages is null || req.Messages.Count == 0)
            return new AiResponse { Ok = false, Error = "Leere KI-Anfrage." };

        var maxTokens = Math.Clamp(req.MaxTokens ?? 1000, 16, MaxCompletionTokens);
        try
        {
            // OpenAI-compatible /chat/completions. The model is the CLOUD's, not the instance's.
            var payload = new
            {
                model = cfg.Model,
                messages = req.Messages.Select(m => new { role = NormalizeRole(m.Role), content = m.Content ?? "" }).ToArray(),
                max_tokens = maxTokens,
                temperature = 0.4,
            };
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(100);   // AI completions routinely exceed the 30s mail cap
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{cfg.BaseUrl}/chat/completions");
            httpReq.Headers.Add("Authorization", $"Bearer {cfg.ApiKey}");
            httpReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await client.SendAsync(httpReq, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("AI call failed: {Status} {Body}", (int)resp.StatusCode, Trunc(raw));
                return new AiResponse { Ok = false, Error = $"Anbieter-Fehler {(int)resp.StatusCode}: {ProviderError(raw)}" };
            }

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            int prompt = 0, completion = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number) prompt = p.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number) completion = c.GetInt32();
            }
            return new AiResponse { Ok = true, Text = text, PromptTokens = prompt, CompletionTokens = completion };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI call threw");
            return new AiResponse { Ok = false, Error = $"KI-Aufruf fehlgeschlagen: {ex.Message}" };
        }
    }

    private static string NormalizeRole(string? r) => r is "system" or "assistant" ? r! : "user";
    private static string Trunc(string s) => s.Length > 600 ? s[..600] : s;

    private static string ProviderError(string raw)
    {
        try
        {
            using var d = JsonDocument.Parse(raw);
            if (d.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                return m.GetString() ?? "";
        }
        catch { /* not JSON */ }
        return Trunc(raw);
    }
}
