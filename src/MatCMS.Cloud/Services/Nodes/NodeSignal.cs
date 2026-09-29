using System.Collections.Concurrent;

namespace MatCMS.Cloud.Services.Nodes;

/// <summary>
/// In-memory wake-ups (singleton): "a job was enqueued for node N" ends that node's long poll at once, and
/// "job J was reported" ends the wait of whoever asked for it. Only a SHORTCUT — the database is the truth, and
/// both waits re-read it, so a signal lost to a restart costs latency, never a job.
/// </summary>
public sealed class NodeSignal
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _nodes = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _jobs = new();

    private SemaphoreSlim For(int nodeId) => _nodes.GetOrAdd(nodeId, _ => new SemaphoreSlim(0, 1));

    /// <summary>A job is waiting for <paramref name="nodeId"/>. A release while nobody waits is kept (count 1),
    /// so a job enqueued between "nothing to hand out" and the start of the wait is not missed; a stale one only
    /// costs a single extra, empty beat.</summary>
    public void NotifyNode(int nodeId)
    {
        try { For(nodeId).Release(); } catch (SemaphoreFullException) { }
    }

    public async Task WaitNodeAsync(int nodeId, TimeSpan timeout, CancellationToken ct)
    {
        try { await For(nodeId).WaitAsync(timeout, ct); } catch (OperationCanceledException) { }
    }

    public Task JobTask(long jobId) =>
        _jobs.GetOrAdd(jobId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void CompleteJob(long jobId)
    {
        if (_jobs.TryRemove(jobId, out var tcs)) tcs.TrySetResult();
    }

    public void ForgetJob(long jobId) => _jobs.TryRemove(jobId, out _);
}
