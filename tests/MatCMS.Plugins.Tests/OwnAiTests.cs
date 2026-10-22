using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Services;
using MatCMS.Shared;
using MatCMS.Shared.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The AI without a cloud: the shared provider clients (also the cloud's) and the site's own connection in Einstellungen → KI.</summary>
public class OwnAiTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public OwnAiTests(AppFactory app) => _app = app;

    /// <summary>A provider that answers from a script and keeps what it was sent.</summary>
    private sealed class Provider(params (int status, string body)[] answers) : HttpMessageHandler
    {
        private int _i;
        public List<JsonObject> Sent { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            var (status, body) = answers[Math.Min(_i++, answers.Length - 1)];
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static string Answer(string text, string finish = "stop") =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = text }, ["finish_reason"] = finish }), ["usage"] = new JsonObject { ["prompt_tokens"] = 5, ["completion_tokens"] = 7 } }.ToJsonString();

    private static AiRequest Request(bool withPicture = false) => new()
    {
        Json = true, MaxTokens = 500,
        Messages =
        {
            new AiMessage { Role = "system", Content = "Antworte als JSON." },
            new AiMessage { Role = "user", Content = "Hallo", Attachments = withPicture ? new() { new AiAttachment { Kind = "image", MediaType = "image/jpeg", Name = "a.jpg", Data = "AAAA" } } : new() },
        },
    };

    [Fact]
    public async Task A_refused_parameter_is_dropped_and_the_call_retried_and_an_empty_answer_says_why()
    {
        var p = new Provider(
            (400, """{"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead."}}"""),
            (400, """{"error":{"message":"Unsupported value: 'temperature' does not support 0.4"}}"""),
            (422, """{"error":{"message":"unknown variant `image_url`, expected `text`"}}"""),
            (200, Answer("{\"ok\":true}")));
        var r = await ChatCompletionsClient.CompleteAsync(new HttpClient(p), "https://x.example/v1/", "k", "m", Request(withPicture: true), 16000);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("{\"ok\":true}", r.Text);
        Assert.Contains("Bilder", r.Note);
        var last = p.Sent.Last();
        Assert.Null(last["max_tokens"]);
        Assert.Equal(500, last["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(last["temperature"]);
        Assert.Equal("json_object", last["response_format"]!["type"]!.GetValue<string>());
        Assert.IsAssignableFrom<JsonValue>(last["messages"]![1]!["content"]);      // the picture gave way to text
        Assert.IsType<JsonArray>(p.Sent[0]["messages"]![1]!["content"]);  // it was sent as a part first

        var empty = await ChatCompletionsClient.CompleteAsync(new HttpClient(new Provider((200, Answer("", "length")))), "", "k", "m", Request(), 16000);
        Assert.False(empty.Ok);
        Assert.Contains("Längenlimit", empty.Error);
    }

    [Fact]
    public void The_chatgpt_request_carries_the_system_prompt_as_instructions_and_pictures_as_input_images()
    {
        var body = JsonNode.Parse(ChatGptAccount.BuildBody("gpt-5", Request(withPicture: true).Messages))!;
        Assert.Equal("Antworte als JSON.", body["instructions"]!.GetValue<string>());
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.True(body["stream"]!.GetValue<bool>());
        var parts = body["input"]![0]!["content"]!.AsArray();
        Assert.Equal("input_text", parts[0]!["type"]!.GetValue<string>());
        Assert.Equal("input_image", parts[1]!["type"]!.GetValue<string>());
    }

    /// <summary>Answers GET /models like OpenRouter: prices as strings, "0" for free ones.</summary>
    private sealed class Models : HttpMessageHandler
    {
        public string? Auth { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Auth = request.Headers.Authorization?.ToString();
            const string body = """{"data":[{"id":"z-ai/glm:free","name":"GLM"},{"id":"openai/gpt-4o","name":"GPT-4o","pricing":{"prompt":"0.0000025","completion":"0.00001"}},{"id":"meta/llama","name":"Llama","pricing":{"prompt":"0","completion":"0"}},{"id":"plain"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task The_model_list_marks_free_models_and_never_guesses_one_free()
    {
        var h = new Models();
        var (models, error) = await ChatCompletionsClient.ListModelsAsync(new HttpClient(h), "https://openrouter.ai/api/v1/", "sk-or", default);
        Assert.Null(error);
        Assert.Equal("Bearer sk-or", h.Auth);
        Assert.Equal(["meta/llama", "z-ai/glm:free"], models!.Where(m => m.Free).Select(m => m.Id).ToArray());
        Assert.False(models!.Single(m => m.Id == "plain").Free);
    }

    private static async Task<HttpResponseMessage> PostForm(HttpClient c, string url, string page, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = AppFactory.Token(await c.GetStringAsync(page))!;
        return await c.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task Ai_profiles_are_kept_side_by_side_one_is_active_keys_stay_encrypted_and_out_of_backups()
    {
        var admin = _app.Browser();
        await AppFactory.LoginAsync(admin);
        Assert.Contains("data-panel=\"ai\"", await admin.GetStringAsync("/admin/settings?tab=ai"));

        // Two profiles: DeepSeek, then OpenRouter with a free model — only the one saved with "activate" is in use.
        var r = await PostForm(admin, "/admin/settings/aiprofile", "/admin/settings/aiprofile", new()
        {
            ["name"] = "DeepSeek Flash", ["provider"] = "openai", ["baseUrl"] = "https://api.deepseek.com/", ["model"] = "deepseek-chat", ["apiKey"] = "sk-geheim-123", ["activate"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        await PostForm(admin, "/admin/settings/aiprofile", "/admin/settings/aiprofile", new()
        {
            ["name"] = "", ["provider"] = "openai", ["baseUrl"] = "https://openrouter.ai/api/v1", ["model"] = "meta/llama:free", ["apiKey"] = "sk-or-456",
        });

        string deepseekId, routerId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.DoesNotContain("sk-geheim", db.SiteSettings.First(s => s.Key == SettingKeys.AiProfiles).Value);
            var own = scope.ServiceProvider.GetRequiredService<OwnAi>();
            var set = await own.ProfilesAsync();
            Assert.Equal(2, set.Profiles.Count);
            deepseekId = set.Profiles.Single(p => p.Name == "DeepSeek Flash").Id;
            routerId = set.Profiles.Single(p => p.Name == "OpenRouter · meta/llama:free").Id;    // named from address + model
            Assert.Equal(deepseekId, set.ActiveId);                                             // the second was not activated
            var cfg = await own.GetAsync();
            Assert.Equal(("openai", "https://api.deepseek.com", "deepseek-chat", "sk-geheim-123"), (cfg.Provider, cfg.BaseUrl, cfg.Model, cfg.ApiKey));
            Assert.True(scope.ServiceProvider.GetRequiredService<AiService>().Enabled);
        }

        // The key is never sent back; saving without one keeps it. Activating switches what calls go through.
        var edit = await admin.GetStringAsync("/admin/settings/aiprofile/" + deepseekId);
        Assert.DoesNotContain("sk-geheim", edit);
        await PostForm(admin, "/admin/settings/aiprofile/" + deepseekId, "/admin/settings/aiprofile/" + deepseekId, new()
        {
            ["id"] = deepseekId, ["name"] = "DeepSeek Flash", ["provider"] = "openai", ["baseUrl"] = "https://api.deepseek.com", ["model"] = "deepseek-v4-flash", ["apiKey"] = "", ["activate"] = "true",
        });
        await PostForm(admin, "/admin/settings?handler=AiActivate", "/admin/settings?tab=ai", new() { ["id"] = routerId });
        using (var scope = _app.Services.CreateScope())
        {
            var own = scope.ServiceProvider.GetRequiredService<OwnAi>();
            var set = await own.ProfilesAsync();
            Assert.Equal(routerId, set.ActiveId);
            Assert.Equal("sk-geheim-123", own.Unprotect(set.Profiles.Single(p => p.Id == deepseekId).Key));
            Assert.Equal("deepseek-v4-flash", set.Profiles.Single(p => p.Id == deepseekId).Model);
            var cfg = await own.GetAsync();
            Assert.Equal(("meta/llama:free", "sk-or-456"), (cfg.Model, cfg.ApiKey));
        }

        // The active profile cannot be deleted; another one can. "" = back to the cloud.
        await PostForm(admin, "/admin/settings?handler=AiDelete", "/admin/settings?tab=ai", new() { ["id"] = routerId });
        await PostForm(admin, "/admin/settings?handler=AiDelete", "/admin/settings?tab=ai", new() { ["id"] = deepseekId });
        await PostForm(admin, "/admin/settings?handler=AiActivate", "/admin/settings?tab=ai", new() { ["id"] = "" });
        using (var scope = _app.Services.CreateScope())
        {
            var set = await scope.ServiceProvider.GetRequiredService<OwnAi>().ProfilesAsync();
            Assert.Equal([routerId], set.Profiles.Select(p => p.Id).ToArray());
            Assert.Equal("", set.ActiveId);
        }

        // A backup carries the site's settings, but not its AI profiles.
        using (var scope = _app.Services.CreateScope())
        {
            var transfer = scope.ServiceProvider.GetRequiredService<ContentTransferService>();
            var zip = await transfer.ExportAsync(new ContentTransferService.BackupOptions());
            using var archive = new ZipArchive(new MemoryStream(zip));
            using var reader = new StreamReader(archive.GetEntry("content.json")!.Open());
            var content = await reader.ReadToEndAsync();
            Assert.Contains("\"Settings\"", content);
            foreach (var key in SettingKeys.AiOwn) Assert.DoesNotContain("\"" + key + "\"", content);
        }
    }

    [Fact]
    public void A_site_set_up_before_profiles_keeps_its_connection_as_the_active_profile()
    {
        var old = new Dictionary<string, string>
        {
            [SettingKeys.AiOwnProvider] = "openai", [SettingKeys.AiOwnBaseUrl] = "https://api.deepseek.com", [SettingKeys.AiOwnModel] = "deepseek-chat",
            [SettingKeys.AiOwnApiKey] = "CIPHER",
        };
        string Get(string k) => old.TryGetValue(k, out var v) ? v : "";
        var set = OwnAi.Read(Get);
        var p = Assert.Single(set.Profiles);
        Assert.Equal(p.Id, set.ActiveId);
        Assert.Equal(("DeepSeek · deepseek-chat", "CIPHER"), (p.Name, p.Key));
        Assert.True(OwnAi.ActiveReady(Get));
        old[SettingKeys.AiOwnProvider] = "";     // configured, but the site went through the cloud
        Assert.Equal("", OwnAi.Read(Get).ActiveId);
    }

    [Fact]
    public async Task Every_chatgpt_profile_has_its_own_sign_in_the_old_one_is_moved_to_the_first_and_none_goes_into_a_backup()
    {
        using var scope = _app.Services.CreateScope();
        var own = scope.ServiceProvider.GetRequiredService<OwnAi>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await own.SetAsync(SettingKeys.AiOwnChatGptTokens, own.Protect("""{"accessToken":"a","refreshToken":"r"}"""));

        var accounts = scope.ServiceProvider.GetRequiredService<SiteChatGptAccounts>();
        await accounts.For("profil-a").GetStatusAsync(poll: false);       // reads its tokens → takes the old sign-in over
        string Raw(string key) => db.SiteSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefault() ?? "";
        Assert.Equal("", Raw(SettingKeys.AiOwnChatGptTokens));               // moved, not copied: a refresh token may be used once
        Assert.NotEqual("", Raw(SettingKeys.AiChatGptTokensOf("profil-a")));
        Assert.True(await own.HasSignInAsync("profil-a"));
        Assert.False(await own.HasSignInAsync("profil-b"));                  // a second profile signs in on its own

        Assert.True(SettingKeys.IsProtected(SettingKeys.AiChatGptTokensOf("profil-a")));
        var zip = await scope.ServiceProvider.GetRequiredService<ContentTransferService>().ExportAsync(new ContentTransferService.BackupOptions());
        using var archive = new ZipArchive(new MemoryStream(zip));
        using var reader = new StreamReader(archive.GetEntry("content.json")!.Open());
        Assert.DoesNotContain(SettingKeys.AiChatGptTokensPrefix, await reader.ReadToEndAsync());

        await own.ForgetSignInAsync("profil-a");
        Assert.False(await own.HasSignInAsync("profil-a"));
    }
}
