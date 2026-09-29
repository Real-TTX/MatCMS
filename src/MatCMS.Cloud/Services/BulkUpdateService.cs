using System.Collections.Concurrent;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Runs an "update all instances the cloud can reach" job (its own host and connected nodes) in the background and exposes its live progress, so the UI
/// can show a per-instance status (from → to) and an overall bar while the updates run one after
/// another. A singleton: one job's state has to outlive the request that started it and be readable by
/// the polling requests that follow.
/// <para>Updates are sequential on purpose — recreating several containers at once on one host is how
/// you take a machine down; one at a time, each with its own rollback (in <see cref="DockerHostService"/>),
/// is the safe shape.</para>
/// </summary>
public class BulkUpdateService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DockerHostService _docker;
    private readonly ReleaseWatcher _releases;
    private readonly ILogger<BulkUpdateService> _log;

    public BulkUpdateService(IServiceScopeFactory scopes, DockerHostService docker,
        ReleaseWatcher releases, ILogger<BulkUpdateService> log)
    {
        _scopes = scopes; _docker = docker; _releases = releases; _log = log;
    }

    public sealed class Item
    {
        public string PublicId { get; init; } = "";
        public string Name { get; init; } = "";
        public string? From { get; init; }
        public string To { get; init; } = "";
        // pending | updating | done | failed | skipped
        public string Status { get; set; } = "pending";
        public string? Message { get; set; }
    }

    public sealed class Run
    {
        public string Id { get; init; } = "";
        public List<Item> Items { get; init; } = new();
        public bool Done { get; set; }
        public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Run> _runs = new();

    public Run? Get(string id) => id is not null && _runs.TryGetValue(id, out var r) ? r : null;

    /// <summary>Starts a job for the given instance ids and returns its id at once; the work runs on a
    /// background task and its progress is read back via <see cref="Get"/>.</summary>
    public string Start(IReadOnlyList<int> instanceIds)
    {
        var run = new Run { Id = Guid.NewGuid().ToString("N") };
        _runs[run.Id] = run;
        // Fire-and-forget: the caller returns immediately and the UI polls. Errors are captured per
        // item; a crash of the whole loop is logged and simply ends the run.
        _ = Task.Run(() => ExecuteAsync(run, instanceIds));

        // Keep the map from growing without bound: drop runs older than an hour whenever a new one starts.
        foreach (var old in _runs.Values.Where(r => r.Done && DateTime.UtcNow - r.StartedAt > TimeSpan.FromHours(1)).ToList())
            _runs.TryRemove(old.Id, out _);

        return run.Id;
    }

    private async Task ExecuteAsync(Run run, IReadOnlyList<int> instanceIds)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The same update the Hosting tab runs — on the cloud's own daemon OR through a node's agent — so a
            // bulk run and a single click can never differ in guards, rollback or bookkeeping.
            var hosting = scope.ServiceProvider.GetRequiredService<HostingActionsService>();
            var latest = _releases.LatestVersion ?? "?";

            var list = await db.Instances
                .Where(i => instanceIds.Contains(i.Id))
                .OrderBy(i => i.Name)
                .ToListAsync();

            foreach (var i in list)
                run.Items.Add(new Item { PublicId = i.PublicId, Name = i.Name, From = i.Version, To = latest });

            foreach (var i in list)
            {
                var item = run.Items.First(x => x.PublicId == i.PublicId);

                if (!HostingActionsService.CanAct(i))
                {
                    item.Status = "skipped";
                    item.Message = "Läuft weder auf diesem Docker-Host noch auf einem verbundenen Node.";
                    continue;
                }

                item.Status = "updating";
                try
                {
                    var result = await hosting.UpdateAsync(i);
                    item.Status = result.Ok ? "done" : "failed";
                    item.Message = result.Message;
                }
                catch (Exception ex)
                {
                    item.Status = "failed";
                    item.Message = ex.Message;
                    _log.LogError(ex, "Bulk update of {Name} threw", i.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bulk update run {Id} failed", run.Id);
        }
        finally
        {
            run.Done = true;
        }
    }
}
