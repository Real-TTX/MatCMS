using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Shop;

/// <summary>Stripe answered with an error (or not at all). <see cref="Message"/> is Stripe's own sentence, safe to show an operator; a customer gets a general text.</summary>
internal sealed class StripeException(string message, int status, string code) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// Stripe's REST interface, nothing more: a form-encoded POST and a GET, with the secret key as a bearer token. No SDK — the calls the shop makes are four, the SDK
/// is large, and its types would sit in the CMS image for everybody. A test replaces the transport with <see cref="HandlerFactory"/> and talks to no server.
/// </summary>
internal sealed class StripeClient
{
    /// <summary>The API address. A test points it at a fake.</summary>
    public static string ApiBase { get; set; } = "https://api.stripe.com";

    /// <summary>Gives the message handler to use instead of a real connection (tests only).</summary>
    public static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    private static readonly HttpClient Shared = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _key;

    public StripeClient(string secretKey) => _key = secretKey;

    private HttpClient Http() => HandlerFactory is { } f ? new HttpClient(f(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) } : Shared;

    public Task<JsonObject> PostAsync(string path, IEnumerable<KeyValuePair<string, string>> form, string? idempotencyKey = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, new FormUrlEncodedContent(form), idempotencyKey, ct);

    public Task<JsonObject> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, null, ct);

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, HttpContent? body, string? idempotencyKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, ApiBase.TrimEnd('/') + path) { Content = body };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        req.Headers.UserAgent.ParseAdd("MatCMS-Shop/1");
        if (!string.IsNullOrEmpty(idempotencyKey)) req.Headers.Add("Idempotency-Key", idempotencyKey);
        var http = Http();
        try
        {
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            JsonObject? json = null;
            try { json = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { /* a proxy's HTML error page */ }
            if (res.IsSuccessStatusCode && json is not null) return json;
            var err = json?["error"] as JsonObject;
            throw new StripeException(err?["message"]?.GetValue<string>() ?? $"Stripe antwortete mit HTTP {(int)res.StatusCode}.", (int)res.StatusCode, err?["code"]?.GetValue<string>() ?? "");
        }
        catch (HttpRequestException ex) { throw new StripeException("Stripe ist nicht erreichbar: " + ex.Message, 0, "network"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new StripeException("Stripe antwortet nicht (Zeitüberschreitung).", 0, "timeout"); }
        finally { if (HandlerFactory is not null) http.Dispose(); }
    }

    /// <summary>Checks a webhook: <c>Stripe-Signature: t=…,v1=…</c> is the HMAC-SHA256 of <c>t + "." + body</c> with the endpoint's signing secret. The comparison is
    /// constant-time, and a timestamp more than <paramref name="toleranceSeconds"/> away is refused (a recorded notification cannot be replayed later).</summary>
    public static bool VerifySignature(string header, byte[] body, string secret, DateTimeOffset now, int toleranceSeconds = 300)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret)) return false;
        long? timestamp = null; var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t" && long.TryParse(kv[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var t)) timestamp = t;
            else if (kv[0].Trim() == "v1") signatures.Add(kv[1].Trim());
        }
        if (timestamp is null || signatures.Count == 0) return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp.Value) > toleranceSeconds) return false;

        var payload = Encoding.UTF8.GetBytes(timestamp.Value.ToString(CultureInfo.InvariantCulture) + ".").Concat(body).ToArray();
        var expected = System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        foreach (var sig in signatures)
        {
            byte[] given;
            try { given = Convert.FromHexString(sig); } catch (FormatException) { continue; }
            if (given.Length == expected.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(given, expected)) return true;
        }
        return false;
    }

    /// <summary>The header a Stripe-like sender would put on a body (tests, and nothing else, call this).</summary>
    public static string Sign(byte[] body, string secret, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var payload = Encoding.UTF8.GetBytes(t + ".").Concat(body).ToArray();
        return $"t={t},v1={Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)).ToLowerInvariant()}";
    }
}
