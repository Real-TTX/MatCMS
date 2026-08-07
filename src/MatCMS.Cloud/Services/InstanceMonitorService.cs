using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The watchdog: runs every 60 s and does the three things the cloud exists for.
/// <list type="number">
/// <item><b>Dead-man switch</b> — an instance whose heartbeat stopped is flagged offline and mailed
/// about ONCE per outage (<see cref="Instance.OfflineNotified"/>), not once per tick.</item>
/// <item><b>Update notice</b> — a newer MatCMS release than an instance runs is mailed once per
/// release (<see cref="Instance.UpdateNotifiedVersion"/>).</item>
/// <item><b>Auto-update</b> — only for LOCAL instances and only when explicitly switched on.</item>
/// </list>
/// Everything here is best-effort: SMTP or Docker being down must never stop the loop.
/// </summary>
public class InstanceMonitorService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InstanceMonitorService> _log;

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
        // Recipients ride along per mail: two instances on different profiles can have different
        // notification targets, so one global list at send time would be wrong.
        var pending = new List<(string subject, string body, string? recipients)>();

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
                if (notifyUpdate)
                {
                    var how = instance.Hosting == InstanceHosting.Local
                        ? "Diese Instanz läuft lokal — die Cloud kann das Update selbst ausführen."
                        : "Diese Instanz läuft remote — bitte dort ausführen: docker compose pull && docker compose up -d";
                    pending.Add((
                        $"[MatCMS.Cloud] Update {latest} für {instance.Name}",
                        $"Für die Instanz \"{instance.Name}\" ist eine neue Version verfügbar.\r\n" +
                        $"Installiert: {instance.Version ?? "unbekannt"}\r\nVerfügbar: {latest}\r\n\r\n{how}",
                        policy.Recipients));
                }
            }

            // --- 3) auto-update (local only, opt-in) ------------------------
            if (autoUpdate && instance.Hosting == InstanceHosting.Local
                && instance.ContainerId is not null && instances.IsUpdateAvailable(instance))
            {
                instances.Log(instance, InstanceEventKind.UpdateStarted,
                    $"Automatisches Update auf {latest} gestartet.", notified: true);
                await db.SaveChangesAsync(ct);

                var result = await docker.UpdateContainerAsync(instance.ContainerId, ct);
                instances.Log(instance,
                    result.Ok ? InstanceEventKind.UpdateSucceeded : InstanceEventKind.UpdateFailed,
                    result.Message, notified: true);

                if (!result.Ok)
                    pending.Add((
                        $"[MatCMS.Cloud] Update von {instance.Name} fehlgeschlagen",
                        $"Das automatische Update ist fehlgeschlagen:\r\n\r\n{result.Message}",
                        policy.Recipients));
            }
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
}
