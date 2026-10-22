using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Assistant;

/// <summary>
/// One run of the assistant: the editor's message is in the history, now the model and the tools take turns until the
/// model says it is done (or asks something back). Every turn is written down as it happens — the model's message, every
/// action with what it changed, every error — so the editor watches the work instead of waiting for a result.
/// <para>The protocol is a JSON object per answer (message, actions, done) rather than a provider's native tool calling:
/// it works the same through the cloud relay with every model the cloud may be pointed at (DeepSeek, OpenAI, a ChatGPT
/// account), and the relay stays one plain request. An answer that is not that object is asked for again twice, then
/// shown as it is.</para>
/// <para>Bounded: at most <see cref="MaxCallsPerRun"/> model calls and <see cref="MaxActionsPerReply"/> actions per answer.
/// A run that hits the limit stops and says so; the editor can tell it to go on.</para>
/// </summary>
public sealed class AssistantAgent
{
    private readonly AppDbContext _db;
    private readonly AiService _ai;
    private readonly AssistantTools _tools;
    private readonly AssistantBlocks _blocks;
    private readonly AssistantFiles _files;
    private readonly Localizer _t;
    private readonly ILogger<AssistantAgent> _log;

    public const int MaxCallsPerRun = 30, MaxActionsPerReply = 8;

    /// <summary>Older tool results are shortened to this in the request — the newest few stay whole.</summary>
    private const int OldResultChars = 2500, KeepWholeResults = 4;

    public AssistantAgent(AppDbContext db, AiService ai, AssistantTools tools, AssistantBlocks blocks, AssistantFiles files, Localizer t, ILogger<AssistantAgent> log)
    {
        _db = db;
        _ai = ai;
        _tools = tools;
        _blocks = blocks;
        _files = files;
        _t = t;
        _log = log;
    }

    /// <summary>A message as the model sees it. <c>Tool</c> marks a results message (so it may be shortened later).</summary>
    public sealed record HistoryMessage(string Role, string Content, bool Tool = false);

    public static List<HistoryMessage> ReadHistory(string? json)
    {
        try { return JsonSerializer.Deserialize<List<HistoryMessage>>(json ?? "[]") ?? new(); }
        catch { return new(); }
    }

    public async Task RunAsync(int conversationId, CancellationToken ct)
    {
        var conv = await _db.AiConversations.FirstAsync(c => c.Id == conversationId, ct);
        var run = conv.Runs;
        _tools.ConversationId = conv.Id;
        _tools.Run = run;
        _tools.Scope = new AssistantScope(conv.ScopePageId, conv.ScopeBlockId, conv.ScopeLocked);
        var history = ReadHistory(conv.HistoryJson);
        try
        {
            await _blocks.LoadAsync(ct);
            var (imageIndex, images) = await AttachmentsOfRunAsync(conv.Id, run, ct);
            var system = await SystemPromptAsync(conv, ct);
            var badAnswers = 0;
            var finished = false;
            for (var call = 0; call < MaxCallsPerRun && !finished; call++)
            {
                ct.ThrowIfCancellationRequested();
                var request = new AiRequest { Purpose = "assistant", MaxTokens = 12000, Json = true, Messages = Messages(system, history, imageIndex, images) };
                var answer = await _ai.SendAsync(request, ct);
                conv.Tokens += answer.PromptTokens + answer.CompletionTokens;
                var meta = new JsonObject
                {
                    ["model"] = answer.Model, ["promptTokens"] = answer.PromptTokens, ["completionTokens"] = answer.CompletionTokens,
                    ["note"] = answer.Note, ["raw"] = answer.Text,
                };
                if (!answer.Ok)
                {
                    await StepAsync(conv.Id, run, "error", answer.Error ?? "Der KI-Aufruf ist fehlgeschlagen.", meta, ct);
                    break;
                }

                var parsed = Parse(answer.Text);
                if (parsed is null)
                {
                    history.Add(new("assistant", Trim(answer.Text ?? "", 4000)));
                    if (badAnswers++ < 2)
                    {
                        history.Add(new("user", "Deine letzte Antwort war nicht das verlangte JSON-Objekt {\"message\", \"actions\", \"done\"}. Antworte jetzt NUR mit diesem JSON-Objekt."));
                        await StepAsync(conv.Id, run, "info", "Die Antwort hatte nicht das verlangte Format – die KI wird noch einmal gefragt.", meta, ct);
                        continue;
                    }
                    await StepAsync(conv.Id, run, "assistant", answer.Text ?? "", meta, ct);
                    break;
                }

                history.Add(new("assistant", parsed.Canonical));
                meta["actions"] = parsed.Actions.Count;
                // A last answer that acts AND says it is done describes the result ("Erledigt: …") — shown after its actions,
                // where it reads true, rather than before them.
                var closing = parsed.Done && parsed.Actions.Count > 0 && parsed.Message.Length > 0;
                if (!closing) await StepAsync(conv.Id, run, "assistant", parsed.Message, meta, ct);
                if (parsed.Actions.Count == 0) break;

                var results = new JsonArray();
                var problems = false;
                foreach (var a in parsed.Actions.Take(MaxActionsPerReply))
                {
                    ct.ThrowIfCancellationRequested();
                    var outcome = await _tools.RunAsync(a.Tool, a.Args, ct);
                    problems |= !outcome.Ok || outcome.Warnings.Count > 0;
                    await StepAsync(conv.Id, run, "action", outcome.Summary, new JsonObject
                    {
                        ["tool"] = a.Tool, ["args"] = a.Args.DeepClone(), ["ok"] = outcome.Ok,
                        ["warnings"] = new JsonArray(outcome.Warnings.Select(w => (JsonNode)w).ToArray()),
                        ["result"] = outcome.Result?.DeepClone(), ["pageId"] = outcome.PageId, ["changed"] = outcome.Changed,
                    }, ct);
                    results.Add(new JsonObject
                    {
                        ["tool"] = a.Tool, ["ok"] = outcome.Ok, ["result"] = outcome.Result?.DeepClone(),
                        ["warnings"] = new JsonArray(outcome.Warnings.Select(w => (JsonNode)w).ToArray()),
                    });
                }
                if (parsed.Actions.Count > MaxActionsPerReply)
                    results.Add(new JsonObject { ["note"] = $"Nur die ersten {MaxActionsPerReply} Aktionen wurden ausgeführt – schicke den Rest im nächsten Schritt." });
                history.Add(new("user", "Ergebnisse der Aktionen:\n" + results.ToJsonString(), Tool: true));
                await SaveHistoryAsync(conv, history, ct);
                if (closing) await StepAsync(conv.Id, run, "assistant", parsed.Message, meta, ct);

                // "done" with actions that all went through ends the run; a refusal or a warning gets the model one more look.
                finished = parsed.Done && !problems;
                if (call == MaxCallsPerRun - 1 && !finished)
                    await StepAsync(conv.Id, run, "info", $"Nach {MaxCallsPerRun} Schritten angehalten. Schreib „weiter“, wenn die KI weitermachen soll.", null, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StepAsync(conv.Id, run, "info", "Abgebrochen.", null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Assistant run {Conversation}/{Run} failed", conversationId, run);
            await StepAsync(conv.Id, run, "error", "Interner Fehler: " + ex.Message, null, CancellationToken.None);
        }
        finally
        {
            conv.HistoryJson = JsonSerializer.Serialize(history);
            conv.Status = "idle";
            conv.UpdatedAt = DateTime.UtcNow;
            // Whatever a failed tool left half-done in the change tracker must not ride along with the final save.
            foreach (var e in _db.ChangeTracker.Entries().Where(e => e.Entity is not AiConversation && e.State != EntityState.Unchanged).ToList())
                e.State = EntityState.Detached;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // ============================================================================================== the request

    /// <summary>The messages of one call: the system prompt, then the history. Older results are shortened, the pictures of
    /// THIS run ride on its user message (earlier runs' pictures are not sent again — the model has described them by now,
    /// and they are in the media library under their address).</summary>
    public static List<AiMessage> Messages(string system, List<HistoryMessage> history, int imageIndex, List<AiAttachment> images)
    {
        var list = new List<AiMessage> { new() { Role = "system", Content = system } };
        var toolIdx = history.Select((m, i) => (m, i)).Where(x => x.m.Tool).Select(x => x.i).ToList();
        var keepWhole = toolIdx.Skip(Math.Max(0, toolIdx.Count - KeepWholeResults)).ToHashSet();
        for (var i = 0; i < history.Count; i++)
        {
            var m = history[i];
            var content = m.Tool && !keepWhole.Contains(i) ? Trim(m.Content, OldResultChars) + " …[älteres Ergebnis gekürzt – bei Bedarf neu lesen]" : m.Content;
            var msg = new AiMessage { Role = m.Role == "assistant" ? "assistant" : "user", Content = content };
            if (i == imageIndex) msg.Attachments = images;
            list.Add(msg);
        }
        return list;
    }

    private async Task<string> SystemPromptAsync(AiConversation conv, CancellationToken ct)
    {
        var settings = await _db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.AiInstruction || s.Key == SettingKeys.AiActionGuides || s.Key == SettingKeys.AssistantPrompt
                     || s.Key == SettingKeys.SiteName || s.Key == SettingKeys.Languages)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        string S(string k) => settings.TryGetValue(k, out var v) ? v ?? "" : "";
        var context = await ContextAsync(conv, S(SettingKeys.SiteName), S(SettingKeys.Languages), ct);
        return AssistantPrompt.Build(OperatorText(S(SettingKeys.AiInstruction), S(SettingKeys.AiActionGuides)), S(SettingKeys.AssistantPrompt), _blocks.CatalogText() + _tools.PluginActionsText(), context);
    }

    /// <summary>What the cloud's profile says to the assistant: its standing instruction plus, from the per-action map, the
    /// guidance for "assistant". Both are the operator's; the site's own basic instruction comes after them.</summary>
    public static string OperatorText(string instruction, string guidesJson)
    {
        var guide = "";
        try
        {
            var map = string.IsNullOrWhiteSpace(guidesJson) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(guidesJson);
            if (map is not null && map.TryGetValue("assistant", out var g)) guide = (g ?? "").Trim();
        }
        catch (JsonException) { /* a broken map is no guidance */ }
        return string.Join("\n\n", new[] { instruction.Trim(), guide }.Where(x => x.Length > 0));
    }

    /// <summary>Where the assistant stands: the date, the site, and the page/block it was opened on.</summary>
    public async Task<string> ContextAsync(AiConversation conv, string siteName, string languages, CancellationToken ct)
    {
        var lines = new List<string>
        {
            $"Heute ist {DateTime.Now:dd.MM.yyyy}. Website: „{(siteName.Length > 0 ? siteName : "ohne Namen")}“, Standardsprache {Localizer.DefaultCulture}, aktive Sprachen: {(languages.Length > 0 ? languages : Localizer.DefaultCulture)}.",
        };
        if (conv.ScopePageId is int pid && await _db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct) is { } page)
        {
            lines.Add($"Der Nutzer hat dich im Editor der Seite „{page.Title}“ geöffnet (page id {page.Id}, {AssistantTools.PublicUrl(page.Slug, page.Locale)}). „Diese Seite“ meint diese Seite.");
            if (conv.ScopeBlockId is int bid && await _db.ContentBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bid, ct) is { } block)
                lines.Add($"Gewählt ist der Block „{_t[_blocks.Get(block.BlockType)?.Name ?? block.BlockType]}“ (block id {block.Id}, type {block.BlockType}). „Dieser Block“ meint ihn.");
            if (conv.ScopeLocked)
                lines.Add(conv.ScopeBlockId is not null
                    ? "Du darfst NUR diesen Block (und was in ihm steckt) ändern. Lesen darfst du alles."
                    : "Du darfst NUR diese Seite ändern. Lesen darfst du alles.");
        }
        else lines.Add("Der Nutzer arbeitet an der ganzen Website.");
        return string.Join("\n", lines);
    }

    private async Task<(int index, List<AiAttachment> images)> AttachmentsOfRunAsync(int conversationId, int run, CancellationToken ct)
    {
        var step = await _db.AiSteps.AsNoTracking().Where(s => s.ConversationId == conversationId && s.Run == run && s.Kind == "user")
            .OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (step?.DataJson is null) return (-1, new());
        var data = JsonNode.Parse(step.DataJson) as JsonObject;
        var index = data?["historyIndex"]?.GetValue<int>() ?? -1;
        var images = new List<AiAttachment>();
        foreach (var f in (data?["files"] as JsonArray) ?? new JsonArray())
            if (f is JsonObject o && _files.ModelAttachment(o["modelFile"]?.GetValue<string>(), o["name"]?.GetValue<string>() ?? "Bild") is { } a)
                images.Add(a);
        return (index, images);
    }

    // ============================================================================================== the answer

    public sealed record ParsedAction(string Tool, JsonObject Args);
    public sealed record Parsed(string Message, List<ParsedAction> Actions, bool Done, string Canonical);

    /// <summary>Reads the model's answer leniently: a stray Markdown fence or prose around the object, "name"/"action" for
    /// "tool", "arguments"/"input"/"params" for "args", a done given as text. Null = not the protocol at all.</summary>
    public static Parsed? Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();
        }
        var a = s.IndexOf('{');
        var b = s.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        JsonObject? obj;
        try { obj = JsonNode.Parse(s[a..(b + 1)]) as JsonObject; } catch { return null; }
        if (obj is null || !(obj.ContainsKey("message") || obj.ContainsKey("actions") || obj.ContainsKey("done"))) return null;

        var message = AssistantBlocks.Str(obj["message"]).Trim();
        var actions = new List<ParsedAction>();
        if (obj["actions"] is JsonArray arr)
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var tool = AssistantBlocks.Str(o["tool"] ?? o["name"] ?? o["action"]).Trim();
                if (tool.Length == 0) continue;
                var args = (o["args"] ?? o["arguments"] ?? o["input"] ?? o["params"]) switch
                {
                    JsonObject x => (JsonObject)x.DeepClone(),
                    JsonValue v when v.TryGetValue<string>(out var str) && TryObject(str) is { } parsedArgs => parsedArgs,
                    _ => new JsonObject(),
                };
                actions.Add(new ParsedAction(tool, args));
            }
        var done = obj["done"] is JsonValue dv && ((dv.TryGetValue<bool>(out var db) && db) || (dv.TryGetValue<string>(out var ds) && ds.Trim().ToLowerInvariant() is "true" or "ja" or "yes"));
        var canonical = new JsonObject
        {
            ["message"] = message,
            ["actions"] = new JsonArray(actions.Select(x => (JsonNode)new JsonObject { ["tool"] = x.Tool, ["args"] = x.Args.DeepClone() }).ToArray()),
            ["done"] = done,
        }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return new Parsed(message, actions, done, canonical);
    }

    private static JsonObject? TryObject(string s)
    {
        try { return JsonNode.Parse(s) as JsonObject; } catch { return null; }
    }

    // ============================================================================================== bookkeeping

    private async Task StepAsync(int conversationId, int run, string kind, string text, JsonObject? data, CancellationToken ct)
    {
        _db.AiSteps.Add(new AiStep
        {
            ConversationId = conversationId, Run = run, Kind = kind, Text = Trim(text, 20000),
            DataJson = data?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task SaveHistoryAsync(AiConversation conv, List<HistoryMessage> history, CancellationToken ct)
    {
        conv.HistoryJson = JsonSerializer.Serialize(history);
        conv.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
