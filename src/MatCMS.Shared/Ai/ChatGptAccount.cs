using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatCMS.Shared.Ai;

/// <summary>Where the ChatGPT sign-in talks to. The defaults are OpenAI's; a test can point an application at a local stand-in.</summary>
public sealed record ChatGptEndpoints(string Issuer, string BackendBase, int MinPollSeconds)
{
    public static ChatGptEndpoints Default { get; } = new("https://auth.openai.com", "https://chatgpt.com/backend-api/codex", 3);
}

/// <summary>What has to outlive one request: the running device login and the lock that serialises everything that talks to the
/// token endpoint. ONE per process (a singleton) — a refresh token may be used by exactly one caller at a time.</summary>
public sealed class ChatGptLoginState
{
    public SemaphoreSlim Lock { get; } = new(1, 1);
    public ChatGptAccount.PendingLogin? Pending { get; set; }
    public string? LastLoginError { get; set; }
    public int TransportErrors { get; set; }
    public DateTime ModelsUntil { get; set; }
    public IReadOnlyList<ChatGptAccount.ModelInfo> Models { get; set; } = Array.Empty<ChatGptAccount.ModelInfo>();
}

/// <summary>One <see cref="ChatGptLoginState"/> per signed-in account (a profile of its own each), kept for the application's life — the lock
/// that guards a refresh and a login under way must be the SAME object for every request about that account. Register as a singleton.</summary>
public sealed class ChatGptLoginStates
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChatGptLoginState> _states = new(StringComparer.Ordinal);
    public ChatGptLoginState For(string? account) => _states.GetOrAdd(account ?? "", _ => new ChatGptLoginState());
}

/// <summary>Where an application keeps the sign-in: ONE encrypted value it alone can read. The account only ever hands it the
/// token document as JSON (null = signed out) — encrypting, and keeping it out of every backup and copy, is the store's job.</summary>
public interface IChatGptTokenStore
{
    Task<string?> LoadAsync(CancellationToken ct);
    Task SaveAsync(string? json, CancellationToken ct);
}

/// <summary>
/// "Mit ChatGPT anmelden": an application signs in to a ChatGPT account the way OpenAI's Codex CLI and Hermes do — the OAuth
/// <b>device code</b> flow — and then uses that account for its AI calls instead of an API key. Used by the cloud (one account
/// for all its sites) and by the CMS (a site's own account, without a cloud); each brings its own token store and identity.
///
/// <para>The flow: ask OpenAI for a user code → the person types it on https://auth.openai.com/codex/device → polling until it is
/// confirmed → the authorization code is exchanged for tokens → the access token is renewed shortly before it expires. Calls go
/// to the Responses API of the Codex backend as a server-sent-event stream. The client id is the PUBLIC one of the Codex CLI;
/// OpenAI offers no registration for this, and the backend is undocumented — if OpenAI changes either, the sign-in stops and an
/// API key is the way out.</para>
///
/// <para>Traps, all load-bearing: refresh tokens ROTATE and may be used once — a second use answers <c>refresh_token_reused</c>
/// and the sign-in is dead (hence the lock, the re-read inside it, and that the tokens are never copied anywhere); a token the
/// server refused is marked <see cref="TokenSet.Dead"/> instead of being replayed; a transient failure (network, 5xx, 429) must
/// NOT kill the sign-in; OpenAI's edge answers HTTP 530 to a default library User-Agent, so every request names its application.
/// The backend takes no length cap, so <c>MaxTokens</c> is not passed on.</para>
/// </summary>
public class ChatGptAccount
{
    /// <summary>The public OAuth client id of OpenAI's Codex CLI (the same one Hermes uses).</summary>
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    private static readonly TimeSpan LoginLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(120);
    private const int MaxTransportErrors = 6;

    private readonly IChatGptTokenStore _store;
    private readonly Func<HttpClient> _http;
    private readonly ChatGptEndpoints _ep;
    private readonly ChatGptLoginState _state;
    private readonly Action<string>? _log;

    /// <param name="userAgent">The application's own name ("MatCMS-Cloud/1.14") — never a library default.</param>
    /// <param name="originator">The backend's originator header ("matcms-cloud").</param>
    /// <param name="settingsHint">Where the person signs in again, for the error texts ("Einstellungen → KI").</param>
    public ChatGptAccount(IChatGptTokenStore store, Func<HttpClient> http, ChatGptEndpoints ep, ChatGptLoginState state,
        string userAgent, string originator, string settingsHint = "Einstellungen → KI", Action<string>? log = null)
    {
        _store = store; _http = http; _ep = ep; _state = state; _log = log;
        UserAgent = userAgent; Originator = originator; _hint = settingsHint;
    }

    private readonly string _hint;
    public string UserAgent { get; }
    public string Originator { get; }
    public string Backend => _ep.BackendBase;

    // ---- shapes ----------------------------------------------------------------------------------------------------------

    /// <summary>The stored sign-in. <see cref="Dead"/> = the server refused the refresh token; the person has to sign in again.</summary>
    public sealed class TokenSet
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public string IdToken { get; set; } = "";
        public string AccountId { get; set; } = "";
        public string Email { get; set; } = "";
        public string Plan { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public DateTime LastRefresh { get; set; }
        public DateTime ConnectedAt { get; set; }
        public bool Dead { get; set; }
        public string? Error { get; set; }
    }

    public sealed class PendingLogin
    {
        public string DeviceAuthId { get; init; } = "";
        public string UserCode { get; init; } = "";
        public string VerificationUrl { get; init; } = "";
        public DateTime ExpiresAt { get; init; }
        public int IntervalSeconds { get; init; }
        public DateTime NextPollAt { get; set; }
    }

    public sealed record ModelInfo(string Slug, string Name);

    public sealed record Status(bool Connected, string? Email, string? AccountId, string? Plan, DateTime? AccessExpiresAt, DateTime? ConnectedAt,
        DateTime? LastRefresh, string? Error, PendingLogin? Pending, string? LoginError);

    /// <summary>The one JSON shape of a status for pages, REST and MCP. Never carries a token.</summary>
    public static object StatusJson(Status s, IEnumerable<ModelInfo>? models = null) => new
    {
        connected = s.Connected, email = s.Email, accountId = s.AccountId, plan = s.Plan, accessExpiresAt = s.AccessExpiresAt,
        connectedAt = s.ConnectedAt, lastRefresh = s.LastRefresh, error = s.Error, loginError = s.LoginError,
        pending = s.Pending is null ? null : new { userCode = s.Pending.UserCode, verificationUrl = s.Pending.VerificationUrl, expiresAt = s.Pending.ExpiresAt, intervalSeconds = s.Pending.IntervalSeconds },
        models = models?.Select(m => new { slug = m.Slug, name = m.Name }),
    };

    // ---- reading and writing the stored sign-in ------------------------------------------------------------------------

    private async Task<TokenSet?> LoadAsync(CancellationToken ct = default)
    {
        var json = await _store.LoadAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<TokenSet>(json); } catch (JsonException) { return null; }
    }

    private Task SaveAsync(TokenSet? t, CancellationToken ct = default) => _store.SaveAsync(t is null ? null : JsonSerializer.Serialize(t), ct);

    /// <summary>Whether a usable sign-in is stored.</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default) => await LoadAsync(ct) is { Dead: false };

    public async Task<Status> GetStatusAsync(bool poll, CancellationToken ct = default)
    {
        if (poll) await AdvanceLoginAsync(ct);
        var t = await LoadAsync(ct);
        return new Status(t is { Dead: false }, Null(t?.Email), Null(t?.AccountId), Null(t?.Plan),
            t?.ExpiresAt, t?.ConnectedAt, t is { LastRefresh.Year: > 1 } ? t.LastRefresh : null, t?.Error, _state.Pending, _state.LastLoginError);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _state.Lock.WaitAsync(ct);
        try
        {
            _state.Pending = null; _state.LastLoginError = null; _state.Models = Array.Empty<ModelInfo>(); _state.ModelsUntil = default;
            await SaveAsync(null, ct);
        }
        finally { _state.Lock.Release(); }
    }

    // ---- login: user code → confirm → tokens -----------------------------------------------------------------------------

    /// <summary>Asks OpenAI for a user code. The person types it on <see cref="PendingLogin.VerificationUrl"/>; a later
    /// <see cref="GetStatusAsync"/> with <c>poll</c> notices the confirmation. Starting again replaces a running login.</summary>
    public async Task<(PendingLogin? Pending, string? Error)> StartLoginAsync(CancellationToken ct = default)
    {
        await _state.Lock.WaitAsync(ct);
        try
        {
            var r = await SendAsync(HttpMethod.Post, $"{_ep.Issuer}/api/accounts/deviceauth/usercode", Json(new { client_id = ClientId }), ct);
            if (r.NetError is not null) return (null, "OpenAI ist nicht erreichbar: " + r.NetError);
            if (r.Status == 404) return (null, "Die Geräte-Anmeldung ist bei OpenAI für diesen Zugang nicht aktiviert.");
            if (r.Status == 429) return (null, "OpenAI bremst gerade (zu viele Anfragen). Bitte in einer Minute erneut versuchen.");
            if (r.Status != 200) return (null, $"OpenAI hat die Anmeldung nicht gestartet (HTTP {r.Status}).");

            using var doc = ParseJson(r.Body);
            var root = doc?.RootElement;
            var deviceAuthId = Str(root, "device_auth_id");
            var userCode = Str(root, "user_code") ?? Str(root, "usercode");
            if (string.IsNullOrEmpty(deviceAuthId) || string.IsNullOrEmpty(userCode))
                return (null, "OpenAI hat keinen Anmelde-Code geliefert (unerwartete Antwort).");
            var interval = Math.Max(_ep.MinPollSeconds, IntervalOf(root));
            _state.Pending = new PendingLogin
            {
                DeviceAuthId = deviceAuthId, UserCode = userCode, VerificationUrl = $"{_ep.Issuer}/codex/device",
                ExpiresAt = DateTime.UtcNow + LoginLifetime, IntervalSeconds = interval, NextPollAt = DateTime.UtcNow.AddSeconds(interval),
            };
            _state.LastLoginError = null; _state.TransportErrors = 0;
            return (_state.Pending, null);
        }
        finally { _state.Lock.Release(); }
    }

    /// <summary>One step of a running login: at most ONE request to OpenAI, and only once its poll interval has passed — however
    /// often a page asks.</summary>
    private async Task AdvanceLoginAsync(CancellationToken ct)
    {
        var p = _state.Pending;
        if (p is null) return;
        if (DateTime.UtcNow >= p.ExpiresAt) { _state.Pending = null; _state.LastLoginError = "Der Code ist abgelaufen. Bitte die Anmeldung neu starten."; return; }
        if (DateTime.UtcNow < p.NextPollAt) return;

        await _state.Lock.WaitAsync(ct);
        try
        {
            p = _state.Pending;
            if (p is null || DateTime.UtcNow < p.NextPollAt) return;   // someone else polled while we waited
            p.NextPollAt = DateTime.UtcNow.AddSeconds(p.IntervalSeconds);

            var r = await SendAsync(HttpMethod.Post, $"{_ep.Issuer}/api/accounts/deviceauth/token", Json(new { device_auth_id = p.DeviceAuthId, user_code = p.UserCode }), ct);
            if (r.NetError is not null)
            {
                if (++_state.TransportErrors >= MaxTransportErrors) FailLogin("OpenAI ist nicht erreichbar: " + r.NetError);
                return;
            }
            _state.TransportErrors = 0;
            if (r.Status is 403 or 404) return;                                   // not typed in yet
            if (r.Status == 429) { p.NextPollAt = DateTime.UtcNow.AddSeconds(Math.Max(p.IntervalSeconds, 10)); return; }
            if (r.Status != 200) { FailLogin($"OpenAI hat die Anmeldung abgebrochen (HTTP {r.Status})."); return; }

            using var doc = ParseJson(r.Body);
            var code = Str(doc?.RootElement, "authorization_code");
            var verifier = Str(doc?.RootElement, "code_verifier");
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(verifier)) { FailLogin("OpenAI hat keinen Autorisierungs-Code geliefert."); return; }

            var x = await SendAsync(HttpMethod.Post, $"{_ep.Issuer}/oauth/token", Form(new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = $"{_ep.Issuer}/deviceauth/callback",
                ["client_id"] = ClientId, ["code_verifier"] = verifier,
            }), ct);
            if (x.NetError is not null || x.Status != 200)
            {
                FailLogin("Der Token-Tausch bei OpenAI ist fehlgeschlagen" + (x.NetError is null ? $" (HTTP {x.Status}: {ErrorText(x.Body)})." : ": " + x.NetError));
                return;
            }
            using var td = ParseJson(x.Body);
            var t = new TokenSet { ConnectedAt = DateTime.UtcNow };
            if (!Apply(t, td?.RootElement)) { FailLogin("OpenAI hat keinen Zugriffs-Token geliefert."); return; }
            await SaveAsync(t, ct);
            _state.Pending = null; _state.LastLoginError = null;
            _log?.Invoke($"ChatGPT sign-in completed for {t.Email} (plan {t.Plan})");
        }
        finally { _state.Lock.Release(); }
    }

    private void FailLogin(string message) { _state.Pending = null; _state.LastLoginError = message; _log?.Invoke("ChatGPT sign-in failed: " + message); }

    // ---- the access token, renewed when needed ---------------------------------------------------------------------------

    /// <summary>A valid access token (renewed first when it expires within two minutes, or when <paramref name="force"/>) and the
    /// ChatGPT account id the backend wants in a header. Never throws; <c>Error</c> is something a person can read.</summary>
    public async Task<(string? Token, string? AccountId, string? Error)> GetAccessTokenAsync(CancellationToken ct = default, bool force = false)
    {
        var t = await LoadAsync(ct);
        if (t is null) return (null, null, "Nicht bei ChatGPT angemeldet.");
        if (t.Dead) return (null, null, t.Error ?? DeadMessage);
        if (!force && t.ExpiresAt > DateTime.UtcNow + RefreshSkew) return (t.AccessToken, t.AccountId, null);

        await _state.Lock.WaitAsync(ct);
        try
        {
            t = await LoadAsync(ct);   // another caller may have rotated the token while we waited — replaying the old one would kill the sign-in
            if (t is null) return (null, null, "Nicht bei ChatGPT angemeldet.");
            if (t.Dead) return (null, null, t.Error ?? DeadMessage);
            if (!force && t.ExpiresAt > DateTime.UtcNow + RefreshSkew) return (t.AccessToken, t.AccountId, null);
            if (string.IsNullOrEmpty(t.RefreshToken)) return await MarkDeadAsync(t, DeadMessage, ct);

            var r = await SendAsync(HttpMethod.Post, $"{_ep.Issuer}/oauth/token", Form(new()
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = t.RefreshToken, ["client_id"] = ClientId,
            }), ct);

            if (r.NetError is null && r.Status == 200)
            {
                using var doc = ParseJson(r.Body);
                if (Apply(t, doc?.RootElement))
                {
                    t.LastRefresh = DateTime.UtcNow; t.Error = null;
                    await SaveAsync(t, ct);
                    return (t.AccessToken, t.AccountId, null);
                }
            }

            var code = r.NetError is null ? ErrorCode(r.Body) : null;
            var permanent = r.NetError is null && (r.Status is 401 or 403 || (r.Status == 400 && PermanentCodes.Contains(code ?? "")));
            if (permanent)
            {
                _log?.Invoke($"ChatGPT refresh was refused ({r.Status} {code}) — the sign-in is dead");
                return await MarkDeadAsync(t, code == "refresh_token_reused"
                    ? "Der Refresh-Token wurde bereits von einem anderen Client verbraucht (z. B. Codex CLI oder Hermes mit demselben Konto) – bitte neu anmelden."
                    : DeadMessage, ct);
            }

            // Transient (network, 5xx, 429): keep the sign-in. A token that is still valid for a little longer can carry this call.
            _log?.Invoke($"ChatGPT token refresh failed transiently: {r.Status} {r.NetError ?? ErrorText(r.Body)}");
            if (t.ExpiresAt > DateTime.UtcNow.AddSeconds(10)) return (t.AccessToken, t.AccountId, null);
            return (null, null, "Die ChatGPT-Anmeldung konnte gerade nicht erneuert werden (" + (r.NetError ?? $"HTTP {r.Status}") + "). Bitte später erneut versuchen.");
        }
        finally { _state.Lock.Release(); }
    }

    private string DeadMessage => $"Die ChatGPT-Anmeldung ist abgelaufen oder wurde widerrufen – bitte unter {_hint} neu anmelden.";
    private static readonly HashSet<string> PermanentCodes = new(StringComparer.OrdinalIgnoreCase)
        { "invalid_grant", "invalid_token", "invalid_request", "unauthorized_client", "refresh_token_reused", "refresh_token_expired", "refresh_token_invalidated" };

    private async Task<(string?, string?, string?)> MarkDeadAsync(TokenSet t, string message, CancellationToken ct)
    {
        t.Dead = true; t.Error = message; t.RefreshToken = "";   // a dead refresh token is never replayed
        await SaveAsync(t, ct);
        return (null, null, message);
    }

    // ---- model list (best effort: it only fills a suggestion list) --------------------------------------------------------

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow < _state.ModelsUntil) return _state.Models;   // failures are remembered for a minute too, so a dead backend cannot stall every page load
        var (token, accountId, _) = await GetAccessTokenAsync(ct);
        if (token is null) return Array.Empty<ModelInfo>();
        var req = new HttpRequestMessage(HttpMethod.Get, $"{_ep.BackendBase}/models?client_version=1.0.0");
        ApplyBackendHeaders(req, token, accountId);
        var r = await SendAsync(req, ct);
        if (r.NetError is not null || r.Status != 200) { _state.Models = Array.Empty<ModelInfo>(); _state.ModelsUntil = DateTime.UtcNow.AddMinutes(1); return _state.Models; }
        var list = new List<ModelInfo>();
        using var doc = ParseJson(r.Body);
        if (doc is not null)
        {
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("models", out var m) ? m
                : doc.RootElement.TryGetProperty("data", out var d) ? d : default;
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    var slug = Str(e, "slug") ?? Str(e, "id");
                    if (string.IsNullOrEmpty(slug)) continue;
                    var vis = Str(e, "visibility");
                    if (vis is not null && vis != "list") continue;
                    list.Add(new ModelInfo(slug, Str(e, "display_name") ?? slug));
                }
        }
        _state.Models = list; _state.ModelsUntil = DateTime.UtcNow.AddMinutes(10);
        return list;
    }

    /// <summary>The headers the ChatGPT backend wants. The account id is a header of its own; the rest only says who is asking.</summary>
    public void ApplyBackendHeaders(HttpRequestMessage req, string token, string? accountId)
    {
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (!string.IsNullOrEmpty(accountId)) req.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", accountId);
        req.Headers.TryAddWithoutValidation("originator", Originator);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);   // the default .NET agent is answered with HTTP 530 by OpenAI's edge
        req.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
    }

    // ---- one call through the account ------------------------------------------------------------------------------------

    /// <summary>One AI call through the signed-in account: the Responses API of the Codex backend, answered as a server-sent-event
    /// stream collected into an <see cref="AiResponse"/>. Never throws. What the backend insists on (from Codex and Hermes):
    /// <c>stream:true</c> and <c>store:false</c>, the system prompt as <c>instructions</c>, the whole history in <c>input</c>, and
    /// none of the sampling or length parameters.</summary>
    public async Task<AiResponse> CompleteAsync(string model, IReadOnlyList<AiMessage> messages, CancellationToken ct = default)
    {
        try
        {
            var body = BuildBody(model, messages);
            // A token the server refuses although it looked valid (revoked, rotated by another client) gets exactly one forced renewal.
            for (var attempt = 0; ; attempt++)
            {
                var (token, accountId, error) = await GetAccessTokenAsync(ct, force: attempt > 0);
                if (token is null) return Fail(error ?? "Nicht bei ChatGPT angemeldet.", model);

                var client = _http();
                client.Timeout = TimeSpan.FromSeconds(280);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_ep.BackendBase}/responses")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                ApplyBackendHeaders(req, token, accountId);
                req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode) { var a = await ReadStreamAsync(resp, ct); a.Model = model; return a; }

                var raw = await resp.Content.ReadAsStringAsync(ct);
                var status = (int)resp.StatusCode;
                if (status == 401 && attempt == 0) continue;
                _log?.Invoke($"ChatGPT call failed: {status} {(raw.Length > 600 ? raw[..600] : raw)}");
                return Fail(status switch
                {
                    401 or 403 => $"ChatGPT hat den Zugriff abgelehnt (HTTP {status}) – bitte unter {_hint} neu anmelden.",
                    429 => "Das Kontingent des ChatGPT-Kontos ist gerade erreicht: " + ErrorText(raw),
                    _ => $"ChatGPT-Fehler {status}: {ErrorText(raw)}",
                }, model);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            // A refused or dropped connection: .NET's own text is English and OS-localised — the person gets one plain German sentence.
            _log?.Invoke("ChatGPT connection failed: " + ex.Message);
            return Fail("Die Verbindung zu ChatGPT ist fehlgeschlagen oder wurde unterbrochen. Bitte später erneut versuchen.", model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log?.Invoke("ChatGPT call threw: " + ex.Message);
            return Fail($"KI-Aufruf fehlgeschlagen: {ex.Message}", model);
        }
    }

    /// <summary>The request document. System messages become <c>instructions</c> (required, so a call without one gets a neutral
    /// default); the rest become input items — user text is <c>input_text</c> (plus pictures and PDFs as <c>input_image</c> /
    /// <c>input_file</c>), earlier assistant text <c>output_text</c>.</summary>
    public static string BuildBody(string model, IReadOnlyList<AiMessage> messages)
    {
        var instructions = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content ?? "").Where(s => s.Length > 0));
        var items = messages.Where(m => m.Role != "system")
            .Select(m => (Assistant: m.Role == "assistant", Text: m.Content ?? "", Files: m.Attachments ?? new()))
            .ToList();
        if (items.Count == 0) { items.Add((false, instructions, new())); instructions = ""; }   // only a system message: it is the task
        if (instructions.Length == 0) instructions = "You are a helpful assistant.";

        var input = new JsonArray();
        foreach (var i in items)
        {
            var content = new JsonArray
            {
                new JsonObject { ["type"] = i.Assistant ? "output_text" : "input_text", ["text"] = i.Text },
            };
            if (!i.Assistant)
                foreach (var f in i.Files)
                {
                    var dataUrl = $"data:{f.MediaType};base64,{f.Data}";
                    content.Add(f.Kind == "file"
                        ? new JsonObject { ["type"] = "input_file", ["filename"] = f.Name, ["file_data"] = dataUrl }
                        : new JsonObject { ["type"] = "input_image", ["image_url"] = dataUrl });
                }
            input.Add(new JsonObject { ["type"] = "message", ["role"] = i.Assistant ? "assistant" : "user", ["content"] = content });
        }

        return new JsonObject
        {
            ["model"] = model, ["instructions"] = instructions, ["input"] = input, ["store"] = false, ["stream"] = true,
        }.ToJsonString();
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

        if (failure is not null) return Fail(failure, null);
        if (!completed) return Fail("Die Antwort von ChatGPT wurde unterbrochen.", null);
        var answer = text.Length > 0 ? text.ToString() : completedText ?? "";
        if (string.IsNullOrWhiteSpace(answer)) return new AiResponse { Ok = false, Error = "ChatGPT hat eine leere Antwort geliefert.", PromptTokens = prompt, CompletionTokens = completion };
        return new AiResponse { Ok = true, Text = answer, PromptTokens = prompt, CompletionTokens = completion };
    }

    private static int Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

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

    private static AiResponse Fail(string error, string? model) => new() { Ok = false, Error = error, Model = model };

    // ---- token documents --------------------------------------------------------------------------------------------------

    /// <summary>Takes id/access/refresh token out of a token response into <paramref name="t"/>. False without an access token.
    /// Expiry and account come from the JWT claims — the response's <c>expires_in</c> is only the fallback.</summary>
    private static bool Apply(TokenSet t, JsonElement? root)
    {
        var access = Str(root, "access_token");
        if (string.IsNullOrEmpty(access)) return false;
        t.AccessToken = access;
        if (Str(root, "refresh_token") is { Length: > 0 } rt) t.RefreshToken = rt;   // rotated; keep the old one only when none came back
        if (Str(root, "id_token") is { Length: > 0 } it) t.IdToken = it;

        var ac = Claims(t.AccessToken); var ic = Claims(t.IdToken);
        var exp = ac is { } a && a.TryGetProperty("exp", out var e) && e.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(e.GetInt64()).UtcDateTime : (DateTime?)null;
        if (exp is null && root is { } r && r.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number) exp = DateTime.UtcNow.AddSeconds(ei.GetInt64());
        t.ExpiresAt = exp ?? DateTime.UtcNow.AddMinutes(30);

        var auth = AuthClaim(ac) ?? AuthClaim(ic);
        if (auth is { } au)
        {
            if (Str(au, "chatgpt_account_id") is { Length: > 0 } id) t.AccountId = id;
            if (Str(au, "chatgpt_plan_type") is { Length: > 0 } plan) t.Plan = plan;
        }
        t.Email = Str(ic, "email") ?? (ac is { } a2 && a2.TryGetProperty("https://api.openai.com/profile", out var pr) ? Str(pr, "email") : null) ?? t.Email;
        return true;
    }

    private static JsonElement? AuthClaim(JsonElement? claims) =>
        claims is { } c && c.TryGetProperty("https://api.openai.com/auth", out var a) && a.ValueKind == JsonValueKind.Object ? a : null;

    private static JsonElement? Claims(string? jwt)
    {
        var parts = (jwt ?? "").Split('.');
        if (parts.Length < 2) return null;
        var p = parts[1].Replace('-', '+').Replace('_', '/');
        p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
        try { using var d = JsonDocument.Parse(Convert.FromBase64String(p)); return d.RootElement.Clone(); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    // ---- HTTP -------------------------------------------------------------------------------------------------------------

    private sealed record Reply(int Status, string Body, string? NetError);

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    private static FormUrlEncodedContent Form(Dictionary<string, string> f) => new(f);

    private Task<Reply> SendAsync(HttpMethod method, string url, HttpContent content, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        return SendAsync(req, ct);
    }

    private async Task<Reply> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            var client = _http();
            client.Timeout = TimeSpan.FromSeconds(20);
            using var resp = await client.SendAsync(req, ct);
            var body = await ReadCappedAsync(resp, ct);
            return new Reply((int)resp.StatusCode, body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new Reply(0, "", ex.Message);
        }
    }

    /// <summary>The body, but never more than 1 MiB — these are small JSON answers, and a misbehaving server must not fill memory.</summary>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[1024 * 1024];
        var n = 0;
        while (n < buf.Length) { var k = await s.ReadAsync(buf.AsMemory(n), ct); if (k == 0) break; n += k; }
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    private static JsonDocument? ParseJson(string body) { try { return JsonDocument.Parse(body); } catch (JsonException) { return null; } }

    private static string? Str(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static string? Null(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static int IntervalOf(JsonElement? root) => int.TryParse(Str(root, "interval")?.Trim(), out var n) && n > 0 ? n : 5;

    /// <summary>OpenAI's error documents come as <c>{"error":"invalid_grant"}</c> or <c>{"error":{"code":"…","message":"…"}}</c>.</summary>
    public static string? ErrorCode(string body)
    {
        using var doc = ParseJson(body);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (!doc.RootElement.TryGetProperty("error", out var e)) return Str(doc.RootElement, "code");
        return e.ValueKind == JsonValueKind.String ? e.GetString() : Str(e, "code") ?? Str(e, "type");
    }

    public static string ErrorText(string body)
    {
        using var doc = ParseJson(body);
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            var r = doc.RootElement;
            if (Str(r, "detail") is { Length: > 0 } d) return Trunc(d);
            if (r.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return Trunc(Str(r, "error_description") ?? e.GetString() ?? "");
                if (Str(e, "message") is { Length: > 0 } m) return Trunc(m);
            }
        }
        return Trunc(body);
    }

    private static string Trunc(string s) => s.Length > 300 ? s[..300] : s;
}
