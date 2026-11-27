using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin;
using MatCMS.Services.Assistant;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;

namespace MatCMS.Services.Mcp;

/// <summary>
/// The site's OWN MCP server at <c>/mcp</c>: an AI client (Claude, ChatGPT, Cursor …) works on a site WITHOUT a cloud, at once — not through the
/// cloud's queued content operations. Off until the site's admin switches it on (<see cref="SettingKeys.McpEnabled"/>, Admin → API-Schlüssel).
/// <list type="bullet">
/// <item><b>Who:</b> a site API key (<see cref="SiteApiKeyService"/>, Bearer <c>msk_…</c>) — checked on EVERY request by the middleware in
/// <see cref="Map"/>, which hands the key to the handlers in <c>HttpContext.Items</c>. Its core rights (<c>core.pages:write</c> …) decide the core
/// tools, its plugin rights the plugin operations; <c>tools/list</c> shows only what the key may call, and a call is checked again.</item>
/// <item><b>What:</b> the site AI assistant's tools (<see cref="AssistantTools"/>) — the SAME validation (<see cref="AssistantBlocks"/> is the trusted
/// boundary), the same write path, nested blocks and every language. Never <c>ContentOpApplier</c>/<c>BlockGenerator</c>, the weaker cloud path.</item>
/// <item><b>Undo and log:</b> the undo journal (<see cref="AiChange"/>) hangs off a conversation, so every key gets one (<see cref="AiConversation.Source"/>
/// = "mcp"; a new one after <see cref="ConversationGap"/> without a call). Every WRITE call is a run with its action step — the site's admin reads
/// what the client did in Admin → KI-Assistent and takes it back there, the client with <c>undo_last_change</c>.</item>
/// <item><b>Plugins:</b> the described, non-internal operations (<see cref="PluginContributions.ExternalOperations"/>); reading one needs
/// <c>{plugin}:read</c>, writing <c>{plugin}:write</c>, customers' data <c>{plugin}:personal</c> (and write as well when it writes). A personal answer
/// is not copied into the log.</item>
/// </list>
/// Stateless transport: no session lives in memory, so the server works behind the cloud's edge and across restarts.
/// </summary>
public sealed class SiteMcpServer
{
    public const string ItemKey = "mcp.sitekey";
    public const string ListOperations = "list_plugin_operations", RunOperation = "run_plugin_operation", UndoLast = "undo_last_change";

    /// <summary>How long a key's log stays one conversation: calls after a longer pause start a new one, so the admin's list reads as sessions.</summary>
    public static readonly TimeSpan ConversationGap = TimeSpan.FromHours(2);

    /// <summary>Write calls one after another: a run number per call and SQLite's single writer make parallel writes pointless anyway, and the
    /// "newest run" rule of undo needs them in an order.</summary>
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    private readonly AppDbContext _db;
    private readonly AssistantTools _tools;
    private readonly AssistantBlocks _blocks;
    private readonly AssistantUndo _undo;
    private readonly PluginContributions _plugins;

    public SiteMcpServer(AppDbContext db, AssistantTools tools, AssistantBlocks blocks, AssistantUndo undo, PluginContributions plugins)
    {
        _db = db;
        _tools = tools;
        _blocks = blocks;
        _undo = undo;
        _plugins = plugins;
    }

    // ============================================================================================== wiring

    public static void AddTo(IServiceCollection services)
    {
        services.AddScoped<SiteMcpServer>();
        services.AddMcpServer(o => o.ServerInfo = new Implementation { Name = "matcms-site", Title = "MatCMS", Version = AppVersion() })
            .WithHttpTransport(o =>
            {
                o.Stateless = true;
                // Per request (stateless = every request is its own session): the instructions name THIS site.
                o.ConfigureSessionOptions = async (http, options, ct) =>
                    options.ServerInstructions = await InstructionsAsync(http.RequestServices, ct);
            })
            .WithListToolsHandler(async (request, ct) =>
            {
                var http = request.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext!;
                return new ListToolsResult { Tools = request.Services!.GetRequiredService<SiteMcpServer>().ToolsFor(KeyOf(http)) };
            })
            .WithCallToolHandler(async (request, ct) =>
            {
                var http = request.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext!;
                var p = request.Params!;
                return await request.Services!.GetRequiredService<SiteMcpServer>().CallAsync(KeyOf(http), p.Name, Args(p.Arguments), ct);
            });
    }

    private static SiteApiKey KeyOf(HttpContext http) => http.Items[ItemKey] as SiteApiKey
        ?? throw new InvalidOperationException("MCP request without an authenticated site API key.");

    private static JsonObject Args(IDictionary<string, JsonElement>? arguments)
    {
        var o = new JsonObject();
        if (arguments is null) return o;
        foreach (var (k, v) in arguments) o[k] = JsonNode.Parse(v.GetRawText());
        return o;
    }

    private static string AppVersion() =>
        typeof(SiteMcpServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// The door: <c>/mcp</c> answers 404 while the server is switched off (the site does not even say it has one), 401 without a usable key, 429 over
    /// the key's limit (the counter <c>/api/v1</c> uses). Only then the MCP endpoint itself.
    /// </summary>
    public static void Map(WebApplication app)
    {
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/mcp"), branch => branch.Use(async (ctx, next) =>
        {
            var site = ctx.RequestServices.GetRequiredService<SiteContext>();
            if (site.Get(SettingKeys.McpEnabled) != "1")
            {
                // With a body: UseStatusCodePagesWithReExecute re-runs every EMPTY error answer as the status page, and for a POST that
                // ended as a bare 400 instead of the 404.
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("Not found.");
                return;
            }
            ctx.Response.Headers.CacheControl = "no-store";
            var keys = ctx.RequestServices.GetRequiredService<SiteApiKeyService>();
            var presented = PluginApiEndpoint.PresentedKey(ctx.Request);
            var key = presented.Length == 0 ? null : await keys.FindAsync(presented, ctx.RequestAborted);
            if (key is null)
            {
                ctx.Response.Headers.WWWAuthenticate = presented.Length == 0 ? "Bearer" : "Bearer error=\"invalid_token\"";
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = presented.Length == 0
                    ? "Kein API-Schlüssel. Senden Sie ihn als „Authorization: Bearer msk_…“."
                    : "Der API-Schlüssel ist unbekannt, widerrufen oder abgelaufen." });
                return;
            }
            if (!PluginApiEndpoint.CountCall(key.Id))
            {
                ctx.Response.Headers.RetryAfter = (60 - DateTime.UtcNow.Second).ToString(System.Globalization.CultureInfo.InvariantCulture);
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsJsonAsync(new { error = $"Zu viele Aufrufe (höchstens {PluginApiEndpoint.CallsPerMinute} pro Minute und Schlüssel)." });
                return;
            }
            await keys.TouchAsync(key.Id, ctx.RequestAborted);
            ctx.Items[ItemKey] = key;
            await next();
        }));
        app.MapMcp("/mcp").DisableAntiforgery();
    }

    /// <summary>What the client is told once, before any tool: which site, its languages, and how to work on it well.</summary>
    public static async Task<string> InstructionsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var site = sp.GetRequiredService<SiteContext>();
        var languages = site.Get(SettingKeys.Languages) is { Length: > 0 } l ? l : Localizer.DefaultCulture;
        await Task.CompletedTask;
        return $"""
            This is the MatCMS website "{site.Get(SettingKeys.SiteName)}" (languages: {languages}; default: {Localizer.DefaultCulture}).
            You change the LIVE site: every write is visible to visitors at once.
            How to work well:
            1. site_overview first (pages, menus, design, forms), block_catalog before writing blocks, read_page before changing a page.
            2. Prefer small changes: update_block for one block, add_blocks to insert; set_page_blocks rebuilds a whole page.
            3. Pictures only from the media library (list_media) or after upload_media/create_svg — an outside address is dropped.
            4. Warnings in an answer say what was dropped and why; correct it instead of repeating the same call.
            5. undo_last_change takes back your newest change, one call at a time. The site's admin sees every change you make and can undo it too.
            Answers and warnings are in German; write content in the language of the page.
            """;
    }

    // ============================================================================================== tools/list

    /// <summary>Whether a key may call a catalogue tool at all (the plugin tools need at least one operation it may run).</summary>
    public bool Allows(SiteApiKey key, SiteMcpTool tool) => tool.Name switch
    {
        ListOperations => OperationsFor(key).Count > 0,
        RunOperation => OperationsFor(key).Count > 0,
        UndoLast => key.Rights.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(r => r.EndsWith(":write", StringComparison.Ordinal)),
        _ => SiteApiKeyService.Allows(key.Rights, tool.Area, tool.Access),
    };

    public List<Tool> ToolsFor(SiteApiKey key) => SiteMcpCatalog.All.Where(t => Allows(key, t)).Select(t => new Tool
    {
        Name = t.Name,
        Title = t.Title,
        Description = t.Description,
        InputSchema = JsonDocument.Parse(t.Schema).RootElement.Clone(),
        Annotations = new ToolAnnotations
        {
            Title = t.Title, ReadOnlyHint = t.ReadOnly, DestructiveHint = t.ReadOnly ? false : t.Destructive,
            IdempotentHint = t.Idempotent, OpenWorldHint = false,
        },
    }).ToList();

    /// <summary>The plugin operations this key may run: described, never internal, and with the plugin's right for what the operation does.</summary>
    public List<PluginRegistry.OperationEntry> OperationsFor(SiteApiKey key) =>
        _plugins.ExternalOperations.Where(o => MayRun(key.Rights, o)).ToList();

    public static bool MayRun(string rights, PluginRegistry.OperationEntry o)
    {
        if (o.Spec is not { Internal: false } spec) return false;
        if (spec.PersonalData && !SiteApiKeyService.Allows(rights, o.Owner, ApiAccess.PersonalData)) return false;
        return SiteApiKeyService.Allows(rights, o.Owner, spec.Writes ? ApiAccess.Write : ApiAccess.Read)
            // A read of customers' data needs only "personal" — the right that exists for exactly that.
            || (spec.PersonalData && !spec.Writes);
    }

    // ============================================================================================== tools/call

    public async Task<CallToolResult> CallAsync(SiteApiKey key, string name, JsonObject args, CancellationToken ct)
    {
        var tool = SiteMcpCatalog.All.FirstOrDefault(t => t.Name == name);
        if (tool is null) return Error($"Unbekanntes Werkzeug „{name}“.");
        if (!Allows(key, tool)) return Error(tool.Area.Length > 0
            ? $"Dieser Schlüssel hat nicht das Recht „{tool.Area}:{SiteApiKeyService.Word(tool.Access)}“."
            : "Dieser Schlüssel darf das nicht.");

        _tools.Scope = new AssistantScope(null, null, false);
        _tools.OperationFilter = o => MayRun(key.Rights, o);
        await _blocks.LoadAsync(ct);

        switch (name)
        {
            case "block_catalog":
                return Text(new JsonObject { ["catalog"] = _blocks.CatalogText() });
            case ListOperations:
                return Text(new JsonObject
                {
                    ["operations"] = new JsonArray(OperationsFor(key).OrderBy(o => o.Owner).ThenBy(o => o.Name).Select(o => (JsonNode)new JsonObject
                    {
                        ["name"] = o.QualifiedName, ["plugin"] = o.Owner, ["pluginName"] = o.Info.Name, ["description"] = o.Spec!.Description,
                        ["arguments"] = o.Spec.Arguments, ["writes"] = o.Spec.Writes, ["personalData"] = o.Spec.PersonalData,
                    }).ToArray())
                });
            case UndoLast:
                return await UndoAsync(key, ct);
        }

        var writes = !tool.ReadOnly && (name != RunOperation || OperationWrites(args));
        var personal = name == RunOperation && OperationPersonal(args);
        // The assistant calls it plugin_action; the MCP name says what it is next to list_plugin_operations.
        var inner = name == RunOperation ? "plugin_action" : name;
        if (!writes)
        {
            var read = await _tools.RunAsync(inner, args, ct);
            if (personal) await LogAsync(key, name, args, read, personal: true, ct);
            return Result(read);
        }

        await WriteGate.WaitAsync(ct);
        try
        {
            var conv = await ConversationAsync(key, ct);
            conv.Runs++;
            conv.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _tools.ConversationId = conv.Id;
            _tools.Run = conv.Runs;

            ToolOutcome outcome;
            if (name == "upload_media")
            {
                var file = AssistantBlocks.Str(args["fileName"]).Trim();
                byte[] bytes;
                try { bytes = Convert.FromBase64String(AssistantBlocks.Str(args["dataBase64"]).Trim()); }
                catch (FormatException) { return Error("dataBase64 ist kein gültiges Base64."); }
                outcome = await _tools.UploadMediaAsync(file, bytes, AssistantBlocks.Str(args["alt"]), ct);
                args = new JsonObject { ["fileName"] = file, ["bytes"] = bytes.Length };   // the log keeps what, not the file a second time
            }
            else outcome = await _tools.RunAsync(inner, args, ct);

            await StepAsync(conv, name, args, outcome, personal, ct);
            return Result(outcome);
        }
        finally { WriteGate.Release(); }
    }

    // Qualified (blog.post.create) or a short name only one plugin declares — the same lookup the assistant uses.
    private PluginRegistry.OperationEntry? OperationOf(JsonObject args) =>
        _plugins.FindOperation(AssistantBlocks.Str(args["name"]).Trim()) is { Spec.Internal: false } o ? o : null;

    private bool OperationWrites(JsonObject args) => OperationOf(args)?.Spec?.Writes ?? true;
    private bool OperationPersonal(JsonObject args) => OperationOf(args)?.Spec?.PersonalData ?? false;

    /// <summary>The key's current log: its newest MCP conversation if it was used within <see cref="ConversationGap"/>, else a new one.</summary>
    private async Task<AiConversation> ConversationAsync(SiteApiKey key, CancellationToken ct)
    {
        var since = DateTime.UtcNow - ConversationGap;
        var conv = await _db.AiConversations
            .Where(c => c.Source == AiConversation.SourceMcp && c.ApiKeyId == key.Id && c.UpdatedAt >= since)
            .OrderByDescending(c => c.UpdatedAt).FirstOrDefaultAsync(ct);
        if (conv is not null) return conv;
        conv = new AiConversation
        {
            Source = AiConversation.SourceMcp, ApiKeyId = key.Id, CreatedBy = "api:" + key.Name,
            Title = $"MCP · {key.Name} · {DateTime.Now:dd.MM.yyyy HH:mm}",
        };
        _db.AiConversations.Add(conv);
        await _db.SaveChangesAsync(ct);
        return conv;
    }

    /// <summary>One action step, in the shape the assistant writes (so the admin's page draws it the same). An answer with customers' data is not
    /// copied: the log is read by every admin, the data belongs to the plugin's own pages.</summary>
    private async Task StepAsync(AiConversation conv, string tool, JsonObject args, ToolOutcome outcome, bool personal, CancellationToken ct)
    {
        _db.AiSteps.Add(new AiStep
        {
            ConversationId = conv.Id, Run = conv.Runs, Kind = "action", Text = outcome.Summary,
            DataJson = new JsonObject
            {
                ["tool"] = tool, ["args"] = args.DeepClone(), ["ok"] = outcome.Ok,
                ["warnings"] = new JsonArray(outcome.Warnings.Select(w => (JsonNode)w).ToArray()),
                ["result"] = personal ? JsonValue.Create("(personenbezogen – nicht protokolliert)") : outcome.Result?.DeepClone(),
                ["pageId"] = outcome.PageId, ["changed"] = outcome.Changed, ["via"] = "mcp",
            }.ToJsonString(),
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>A READ of customers' data is logged too (who looked at orders, and when) — without the answer.</summary>
    private async Task LogAsync(SiteApiKey key, string tool, JsonObject args, ToolOutcome outcome, bool personal, CancellationToken ct)
    {
        await WriteGate.WaitAsync(ct);
        try
        {
            var conv = await ConversationAsync(key, ct);
            conv.UpdatedAt = DateTime.UtcNow;
            await StepAsync(conv, tool, args, outcome, personal, ct);
        }
        finally { WriteGate.Release(); }
    }

    /// <summary>The newest change of this key, wherever it is — its newest conversation that still has an undoable run.</summary>
    private async Task<CallToolResult> UndoAsync(SiteApiKey key, CancellationToken ct)
    {
        await WriteGate.WaitAsync(ct);
        try
        {
            var convs = await _db.AiConversations.Where(c => c.Source == AiConversation.SourceMcp && c.ApiKeyId == key.Id)
                .OrderByDescending(c => c.UpdatedAt).Take(20).ToListAsync(ct);
            foreach (var conv in convs)
            {
                if (await _undo.UndoableRunAsync(conv.Id, ct) is not int run) continue;
                var lines = (await _undo.RevertAsync(conv.Id, run, ct)).Where(l => l.Length > 0).ToList();
                var text = $"Änderung {run} rückgängig gemacht" + (lines.Count > 0 ? ": " + string.Join(", ", lines) + "." : ".");
                _db.AiSteps.Add(new AiStep { ConversationId = conv.Id, Run = conv.Runs, Kind = "info", Text = text, DataJson = new JsonObject { ["undo"] = run, ["via"] = "mcp" }.ToJsonString() });
                conv.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return Text(new JsonObject { ["ok"] = true, ["summary"] = text });
            }
            return Error("Es gibt nichts rückgängig zu machen.");
        }
        finally { WriteGate.Release(); }
    }

    // ============================================================================================== answers

    private static CallToolResult Result(ToolOutcome o)
    {
        var body = new JsonObject { ["ok"] = o.Ok, ["summary"] = o.Summary };
        if (o.Result is not null) body["result"] = o.Result.DeepClone();
        if (o.Warnings.Count > 0) body["warnings"] = new JsonArray(o.Warnings.Select(w => (JsonNode)w).ToArray());
        return new CallToolResult { Content = [new TextContentBlock { Text = body.ToJsonString(Json) }], IsError = !o.Ok };
    }

    private static CallToolResult Text(JsonObject body) => new() { Content = [new TextContentBlock { Text = body.ToJsonString(Json) }] };

    private static CallToolResult Error(string message) => new()
    {
        Content = [new TextContentBlock { Text = new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString(Json) }], IsError = true,
    };

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
