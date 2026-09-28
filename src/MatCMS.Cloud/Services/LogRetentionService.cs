using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Prunes the cloud's OWN log table on a schedule so it cannot grow without bound (the counterpart to
/// the opt-in request log). Retention is per category, in days (<see cref="SettingKeys.LogRetentionErrorsDays"/>
/// for errors/5xx, <see cref="SettingKeys.LogRetentionRequestsDays"/> for the full request log); 0 means
/// "keep" and only the hard count cap applies. Instance logs are a separate mirror, pruned on the
/// heartbeat. Runs hourly and once shortly after start.
/// </summary>
public class LogRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LogRetentionService> _log;
    public LogRetentionService(IServiceScopeFactory scopes, ILogger<LogRetentionService> log)
    {
        _scopes = scopes; _log = log;
    }

    private const int HardCap = 20000;
    private const int DefaultErrorsDays = 90;
    private const int DefaultRequestsDays = 14;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await SweepAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "Log-Retention-Sweep fehlgeschlagen"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Cloud.Data.AppDbContext>();

        int Days(string key, int fallback)
        {
            var v = db.CloudSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefault();
            return int.TryParse(v, out var d) && d >= 0 ? d : fallback;
        }

        var errDays = Days(SettingKeys.LogRetentionErrorsDays, DefaultErrorsDays);
        var reqDays = Days(SettingKeys.LogRetentionRequestsDays, DefaultRequestsDays);
        var now = DateTime.UtcNow;

        if (reqDays > 0)
            await db.Logs.Where(l => l.Category == "webrequest" && l.CreatedAt < now.AddDays(-reqDays)).ExecuteDeleteAsync(ct);
        if (errDays > 0)
            await db.Logs.Where(l => l.Category != "webrequest" && l.CreatedAt < now.AddDays(-errDays)).ExecuteDeleteAsync(ct);

        var total = await db.Logs.CountAsync(ct);
        if (total > HardCap)
        {
            var cutId = await db.Logs.OrderByDescending(l => l.Id).Skip(HardCap).Select(l => l.Id).FirstOrDefaultAsync(ct);
            if (cutId > 0) await db.Logs.Where(l => l.Id <= cutId).ExecuteDeleteAsync(ct);
        }
    }
}
