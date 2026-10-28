using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Assistant;
using MatCMS.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>A stand-in for the cloud relay: answers from a script and keeps every request it was sent.</summary>
public sealed class FakeAi : IAiTransport
{
    private readonly Queue<string> _answers = new();
    public List<AiRequest> Requests { get; } = new();
    public void Say(params string[] answers) { foreach (var a in answers) _answers.Enqueue(a); }
    public Task<AiResponse> SendAsync(AiRequest request, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        var text = _answers.Count > 0 ? _answers.Dequeue() : "{\"message\":\"Fertig.\",\"actions\":[],\"done\":true}";
        // "ERROR:…" plays a provider that failed with that message.
        if (text.StartsWith("ERROR:")) return Task.FromResult(new AiResponse { Ok = false, Error = text[6..], Model = "fake-model" });
        return Task.FromResult(new AiResponse { Ok = true, Text = text, Model = "fake-model", PromptTokens = 10, CompletionTokens = 5 });
    }
}

/// <summary>The application with the fake relay in place of the cloud.</summary>
public sealed class AssistantApp : IDisposable
{
    private readonly AppFactory _base = new();
    public WebApplicationFactory<Program> App { get; }
    public FakeAi Ai { get; } = new();

    public AssistantApp()
    {
        App = _base.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IAiTransport>();
            s.AddSingleton<IAiTransport>(Ai);
        }));
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.AiTransport, Value = "cloud" });
        db.Media.Add(new Media { Url = "/uploads/berg.jpg", FileName = "berg.jpg", ContentType = "image/jpeg", Alt = "Berg" });
        db.SaveChanges();
    }

    public HttpClient Browser() => App.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private readonly SemaphoreSlim _adminGate = new(1, 1);
    private HttpClient? _admin;

    /// <summary>ONE signed-in browser for all tests of a class: /login allows ten sign-ins a minute per address, and a class with many tests
    /// would run into that limit.</summary>
    public async Task<HttpClient> AdminAsync()
    {
        await _adminGate.WaitAsync();
        try { if (_admin is null) { var c = Browser(); await AppFactory.LoginAsync(c); _admin = c; } return _admin; }
        finally { _adminGate.Release(); }
    }

    public T Use<T>(Func<AppDbContext, T> read)
    {
        using var scope = App.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public void Dispose()
    {
        App.Dispose();
        _base.Dispose();
    }
}

/// <summary>The AI assistant through HTTP, in the real application, with a scripted model: a run reads, builds, is shown step by
/// step, refuses what it must, and comes back with "Rückgängig".</summary>
public class AssistantTests : IClassFixture<AssistantApp>
{
    private readonly AssistantApp _app;
    public AssistantTests(AssistantApp app) => _app = app;

    private Task<HttpClient> AdminAsync() => _app.AdminAsync();

    /// <summary>Sends a message like the page does and waits until the run is over. Returns the conversation id and its steps.</summary>
    private async Task<(int id, JsonArray steps, JsonObject state)> SendAsync(HttpClient c, string message, int? id = null, int? page = null, int? block = null,
        bool locked = false, params (string name, byte[] bytes, string type)[] files)
    {
        var html = await c.GetStringAsync("/admin/ai");
        var form = new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(html)!), "__RequestVerificationToken" },
            { new StringContent(message), "message" },
            { new StringContent(locked ? "true" : "false"), "locked" },
        };
        if (id is int i) form.Add(new StringContent(i.ToString()), "id");
        if (page is int p) form.Add(new StringContent(p.ToString()), "pageId");
        if (block is int b) form.Add(new StringContent(b.ToString()), "blockId");
        foreach (var (name, bytes, type) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(type);
            form.Add(part, "files", name);
        }
        var res = await c.PostAsync("/admin/ai?handler=Send", form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var answer = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
        Assert.True(answer["ok"]!.GetValue<bool>(), answer["error"]?.ToString());
        var conv = answer["id"]!.GetValue<int>();

        for (var n = 0; n < 200; n++)
        {
            var state = JsonNode.Parse(await c.GetStringAsync($"/admin/ai?handler=Steps&id={conv}"))!.AsObject();
            if (state["status"]!.GetValue<string>() == "idle") return (conv, state["steps"]!.AsArray(), state);
            await Task.Delay(50);
        }
        throw new TimeoutException("the run did not finish");
    }

    private static IEnumerable<JsonObject> Kind(JsonArray steps, string kind) =>
        steps.Select(s => s!.AsObject()).Where(s => s["kind"]!.GetValue<string>() == kind);

    [Fact]
    public async Task A_run_builds_a_page_with_pictures_choices_and_nested_blocks_reports_what_it_dropped_and_can_be_undone()
    {
        var admin = await AdminAsync();
        _app.Ai.Say(
            """{"message":"Ich sehe mir die Website an.","actions":[{"tool":"site_overview","args":{}}],"done":false}""",
            """
            ```json
            {"message":"Ich lege die Seite an.","actions":[{"tool":"create_page","args":{"title":"Über uns","slug":"ueber-uns-ki","blocks":[
              {"type":"hero","data":{"heading":"Wir sind da","image":"/uploads/berg.jpg","heroStyle":"overlay","buttonUrl":"javascript:alert(1)","unbekannt":"x","imageHeight":"riesig"}},
              {"type":"richtext","data":{"heading":"Geschichte","body":"<p>Seit 1990.</p><script>alert(1)</script>"}},
              {"type":"accordion","data":{"heading":"Fragen"},"children":[{"type":"faq","data":{"question":"Wann?","answer":"<p>Immer.</p>"}}]},
              {"type":"faq","data":{"question":"Lose Frage"}},
              {"type":"html","data":{"html":"<script>x</script>"}},
              {"type":"hero","data":{"heading":"Bild fremd","image":"/uploads/gibtsnicht.jpg"}}
            ]}}],"done":false}
            ```
            """);
        var before = _app.Ai.Requests.Count;
        var (convId, steps, state) = await SendAsync(admin, "Leg eine Seite Über uns an.");

        var page = _app.Use(db => db.Pages.Include(p => p.Blocks).First(p => p.Slug == "ueber-uns-ki"));
        var blocks = page.Blocks.OrderBy(b => b.ParentId ?? 0).ThenBy(b => b.SortOrder).ToList();
        var top = blocks.Where(b => b.ParentId == null).OrderBy(b => b.SortOrder).ToList();
        Assert.Equal(new[] { "hero", "richtext", "accordion", "hero" }, top.Select(b => b.BlockType));
        var hero = JsonNode.Parse(top[0].DataJson)!.AsObject();
        Assert.Equal("/uploads/berg.jpg", hero["image"]!.GetValue<string>());
        Assert.Equal("overlay", hero["heroStyle"]!.GetValue<string>());
        Assert.Null(hero["buttonUrl"]);                     // javascript: refused
        Assert.Null(hero["unbekannt"]);
        Assert.Null(hero["imageHeight"]);                   // not one of the options
        Assert.DoesNotContain("<script", top[1].DataJson);
        Assert.Contains("Seit 1990.", top[1].DataJson);
        var faq = Assert.Single(blocks, b => b.ParentId == top[2].Id);
        Assert.Equal("faq", faq.BlockType);
        Assert.Null(JsonNode.Parse(top[3].DataJson)!["image"]);   // not in the media library

        // What the editor sees: the AI's messages, every action, and the warnings the model got back.
        var actions = Kind(steps, "action").ToList();
        Assert.Equal(new[] { "site_overview", "create_page" }, actions.Select(a => a["data"]!["tool"]!.GetValue<string>()));
        var warnings = actions[1]["data"]!["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.True(warnings.Any(w => w.Contains("javascript:alert(1)")), string.Join(" || ", warnings));
        Assert.Contains(warnings, w => w.Contains("unbekannt"));
        Assert.Contains(warnings, w => w.Contains("riesig"));
        Assert.Contains(warnings, w => w.Contains("\"faq\" kann nur innerhalb"));
        Assert.Contains(warnings, w => w.Contains("\"html\""));
        Assert.Contains(warnings, w => w.Contains("gibtsnicht.jpg"));
        Assert.Equal("/ueber-uns-ki", actions[1]["previewUrl"]!.GetValue<string>());
        Assert.Contains(Kind(steps, "assistant"), s => s["text"]!.GetValue<string>() == "Ich lege die Seite an." && s["data"]!["raw"] != null && s["data"]!["model"]!.GetValue<string>() == "fake-model");
        // A warning keeps the run going for one more look: the model got the results back and said "done".
        Assert.Equal(3, _app.Ai.Requests.Count - before);
        Assert.All(_app.Ai.Requests.Skip(before), r => Assert.True(r.Json && r.Purpose == "assistant"));
        Assert.Equal(1, state["undoRun"]!.GetValue<int>());

        // The system prompt carries the full catalogue (pictures and choices, not only text), the tools and the protocol.
        var system = _app.Ai.Requests.Last().Messages[0].Content;
        Assert.Contains("heroStyle", system);
        Assert.Contains("\"overlay\"", system);
        Assert.Contains("set_page_blocks", system);
        Assert.Contains("\"done\"", system);
        Assert.DoesNotContain("- html —", system);         // the raw-HTML block is not offered

        // Second run: change ONE field of the hero, the rest stays.
        _app.Ai.Say(
            """{"message":"","actions":[{"tool":"update_block","args":{"block":HERO,"data":{"heading":"Neu","heroStyle":null}}}],"done":true}""".Replace("HERO", top[0].Id.ToString()));
        (_, steps, state) = await SendAsync(admin, "Andere Überschrift, ohne Overlay.", convId);
        hero = JsonNode.Parse(_app.Use(db => db.ContentBlocks.First(b => b.Id == top[0].Id).DataJson))!.AsObject();
        Assert.Equal("Neu", hero["heading"]!.GetValue<string>());
        Assert.Equal("/uploads/berg.jpg", hero["image"]!.GetValue<string>());
        Assert.Null(hero["heroStyle"]);
        Assert.Equal(2, state["undoRun"]!.GetValue<int>());

        // Undo is newest first: run 2 brings the heading back, run 1 removes the page.
        var token = AppFactory.Token(await admin.GetStringAsync("/admin/ai"))!;
        var undo = await admin.PostAsync("/admin/ai?handler=Undo", new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = convId.ToString(), ["__RequestVerificationToken"] = token }));
        Assert.True(JsonNode.Parse(await undo.Content.ReadAsStringAsync())!["ok"]!.GetValue<bool>());
        hero = JsonNode.Parse(_app.Use(db => db.ContentBlocks.First(b => b.Id == top[0].Id).DataJson))!.AsObject();   // same id as before
        Assert.Equal("Wir sind da", hero["heading"]!.GetValue<string>());
        Assert.Equal("overlay", hero["heroStyle"]!.GetValue<string>());

        undo = await admin.PostAsync("/admin/ai?handler=Undo", new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = convId.ToString(), ["__RequestVerificationToken"] = token }));
        Assert.True(JsonNode.Parse(await undo.Content.ReadAsStringAsync())!["ok"]!.GetValue<bool>());
        Assert.False(_app.Use(db => db.Pages.Any(p => p.Slug == "ueber-uns-ki")));
        Assert.False(_app.Use(db => db.ContentBlocks.Any(b => b.Id == faq.Id)));
    }

    [Fact]
    public async Task Opened_on_a_block_with_Nur_hier_aendern_the_AI_may_change_that_block_only()
    {
        var (pageId, a, b) = _app.Use(db =>
        {
            var p = new Page { Title = "Scope", Slug = "scope-test", Locale = "de" };
            p.Blocks.Add(new ContentBlock { BlockType = "richtext", DataJson = "{\"heading\":\"A\"}", SortOrder = 0 });
            p.Blocks.Add(new ContentBlock { BlockType = "richtext", DataJson = "{\"heading\":\"B\"}", SortOrder = 1 });
            db.Pages.Add(p);
            db.SaveChanges();
            var ids = p.Blocks.OrderBy(x => x.SortOrder).Select(x => x.Id).ToList();
            return (p.Id, ids[0], ids[1]);
        });
        var admin = await AdminAsync();
        // Opened from the editor: the address carries the scope ("page" itself is a route name Razor Pages keeps for itself).
        var opened = await admin.GetStringAsync($"/admin/ai?pageId={pageId}&blockId={a}");
        Assert.Contains($"data-page=\"{pageId}\"", opened);
        Assert.Contains($"data-block=\"{a}\"", opened);
        _app.Ai.Say("""
            {"message":"","actions":[
              {"tool":"update_block","args":{"block":BLOCK_B,"data":{"heading":"fremd"}}},
              {"tool":"update_block","args":{"block":BLOCK_A,"data":{"heading":"eigen"}}},
              {"tool":"update_theme","args":{"values":{"AccentColor":"#ff0000"}}}
            ],"done":true}
            """.Replace("BLOCK_B", b.ToString()).Replace("BLOCK_A", a.ToString()));
        var (_, steps, _) = await SendAsync(admin, "Ändern.", page: pageId, block: a, locked: true);
        Assert.Equal("eigen", JsonNode.Parse(_app.Use(db => db.ContentBlocks.First(x => x.Id == a).DataJson))!["heading"]!.GetValue<string>());
        Assert.Equal("B", JsonNode.Parse(_app.Use(db => db.ContentBlocks.First(x => x.Id == b).DataJson))!["heading"]!.GetValue<string>());
        var oks = Kind(steps, "action").Select(s => s["data"]!["ok"]!.GetValue<bool>()).ToList();
        Assert.Equal(new[] { false, true, false }, oks);
        Assert.Contains("beschränkt", Kind(steps, "action").First()["data"]!["result"]!["error"]!.GetValue<string>());
        // The model is told where it stands.
        Assert.Contains($"block id {a}", _app.Ai.Requests.Last().Messages[0].Content);
    }

    [Fact]
    public async Task Attached_files_reach_the_model_a_picture_lands_in_the_media_library_and_a_text_file_is_read()
    {
        var admin = await AdminAsync();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var before = _app.Ai.Requests.Count;
        var (_, steps, _) = await SendAsync(admin, "Nimm das Foto und die Preise.", files: new[]
        {
            ("foto.png", png, "image/png"),
            ("preise.txt", System.Text.Encoding.UTF8.GetBytes("Kaffee 3,50 €\nTee 2,90 €"), "text/plain"),
            ("virus.exe", new byte[] { 1, 2, 3 }, "application/octet-stream"),
        });
        var user = Kind(steps, "user").Single();
        var files = user["data"]!["files"]!.AsArray();
        Assert.Equal(3, files.Count);
        var url = files[0]!["url"]!.GetValue<string>();
        Assert.StartsWith("/uploads/", url);
        Assert.True(_app.Use(db => db.Media.Any(m => m.Url == url && m.Tags == "KI-Anhang")));
        Assert.NotNull(files[2]!["error"]);

        var request = _app.Ai.Requests[before];
        var message = request.Messages.Last(m => m.Role == "user");
        Assert.Contains("Kaffee 3,50 €", message.Content);
        Assert.Contains(url, message.Content);                           // the model knows where the picture is now
        var picture = Assert.Single(message.Attachments);
        Assert.Equal("image/jpeg", picture.MediaType);                   // the scaled-down copy, not the upload itself
    }

    [Fact]
    public async Task The_basic_instruction_is_editable_and_goes_first_into_every_call()
    {
        var admin = await AdminAsync();
        var html = await admin.GetStringAsync("/admin/ai?tab=prompt");
        Assert.Contains("Du bist der KI-Assistent", html);               // the default is shown to start from
        var save = await admin.PostAsync("/admin/ai?handler=Prompt", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["prompt"] = "Schreibe immer im Ton eines Bergführers.", ["__RequestVerificationToken"] = AppFactory.Token(html)!,
        }));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        await SendAsync(admin, "Hallo");
        var system = _app.Ai.Requests.Last().Messages[0].Content;
        Assert.StartsWith("Schreibe immer im Ton eines Bergführers.", system);
        Assert.Contains("## So antwortest du", system);                  // the protocol is never part of what can be edited away

        var full = await admin.GetStringAsync("/admin/ai?handler=FullPrompt");
        Assert.StartsWith("Schreibe immer im Ton eines Bergführers.", full);

        html = await admin.GetStringAsync("/admin/ai?tab=prompt");
        await admin.PostAsync("/admin/ai?handler=Prompt", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["reset"] = "true", ["__RequestVerificationToken"] = AppFactory.Token(html)!,
        }));
        Assert.Equal("", _app.Use(db => db.SiteSettings.First(s => s.Key == SettingKeys.AssistantPrompt).Value));
    }

    [Fact]
    public void The_answer_is_read_leniently_and_anything_else_is_not_taken_for_the_protocol()
    {
        var p = AssistantAgent.Parse("Hier: ```json\n{\"message\":\"x\",\"actions\":[{\"name\":\"read_page\",\"arguments\":\"{\\\"page\\\":3}\"}],\"done\":\"true\"}\n```")!;
        Assert.Equal("x", p.Message);
        Assert.Equal("read_page", p.Actions.Single().Tool);
        Assert.Equal(3, p.Actions.Single().Args["page"]!.GetValue<int>());
        Assert.True(p.Done);
        Assert.Null(AssistantAgent.Parse("Ich habe die Seite geändert."));
        Assert.Null(AssistantAgent.Parse("{\"foo\":1}"));
    }

    [Fact]
    public async Task About_this_website_starts_from_a_hand_written_basic_instruction_is_saved_only_on_save_and_reaches_the_assistant()
    {
        async Task SetAsync(string key, string? value)
        {
            using var scope = _app.App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SiteSettings.RemoveRange(db.SiteSettings.Where(s => s.Key == key));
            if (value is not null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
            await db.SaveChangesAsync();
        }
        try
        {
            await SetAsync(SettingKeys.AiSiteContext, null);
            await SetAsync(SettingKeys.AssistantPrompt, "Wir sind die Kaffeeroesterei Kruemel und siezen.");
            var admin = await AdminAsync();
            var settings = await admin.GetStringAsync("/admin/settings?tab=ai");
            Assert.Contains("Wir sind die Kaffeeroesterei Kruemel und siezen.</textarea>", settings);   // offered as the start …
            using (var scope = _app.App.Services.CreateScope())                                          // … not stored
                Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteSettings.AnyAsync(s => s.Key == SettingKeys.AiSiteContext));

            var res = await AppFactory.PostAsync(admin, "/admin/settings?handler=AiContext", new() { ["context"] = "Wir duzen alle.", ["__RequestVerificationToken"] = AppFactory.Token(settings)! });
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            Assert.Contains("Wir duzen alle.</textarea>", await admin.GetStringAsync("/admin/settings?tab=ai"));
            var page = await admin.GetStringAsync("/admin/ai?tab=prompt");
            Assert.Contains("Wir duzen alle.", page);                                                    // shown as what comes before the basic instruction
            var full = await admin.GetStringAsync("/admin/ai?handler=FullPrompt");
            Assert.True(full.IndexOf("Wir duzen alle.") >= 0 && full.IndexOf("Wir duzen alle.") < full.IndexOf("Kaffeeroesterei"));

            // Emptied on purpose stays empty: the basic instruction is not offered again.
            await AppFactory.PostAsync(admin, "/admin/settings?handler=AiContext", new() { ["context"] = "", ["__RequestVerificationToken"] = AppFactory.Token(settings)! });
            Assert.DoesNotContain("Kaffeeroesterei Kruemel und siezen.</textarea>", await admin.GetStringAsync("/admin/settings?tab=ai"));
        }
        finally
        {
            await SetAsync(SettingKeys.AiSiteContext, null);
            await SetAsync(SettingKeys.AssistantPrompt, null);
        }
    }

    [Fact]
    public void Operator_then_site_context_then_the_guide_for_this_use_come_before_the_assistants_own_instruction()
    {
        var settings = new Dictionary<string, string>
        {
            [SettingKeys.AiInstruction] = "Marke: Röstkunst.",
            [SettingKeys.AiSiteContext] = "Wir siezen.",
            [SettingKeys.AiActionGuides] = "{\"assistant\":\"Nie Seiten löschen.\",\"seo\":\"kurz\",\"plugin:bot\":\"Keine Preise.\"}",
        };
        string? Get(string k) => settings.TryGetValue(k, out var v) ? v : null;
        var text = AssistantAgent.Layers(Get);
        Assert.True(text.IndexOf("Marke: Röstkunst.") < text.IndexOf("Wir siezen.") && text.IndexOf("Wir siezen.") < text.IndexOf("Nie Seiten löschen."));
        Assert.DoesNotContain("kurz", text);                                       // another use's guide is not this one's
        Assert.DoesNotContain("Keine Preise.", text);
        Assert.Contains("Keine Preise.", AiService.PreambleText(Get, AiService.PluginPurpose("bot")));
        settings[SettingKeys.AiActionGuides] = "kaputt";
        Assert.DoesNotContain("kaputt", AssistantAgent.Layers(Get));
        var prompt = AssistantPrompt.Build(text, "", "- hero …", "Heute.");
        Assert.True(prompt.IndexOf("Wir siezen.") < prompt.IndexOf("Du bist der KI-Assistent"));
        Assert.Equal("", AiService.PreambleText(_ => null, "assistant"));    // nothing set = nothing added
    }

    [Fact]
    public void An_svg_keeps_its_drawing_and_loses_everything_that_could_run()
    {
        var dirty = """
            <?xml version="1.0"?>
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 10 10" onload="alert(1)">
              <script>alert(2)</script>
              <style>@import url(https://evil.example/x.css); .a{fill:red}</style>
              <defs><linearGradient id="g"><stop offset="0" stop-color="#f00"/></linearGradient></defs>
              <a href="javascript:alert(3)"><rect width="10" height="10" fill="url(#g)"/></a>
              <circle cx="5" cy="5" r="3" style="fill:url(https://evil.example/p)" onclick="x()"/>
              <use xlink:href="#g"/><use href="https://evil.example/s.svg#x"/>
              <image href="data:image/png;base64,AAAA"/>
              <foreignObject><div xmlns="http://www.w3.org/1999/xhtml">html</div></foreignObject>
              <text x="1" y="9">Röstkunst</text>
            </svg>
            """;
        var clean = SafeSvg.Sanitize(dirty)!;
        foreach (var bad in new[] { "script", "onload", "onclick", "javascript:", "evil.example", "@import", "foreignObject", "<image", "<a " })
            Assert.DoesNotContain(bad, clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("viewBox=\"0 0 10 10\"", clean);
        Assert.Contains("fill=\"url(#g)\"", clean);
        Assert.Contains("Röstkunst", clean);
        Assert.Contains("href=\"#g\"", clean);
        Assert.Null(SafeSvg.Sanitize("<html><body>kein svg</body></html>"));
        Assert.Null(SafeSvg.Sanitize("<!DOCTYPE svg [<!ENTITY x \"y\">]><svg xmlns=\"http://www.w3.org/2000/svg\">&x;</svg>"));
    }

    [Fact]
    public async Task An_uploaded_svg_is_cleaned_and_served_so_it_cannot_run_and_the_ai_can_draw_a_logo_and_set_it()
    {
        var admin = await AdminAsync();
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4 4\" onload=\"alert(1)\"><rect width=\"4\" height=\"4\"/></svg>"));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
        form.Add(part, "file", "logo.svg");
        var up = await admin.PostAsync("/admin/api/upload", form);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var url = JsonNode.Parse(await up.Content.ReadAsStringAsync())!["url"]!.GetValue<string>();
        var served = await _app.Browser().GetAsync(url);
        Assert.Equal("image/svg+xml", served.Content.Headers.ContentType!.MediaType);
        Assert.Contains("default-src 'none'", served.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("onload", await served.Content.ReadAsStringAsync());

        _app.Ai.Say(
            """{"message":"","actions":[{"tool":"create_svg","args":{"name":"Logo Bergblick","alt":"Logo","svg":"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 120 40\"><script>x</script><text x=\"0\" y=\"30\">Bergblick</text></svg>"}}],"done":false}""",
            """{"message":"","actions":[{"tool":"list_media","args":{"search":"logo-bergblick"}}],"done":false}""");
        var (convId, steps, _) = await SendAsync(admin, "Zeichne ein Logo.");
        var made = Kind(steps, "action").First()["data"]!;
        Assert.True(made["ok"]!.GetValue<bool>());
        var logo = made["result"]!["url"]!.GetValue<string>();
        Assert.DoesNotContain("script", await (await _app.Browser().GetAsync(logo)).Content.ReadAsStringAsync());

        _app.Ai.Say("""{"message":"Logo gesetzt.","actions":[{"tool":"update_settings","args":{"logoUrl":"LOGO","faviconUrl":"LOGO"}}],"done":true}""".Replace("LOGO", logo));
        await SendAsync(admin, "Setz es als Logo und Favicon.", convId);
        Assert.Equal(logo, _app.Use(db => db.SiteSettings.First(s => s.Key == SettingKeys.LogoUrl).Value));
        Assert.Equal(logo, _app.Use(db => db.SiteSettings.First(s => s.Key == SettingKeys.FaviconUrl).Value));
    }

    [Fact]
    public void Links_the_assistant_may_write()
    {
        foreach (var ok in new[] { "/kontakt", "#faq", "https://example.de", "mailto:a@b.de", "tel:+49123" })
            Assert.True(AssistantBlocks.IsSafeLink(ok), ok);
        foreach (var bad in new[] { "javascript:alert(1)", "//evil.example", "data:text/html,x", "vbscript:x" })
            Assert.False(AssistantBlocks.IsSafeLink(bad), bad);
    }
}
