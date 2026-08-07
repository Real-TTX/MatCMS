namespace MatCMS.Services;

/// <summary>
/// Background worker that runs due scheduled backups. Wakes every few minutes, loads the schedule
/// config, and — if enabled and the interval has elapsed — writes a backup to disk. Each check runs
/// in its own DI scope (AppDbContext is scoped). Failures are logged and never crash the app.
/// </summary>
public class BackupSchedulerService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackupSchedulerService> _log;

    public BackupSchedulerService(IServiceScopeFactory scopeFactory, ILogger<BackupSchedulerService> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so the first check doesn't race the DB seeder / migrations.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var mgr = scope.ServiceProvider.GetRequiredService<BackupManager>();
                var cfg = await mgr.GetConfigAsync();
                if (BackupManager.IsDue(cfg, DateTime.UtcNow))
                {
                    var name = await mgr.RunAsync(cfg);
                    _log.LogInformation("Scheduled backup written: {Name}", name);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scheduled backup failed");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }
}
