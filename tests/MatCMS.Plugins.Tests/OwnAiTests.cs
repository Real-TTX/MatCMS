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

    [Fact]
    public async Task An_own_api_key_switches_ai_on_is_stored_encrypted_kept_on_save_and_never_goes_into_a_backup()
    {
        var admin = _app.Browser();
        await AppFactory.LoginAsync(admin);
        var html = await admin.GetStringAsync("/admin/settings?tab=ai");
        Assert.Contains("data-panel=\"ai\"", html);
        var save = await admin.PostAsync("/admin/settings?handler=Ai", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["provider"] = "openai", ["baseUrl"] = "https://api.deepseek.com/", ["model"] = "deepseek-chat", ["apiKey"] = "sk-geheim-123",
            ["__RequestVerificationToken"] = AppFactory.Token(html)!,
        }));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = db.SiteSettings.First(s => s.Key == SettingKeys.AiOwnApiKey).Value;
            Assert.DoesNotContain("sk-geheim", stored);
            var own = await scope.ServiceProvider.GetRequiredService<OwnAi>().GetAsync();
            Assert.Equal(("openai", "https://api.deepseek.com", "deepseek-chat", "sk-geheim-123"), (own.Provider, own.BaseUrl, own.Model, own.ApiKey));
            Assert.True(scope.ServiceProvider.GetRequiredService<AiService>().Enabled);
        }

        // Saving again without a key keeps the stored one.
        html = await admin.GetStringAsync("/admin/settings?tab=ai");
        Assert.DoesNotContain("sk-geheim", html);
        await admin.PostAsync("/admin/settings?handler=Ai", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["provider"] = "openai", ["baseUrl"] = "", ["model"] = "gpt-4o-mini", ["apiKey"] = "", ["__RequestVerificationToken"] = AppFactory.Token(html)!,
        }));
        using (var scope = _app.Services.CreateScope())
            Assert.Equal("sk-geheim-123", (await scope.ServiceProvider.GetRequiredService<OwnAi>().GetAsync()).ApiKey);

        // A backup carries the site's settings, but not its own AI connection.
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
}
