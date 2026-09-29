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

    // Container-state refresh cadence. NOT every tick: a container's state only changes when someone
    // starts/stops it, and both the start/stop handlers and the detail page refresh it immediately — the
    // monitor is only a safety net for changes made OUTSIDE the cloud (a manual `docker stop`). Polling the
    // daemon's full container list once a minute per instance was needless allocation churn.
    private const int ReclassEveryTicks = 5;          // ~5 min
    private int _ticksSinceReclass = ReclassEveryTicks;

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
        // Auto-update runs the SAME update as the Hosting tab — here or through a node's agent.
        var hosting = sp.GetRequiredService<HostingActionsService>();
        var mail = sp.GetRequiredService<EmailService>();
        var removals = sp.GetRequiredService<InstanceRemovalService>();
        // Who hears about what is the notification matrix's business — the ONLY place events become addresses.
        var notify = sp.GetRequiredService<NotificationService>();
        var matrix = notify.Load();

        var settings = await db.CloudSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase, ct);
        bool Flag(string key) =>
            settings.TryGetValue(key, out var v) && (v ?? "").Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

        // Only APPROVED instances are watched. One that is still waiting for approval, or was turned
        // away, must not raise offline alarms — it was never promised to be up.
        var all = await db.Instances.Include(i => i.Profile)
            .Where(i => i.Status == InstanceStatus.Approved)
            .ToListAsync(ct);

        // Keep hosting + container state fresh from the daemon for STOPPED containers (they send no
        // heartbeat, so their state would otherwise stay stale). Only every ReclassEveryTicks, and only
        // when Docker is reachable — the immediate paths (start/stop handler, detail page) cover the rest.
        if (++_ticksSinceReclass >= ReclassEveryTicks)
        {
            _ticksSinceReclass = 0;
            if (await docker.IsReachableAsync(ct))
            {
                foreach (var instance in all)
                {
                    try { await instances.ClassifyAsync(instance, ct); }
                    catch (Exception ex) { _log.LogDebug(ex, "Reclassify failed for instance {Id}", instance.Id); }
                }
                await db.SaveChangesAsync(ct);
            }
        }

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
        // Each mail carries its EVENT and the instance it is about; the recipients are resolved from the matrix
        // at send time — per instance, because an Operator only hears about its own sites.
        var pending = new List<(string Event, Instance? Instance, string Subject, string Body)>();

        // --- 0) removals waiting for a backup ---------------------------------
        // Before the loop below, and over its OWN list: a waiting instance need not be approved (it
        // can have been rejected, or still be pending, in which case no backup will ever arrive and
        // the wait simply goes on saying so), and it has to be looked at even while it is offline.
        await CompleteRemovalsAsync(removals, instances, db, pending, ct);

        // --- 0b) nodes that went silent -------------------------------------
        // Infrastructure: a node that stops beating takes every site on it out of the cloud's reach. Once per
        // outage, like the instance alarm; the node's next beat re-arms it (NodeService.HeartbeatAsync).
        var silentCut = DateTime.UtcNow - 2 * Node.OfflineAfter;
        foreach (var node in await db.Nodes.Where(n => !n.Revoked && !n.OfflineNotified && n.LastSeenAt != null && n.LastSeenAt < silentCut).ToListAsync(ct))
        {
            node.OfflineNotified = true;
            var sites = await db.Instances.CountAsync(i => i.NodeId == node.Id, ct);
            pending.Add((NotifyEvents.NodeOffline, null,
                $"[MatCMS.Cloud] Node {node.Name} ist nicht erreichbar",
                $"Der Node \"{node.Name}\" ({node.HostName ?? "?"}) meldet sich seit {node.LastSeenAt:yyyy-MM-dd HH:mm} UTC nicht mehr.\r\n" +
                $"Websites auf diesem Node: {sites} — die Cloud kann sie bis dahin nicht steuern.\r\n\r\n" +
                "Auf dem Host prüfen: docker ps (läuft der Agent-Container?), docker logs <agent>."));
        }

        // Update notices are COLLECTED, not mailed one by one: a release drop otherwise sent a
        // separate mail for every instance ("richtiger Spam"). They are grouped by recipient list
        // below into one summary per target — recipients can differ per profile, so a single global
        // mail would reach the wrong people.
        var latestVersion = releases.LatestVersion;
        var updates = new List<(Instance Instance, string? Old)>();

        foreach (var instance in all)
        {
            // Policy comes from the instance's profile; the global settings are only the fallback
            // for an instance that has none.
            var policy = ProfileService.PolicyFor(instance.Profile, Flag);
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
                    pending.Add((NotifyEvents.Offline, instance,
                        $"[MatCMS.Cloud] {instance.Name} ist offline",
                        $"Die Instanz \"{instance.Name}\" meldet sich nicht mehr.\r\n" +
                        $"Letzter Heartbeat: {since:yyyy-MM-dd HH:mm} UTC\r\n" +
                        $"Version: {instance.Version ?? "unbekannt"}\r\n" +
                        $"Host: {instance.HostName ?? "unbekannt"} ({InstanceService.Describe(instance.Hosting)})"));
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
                    updates.Add((instance, instance.Version));
            }

            // --- 3) auto-update (this host or a node, opt-in) ---------------
            // Attempted ONCE per available version. "Update available" stays true until the instance
            // has restarted and reported its new version, so without the mark this would re-run the
            // update — and mail about every failure — on every 60 s tick, forever.
            if (autoUpdate && HostingActionsService.CanAct(instance) && instances.IsUpdateAvailable(instance)
                && instance.AutoUpdateAttemptedVersion != latest)
            {
                instance.AutoUpdateAttemptedVersion = latest;
                await db.SaveChangesAsync(ct);

                // Logs started/succeeded/failed itself, and moves the version forward on success.
                var result = await hosting.UpdateAsync(instance, ct);

                // Cleared on success so the next release is attempted again; kept on failure so a
                // broken update is reported once and then left to a human.
                if (result.Ok) instance.AutoUpdateAttemptedVersion = null;
                else
                    pending.Add((NotifyEvents.UpdateFailed, instance,
                        $"[MatCMS.Cloud] Update von {instance.Name} fehlgeschlagen",
                        $"Das automatische Update ist fehlgeschlagen:\r\n\r\n{result.Message}"));
            }
        }

        // ONE summary per recipient instead of a mail per instance ("richtiger Spam") — and per recipient, not
        // per list: an Operator's summary names only its own sites, so recipients are grouped by the exact set
        // of instances they may hear about, and each group gets one mail.
        var perRecipient = new Dictionary<string, List<(Instance Instance, string? Old)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in updates)
            foreach (var to in await notify.RecipientsAsync(NotifyEvents.Update, u.Instance, matrix, ct))
            {
                if (!perRecipient.TryGetValue(to, out var list)) perRecipient[to] = list = new();
                list.Add(u);
            }
        var summaries = new List<(List<string> To, string Subject, string Body)>();
        foreach (var group in perRecipient.GroupBy(kv => string.Join(",", kv.Value.Select(x => x.Instance.Id).OrderBy(x => x))))
        {
            var items = group.First().Value;
            var n = items.Count;
            var lines = string.Join("\r\n", items.Select(u =>
                $"• {u.Instance.Name}: {u.Old ?? "unbekannt"} → {latestVersion} [{InstanceService.Describe(u.Instance.Hosting)}]"));
            summaries.Add((group.Select(kv => kv.Key).ToList(),
                $"[MatCMS.Cloud] Update {latestVersion} verfügbar ({n} Instanz{(n == 1 ? "" : "en")})",
                $"Für folgende Instanz{(n == 1 ? "" : "en")} ist die neue Version {latestVersion} verfügbar:\r\n\r\n" +
                lines + "\r\n\r\n" +
                "Instanzen auf diesem Host oder einem Node kann die Cloud selbst aktualisieren (Instanz → Hosting → Update). " +
                "Für entfernte Instanzen dort ausführen: docker compose pull && docker compose up -d"));
        }

        await db.SaveChangesAsync(ct);

        if (pending.Count == 0 && summaries.Count == 0) return;

        if (!await mail.IsConfiguredAsync())
        {
            _log.LogInformation("{Count} notification(s) suppressed — no SMTP config", pending.Count);
            return;
        }

        foreach (var (ev, inst, subject, body) in pending)
        {
            var to = await notify.RecipientsAsync(ev, inst, matrix, ct);
            if (to.Count == 0)
            {
                _log.LogInformation("Notification '{Subject}' ({Event}) suppressed — nobody subscribed", subject, ev);
                continue;
            }
            var (ok, error) = await mail.SendAsync(to, subject, body);
            if (!ok) _log.LogWarning("Notification '{Subject}' could not be sent: {Error}", subject, error);
        }
        foreach (var (to, subject, body) in summaries)
        {
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
    /// <para>A fleet event ("Entfernen" in the matrix), not an instance one: the instance is about to stop
    /// existing, and the person waiting on this runs the cloud rather than the site.</para>
    /// </summary>
    private async Task CompleteRemovalsAsync(
        InstanceRemovalService removals, InstanceService instances, AppDbContext db,
        List<(string Event, Instance? Instance, string Subject, string Body)> pending, CancellationToken ct)
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
                pending.Add((NotifyEvents.Removal, null,
                    $"[MatCMS.Cloud] {name} wurde nach dem Backup entfernt",
                    $"Das Backup der Instanz \"{name}\" ist eingetroffen und liegt im Archiv.\r\n" +
                    $"Erst danach wurde sie entfernt.\r\n\r\n{outcome.Message}"));
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

            pending.Add((NotifyEvents.Removal, null,
                $"[MatCMS.Cloud] Entfernen von {name} wartet weiterhin",
                $"Das Entfernen der Instanz \"{name}\" wurde vorgemerkt, ist aber noch nicht geschehen.\r\n" +
                $"Vorgemerkt am: {instance.PendingRemovalAt:yyyy-MM-dd HH:mm} UTC\r\n\r\n" +
                $"{problem}\r\n\r\n" +
                "Es wurde nichts entfernt, und es wird auch nichts entfernt, solange das Backup nicht " +
                "hier ist. In der Cloud lässt sich das Backup erneut anfordern oder das Entfernen " +
                "zurücknehmen."));
        }
    }
}
