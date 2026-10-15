using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Shop;

/// <summary>PayPal answered with an error (or not at all). <see cref="Message"/> is PayPal's own sentence, safe to show an operator; a customer gets a general text.</summary>
internal sealed class PayPalException(string message, int status, string code) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// PayPal's REST interface (Orders v2), nothing more: the client-credentials token, a JSON POST and a GET. No SDK — the shop makes five calls, and the SDK's types would sit
/// in the CMS image for everybody. A test replaces the transport with <see cref="HandlerFactory"/> and talks to no server.
/// </summary>
internal sealed class PayPalClient
{
    /// <summary>Gives the message handler to use instead of a real connection (tests only).</summary>
    public static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    private static readonly HttpClient Shared = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Access tokens live about nine hours; one per account and mode is kept (a second before it ends it is asked for again).</summary>
    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> Tokens = new();

    private readonly string _id, _secret;
    private readonly bool _sandbox;

    public PayPalClient(string clientId, string secret, bool sandbox) { _id = clientId; _secret = secret; _sandbox = sandbox; }

    public string ApiBase => _sandbox ? "https://api-m.sandbox.paypal.com" : "https://api-m.paypal.com";
    private HttpClient Http() => HandlerFactory is { } f ? new HttpClient(f(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) } : Shared;
    private string CacheKey => (_sandbox ? "s:" : "l:") + _id;

    /// <summary>Forgets every kept token (a test starts from none).</summary>
    public static void ForgetTokens() => Tokens.Clear();

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (Tokens.TryGetValue(CacheKey, out var kept) && kept.Until > DateTimeOffset.UtcNow.AddSeconds(30)) return kept.Token;
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/v1/oauth2/token") { Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_id + ":" + _secret)));
        req.Headers.UserAgent.ParseAdd("MatCMS-Shop/1");
        var json = await SendRawAsync(req, ct);
        var token = json["access_token"]?.GetValue<string>() ?? "";
        if (token.Length == 0) throw new PayPalException("PayPal hat kein Zugriffstoken geschickt.", 0, "no_token");
        var seconds = json["expires_in"] is JsonValue v && v.TryGetValue<long>(out var s) ? s : 300;
        Tokens[CacheKey] = (token, DateTimeOffset.UtcNow.AddSeconds(seconds));
        return token;
    }

    public Task<JsonObject> PostAsync(string path, JsonNode? body, string? requestId = null, CancellationToken ct = default) => SendAsync(HttpMethod.Post, path, body ?? new JsonObject(), requestId, ct);
    public Task<JsonObject> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, null, ct);

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonNode? body, string? requestId, CancellationToken ct)
    {
        var token = await TokenAsync(ct);
        using var req = new HttpRequestMessage(method, ApiBase + path);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.UserAgent.ParseAdd("MatCMS-Shop/1");
        if (!string.IsNullOrEmpty(requestId)) req.Headers.Add("PayPal-Request-Id", requestId);
        try { return await SendRawAsync(req, ct); }
        catch (PayPalException ex) when (ex.Status == 401)
        {
            Tokens.TryRemove(CacheKey, out _);          // the token was withdrawn early: the next call asks for a new one
            throw;
        }
    }

    private async Task<JsonObject> SendRawAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var http = Http();
        try
        {
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            JsonObject? json = null;
            try { json = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { /* a proxy's HTML error page */ }
            if (res.IsSuccessStatusCode && json is not null) return json;
            var issue = (json?["details"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var message = json?["message"]?.GetValue<string>() ?? json?["error_description"]?.GetValue<string>() ?? $"PayPal antwortete mit HTTP {(int)res.StatusCode}.";
            if (issue?["description"]?.GetValue<string>() is { Length: > 0 } detail && !message.Contains(detail, StringComparison.Ordinal)) message += " " + detail;
            throw new PayPalException(message, (int)res.StatusCode, issue?["issue"]?.GetValue<string>() ?? json?["name"]?.GetValue<string>() ?? json?["error"]?.GetValue<string>() ?? "");
        }
        catch (HttpRequestException ex) { throw new PayPalException("PayPal ist nicht erreichbar: " + ex.Message, 0, "network"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new PayPalException("PayPal antwortet nicht (Zeitüberschreitung).", 0, "timeout"); }
        finally { if (HandlerFactory is not null) http.Dispose(); }
    }

    /// <summary>
    /// Asks PayPal whether a notification is genuine: PayPal's own check of the five transmission headers against the webhook's id and the event as it was sent. (PayPal signs
    /// with a certificate it publishes; letting PayPal do the verification means no certificate chain is handled here.) Anything but <c>SUCCESS</c> is a no.
    /// </summary>
    public async Task<bool> VerifyWebhookAsync(IReadOnlyDictionary<string, string> headers, string webhookId, string rawBody, CancellationToken ct = default)
    {
        string H(string name) => headers.TryGetValue(name, out var v) ? v : "";
        if (webhookId.Length == 0 || H("PAYPAL-TRANSMISSION-ID").Length == 0 || H("PAYPAL-TRANSMISSION-SIG").Length == 0 || H("PAYPAL-CERT-URL").Length == 0) return false;
        JsonNode? evt;
        try { evt = JsonNode.Parse(rawBody); } catch (JsonException) { return false; }
        if (evt is not JsonObject) return false;
        // The certificate must come from PayPal: a sender who names his own address would be vouching for himself.
        if (!Uri.TryCreate(H("PAYPAL-CERT-URL"), UriKind.Absolute, out var cert) || cert.Scheme != Uri.UriSchemeHttps || !(cert.Host == "paypal.com" || cert.Host.EndsWith(".paypal.com", StringComparison.OrdinalIgnoreCase))) return false;
        var body = new JsonObject
        {
            ["auth_algo"] = H("PAYPAL-AUTH-ALGO"), ["cert_url"] = H("PAYPAL-CERT-URL"), ["transmission_id"] = H("PAYPAL-TRANSMISSION-ID"),
            ["transmission_sig"] = H("PAYPAL-TRANSMISSION-SIG"), ["transmission_time"] = H("PAYPAL-TRANSMISSION-TIME"), ["webhook_id"] = webhookId, ["webhook_event"] = evt.DeepClone()
        };
        try { return (await PostAsync("/v1/notifications/verify-webhook-signature", body, null, ct))["verification_status"]?.GetValue<string>() == "SUCCESS"; }
        catch (PayPalException) { return false; }
    }
}
