using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Assistant;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Ai;

/// <summary>
/// Admin → KI-Assistent. Three tabs: the conversation (chat on the left, the page it works on live on the right), the list
/// of earlier conversations, and the basic instruction. The page itself only starts and reads: a message is posted
/// (<see cref="OnPostSendAsync"/>) and the steps are polled (<see cref="OnGetStepsAsync"/>) while the runner works.
/// </summary>
// Attachments: up to six files of up to 25 MB. Size limits are resource filters, which Razor Pages honour on the page
// model, not on a handler method.
[RequestFormLimits(MultipartBodyLengthLimit = 160L * 1024 * 1024)]
[RequestSizeLimit(160L * 1024 * 1024)]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AssistantService _assistant;
    private readonly AssistantBlocks _blocks;
    private readonly AssistantAgent _agent;
    private readonly Localizer _t;

    public IndexModel(AppDbContext db, AssistantService assistant, AssistantBlocks blocks, AssistantAgent agent, Localizer t)
    {
        _db = db;
        _assistant = assistant;
        _blocks = blocks;
        _agent = agent;
        _t = t;
    }

    public bool Enabled { get; private set; }
    public AiConversation? Conversation { get; private set; }
    public List<ConversationRow> Conversations { get; private set; } = new();
    public string ScopeText { get; private set; } = "";
    public int? ScopePageId { get; private set; }
    public int? ScopeBlockId { get; private set; }
    public bool ScopeLocked { get; private set; }
    public string? PreviewUrl { get; private set; }
    public string SitePrompt { get; private set; } = "";
    public string OperatorInstruction { get; private set; } = "";
    public string Tab { get; private set; } = "chat";

    public sealed record ConversationRow(int Id, string Title, string Scope, DateTime UpdatedAt, int Runs, int Changes, int Tokens, string CreatedBy);

    public async Task OnGetAsync(int? id, int? pageId, int? blockId, string? tab)
    {
        Enabled = _assistant.Enabled;
        Tab = tab is "history" or "prompt" ? tab : "chat";
        if (id is int cid) Conversation = await _db.AiConversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid);
        ScopePageId = Conversation?.ScopePageId ?? pageId;
        ScopeBlockId = Conversation?.ScopeBlockId ?? (pageId is null ? null : blockId);
        ScopeLocked = Conversation?.ScopeLocked ?? ScopePageId is not null;
        (ScopeText, PreviewUrl) = await DescribeScopeAsync(ScopePageId, ScopeBlockId);

        var settings = await _db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.AssistantPrompt || s.Key == SettingKeys.AiInstruction || s.Key == SettingKeys.AiActionGuides).ToDictionaryAsync(s => s.Key, s => s.Value);
        SitePrompt = settings.TryGetValue(SettingKeys.AssistantPrompt, out var sp) && !string.IsNullOrWhiteSpace(sp) ? sp : AssistantPrompt.Default.Trim();
        OperatorInstruction = AssistantAgent.OperatorText(settings.TryGetValue(SettingKeys.AiInstruction, out var oi) ? oi ?? "" : "",
            settings.TryGetValue(SettingKeys.AiActionGuides, out var ag) ? ag ?? "" : "");

        var convs = await _db.AiConversations.AsNoTracking().OrderByDescending(c => c.UpdatedAt).Take(200).ToListAsync();
        var changeCounts = await _db.AiChanges.AsNoTracking().GroupBy(c => c.ConversationId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var pageTitles = await _db.Pages.AsNoTracking().Select(p => new { p.Id, p.Title }).ToDictionaryAsync(p => p.Id, p => p.Title);
        Conversations = convs.Select(c => new ConversationRow(c.Id, c.Title.Length > 0 ? c.Title : _t["assistant.untitled"],
            c.ScopePageId is int pid && pageTitles.TryGetValue(pid, out var pt) ? pt : _t["assistant.scopeSite"],
            c.UpdatedAt, c.Runs, changeCounts.TryGetValue(c.Id, out var n) ? n : 0, c.Tokens, c.CreatedBy)).ToList();
    }

    private async Task<(string text, string? url)> DescribeScopeAsync(int? pageId, int? blockId)
    {
        if (pageId is not int pid || await _db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid) is not { } page)
            return (_t["assistant.scopeSite"], "/");
        var url = AssistantTools.PublicUrl(page.Slug, page.Locale);
        if (blockId is int bid && await _db.ContentBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bid && b.PageId == pid) is { } block)
            return (string.Format(_t["assistant.scopeBlock"], _t[_blocks.Get(block.BlockType)?.Name ?? block.BlockType], page.Title), url);
        return (string.Format(_t["assistant.scopePage"], page.Title), url);
    }

    /// <summary>A message (with files). A first message creates the conversation with the scope it was opened with.</summary>
    public async Task<IActionResult> OnPostSendAsync(int? id, int? pageId, int? blockId, bool locked, string? message)
    {
        if (!_assistant.Enabled) return new JsonResult(new { ok = false, error = _t["assistant.disabled"] });
        var convId = id;
        if (convId is null)
        {
            var conv = await _assistant.CreateAsync(User.Identity?.Name ?? "", pageId, blockId, locked);
            convId = conv.Id;
        }
        var (ok, error) = await _assistant.SendAsync(convId.Value, message ?? "", Request.Form.Files, locked, ct: HttpContext.RequestAborted);
        return new JsonResult(new { ok, error, id = convId });
    }

    /// <summary>The steps after <paramref name="after"/>, the conversation's state and what can be undone. Polled while the
    /// runner works.</summary>
    public async Task<IActionResult> OnGetStepsAsync(int id, int after = 0)
    {
        var conv = await _db.AiConversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (conv is null) return NotFound();
        var steps = await _db.AiSteps.AsNoTracking().Where(s => s.ConversationId == id && s.Id > after).OrderBy(s => s.Id).Take(400).ToListAsync();
        var pageIds = new HashSet<int>();
        var parsed = steps.Select(s => (s, data: Parse(s.DataJson))).ToList();
        foreach (var (_, data) in parsed)
            if (data?["pageId"] is JsonValue v && v.TryGetValue<int>(out var pid)) pageIds.Add(pid);
        var urls = await _db.Pages.AsNoTracking().Where(p => pageIds.Contains(p.Id)).Select(p => new { p.Id, p.Slug, p.Locale })
            .ToDictionaryAsync(p => p.Id, p => AssistantTools.PublicUrl(p.Slug, p.Locale));
        var undo = await _db.AiChanges.AsNoTracking().Where(c => c.ConversationId == id && !c.Reverted).MaxAsync(c => (int?)c.Run);
        return new JsonResult(new
        {
            status = conv.Status,
            tokens = conv.Tokens,
            undoRun = conv.Status == "running" ? null : undo,
            steps = parsed.Select(x => new
            {
                id = x.s.Id, run = x.s.Run, kind = x.s.Kind, text = x.s.Text, at = x.s.CreatedAt,
                data = x.data,
                previewUrl = x.data?["pageId"] is JsonValue pv && pv.TryGetValue<int>(out var p) && urls.TryGetValue(p, out var u) ? u : null,
            }),
        });
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); } catch { return null; }
    }

    public IActionResult OnPostCancel(int id) => new JsonResult(new { ok = _assistant.Cancel(id) });

    public async Task<IActionResult> OnPostUndoAsync(int id)
    {
        var (ok, message) = await _assistant.UndoAsync(id);
        return new JsonResult(new { ok, message });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await _assistant.DeleteAsync(id);
        TempData["Flash"] = _t["assistant.deleted"];
        return RedirectToPage(new { tab = "history" });
    }

    public async Task<IActionResult> OnPostPromptAsync(string? prompt, bool reset)
    {
        var value = reset ? "" : (prompt ?? "").Replace("\r\n", "\n").Trim();
        // Saving the default unchanged stores nothing: a later improvement of the built-in text then reaches this site.
        if (value == AssistantPrompt.Default.Trim()) value = "";
        if (value.Length > 30000) value = value[..30000];
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AssistantPrompt);
        if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.AssistantPrompt, Value = value });
        else row.Value = value;
        await _db.SaveChangesAsync();
        TempData["Flash"] = reset ? _t["assistant.promptReset"] : _t["assistant.promptSaved"];
        return RedirectToPage(new { tab = "prompt" });
    }

    /// <summary>The complete system prompt as the next call would send it (for "Vollständige Anweisung anzeigen").</summary>
    public async Task<IActionResult> OnGetFullPromptAsync(int? id, int? pageId, int? blockId)
    {
        await _blocks.LoadAsync();
        var conv = id is int cid ? await _db.AiConversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid) : null;
        conv ??= new AiConversation { ScopePageId = pageId, ScopeBlockId = blockId, ScopeLocked = pageId is not null };
        var settings = await _db.SiteSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        string S(string k) => settings.TryGetValue(k, out var v) ? v ?? "" : "";
        var context = await _agent.ContextAsync(conv, S(SettingKeys.SiteName), S(SettingKeys.Languages), HttpContext.RequestAborted);
        var text = AssistantPrompt.Build(AssistantAgent.OperatorText(S(SettingKeys.AiInstruction), S(SettingKeys.AiActionGuides)), S(SettingKeys.AssistantPrompt), _blocks.CatalogText(), context);
        return Content(text, "text/plain; charset=utf-8");
    }
}
