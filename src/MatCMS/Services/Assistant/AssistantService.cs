using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Assistant;

/// <summary>
/// The assistant as the admin page uses it: start a conversation, send a message (with files), stop, undo, delete. The
/// work itself is <see cref="AssistantAgent"/>, run by <see cref="AssistantRunner"/>; this is the bookkeeping around it, kept
/// out of the page so the tests drive exactly what the page drives.
/// </summary>
public sealed class AssistantService
{
    private readonly AppDbContext _db;
    private readonly AssistantFiles _files;
    private readonly AssistantRunner _runner;
    private readonly AssistantUndo _undo;
    private readonly AiService _ai;

    public AssistantService(AppDbContext db, AssistantFiles files, AssistantRunner runner, AssistantUndo undo, AiService ai)
    {
        _db = db;
        _files = files;
        _runner = runner;
        _undo = undo;
        _ai = ai;
    }

    public bool Enabled => _ai.Enabled;

    public async Task<AiConversation> CreateAsync(string user, int? pageId, int? blockId, bool locked, CancellationToken ct = default)
    {
        // A block that is not on the page (or a page that is not there) is dropped rather than trusted.
        if (pageId is int p && !await _db.Pages.AnyAsync(x => x.Id == p, ct)) pageId = null;
        if (blockId is int b && (pageId is null || !await _db.ContentBlocks.AnyAsync(x => x.Id == b && x.PageId == pageId, ct))) blockId = null;
        var conv = new AiConversation
        {
            CreatedBy = user, ScopePageId = pageId, ScopeBlockId = blockId, ScopeLocked = locked && pageId is not null,
        };
        _db.AiConversations.Add(conv);
        await _db.SaveChangesAsync(ct);
        return conv;
    }

    /// <summary>Stores the message (and what was attached) and starts the run. Refused while a run is going.</summary>
    public async Task<(bool ok, string? error)> SendAsync(int conversationId, string message, IEnumerable<IFormFile> files, bool? locked = null, bool start = true, CancellationToken ct = default)
    {
        if (!_ai.Enabled) return (false, "KI ist für diese Website nicht aktiviert.");
        var conv = await _db.AiConversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conv is null) return (false, "Gespräch nicht gefunden.");
        if (conv.Status == "running" || _runner.IsRunning(conv.Id)) return (false, "Die KI arbeitet noch – warte, bis sie fertig ist, oder brich ab.");
        message = (message ?? "").Trim();
        var attached = await _files.AcceptAsync(conv.Id, files, ct);
        if (message.Length == 0 && attached.Count == 0) return (false, "Schreib, was die KI tun soll.");
        if (message.Length > 20000) message = message[..20000];
        if (locked is bool l && conv.ScopePageId is not null) conv.ScopeLocked = l;

        var history = AssistantAgent.ReadHistory(conv.HistoryJson);
        history.Add(new AssistantAgent.HistoryMessage("user", ModelText(message, attached)));
        conv.Runs++;
        conv.Status = "running";
        conv.UpdatedAt = DateTime.UtcNow;
        if (conv.Title.Length == 0) conv.Title = Title(message.Length > 0 ? message : attached[0].Name);
        conv.HistoryJson = JsonSerializer.Serialize(history);

        var data = new JsonObject
        {
            ["historyIndex"] = history.Count - 1,
            ["files"] = new JsonArray(attached.Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name, ["kind"] = f.Kind, ["url"] = f.MediaUrl, ["modelFile"] = f.ModelFile,
                ["chars"] = f.Text?.Length ?? 0, ["error"] = f.Error,
            }).ToArray()),
        };
        _db.AiSteps.Add(new AiStep { ConversationId = conv.Id, Run = conv.Runs, Kind = "user", Text = message, DataJson = data.ToJsonString() });
        await _db.SaveChangesAsync(ct);
        if (start) _runner.Enqueue(conv.Id);
        return (true, null);
    }

    /// <summary>What the model reads for a message: the words, then one line per attachment (where it is now), then the
    /// text of each document.</summary>
    public static string ModelText(string message, List<AttachedFile> files)
    {
        var sb = new StringBuilder(message);
        foreach (var f in files)
        {
            sb.Append("\n\n[Anhang: ").Append(f.Name);
            if (f.Error is not null) sb.Append(" – nicht verwendbar: ").Append(f.Error);
            else if (f.Kind == "image") sb.Append(" – Bild").Append(f.MediaUrl is null ? "" : $", liegt in der Mediathek als {f.MediaUrl} (so auf einer Seite verwendbar)");
            else if (f.MediaUrl is not null) sb.Append(" – Dokument, in der Mediathek als ").Append(f.MediaUrl).Append(" (verlinkbar)");
            sb.Append(']');
        }
        foreach (var f in files.Where(f => !string.IsNullOrWhiteSpace(f.Text)))
            sb.Append("\n\n--- Inhalt von ").Append(f.Name).Append(" ---\n").Append(f.Text).Append("\n--- Ende von ").Append(f.Name).Append(" ---");
        return sb.ToString();
    }

    public bool Cancel(int conversationId) => _runner.Cancel(conversationId);

    /// <summary>Undoes the newest run that changed something; writes what was restored into the conversation.</summary>
    public async Task<(bool ok, string message)> UndoAsync(int conversationId, CancellationToken ct = default)
    {
        var conv = await _db.AiConversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conv is null) return (false, "Gespräch nicht gefunden.");
        if (conv.Status == "running") return (false, "Erst abwarten oder abbrechen – die KI arbeitet noch.");
        if (await _undo.UndoableRunAsync(conv.Id, ct) is not int run) return (false, "Es gibt nichts rückgängig zu machen.");
        var lines = (await _undo.RevertAsync(conv.Id, run, ct)).Where(l => l.Length > 0).ToList();
        var text = $"Auftrag {run} rückgängig gemacht" + (lines.Count > 0 ? ": " + string.Join(", ", lines) + "." : ".");
        _db.AiSteps.Add(new AiStep { ConversationId = conv.Id, Run = conv.Runs, Kind = "info", Text = text, DataJson = new JsonObject { ["undo"] = run }.ToJsonString() });
        // The model must know too, or its next step builds on blocks that are gone.
        var history = AssistantAgent.ReadHistory(conv.HistoryJson);
        history.Add(new AssistantAgent.HistoryMessage("user", $"[Hinweis: Der Nutzer hat alle Änderungen deines Auftrags {run} rückgängig gemacht. Lies Seiten neu, bevor du weiterarbeitest.]"));
        conv.HistoryJson = JsonSerializer.Serialize(history);
        conv.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return (true, text);
    }

    public async Task<bool> DeleteAsync(int conversationId, CancellationToken ct = default)
    {
        var conv = await _db.AiConversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conv is null) return false;
        _runner.Cancel(conv.Id);
        _db.AiConversations.Remove(conv);
        await _db.SaveChangesAsync(ct);
        _files.DeleteConversationFiles(conv.Id);
        return true;
    }

    private static string Title(string text)
    {
        var t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length > 80 ? t[..77] + "…" : t;
    }
}
