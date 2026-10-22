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

    /// <summary>What a call goes through: the ACTIVE profile, decrypted. <c>Provider</c> "" = no own profile (the cloud's relay).</summary>
    public sealed record Config(string Provider, string BaseUrl, string Model, string ApiKey, bool HasChatGpt, string ChatGptModel, string Name = "")
    {
        public bool UsesKey => Provider == "openai";
        public bool UsesChatGpt => Provider == "chatgpt";
        /// <summary>Configured far enough to try a call (a dead ChatGPT sign-in is reported by the call itself).</summary>
        public bool Ready => UsesKey ? ApiKey.Length > 0 && Model.Length > 0 : UsesChatGpt && HasChatGpt && ChatGptModel.Length > 0;
    }

    /// <summary>One saved connection. <c>Key</c> is stored ENCRYPTED (never decrypted for a page — only <see cref="GetAsync"/> does,
    /// for the call). All ChatGPT profiles share the site's one ChatGPT sign-in; they differ in the model.</summary>
    public sealed class Profile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>"openai" (API key, any compatible server) or "chatgpt".</summary>
        public string Kind { get; set; } = "openai";
        public string BaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public string Key { get; set; } = "";
        public string ChatGptModel { get; set; } = "";

        public string ModelShown => Kind == "chatgpt" ? ChatGptModel : Model;
    }

    public sealed record ProfileSet(List<Profile> Profiles, string ActiveId)
    {
        public Profile? Active => Profiles.FirstOrDefault(p => p.Id == ActiveId);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    public async Task<Config> GetAsync(CancellationToken ct = default)
    {
        var set = await ProfilesAsync(ct);
        var hasChatGpt = (await GetRawAsync(SettingKeys.AiOwnChatGptTokens, ct)).Length > 0;
        if (set.Active is not { } p) return new Config("", "", "", "", hasChatGpt, "");
        return new Config(p.Kind, p.BaseUrl, p.Model, Unprotect(p.Key), hasChatGpt, p.ChatGptModel, p.Name);
    }

    /// <summary>The saved profiles and which one is in use. A site from before profiles has none stored: its single connection is
    /// returned as the one profile, active if it was in use — and becomes a stored profile on the first save.</summary>
    public async Task<ProfileSet> ProfilesAsync(CancellationToken ct = default)
    {
        var map = await _db.SiteSettings.AsNoTracking().Where(s => SettingKeys.AiOwn.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return Read(k => map.TryGetValue(k, out var v) ? (v ?? "").Trim() : "");
    }

    /// <summary>Profiles from the raw settings — no database, no decryption, so <see cref="AiService"/> can ask cheaply.</summary>
    public static ProfileSet Read(Func<string, string> get)
    {
        var json = get(SettingKeys.AiProfiles);
        if (json.Length > 0)
        {
            List<Profile>? list = null;
            try { list = System.Text.Json.JsonSerializer.Deserialize<List<Profile>>(json, Json); } catch (System.Text.Json.JsonException) { }
            return new ProfileSet((list ?? new()).Where(p => p.Id.Length > 0).ToList(), get(SettingKeys.AiActiveProfile));
        }
        // Before profiles: one connection in loose keys. Turned into a profile with the same values (its key stays the
        // ciphertext it was — the same protector reads it).
        var legacy = get(SettingKeys.AiOwnProvider);
        var hasKeyProfile = get(SettingKeys.AiOwnApiKey).Length > 0 || get(SettingKeys.AiOwnModel).Length > 0;
        var hasChatGptProfile = get(SettingKeys.AiOwnChatGptModel).Length > 0;
        var profiles = new List<Profile>();
        if (hasKeyProfile)
            profiles.Add(new Profile
            {
                Id = "legacy-key", Kind = "openai", BaseUrl = get(SettingKeys.AiOwnBaseUrl), Model = get(SettingKeys.AiOwnModel),
                Key = get(SettingKeys.AiOwnApiKey), Name = DefaultName("openai", get(SettingKeys.AiOwnBaseUrl), get(SettingKeys.AiOwnModel)),
            });
        if (hasChatGptProfile)
            profiles.Add(new Profile { Id = "legacy-chatgpt", Kind = "chatgpt", ChatGptModel = get(SettingKeys.AiOwnChatGptModel), Name = "ChatGPT · " + get(SettingKeys.AiOwnChatGptModel) });
        var active = legacy == "openai" && hasKeyProfile ? "legacy-key" : legacy == "chatgpt" && hasChatGptProfile ? "legacy-chatgpt" : "";
        return new ProfileSet(profiles, active);
    }

    /// <summary>Whether the active profile is set up far enough to try a call — from raw settings, without decrypting.</summary>
    public static bool ActiveReady(Func<string, string> get)
    {
        var p = Read(get).Active;
        if (p is null) return false;
        return p.Kind == "chatgpt"
            ? get(SettingKeys.AiOwnChatGptTokens).Length > 0 && p.ChatGptModel.Length > 0
            : p.Key.Length > 0 && p.Model.Length > 0;
    }

    /// <summary>Stores the whole set. The loose keys of the old single connection are cleared — from now on the profiles are what
    /// counts (the ChatGPT sign-in stays: it is the account every ChatGPT profile uses).</summary>
    public async Task SaveProfilesAsync(ProfileSet set, CancellationToken ct = default)
    {
        var active = set.Profiles.Any(p => p.Id == set.ActiveId) ? set.ActiveId : "";
        await SetAsync(SettingKeys.AiProfiles, System.Text.Json.JsonSerializer.Serialize(set.Profiles, Json), ct);
        await SetAsync(SettingKeys.AiActiveProfile, active, ct);
        foreach (var k in new[] { SettingKeys.AiOwnProvider, SettingKeys.AiOwnBaseUrl, SettingKeys.AiOwnModel, SettingKeys.AiOwnApiKey, SettingKeys.AiOwnChatGptModel })
            await SetAsync(k, "", ct);
    }

    /// <summary>A name for a profile nobody named: the provider read from the address, and the model.</summary>
    public static string DefaultName(string kind, string baseUrl, string model)
    {
        if (kind == "chatgpt") return "ChatGPT" + (model.Length > 0 ? " · " + model : "");
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Host : "api.openai.com";
        var provider = host.Contains("deepseek") ? "DeepSeek" : host.Contains("openrouter") ? "OpenRouter" : host.Contains("openai") ? "OpenAI" : host;
        return provider + (model.Length > 0 ? " · " + model : "");
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
