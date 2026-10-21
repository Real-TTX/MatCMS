using System.Collections.Concurrent;
using System.Threading.Channels;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Assistant;

/// <summary>
/// Runs the assistant in the background: a run takes minutes (a model call alone can take one), far longer than a request
/// should wait, so the page only starts it and then reads the steps as they are written. One run per conversation at a
/// time, at most <see cref="Parallel"/> conversations at once; each can be stopped.
/// <para>A restart interrupts whatever was running. On start every conversation still marked "running" is set back with a
/// line saying so — otherwise its page would wait for a run that no process is doing any more.</para>
/// </summary>
public sealed class AssistantRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AssistantRunner> _log;
    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();
    private readonly SemaphoreSlim _slots = new(Parallel);

    public const int Parallel = 3;

    public AssistantRunner(IServiceScopeFactory scopes, ILogger<AssistantRunner> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public void Enqueue(int conversationId) => _queue.Writer.TryWrite(conversationId);

    public bool Cancel(int conversationId)
    {
        if (!_running.TryGetValue(conversationId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    public bool IsRunning(int conversationId) => _running.ContainsKey(conversationId);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stuck = await db.AiConversations.Where(c => c.Status == "running").ToListAsync(stop);
            foreach (var c in stuck)
            {
                c.Status = "idle";
                db.AiSteps.Add(new AiStep { ConversationId = c.Id, Run = c.Runs, Kind = "info", Text = "Unterbrochen – die Website wurde neu gestartet." });
            }
            if (stuck.Count > 0) await db.SaveChangesAsync(stop);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A database that is not there yet (first start, before migrations) must not stop the runner.
            _log.LogDebug(ex, "Assistant runner: could not reset interrupted conversations");
        }

        await foreach (var id in _queue.Reader.ReadAllAsync(stop))
            _ = RunOneAsync(id, stop);
    }

    private async Task RunOneAsync(int conversationId, CancellationToken stop)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (!_running.TryAdd(conversationId, cts)) return;
        try
        {
            await _slots.WaitAsync(cts.Token);
            try
            {
                using var scope = _scopes.CreateScope();
                var agent = scope.ServiceProvider.GetRequiredService<AssistantAgent>();
                await agent.RunAsync(conversationId, cts.Token);
            }
            finally { _slots.Release(); }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Stopped while waiting for a slot: the conversation is marked idle by whoever cancelled.
            await MarkIdleAsync(conversationId, "Abgebrochen.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Assistant run for conversation {Id} failed", conversationId);
            await MarkIdleAsync(conversationId, "Interner Fehler: " + ex.Message);
        }
        finally
        {
            _running.TryRemove(conversationId, out _);
        }
    }

    private async Task MarkIdleAsync(int conversationId, string why)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var c = await db.AiConversations.FirstOrDefaultAsync(x => x.Id == conversationId);
            if (c is null || c.Status != "running") return;
            c.Status = "idle";
            db.AiSteps.Add(new AiStep { ConversationId = c.Id, Run = c.Runs, Kind = "info", Text = why });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not mark conversation {Id} idle", conversationId); }
    }
}
