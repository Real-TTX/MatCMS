using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The watchdog: runs every 60 s and does the three things the cloud exists for.
/// <list type="number">
/// <item><b>Dead-man switch</b> — an instance whose heartbeat stopped is flagged offline and mailed
/// about ONCE per outage (<see cref="Instance.OfflineNotified"/>), not once per tick.</item>
/// <item><b>Update notice</b> — a newer MatCMS release than an instance runs is flagged once per
/// release per instance (<see cref="Instance.UpdateNotifiedVersion"/>), but the MAILS are collected
/// and sent as ONE summary per recipient list ("Site: old → new"), not one mail per instance.</item>
/// <item><b>Auto-update</b> — only for LOCAL instances and only when explicitly switched on.</item>
/// <item><b>Delayed removals</b> — an instance whose removal is waiting for a backup is removed
/// here, once the backup has actually arrived, and only then. This is also where a wait that has
/// been running too long turns into a mail, because a removal that quietly never happens is a
/// removal the operator believes has happened.</item>
/// </list>
/// Everything here is best-effort: SMTP or Docker being down must never stop the loop.
/// </summary>
public class InstanceMonitorService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InstanceMonitorService> _log;

    // Retention is enforced on every upload; this counts ticks so the time-based tiers (GFS) also get
    // a sweep when uploads stop. 60 ticks × 60 s ≈ hourly, which is plenty for day/week/month buckets.
    private const int SweepEveryTicks = 60;
    private int _ticksSinceSweep = SweepEveryTicks;   // sweep on the first tick too

    public InstanceMonitorService(IServiceScopeFactory scopes, ILogger<InstanceMonitorService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the release watcher a moment to complete its first poll, so the very first tick
        // doesn't announce "no update known" for every instance.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogError(ex, "Instance monitor tick failed"); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var instances = sp.GetRequiredService<InstanceService>();
        var releases = sp.GetRequiredService<ReleaseWatcher>();
        var docker = sp.GetRequiredService<DockerHostService>();
        var mail = sp.GetRequiredService<EmailService>();
        var removals = sp.GetRequiredService<InstanceRemovalService>();

        var settings = await db.CloudSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase, ct);
        bool Flag(string key) =>
            settings.TryGetValue(key, out var v) && (v ?? "").Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

        var globalRecipients = settings.TryGetValue(SettingKeys.NotifyRecipients, out var gr) ? gr : null;

        // Only APPROVED instances are watched. One that is still waiting for approval, or was turned
        // away, must not raise offline alarms — it was never promised to be up.
        var all = await db.Instances.Include(i => i.Profile)
            .Where(i => i.Status == InstanceStatus.Approved)
            .ToListAsync(ct);

        // Periodic retention sweep (~hourly): prune time-based tiers for sites that stopped uploading.
        // Uploads prune themselves in BackupStore.StoreAsync, so this only has to catch the tail.
        if (++_ticksSinceSweep >= SweepEveryTicks)
        {
            _ticksSinceSweep = 0;
            var backups = sp.GetRequiredService<BackupStore>();
            foreach (var instance in all)
            {
                try { await backups.EnforceRetentionAsync(instance.Id, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "Retention sweep failed for instance {Id}", instance.Id); }
            }
        }
        // Recipients ride along per mail: two instances on different profiles can have different
        // notification targets, so one global list at send time would be wrong.
        var pending = new List<(string subject, string body, string? recipients)>();

        // --- 0) removals waiting for a backup ---------------------------------
        // Before the loop below, and over its OWN list: a waiting instance need not be approved (it
        // can have been rejected, or still be pending, in which case no backup will ever arrive and
        // the wait simply goes on saying so), and it has to be looked at even while it is offline.
        await CompleteRemovalsAsync(removals, instances, db, pending, globalRecipients, ct);

        // Update notices are COLLECTED, not mailed one by one: a release drop otherwise sent a
        // separate mail for every instance ("richtiger Spam"). They are grouped by recipient list
        // below into one summary per target — recipients can differ per profile, so a single global
        // mail would reach the wrong people.
        var latestVersion = releases.LatestVersion;
        var updates = new List<(string Name, string? Old, InstanceHosting Hosting, string? Recipients)>();

        foreach (var instance in all)
        {
            // Policy comes from the instance's profile; the global settings are only the fallback
            // for an instance that has none.
            var policy = ProfileService.PolicyFor(instance.Profile, Flag, globalRecipients);
            var notifyOffline = policy.NotifyOffline;
            var notifyUpdate = policy.NotifyUpdate;
            var autoUpdate = policy.AutoUpdateLocal;

            // --- 1) offline -------------------------------------------------
            if (instance.HasConnected && !InstanceService.IsOnline(instance) && !instance.OfflineNotified)
            {
                var since = instance.LastHeartbeatUtc!.Value;
                instances.Log(instance, InstanceEventKind.Offline,
                    $"Kein Heartbeat seit {since:yyyy-MM-dd HH:mm} UTC.", notified: !notifyOffline);
                instance.OfflineNotified = true;
                if (notifyOffline)
                    pending.Add((
                        $"[MatCMS.Cloud] {instance.Name} ist offline",
                        $"Die Instanz \"{instance.Name}\" meldet sich nicht mehr.\r\n" +
                        $"Letzter Heartbeat: {since:yyyy-MM-dd HH:mm} UTC\r\n" +
                        $"Version: {instance.Version ?? "unbekannt"}\r\n" +
                        $"Host: {instance.HostName ?? "unbekannt"} ({InstanceService.Describe(instance.Hosting)})",
                        policy.Recipients));
            }

            // --- 2) update available ----------------------------------------
            var latest = releases.LatestVersion;
            if (latest is not null && instances.IsUpdateAvailable(instance)
                && instance.UpdateNotifiedVersion != latest)
            {
                instance.UpdateNotifiedVersion = latest;
                instances.Log(instance, InstanceEventKind.UpdateAvailable,
                    $"Neue Version {latest} verfügbar (läuft {instance.Version ?? "?"}).", notified: !notifyUpdate);
                // Collected, not sent here — see the grouping after the loop.
                if (notifyUpdate)
                    updates.Add((instance.Name, instance.Version, instance.Hosting, policy.Recipients));
            }

            // --- 3) auto-update (local only, opt-in) ------------------------
            // Attempted ONCE per available version. "Update available" stays true until the instance
            // has restarted and reported its new version, so without the mark this would re-run the
            // update — and mail about every failure — on every 60 s tick, forever.
            if (autoUpdate && instance.Hosting == InstanceHosting.Local
                && instance.ContainerId is not null && instances.IsUpdateAvailable(instance)
                && instance.AutoUpdateAttemptedVersion != latest)
            {
                instance.AutoUpdateAttemptedVersion = latest;
                instances.Log(instance, InstanceEventKind.UpdateStarted,
                    $"Automatisches Update auf {latest} gestartet.", notified: true);
                await db.SaveChangesAsync(ct);

                var result = await docker.UpdateContainerAsync(instance.ContainerId, ct);
                instances.Log(instance,
                    result.Ok ? InstanceEventKind.UpdateSucceeded : InstanceEventKind.UpdateFailed,
                    result.Message, notified: true);

                // Cleared on success so the next release is attempted again; kept on failure so a
                // broken update is reported once and then left to a human.
                if (result.Ok) instance.AutoUpdateAttemptedVersion = null;
                else
                    pending.Add((
                        $"[MatCMS.Cloud] Update von {instance.Name} fehlgeschlagen",
                        $"Das automatische Update ist fehlgeschlagen:\r\n\r\n{result.Message}",
                        policy.Recipients));
            }
        }

        // One summary per distinct recipient list: "Site: alte Version → neue Version", instead of a
        // separate mail per instance. Grouped so two profiles with different notification targets each
        // get their own summary, and an empty key (no per-profile override) falls back at send time.
        foreach (var group in updates.GroupBy(u => u.Recipients ?? ""))
        {
            var items = group.ToList();
            var n = items.Count;
            var lines = string.Join("\r\n", items.Select(u =>
                $"• {u.Name}: {u.Old ?? "unbekannt"} → {latestVersion} [{InstanceService.Describe(u.Hosting)}]"));
            pending.Add((
                $"[MatCMS.Cloud] Update {latestVersion} verfügbar ({n} Instanz{(n == 1 ? "" : "en")})",
                $"Für folgende Instanz{(n == 1 ? "" : "en")} ist die neue Version {latestVersion} verfügbar:\r\n\r\n" +
                lines + "\r\n\r\n" +
                "Lokale Instanzen kann die Cloud selbst aktualisieren (Instanz → „Jetzt aktualisieren“). " +
                "Für entfernte Instanzen dort ausführen: docker compose pull && docker compose up -d",
                group.Key.Length == 0 ? null : group.Key));
        }

        await db.SaveChangesAsync(ct);

        if (pending.Count == 0) return;

        if (!await mail.IsConfiguredAsync())
        {
            _log.LogInformation("{Count} notification(s) suppressed — no SMTP config", pending.Count);
            return;
        }

        var fallback = await mail.ResolveRecipientsAsync();
        foreach (var (subject, body, overrideRecipients) in pending)
        {
            var to = EmailService.ParseRecipients(overrideRecipients);
            if (to.Count == 0) to = fallback;
            if (to.Count == 0)
            {
                _log.LogInformation("Notification '{Subject}' suppressed — no recipients", subject);
                continue;
            }

            var (ok, error) = await mail.SendAsync(to, subject, body);
            if (!ok) _log.LogWarning("Notification '{Subject}' could not be sent: {Error}", subject, error);
        }
    }

    /// <summary>
    /// Finishes the removals that were waiting for a backup — and, for the ones still waiting, makes
    /// sure somebody eventually hears about it.
    ///
    /// <para><b>Nothing here has a deadline that removes anything.</b> The wait ends when the backup
    /// arrives, or when an operator takes it back. What the clock does instead is raise a notice:
    /// after <see cref="InstanceRemovalService.WaitNoticeAfter"/> the operator is told, once, that a
    /// removal they confirmed has not happened, and why. That is the difference between a state that
    /// is patient and one that is stuck — while a "for safety, remove it anyway" timer would destroy
    /// exactly the site this way exists to protect.</para>
    ///
    /// <para>Recipients come from the global notification settings rather than the instance's
    /// profile: the instance may have none, it is about to stop existing, and the person waiting on
    /// this is the operator of the cloud rather than of the site.</para>
    /// </summary>
    private async Task CompleteRemovalsAsync(
        InstanceRemovalService removals, InstanceService instances, AppDbContext db,
        List<(string subject, string body, string? recipients)> pending,
        string? globalRecipients, CancellationToken ct)
    {
        var waiting = await removals.PendingAsync(ct);
        foreach (var instance in waiting)
        {
            var name = instance.Name;

            // Removes only if the backup is really here. Null means "still nothing to do", which is
            // the normal answer for as long as the wait lasts.
            var outcome = await removals.TryCompletePendingAsync(instance, ct);
            if (outcome is { Removed: true })
            {
                _log.LogWarning("Delayed removal of {Name} completed: {Message}", name, outcome.Message);
                pending.Add((
                    $"[MatCMS.Cloud] {name} wurde nach dem Backup entfernt",
                    $"Das Backup der Instanz \"{name}\" ist eingetroffen und liegt im Archiv.\r\n" +
                    $"Erst danach wurde sie entfernt.\r\n\r\n{outcome.Message}",
                    globalRecipients));
                continue;
            }

            // Everything below is about a wait that is still running. One mail per request, guarded
            // by the same kind of flag as the offline alert — a notice repeated every 60 s is a
            // notice nobody reads.
            if (instance.BackupWaitNotified) continue;

            var problem =
                instance.PendingRemovalError is string removalError
                    ? removalError
                : instance.BackupRequestError is string backupError
                    ? $"Die Instanz konnte das Backup nicht erstellen: {backupError}"
                : instance.PendingRemovalAt is DateTime since
                  && DateTime.UtcNow - since > InstanceRemovalService.WaitNoticeAfter
                    ? (InstanceService.IsOnline(instance)
                        ? "Die Instanz meldet sich, hat das angeforderte Backup aber noch nicht abgeliefert."
                        : "Die Instanz meldet sich nicht, das angeforderte Backup kann daher nicht eintreffen.")
                : null;
            if (problem is null) continue;

            instance.BackupWaitNotified = true;
            instances.Log(instance, InstanceEventKind.RemovalPending,
                $"Entfernen wartet weiterhin: {problem}");
            await db.SaveChangesAsync(ct);

            pending.Add((
                $"[MatCMS.Cloud] Entfernen von {name} wartet weiterhin",
                $"Das Entfernen der Instanz \"{name}\" wurde vorgemerkt, ist aber noch nicht geschehen.\r\n" +
                $"Vorgemerkt am: {instance.PendingRemovalAt:yyyy-MM-dd HH:mm} UTC\r\n\r\n" +
                $"{problem}\r\n\r\n" +
                "Es wurde nichts entfernt, und es wird auch nichts entfernt, solange das Backup nicht " +
                "hier ist. In der Cloud lässt sich das Backup erneut anfordern oder das Entfernen " +
                "zurücknehmen.",
                globalRecipients));
        }
    }
}
