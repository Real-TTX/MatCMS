using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// "Update these instances": which ones can be updated and starting a run for a chosen subset. ONE place behind the
/// Hosting → Updates page, <c>/api/v1/hosting/updates</c> and the MCP tools, so a selection is checked the same way
/// everywhere — only instances that are candidates right now are updated, whatever ids a caller sends.
/// </summary>
public class InstanceUpdatesService
{
    private readonly AppDbContext _db;
    private readonly InstanceService _instances;
    private readonly BulkUpdateService _bulk;
    private readonly CloudUpdaterService _updater;

    public InstanceUpdatesService(AppDbContext db, InstanceService instances, BulkUpdateService bulk, CloudUpdaterService updater)
    {
        _db = db; _instances = instances; _bulk = bulk; _updater = updater;
    }

    /// <summary>Approved instances the cloud can update itself — on its own host or through a node — that are behind
    /// the latest release. <paramref name="scope"/> narrows it to what an API key may reach.</summary>
    public async Task<List<Instance>> CandidatesAsync(Func<Instance, bool>? scope = null, CancellationToken ct = default)
    {
        var list = await _db.Instances.AsNoTracking().Include(i => i.Node)
            .Where(i => i.Status == InstanceStatus.Approved
                        && (i.Hosting == InstanceHosting.Local || (i.Hosting == InstanceHosting.Node && i.NodeId != null)) && i.ContainerId != null)
            .OrderBy(i => i.Name).ToListAsync(ct);
        return list.Where(i => _instances.IsUpdateAvailable(i) && (scope?.Invoke(i) ?? true)).ToList();
    }

    public sealed record StartResult(bool Ok, string? RunId, string? Error, int Count);

    /// <summary>Starts a run for the given public ids (null = every candidate). Ids that are no candidate (current
    /// already, remote, out of scope, unknown) are dropped, not an error — the list may have gone stale while the
    /// page was open. Refused while the cloud updates itself: the restart would cut the run off.</summary>
    public async Task<StartResult> StartAsync(IEnumerable<string>? publicIds, Func<Instance, bool>? scope = null, CancellationToken ct = default)
    {
        if (_updater.LastRun() is { InFlight: true })
            return new(false, null, "Die Cloud aktualisiert sich gerade selbst — danach erneut starten.", 0);
        var candidates = await CandidatesAsync(scope, ct);
        var wanted = publicIds is null ? null : new HashSet<string>(publicIds, StringComparer.Ordinal);
        var ids = candidates.Where(i => wanted is null || wanted.Contains(i.PublicId)).Select(i => i.Id).ToList();
        if (ids.Count == 0) return new(false, null, "Keine der gewählten Instanzen braucht ein Update, das die Cloud ausführen kann.", 0);
        return new(true, _bulk.Start(ids), null, ids.Count);
    }

    /// <summary>A run's progress in the one JSON shape the page, REST and MCP share. Null = unknown run.</summary>
    public object? Progress(string runId)
    {
        var r = _bulk.Get(runId);
        if (r is null) return null;
        return new
        {
            found = true, done = r.Done, total = r.Items.Count,
            completed = r.Items.Count(i => i.Status is "done" or "failed" or "skipped"),
            items = r.Items.Select(i => new { i.Name, i.From, i.To, i.Status, i.Message })
        };
    }
}
