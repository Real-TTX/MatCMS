using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>The apply history of every visible instance (<see cref="InstanceSyncRun"/>, newest first) — the dashboard's
/// "Letzte Abgleiche", its full page (Admin/Syncs), /api/v1/syncs and MCP list_sync_runs.</summary>
public sealed class SyncHistoryService
{
    private readonly AppDbContext _db;
    public SyncHistoryService(AppDbContext db) => _db = db;

    /// <summary>What a run came to, for the filter: error (the apply threw), failed (items failed), changes, nochange.</summary>
    public static string Outcome(InstanceSyncRun r) =>
        !string.IsNullOrEmpty(r.Error) ? "error" : r.Failed > 0 ? "failed" : r.Installed + r.Updated > 0 ? "changes" : "nochange";

    /// <param name="allowed">Instance ids the caller may see; null = all.</param>
    public async Task<List<InstanceSyncRun>> ListAsync(IReadOnlySet<int>? allowed, int take, CancellationToken ct = default)
    {
        var q = _db.InstanceSyncRuns.AsNoTracking().Include(r => r.Instance).AsQueryable();
        if (allowed is not null) q = q.Where(r => allowed.Contains(r.InstanceId));
        return await q.OrderByDescending(r => r.RanAt).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct);
    }
}
