using System.Collections.Concurrent;
using System.Globalization;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Plugins;

/// <summary>
/// Runs the jobs plugins declared (<see cref="IPluginBuilder.Job"/>). One timer for all of them: every few seconds it looks
/// at the registry — so a plugin that is loaded or unloaded changes the job list at once, nothing is scheduled "inside" a
/// plugin that could outlive it.
/// <para><b>Guarantees:</b> a job runs one at a time (a run that is still going is not started again); an exception stays with
/// its plugin (logged there, shown under Plugins) and never reaches the host; the time of the last run is stored, so a
/// restart does not run every job again at once; a job that has never run waits one full interval (or until its time of day)
/// instead of firing on first sight.</para>
/// </summary>
public sealed class PluginJobService : BackgroundService
{
    /// <summary>What the admin shows for one job.</summary>
    public sealed record JobStatus(string Owner, string Name, string Schedule, DateTime? LastRunUtc, TimeSpan? LastDuration,
        string? LastError, bool Running, DateTime? NextDueUtc);

    private sealed class State
    {
        public DateTime? LastRun;
        public TimeSpan? Duration;
        public string? Error;
        public DateTime? NextDue;
        public int Running;
        public bool Loaded;
    }

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxRun = TimeSpan.FromMinutes(15);

    private readonly PluginRegistry _registry;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PluginJobService> _log;
    private readonly ConcurrentDictionary<string, State> _state = new(StringComparer.Ordinal);

    /// <summary>Creates the service.</summary>
    public PluginJobService(PluginRegistry registry, IServiceScopeFactory scopes, ILogger<PluginJobService> log)
    {
        _registry = registry; _scopes = scopes; _log = log;
    }

    private static string KeyOf(string owner, string name) => owner + "/" + name;
    private static string SettingKey(string owner, string name) => "plugin.job." + owner + "." + name;

    /// <summary>When a job is next due, given when it last ran. A never-run job waits a full interval (or for its time of day).</summary>
    public static DateTime NextDue(JobSchedule schedule, DateTime? lastRunUtc, DateTime nowUtc)
    {
        if (schedule.Every is { } every) return (lastRunUtc ?? nowUtc) + every;
        var at = schedule.DailyAt ?? new TimeOnly(3, 0);
        // Due at the next occurrence of the time of day after the last run (or after now when it never ran).
        var from = lastRunUtc ?? nowUtc;
        var candidate = from.Date + at.ToTimeSpan();
        if (candidate <= from) candidate = candidate.AddDays(1);
        return candidate;
    }

    /// <summary>The state of every job of every loaded plugin.</summary>
    public IReadOnlyList<JobStatus> Status() =>
        _registry.Jobs.Select(j =>
        {
            _state.TryGetValue(KeyOf(j.Owner, j.Name), out var s);
            return new JobStatus(j.Owner, j.Name, Describe(j.Schedule), s?.LastRun, s?.Duration, s?.Error, s is { Running: > 0 }, s?.NextDue);
        }).ToList();

    private static string Describe(JobSchedule s) =>
        s.Every is { } e ? (e.TotalHours >= 1 && e.TotalHours % 1 == 0 ? $"alle {e.TotalHours:0} h" : $"alle {Math.Max(1, (int)e.TotalMinutes)} min")
                         : $"täglich {s.DailyAt:HH\\:mm} UTC";

    /// <summary>Runs a job now, as the admin's "Jetzt ausführen" does. False when no such job is loaded or it is running already.</summary>
    public async Task<bool> RunNowAsync(string owner, string name, CancellationToken ct = default)
    {
        var job = _registry.Jobs.FirstOrDefault(j => j.Owner == owner && j.Name == name);
        if (job is null) return false;
        return await RunAsync(job, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first look comes a little after start, when the plugins have been loaded.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(DateTime.UtcNow, stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "Plugin job tick failed"); }
            try { await Task.Delay(Tick, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One look at the job list: starts what is due. Public so a test can drive time.</summary>
    public async Task TickAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var jobs = _registry.Jobs;
        var live = jobs.Select(j => KeyOf(j.Owner, j.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var k in _state.Keys.Where(k => !live.Contains(k)).ToList()) _state.TryRemove(k, out _);

        foreach (var job in jobs)
        {
            var st = _state.GetOrAdd(KeyOf(job.Owner, job.Name), _ => new State());
            if (!st.Loaded) { st.Loaded = true; st.LastRun = await LoadLastRunAsync(job.Owner, job.Name, ct); st.NextDue = NextDue(job.Schedule, st.LastRun, nowUtc); }
            if (st.NextDue is { } due && due <= nowUtc && st.Running == 0)
                _ = Task.Run(() => RunAsync(job, ct, nowUtc), CancellationToken.None);   // not awaited: one slow job must not hold the others up
        }
    }

    /// <param name="job">The job.</param>
    /// <param name="stopping">The host's shutdown token.</param>
    /// <param name="clock">The time the run counts from (the tick's, so a test can drive time); the real time when null.</param>
    private async Task<bool> RunAsync(PluginRegistry.JobEntry job, CancellationToken stopping, DateTime? clock = null)
    {
        var st = _state.GetOrAdd(KeyOf(job.Owner, job.Name), _ => new State());
        if (Interlocked.CompareExchange(ref st.Running, 1, 0) != 0) return false;
        var started = clock ?? DateTime.UtcNow;
        var wall = DateTime.UtcNow;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            cts.CancelAfter(MaxRun);
            await using var scope = _scopes.CreateAsyncScope();
            var ctx = PluginRequestContext.ForBackground(job.Info, scope.ServiceProvider, _registry);
            await job.Handler(ctx, cts.Token);
            st.Error = null;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { st.Error = "abgebrochen (Herunterfahren)"; }
        catch (Exception ex)
        {
            st.Error = ex.Message;
            _registry.AddLog(job.Owner, $"Aufgabe '{job.Name}': {ex.Message}");
        }
        finally
        {
            st.LastRun = started;
            st.Duration = DateTime.UtcNow - wall;
            st.NextDue = NextDue(job.Schedule, started, started);
            Interlocked.Exchange(ref st.Running, 0);
        }
        try { await SaveLastRunAsync(job.Owner, job.Name, started); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not store the last run of plugin job {Job}", KeyOf(job.Owner, job.Name)); }
        return true;
    }

    private async Task<DateTime?> LoadLastRunAsync(string owner, string name, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var v = await db.SiteSettings.AsNoTracking().Where(s => s.Key == SettingKey(owner, name)).Select(s => s.Value).FirstOrDefaultAsync(ct);
            return DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        }
        catch { return null; }
    }

    private async Task SaveLastRunAsync(string owner, string name, DateTime whenUtc)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = SettingKey(owner, name);
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        var text = whenUtc.ToString("o", CultureInfo.InvariantCulture);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = text }); else row.Value = text;
        await db.SaveChangesAsync();
    }
}
