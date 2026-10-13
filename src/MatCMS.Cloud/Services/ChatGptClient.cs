using System.Text;
using System.Text.Json;
using MatCMS.Shared;

namespace MatCMS.Cloud.Services;

/// <summary>
/// One AI call through the signed-in ChatGPT account (<see cref="ChatGptAuthService"/>): the Responses API of the Codex backend
/// (<c>chatgpt.com/backend-api/codex/responses</c>), answered as a server-sent-event stream that is collected here into the same
/// <see cref="AiResponse"/> the API-key provider returns. Never throws; every failure comes back as <c>Ok=false</c> with a reason.
///
/// <para>What the backend insists on, all learned from Codex and Hermes: <c>stream:true</c> and <c>store:false</c> (anything else is
/// refused), the system prompt as <c>instructions</c> (a system-role input item is refused), the whole history in <c>input</c> on
/// every call, and none of the sampling or length parameters the chat-completions provider sends — so, unlike that provider, a cap on
/// the completion length cannot be passed on here.</para>
/// </summary>
public class ChatGptClient
{
    private readonly ChatGptAuthService _auth;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ChatGptClient> _log;

    public ChatGptClient(ChatGptAuthService auth, IHttpClientFactory http, ILogger<ChatGptClient> log)
    {
        _auth = auth; _http = http; _log = log;
    }

    public async Task<AiResponse> CompleteAsync(string model, IReadOnlyList<AiMessage> messages, CancellationToken ct = default)
    {
        try
        {
            var body = BuildBody(model, messages);
            // A token the server refuses although it looked valid (revoked, rotated by another client) gets exactly one forced renewal.
            for (var attempt = 0; ; attempt++)
            {
                var (token, accountId, error) = await _auth.GetAccessTokenAsync(ct, force: attempt > 0);
                if (token is null) return Fail(error ?? "Nicht bei ChatGPT angemeldet.");

                var client = _http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(100);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_auth.Backend}/responses")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                ChatGptAuthService.ApplyBackendHeaders(req, token, accountId);
                req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode) return await ReadStreamAsync(resp, ct);

                var raw = await resp.Content.ReadAsStringAsync(ct);
                var status = (int)resp.StatusCode;
                if (status == 401 && attempt == 0) continue;
                _log.LogWarning("ChatGPT call failed: {Status} {Body}", status, raw.Length > 600 ? raw[..600] : raw);
                return Fail(status switch
                {
                    401 or 403 => $"ChatGPT hat den Zugriff abgelehnt (HTTP {status}) – bitte unter Einstellungen → KI neu anmelden.",
                    429 => "Das Kontingent des ChatGPT-Kontos ist gerade erreicht: " + ChatGptAuthService.ErrorText(raw),
                    _ => $"ChatGPT-Fehler {status}: {ChatGptAuthService.ErrorText(raw)}",
                });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            // A refused or dropped connection: .NET's own text is English and OS-localised — the operator gets one plain German sentence.
            _log.LogWarning(ex, "ChatGPT connection failed");
            return Fail("Die Verbindung zu ChatGPT ist fehlgeschlagen oder wurde unterbrochen. Bitte später erneut versuchen.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "ChatGPT call threw");
            return Fail($"KI-Aufruf fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>The request document. System messages become <c>instructions</c> (required, so a call without one gets a neutral
    /// default); the rest become input items — user text is <c>input_text</c>, earlier assistant text <c>output_text</c>.</summary>
    internal static string BuildBody(string model, IReadOnlyList<AiMessage> messages)
    {
        var instructions = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content ?? "").Where(s => s.Length > 0));
        var items = messages.Where(m => m.Role != "system")
            .Select(m => (Assistant: m.Role == "assistant", Text: m.Content ?? ""))
            .ToList();
        if (items.Count == 0) { items.Add((false, instructions)); instructions = ""; }   // only a system message: it is the task
        if (instructions.Length == 0) instructions = "You are a helpful assistant.";

        return JsonSerializer.Serialize(new
        {
            model,
            instructions,
            input = items.Select(i => new
            {
                type = "message",
                role = i.Assistant ? "assistant" : "user",
                content = new[] { new { type = i.Assistant ? "output_text" : "input_text", text = i.Text } },
            }).ToArray(),
            store = false,
            stream = true,
        });
    }

    /// <summary>Collects the event stream. A call counts as successful only after <c>response.completed</c>; text that arrived before an
    /// interrupted stream or a <c>response.failed</c> is thrown away rather than returned as if it were the whole answer.</summary>
    private static async Task<AiResponse> ReadStreamAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        string? failure = null, completedText = null;
        var completed = false;
        int prompt = 0, completion = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                switch (type)
                {
                    case "response.output_text.delta":
                        if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String) text.Append(d.GetString());
                        break;
                    case "response.completed":
                        completed = true;
                        if (root.TryGetProperty("response", out var r))
                        {
                            if (r.TryGetProperty("usage", out var u))
                            {
                                prompt = Int(u, "input_tokens");
                                completion = Int(u, "output_tokens");
                            }
                            completedText = OutputText(r);
                        }
                        break;
                    case "response.failed":
                    case "response.incomplete":
                    case "error":
                        failure ??= FailureText(root, type);
                        break;
                }
            }
            if (completed) break;
        }

        if (failure is not null) return Fail(failure);
        if (!completed) return Fail("Die Antwort von ChatGPT wurde unterbrochen.");
        var answer = text.Length > 0 ? text.ToString() : completedText ?? "";
        return new AiResponse { Ok = true, Text = answer, PromptTokens = prompt, CompletionTokens = completion };
    }

    private static int Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>The text of a finished response, for the case that no delta events came (some answers only arrive whole).</summary>
    private static string? OutputText(JsonElement response)
    {
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return null;
        var sb = new StringBuilder();
        foreach (var item in output.EnumerateArray())
            if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                    if (part.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String) sb.Append(txt.GetString());
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static string FailureText(JsonElement root, string? type)
    {
        var holder = root.TryGetProperty("response", out var r) ? r : root;
        string? message = null, code = null;
        if (holder.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
        {
            message = e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            code = e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        }
        message ??= root.TryGetProperty("message", out var rm) && rm.ValueKind == JsonValueKind.String ? rm.GetString() : null;
        if (type == "response.incomplete" && message is null)
            message = holder.TryGetProperty("incomplete_details", out var det) && det.TryGetProperty("reason", out var why) ? "unvollständig: " + why.GetString() : "unvollständig";
        return "ChatGPT hat die Antwort nicht geliefert: " + (message ?? code ?? "unbekannter Fehler") + (code is not null && message is not null ? $" ({code})" : "");
    }

    private static AiResponse Fail(string error) => new() { Ok = false, Error = error };
}
