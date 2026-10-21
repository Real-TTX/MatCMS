using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatCMS.Shared.Ai;

/// <summary>
/// One call to an OpenAI-compatible <c>/chat/completions</c> endpoint (OpenAI, DeepSeek, any compatible server) with an API key.
/// Shared by the cloud (its provider for every connected site) and the CMS (a site's OWN key, without a cloud) — one
/// implementation of the traps below, not two that drift. Never throws: failures come back as <c>Ok=false</c> with a reason.
/// <para>What providers differ in, learned per call: the name of the length cap (newer OpenAI models want
/// <c>max_completion_tokens</c>), whether they take a temperature (reasoning models do not), JSON mode, and pictures (DeepSeek
/// reads text only). A refusal that names one of these is retried without it — each switch flips once. An EMPTY answer is a
/// failure that says why: a reasoning model spends the length cap on thinking first, and a too-small cap used to come back as a
/// "successful" empty text.</para>
/// </summary>
public static class ChatCompletionsClient
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    public static async Task<AiResponse> CompleteAsync(HttpClient client, string baseUrl, string apiKey, string model, AiRequest req,
        int maxCompletionTokens, Action<string>? warn = null, CancellationToken ct = default)
    {
        if (req.Messages is null || req.Messages.Count == 0) return new AiResponse { Ok = false, Error = "Leere KI-Anfrage." };
        baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).Trim().TrimEnd('/');
        var maxTokens = Math.Clamp(req.MaxTokens ?? 2000, 16, maxCompletionTokens);
        var useCompletionTokens = false;
        var useTemperature = true;
        var useJson = req.Json;
        var useAttachments = req.Messages.Any(m => m.Attachments is { Count: > 0 });
        string? note = null;
        try
        {
            // AI completions routinely exceed a short timeout, and a reasoning model writing a whole page thinks first.
            client.Timeout = TimeSpan.FromSeconds(280);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var payload = new JsonObject
                {
                    ["model"] = model,
                    ["messages"] = BuildMessages(req.Messages, useAttachments),
                    [useCompletionTokens ? "max_completion_tokens" : "max_tokens"] = maxTokens,
                };
                if (useTemperature) payload["temperature"] = 0.4;
                if (useJson) payload["response_format"] = new JsonObject { ["type"] = "json_object" };

                using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
                httpReq.Headers.Add("Authorization", $"Bearer {apiKey}");
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
                    warn?.Invoke($"AI call failed: {status} {Trunc(raw)}");
                    return new AiResponse
                    {
                        Ok = false, Model = model,
                        Error = status == 401 ? "Der API-Schlüssel wurde abgelehnt (HTTP 401)." : $"Anbieter-Fehler {status}: {why}",
                    };
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
                if (string.IsNullOrWhiteSpace(text))
                    return new AiResponse
                    {
                        Ok = false, PromptTokens = prompt, CompletionTokens = completion, Model = model,
                        Error = finish == "length"
                            ? "Die KI hat ihr Längenlimit erreicht, bevor sie antworten konnte (bei Denk-Modellen zählt das Nachdenken mit)."
                            : "Die KI hat eine leere Antwort geliefert.",
                    };
                if (finish == "length")
                    note = (note is null ? "" : note + " ") + "Die Antwort wurde am Längenlimit abgeschnitten.";
                return new AiResponse { Ok = true, Text = text, PromptTokens = prompt, CompletionTokens = completion, Model = model, Note = note };
            }
            return new AiResponse { Ok = false, Error = "Der KI-Anbieter hat die Anfrage wiederholt abgelehnt.", Model = model };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            warn?.Invoke("AI call threw: " + ex.Message);
            return new AiResponse
            {
                Ok = false, Model = model,
                Error = ex is TaskCanceledException ? "Die KI hat nicht rechtzeitig geantwortet." : $"KI-Aufruf fehlgeschlagen: {ex.Message}",
            };
        }
    }

    /// <summary>The messages in chat-completions form. A message with files becomes a list of parts (its text, then each picture as
    /// a data URL, each PDF as a file part); without, or with files switched off, it stays a plain string — the one shape every
    /// OpenAI-compatible provider understands. Switched off, a picture leaves a line saying it was there.</summary>
    public static JsonArray BuildMessages(IEnumerable<AiMessage> messages, bool withAttachments)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            var role = m.Role is "system" or "assistant" ? m.Role : "user";
            var files = m.Attachments ?? new();
            if (files.Count == 0 || role != "user")
            {
                arr.Add(new JsonObject { ["role"] = role, ["content"] = m.Content ?? "" });
                continue;
            }
            if (!withAttachments)
            {
                var names = string.Join(", ", files.Select(f => f.Name));
                arr.Add(new JsonObject { ["role"] = role, ["content"] = (m.Content ?? "") + $"\n\n[Angehängt, für dieses Modell nicht lesbar: {names}]" });
                continue;
            }
            var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Content ?? "" } };
            foreach (var f in files)
            {
                var dataUrl = $"data:{f.MediaType};base64,{f.Data}";
                parts.Add(f.Kind == "file"
                    ? new JsonObject { ["type"] = "file", ["file"] = new JsonObject { ["filename"] = f.Name, ["file_data"] = dataUrl } }
                    : new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUrl } });
            }
            arr.Add(new JsonObject { ["role"] = role, ["content"] = parts });
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

    private static string Trunc(string s) => s.Length > 600 ? s[..600] : s;

    private static string ProviderError(string raw)
    {
        try
        {
            using var d = JsonDocument.Parse(raw);
            if (d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m))
                return m.GetString() ?? "";
        }
        catch (JsonException) { /* not JSON */ }
        return Trunc(raw);
    }
}
