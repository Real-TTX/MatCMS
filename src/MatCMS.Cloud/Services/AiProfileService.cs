using System.Text.Json;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The cloud's AI PROFILES (Einstellungen → KI): several provider connections kept side by side — "DeepSeek Flash", "OpenRouter
/// kostenlos", a ChatGPT model — of which one serves every relayed call. The live connection stays where it always was
/// (<c>ai.provider/model/baseUrl/apiKey</c>, read by <see cref="AiService"/>, edited by the settings API and MCP, carried in a
/// configuration backup): activating a profile COPIES its values there. So nothing that reads or writes the live keys changed.
/// <para>Which profile is active is not merely remembered, it is CHECKED: the remembered one counts only while the live keys
/// still hold its values. A change through the API or a restore makes the live connection "own" again instead of a profile
/// claiming to be in use with values it does not have.</para>
/// <para>The list (<c>ai.profiles</c>) is in no settings section: its keys are encrypted with this installation's key ring and
/// would be unreadable on another cloud — like the ChatGPT sign-in, which all ChatGPT profiles share.</para>
/// </summary>
public sealed class AiProfileService
{
    public const string ProfilesKey = "ai.profiles";
    public const string ActiveKey = "ai.activeProfile";

    private readonly CloudContext _cloud;
    private readonly SecretProtector _secrets;

    public AiProfileService(CloudContext cloud, SecretProtector secrets)
    {
        _cloud = cloud; _secrets = secrets;
    }

    public sealed class Profile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>"openai" (API key, any compatible server) or "chatgpt" (the signed-in account).</summary>
        public string Kind { get; set; } = "openai";
        public string BaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        /// <summary>SecretProtector ciphertext; never decrypted for a page.</summary>
        public string Key { get; set; } = "";
    }

    /// <summary><c>ActiveId</c> = the profile whose values the live connection holds right now; "" = none does.</summary>
    public sealed record ProfileSet(List<Profile> Profiles, string ActiveId)
    {
        public Profile? Active => Profiles.FirstOrDefault(p => p.Id == ActiveId);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private string G(string k) => (_cloud.Get(k) ?? "").Trim();

    public ProfileSet Get()
    {
        var json = G(ProfilesKey);
        List<Profile> list;
        if (json.Length > 0)
        {
            try { list = JsonSerializer.Deserialize<List<Profile>>(json, Json) ?? new(); } catch (JsonException) { list = new(); }
        }
        else
        {
            // Before profiles: the live connection is the one profile (its key keeps its ciphertext).
            list = new();
            if (G(SettingKeys.AiModel).Length > 0 || G(SettingKeys.AiApiKey).Length > 0)
            {
                var kind = G(SettingKeys.AiProvider) == AiService.ProviderChatGpt ? "chatgpt" : "openai";
                list.Add(new Profile
                {
                    Id = "legacy", Kind = kind, BaseUrl = G(SettingKeys.AiBaseUrl), Model = G(SettingKeys.AiModel), Key = kind == "chatgpt" ? "" : G(SettingKeys.AiApiKey),
                    Name = DefaultName(kind, G(SettingKeys.AiBaseUrl), G(SettingKeys.AiModel)),
                });
                return new ProfileSet(list, "legacy");
            }
        }
        var remembered = list.FirstOrDefault(p => p.Id == G(ActiveKey));
        return new ProfileSet(list, remembered is not null && IsLive(remembered) ? remembered.Id : "");
    }

    /// <summary>Whether the live connection holds exactly this profile's values (for ChatGPT: its model and its own sign-in).</summary>
    private bool IsLive(Profile p)
    {
        var provider = G(SettingKeys.AiProvider);
        if (provider.Length == 0) provider = "openai";
        return provider == p.Kind && G(SettingKeys.AiModel) == p.Model
            && (p.Kind == "chatgpt" ? G(ChatGptAuthService.AccountKey) == AccountOf(p) : G(SettingKeys.AiBaseUrl) == p.BaseUrl && G(SettingKeys.AiApiKey) == p.Key);
    }

    /// <summary>The ChatGPT sign-in a profile uses: its own (by id), "" for the profile a cloud from before profiles was shown as.</summary>
    public static string AccountOf(Profile p) => p.Id == "legacy" ? "" : p.Id;

    /// <summary>Saves a profile (new when its id is empty). A typed key replaces the stored one, an empty one keeps it. Saving the
    /// ACTIVE profile, or with <paramref name="activate"/>, writes its values into the live connection.</summary>
    public async Task<Profile> SaveAsync(Profile input, string? typedKey, bool clearKey, bool activate)
    {
        var set = Get();
        var wasActive = input.Id.Length > 0 && input.Id == set.ActiveId;
        var p = set.Profiles.FirstOrDefault(x => x.Id == input.Id);
        if (p is null)
        {
            p = new Profile { Id = Guid.NewGuid().ToString("N")[..10] };
            set.Profiles.Add(p);
        }
        p.Kind = input.Kind == "chatgpt" ? "chatgpt" : "openai";
        p.BaseUrl = p.Kind == "chatgpt" ? "" : input.BaseUrl.Trim().TrimEnd('/');
        p.Model = input.Model.Trim();
        if (p.Kind == "chatgpt" || clearKey) p.Key = "";
        else if (!string.IsNullOrWhiteSpace(typedKey)) p.Key = _secrets.Protect(typedKey.Trim());
        var name = input.Name.Trim();
        p.Name = name.Length > 0 ? (name.Length > 80 ? name[..80] : name) : DefaultName(p.Kind, p.BaseUrl, p.Model);
        await StoreAsync(set.Profiles);
        if (activate || wasActive) await ActivateAsync(p.Id);
        return p;
    }

    /// <summary>Copies the profile into the live connection — from the next relayed call on, every site uses it.</summary>
    public async Task<Profile?> ActivateAsync(string id)
    {
        var set = Get();
        if (set.Profiles.FirstOrDefault(x => x.Id == id) is not { } p) return null;
        if (G(ProfilesKey).Length == 0) await StoreAsync(set.Profiles);     // the legacy profile becomes a stored one
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.AiProvider] = p.Kind,
            [SettingKeys.AiModel] = p.Model,
            [SettingKeys.AiBaseUrl] = p.BaseUrl,
            [SettingKeys.AiApiKey] = p.Kind == "chatgpt" ? G(SettingKeys.AiApiKey) : p.Key,   // a ChatGPT profile leaves a stored key alone
            [ChatGptAuthService.AccountKey] = p.Kind == "chatgpt" ? AccountOf(p) : G(ChatGptAuthService.AccountKey),
            [ActiveKey] = p.Id,
        });
        return p;
    }

    /// <summary>Deletes a profile — not the active one, whose values serve every site right now.</summary>
    public async Task<bool> DeleteAsync(string id)
    {
        var set = Get();
        var gone = set.Profiles.FirstOrDefault(x => x.Id == id);
        if (id == set.ActiveId || gone is null) return false;
        set.Profiles.Remove(gone);
        await StoreAsync(set.Profiles);
        // Its sign-in goes with it (never the cloud's one from before profiles, which the API still reaches).
        if (gone.Kind == "chatgpt" && AccountOf(gone).Length > 0)
            await _cloud.SaveAsync(new Dictionary<string, string?> { [ChatGptAuthService.TokensKey(AccountOf(gone))] = "" });
        return true;
    }

    public string KeyOf(string? id) =>
        id is not null && Get().Profiles.FirstOrDefault(x => x.Id == id) is { Key.Length: > 0 } p ? _secrets.Unprotect(p.Key) ?? "" : "";

    private Task StoreAsync(List<Profile> list) =>
        _cloud.SaveAsync(new Dictionary<string, string?> { [ProfilesKey] = JsonSerializer.Serialize(list, Json) });

    public static string DefaultName(string kind, string baseUrl, string model)
    {
        if (kind == "chatgpt") return "ChatGPT" + (model.Length > 0 ? " · " + model : "");
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Host : "api.openai.com";
        var provider = host.Contains("deepseek") ? "DeepSeek" : host.Contains("openrouter") ? "OpenRouter" : host.Contains("openai") ? "OpenAI" : host;
        return provider + (model.Length > 0 ? " · " + model : "");
    }
}
