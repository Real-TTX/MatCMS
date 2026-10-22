using MatCMS.Cloud.Data;
using MatCMS.Shared.Ai;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>Where the cloud's ChatGPT sign-in talks to. The keys exist so a test can point the cloud at a local stand-in
/// (<c>MatCmsCloud:ChatGpt:Issuer</c>, <c>:BackendBase</c>, <c>:MinPollSeconds</c>) — they are not operator settings.</summary>
public static class ChatGptEndpointsConfig
{
    public static ChatGptEndpoints From(IConfiguration c) => new(
        (c["MatCmsCloud:ChatGpt:Issuer"] ?? ChatGptEndpoints.Default.Issuer).TrimEnd('/'),
        (c["MatCmsCloud:ChatGpt:BackendBase"] ?? ChatGptEndpoints.Default.BackendBase).TrimEnd('/'),
        int.TryParse(c["MatCmsCloud:ChatGpt:MinPollSeconds"], out var n) && n > 0 ? n : ChatGptEndpoints.Default.MinPollSeconds);
}

/// <summary>
/// "Mit ChatGPT anmelden" for the CLOUD: one ChatGPT account serving the AI of every approved site. The flow and its traps are
/// <see cref="ChatGptAccount"/> (shared with the CMS, which can sign in on its own); what is the cloud's own is where the tokens
/// live — ONE <c>SecretProtector</c>-encrypted setting <c>ai.chatgpt.tokens</c> that is in no settings section, so neither the
/// API nor a configuration backup ever copies it (a copy elsewhere would invalidate this sign-in on its first refresh).
/// </summary>
public class ChatGptAuthService : ChatGptAccount
{
    /// <summary>The account the LIVE connection uses (the active ChatGPT profile, <see cref="AccountKey"/>) — what the relay, the REST
    /// endpoints and the MCP tools mean by "the" ChatGPT sign-in.</summary>
    public ChatGptAuthService(AppDbContext db, CloudContext cloud, SecretProtector secrets, IHttpClientFactory http,
        ChatGptEndpoints ep, ChatGptLoginStates states, ILogger<ChatGptAuthService> log)
        : this(db, cloud, secrets, http, ep, states, log, (cloud.Get(AccountKey) ?? "").Trim())
    {
    }

    /// <summary>The sign-in of one profile ("" = the cloud's sign-in from before profiles).</summary>
    public ChatGptAuthService(AppDbContext db, CloudContext cloud, SecretProtector secrets, IHttpClientFactory http,
        ChatGptEndpoints ep, ChatGptLoginStates states, ILogger<ChatGptAuthService> log, string account)
        : base(new Store(db, cloud, secrets, account), () => http.CreateClient(), ep, states.For(account), CloudUserAgent, "matcms-cloud", "Einstellungen → KI",
            m => log.LogInformation("{Message}", m))
    {
    }

    /// <summary>Which profile's sign-in the live connection uses; set when a ChatGPT profile is activated.</summary>
    public const string AccountKey = "ai.chatgpt.account";

    /// <summary>Where an account keeps its tokens: <c>ai.chatgpt.tokens</c> for the one from before profiles, <c>ai.chatgpt.tokens.{profile}</c>
    /// for each profile. None of them is in a settings section (API, configuration backup).</summary>
    public static string TokensKey(string account) => account.Length == 0 ? SettingKeys.AiChatGptTokens : SettingKeys.AiChatGptTokens + "." + account;

    /// <summary>The identity the cloud shows OpenAI — its own name, not a borrowed one.</summary>
    public static string CloudUserAgent { get; } = "MatCMS-Cloud/" + (typeof(ChatGptAuthService).Assembly.GetName().Version?.ToString(3) ?? "1");

    private sealed class Store(AppDbContext db, CloudContext cloud, SecretProtector secrets, string account) : IChatGptTokenStore
    {
        private async Task<string?> RawAsync(string key, CancellationToken ct) =>
            await db.CloudSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);

        public async Task<string?> LoadAsync(CancellationToken ct)
        {
            var key = TokensKey(account);
            var raw = await RawAsync(key, ct);
            // A profile that has no sign-in of its own takes over the cloud's one from before profiles — MOVED, never copied:
            // a refresh token may be used once.
            if (string.IsNullOrEmpty(raw) && account.Length > 0 && await RawAsync(SettingKeys.AiChatGptTokens, ct) is { Length: > 0 } legacy)
            {
                await cloud.SaveAsync(new Dictionary<string, string?> { [key] = legacy, [SettingKeys.AiChatGptTokens] = "" });
                raw = legacy;
            }
            return secrets.Unprotect(raw);
        }

        public Task SaveAsync(string? json, CancellationToken ct) => cloud.SaveAsync(new Dictionary<string, string?>
        {
            [TokensKey(account)] = json is null ? "" : secrets.Protect(json),
        });
    }
}

/// <summary>Hands out the ChatGPT sign-in of one profile (scoped).</summary>
public sealed class ChatGptAccounts(AppDbContext db, CloudContext cloud, SecretProtector secrets, IHttpClientFactory http,
    ChatGptEndpoints ep, ChatGptLoginStates states, ILogger<ChatGptAuthService> log)
{
    public ChatGptAuthService For(string account) => new(db, cloud, secrets, http, ep, states, log, account);
}
