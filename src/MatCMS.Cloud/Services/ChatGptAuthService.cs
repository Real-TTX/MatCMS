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
    public ChatGptAuthService(AppDbContext db, CloudContext cloud, SecretProtector secrets, IHttpClientFactory http,
        ChatGptEndpoints ep, ChatGptLoginState state, ILogger<ChatGptAuthService> log)
        : base(new Store(db, cloud, secrets), () => http.CreateClient(), ep, state, CloudUserAgent, "matcms-cloud", "Einstellungen → KI",
            m => log.LogInformation("{Message}", m))
    {
    }

    /// <summary>The identity the cloud shows OpenAI — its own name, not a borrowed one.</summary>
    public static string CloudUserAgent { get; } = "MatCMS-Cloud/" + (typeof(ChatGptAuthService).Assembly.GetName().Version?.ToString(3) ?? "1");

    private sealed class Store(AppDbContext db, CloudContext cloud, SecretProtector secrets) : IChatGptTokenStore
    {
        public async Task<string?> LoadAsync(CancellationToken ct)
        {
            var raw = await db.CloudSettings.AsNoTracking().Where(s => s.Key == SettingKeys.AiChatGptTokens).Select(s => s.Value).FirstOrDefaultAsync(ct);
            return secrets.Unprotect(raw);
        }

        public Task SaveAsync(string? json, CancellationToken ct) => cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.AiChatGptTokens] = json is null ? "" : secrets.Protect(json),
        });
    }
}
