using System.Text;
using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
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
    private readonly ChatGptClient _chatgpt;
    private readonly ChatGptAuthService _chatgptAuth;

    /// <summary>Hard ceiling on completion length regardless of what an instance asks for — bounds the
    /// cost of a single call. Generous on purpose: a reasoning model (DeepSeek, o-series, gpt-5) spends part of
    /// this on thinking before it writes a word, and a cap that is too tight comes back as an EMPTY answer.</summary>
    private const int MaxCompletionTokens = 16000;

    /// <summary>At most this many files per call, each at most this many base64 characters (~10 MB of file).</summary>
    private const int MaxAttachments = 8;
    private const int MaxAttachmentChars = 14_000_000;
    private static readonly string[] AttachmentTypes = ["image/jpeg", "image/png", "image/webp", "image/gif", "application/pdf"];

    public AiService(AppDbContext db, SecretProtector secrets, IHttpClientFactory http, ILogger<AiService> log,
        ChatGptClient chatgpt, ChatGptAuthService chatgptAuth)
    {
        _db = db;
        _secrets = secrets;
        _http = http;
        _log = log;
        _chatgpt = chatgpt;
        _chatgptAuth = chatgptAuth;
    }

    /// <summary>Provider id of the ChatGPT sign-in ("Mit ChatGPT anmelden"): the account, not an API key, pays for the calls.</summary>
    public const string ProviderChatGpt = "chatgpt";

    public sealed record AiConfig(string Provider, string Model, string BaseUrl, string ApiKey, bool ChatGptConnected = false)
    {
        public bool IsChatGpt => Provider == ProviderChatGpt;
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Model) && (IsChatGpt ? ChatGptConnected : !string.IsNullOrWhiteSpace(ApiKey));
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
        // The ChatGPT account only offers its own models — gpt-4o-mini is the API provider default and would simply be refused there.
        if (model.Length == 0 && provider != ProviderChatGpt) model = "gpt-4o-mini";
        var baseUrl = G(SettingKeys.AiBaseUrl).Trim().TrimEnd('/');
        if (baseUrl.Length == 0) baseUrl = "https://api.openai.com/v1";
        // Stored encrypted (SecretProtector); an unmarked legacy value passes through unchanged.
        var key = _secrets.Unprotect(G(SettingKeys.AiApiKey)) ?? "";
        return new AiConfig(provider, model, baseUrl, key, provider == ProviderChatGpt && await _chatgptAuth.IsConnectedAsync());
    }

    public async Task<bool> IsConfiguredAsync() => (await GetConfigAsync()).IsConfigured;

    /// <summary>A short call with the saved provider, to see that it works: the page's "KI testen", REST and MCP. Not booked to any
    /// instance's budget — only <see cref="RelayAsync"/> books.</summary>
    public Task<AiResponse> TestAsync(CancellationToken ct = default) => CompleteAsync(new AiRequest
    {
        Purpose = "test",
        // Room for a reasoning model to think before its one sentence; 60 came back empty from DeepSeek.
        MaxTokens = 1500,
        Messages =
        {
            new AiMessage { Role = "system", Content = "Antworte in einem kurzen deutschen Satz." },
            new AiMessage { Role = "user", Content = "Sag Hallo und nenne den Namen des Modells, das gerade antwortet." },
        },
    }, ct);

    /// <summary>
    /// One call relayed by an instance, with the checks that come before it: the instance must be approved, and the profile must have AI
    /// switched on (a valid token is not permission to spend the key), and the instance's monthly token budget (0/null = unlimited; the
    /// cloud grants it, the instance never sees the number) must not be used up. Only what the provider actually reported is booked.
    /// A refusal comes back as <c>Ok=false</c> with a reason, so the instance can tell its operator why.
    /// </summary>
    public async Task<AiResponse> RelayAsync(Instance instance, AiRequest req, CancellationToken ct = default)
    {
        if (instance.Status != InstanceStatus.Approved)
            return new AiResponse { Ok = false, Error = "Instanz ist nicht freigegeben." };
        if (instance.Profile is null || !instance.Profile.SyncAi)
            return new AiResponse { Ok = false, Error = "KI ist für dieses Profil nicht aktiviert." };

        var period = DateTime.UtcNow.ToString("yyyy-MM");
        var usage = await _db.AiUsages.FirstOrDefaultAsync(u => u.InstanceId == instance.Id && u.Period == period);
        var budget = instance.Profile.AiMonthlyTokenBudget ?? 0;
        if (budget > 0 && (usage?.Tokens ?? 0) >= budget)
            return new AiResponse { Ok = false, Error = "Das monatliche KI-Budget dieser Instanz ist aufgebraucht." };

        // The files are the instance's to send but the cloud's to pay for: bounded here, whatever the request says.
        foreach (var m in req.Messages)
            m.Attachments = (m.Attachments ?? new())
                .Where(a => a is not null && AttachmentTypes.Contains(a.MediaType) && a.Data.Length is > 0 and <= MaxAttachmentChars)
                .Take(MaxAttachments).ToList();
        var res = await CompleteAsync(req, ct);

        if (res.Ok)
        {
            if (usage is null)
            {
                usage = new AiUsage { InstanceId = instance.Id, Period = period };
                _db.AiUsages.Add(usage);
            }
            usage.Tokens += res.PromptTokens + res.CompletionTokens;
            usage.Calls += 1;
            usage.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return res;
    }

    /// <summary>Runs a chat completion with the saved config. Never throws.</summary>
    public async Task<AiResponse> CompleteAsync(AiRequest req, CancellationToken ct = default)
    {
        var cfg = await GetConfigAsync();
        // A signed-in account that died (revoked, refresh token used elsewhere) must say so to EVERY caller — not only to the one
        // that found out — or the operator of a site sees "not configured" and has no idea where to look.
        if (cfg.IsChatGpt && !cfg.ChatGptConnected && !string.IsNullOrWhiteSpace(cfg.Model))
        {
            var (_, _, why) = await _chatgptAuth.GetAccessTokenAsync(ct);
            return new AiResponse { Ok = false, Error = why ?? "Nicht bei ChatGPT angemeldet." };
        }
        if (!cfg.IsConfigured)
            return new AiResponse { Ok = false, Error = cfg.IsChatGpt
                ? "KI ist nicht konfiguriert (ChatGPT-Anmeldung und Modell erforderlich)."
                : "KI ist nicht konfiguriert (Anbieter-Schlüssel und Modell erforderlich)." };
        if (req.Messages is null || req.Messages.Count == 0)
            return new AiResponse { Ok = false, Error = "Leere KI-Anfrage." };

        // The signed-in ChatGPT account: its backend takes no length cap, so MaxTokens is not passed on there.
        if (cfg.IsChatGpt)
        {
            var answer = await _chatgpt.CompleteAsync(cfg.Model, req.Messages, ct);
            answer.Model ??= cfg.Model;
            return answer;
        }

        var maxTokens = Math.Clamp(req.MaxTokens ?? 2000, 16, MaxCompletionTokens);
        // What the provider may refuse, learned per call: providers differ in the name of the length cap (newer OpenAI
        // models want max_completion_tokens), in whether they take a temperature at all (reasoning models do not), in JSON
        // mode and in pictures (DeepSeek reads text only). A refusal that names one of these is retried without it rather
        // than failing the whole action — each switch flips once, so this ends after a handful of attempts at most.
        var useCompletionTokens = false;
        var useTemperature = true;
        var useJson = req.Json;
        var useAttachments = req.Messages.Any(m => m.Attachments is { Count: > 0 });
        string? note = null;
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var payload = new System.Text.Json.Nodes.JsonObject
                {
                    ["model"] = cfg.Model,
                    ["messages"] = BuildMessages(req.Messages, useAttachments),
                    [useCompletionTokens ? "max_completion_tokens" : "max_tokens"] = maxTokens,
                };
                if (useTemperature) payload["temperature"] = 0.4;
                if (useJson) payload["response_format"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "json_object" };

                var client = _http.CreateClient();
                // AI completions routinely exceed the 30s mail cap, and a reasoning model writing a whole page thinks first.
                client.Timeout = TimeSpan.FromSeconds(280);
                using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{cfg.BaseUrl}/chat/completions");
                httpReq.Headers.Add("Authorization", $"Bearer {cfg.ApiKey}");
                httpReq.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

                using var resp = await client.SendAsync(httpReq, ct);
                var raw = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var status = (int)resp.StatusCode;
                    var why = ProviderError(raw);
                    var lower = why.ToLowerInvariant();
                    if (status is 400 or 422)
                    {
                        if (!useCompletionTokens && lower.Contains("max_tokens")) { useCompletionTokens = true; continue; }
                        if (useTemperature && lower.Contains("temperature")) { useTemperature = false; continue; }
                        if (useJson && (lower.Contains("response_format") || lower.Contains("json"))) { useJson = false; continue; }
                        if (useAttachments && (lower.Contains("image") || lower.Contains("file") || lower.Contains("variant") || lower.Contains("content")))
                        {
                            useAttachments = false;
                            note = "Dieses Modell kann angehängte Bilder und PDFs nicht lesen – sie wurden weggelassen.";
                            continue;
                        }
                    }
                    _log.LogWarning("AI call failed: {Status} {Body}", status, Trunc(raw));
                    return new AiResponse { Ok = false, Error = $"Anbieter-Fehler {status}: {why}", Model = cfg.Model };
                }

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var choice = root.GetProperty("choices")[0];
                var text = ContentText(choice.GetProperty("message"));
                var finish = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;
                int prompt = 0, completion = 0;
                if (root.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number) prompt = p.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number) completion = c.GetInt32();
                }
                // An empty answer is a failure, not a success with nothing in it: every caller would otherwise report "the AI had
                // no suggestion". With a reasoning model it nearly always means the cap was spent on thinking.
                if (string.IsNullOrWhiteSpace(text))
                    return new AiResponse
                    {
                        Ok = false, PromptTokens = prompt, CompletionTokens = completion, Model = cfg.Model,
                        Error = finish == "length"
                            ? "Die KI hat ihr Längenlimit erreicht, bevor sie antworten konnte (bei Denk-Modellen zählt das Nachdenken mit)."
                            : "Die KI hat eine leere Antwort geliefert.",
                    };
                if (finish == "length")
                    note = (note is null ? "" : note + " ") + "Die Antwort wurde am Längenlimit abgeschnitten.";
                return new AiResponse { Ok = true, Text = text, PromptTokens = prompt, CompletionTokens = completion, Model = cfg.Model, Note = note };
            }
            return new AiResponse { Ok = false, Error = "Der KI-Anbieter hat die Anfrage wiederholt abgelehnt.", Model = cfg.Model };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "AI call threw");
            return new AiResponse { Ok = false, Error = ex is TaskCanceledException
                ? "Die KI hat nicht rechtzeitig geantwortet."
                : $"KI-Aufruf fehlgeschlagen: {ex.Message}", Model = cfg.Model };
        }
    }

    /// <summary>The messages in chat-completions form. A message with files becomes a list of parts (its text, then each
    /// picture as a data URL, each PDF as a file part); without, or with files switched off, it stays a plain string — the
    /// one shape every OpenAI-compatible provider understands. Switched off, a picture leaves a line saying it was there.</summary>
    private static System.Text.Json.Nodes.JsonArray BuildMessages(IEnumerable<AiMessage> messages, bool withAttachments)
    {
        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var m in messages)
        {
            var role = NormalizeRole(m.Role);
            var files = m.Attachments ?? new();
            if (files.Count == 0 || role != "user")
            {
                arr.Add(new System.Text.Json.Nodes.JsonObject { ["role"] = role, ["content"] = m.Content ?? "" });
                continue;
            }
            if (!withAttachments)
            {
                var names = string.Join(", ", files.Select(f => f.Name));
                arr.Add(new System.Text.Json.Nodes.JsonObject { ["role"] = role, ["content"] = (m.Content ?? "") + $"\n\n[Angehängt, für dieses Modell nicht lesbar: {names}]" });
                continue;
            }
            var parts = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = m.Content ?? "" } };
            foreach (var f in files)
            {
                var dataUrl = $"data:{f.MediaType};base64,{f.Data}";
                parts.Add(f.Kind == "file"
                    ? new System.Text.Json.Nodes.JsonObject { ["type"] = "file", ["file"] = new System.Text.Json.Nodes.JsonObject { ["filename"] = f.Name, ["file_data"] = dataUrl } }
                    : new System.Text.Json.Nodes.JsonObject { ["type"] = "image_url", ["image_url"] = new System.Text.Json.Nodes.JsonObject { ["url"] = dataUrl } });
            }
            arr.Add(new System.Text.Json.Nodes.JsonObject { ["role"] = role, ["content"] = parts });
        }
        return arr;
    }

    /// <summary>The answer text of a chat-completions message: a string, or (some providers) a list of parts.</summary>
    private static string ContentText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in c.EnumerateArray())
            if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) sb.Append(t.GetString());
        return sb.ToString();
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
