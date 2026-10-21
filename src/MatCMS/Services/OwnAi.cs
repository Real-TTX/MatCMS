using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Shared;
using MatCMS.Shared.Ai;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// The site's OWN AI connection (Einstellungen → KI), so the AI works without a cloud — MatCMS has to run on its own. Two ways,
/// the same two the cloud offers and with the same code (<see cref="ChatCompletionsClient"/>, <see cref="ChatGptAccount"/> in
/// MatCMS.Shared): an API key for OpenAI or any compatible server (DeepSeek, a local model server), or a signed-in ChatGPT account.
/// The key and the sign-in are encrypted with this installation's DataProtection keys and are in no backup
/// (<see cref="SettingKeys.AiOwn"/>).
/// </summary>
public sealed class OwnAi
{
    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;

    public OwnAi(AppDbContext db, IDataProtectionProvider protection)
    {
        _db = db;
        _protector = protection.CreateProtector("MatCMS.AiSecrets");
    }

    public sealed record Config(string Provider, string BaseUrl, string Model, string ApiKey, bool HasChatGpt, string ChatGptModel)
    {
        public bool UsesKey => Provider == "openai";
        public bool UsesChatGpt => Provider == "chatgpt";
        /// <summary>Configured far enough to try a call (a dead ChatGPT sign-in is reported by the call itself).</summary>
        public bool Ready => UsesKey ? ApiKey.Length > 0 && Model.Length > 0 : UsesChatGpt && HasChatGpt && ChatGptModel.Length > 0;
    }

    public async Task<Config> GetAsync(CancellationToken ct = default)
    {
        var map = await _db.SiteSettings.AsNoTracking().Where(s => SettingKeys.AiOwn.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        string G(string k) => map.TryGetValue(k, out var v) ? (v ?? "").Trim() : "";
        var provider = G(SettingKeys.AiOwnProvider) is "openai" or "chatgpt" ? G(SettingKeys.AiOwnProvider) : "";
        return new Config(provider, G(SettingKeys.AiOwnBaseUrl), G(SettingKeys.AiOwnModel), Unprotect(G(SettingKeys.AiOwnApiKey)),
            G(SettingKeys.AiOwnChatGptTokens).Length > 0, G(SettingKeys.AiOwnChatGptModel));
    }

    public string Protect(string plain) => _protector.Protect(plain);

    public string Unprotect(string stored)
    {
        if (stored.Length == 0) return "";
        try { return _protector.Unprotect(stored); }
        catch (System.Security.Cryptography.CryptographicException) { return ""; }   // another installation's ciphertext: unreadable here, so "not set"
    }

    public async Task<string> GetRawAsync(string key, CancellationToken ct = default) =>
        await _db.SiteSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct) ?? "";

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else row.Value = value;
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>The site's ChatGPT sign-in: <see cref="ChatGptAccount"/> with its tokens in ONE encrypted site setting.</summary>
public sealed class SiteChatGptAccount : ChatGptAccount
{
    public SiteChatGptAccount(OwnAi own, IHttpClientFactory http, ChatGptLoginState state, ILogger<SiteChatGptAccount> log)
        : base(new Store(own), () => http.CreateClient(), ChatGptEndpoints.Default, state, SiteUserAgent, "matcms", "Einstellungen → KI",
            m => log.LogInformation("{Message}", m))
    {
    }

    public static string SiteUserAgent { get; } = "MatCMS/" + (typeof(SiteChatGptAccount).Assembly.GetName().Version?.ToString(3) ?? "1");

    private sealed class Store(OwnAi own) : IChatGptTokenStore
    {
        public async Task<string?> LoadAsync(CancellationToken ct)
        {
            var raw = await own.GetRawAsync(SettingKeys.AiOwnChatGptTokens, ct);
            var json = own.Unprotect(raw);
            return json.Length == 0 ? null : json;
        }

        public Task SaveAsync(string? json, CancellationToken ct) =>
            own.SetAsync(SettingKeys.AiOwnChatGptTokens, json is null ? "" : own.Protect(json), ct);
    }
}

/// <summary>
/// How a model call leaves this site: through its own connection when one is set up on the site, otherwise through the
/// connected cloud's relay. The site's own choice wins — whoever set up a key here wants it used, profile or not.
/// </summary>
public sealed class SiteAiTransport : IAiTransport
{
    private readonly OwnAi _own;
    private readonly SiteChatGptAccount _chatgpt;
    private readonly CloudService _cloud;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SiteAiTransport> _log;

    /// <summary>A site's own key pays for its calls; the same generous cap as the cloud (reasoning models think first).</summary>
    public const int MaxCompletionTokens = 16000;

    public SiteAiTransport(OwnAi own, SiteChatGptAccount chatgpt, CloudService cloud, IHttpClientFactory http, ILogger<SiteAiTransport> log)
    {
        _own = own; _chatgpt = chatgpt; _cloud = cloud; _http = http; _log = log;
    }

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken ct = default)
    {
        var cfg = await _own.GetAsync(ct);
        if (cfg.UsesChatGpt)
        {
            if (cfg.ChatGptModel.Length == 0) return new AiResponse { Ok = false, Error = "Kein ChatGPT-Modell gewählt (Einstellungen → KI)." };
            return await _chatgpt.CompleteAsync(cfg.ChatGptModel, request.Messages, ct);
        }
        if (cfg.UsesKey)
        {
            if (cfg.ApiKey.Length == 0 || cfg.Model.Length == 0) return new AiResponse { Ok = false, Error = "API-Schlüssel oder Modell fehlt (Einstellungen → KI)." };
            return await ChatCompletionsClient.CompleteAsync(_http.CreateClient(), cfg.BaseUrl, cfg.ApiKey, cfg.Model, request, MaxCompletionTokens,
                m => _log.LogWarning("{Message}", m), ct);
        }
        return await _cloud.RelayAiAsync(request, ct);
    }
}
