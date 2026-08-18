using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Taking an instance out of the cloud — all of it, in one place.
///
/// <para>It used to live on the confirmation page, which was fine while every way happened the
/// moment the button was pressed. The third way does not: "back up first, then remove" is confirmed
/// now and carried out minutes later by the watchdog, once the backup has actually arrived. That
/// left the choice between a page and a worker each knowing how to delete a customer's site, and two
/// implementations of THAT are two chances to get the guards subtly different. So the page and the
/// worker both call in here.</para>
///
/// <para>The guards that travel with it, all three load-bearing:
/// <list type="number">
/// <item>The target is resolved from the RECORD and the daemon, never from the caller. What the
/// caller passes is only compared, so a stale page cannot instruct a removal.</item>
/// <item>Only a container carrying this cloud's own label may be torn down
/// (<see cref="DockerHostService.ManagedLabel"/>). Local means "on a daemon we can reach", which is
/// equally true of a site somebody else started next to us.</item>
/// <item>The record is deleted LAST and only if the container really went. An instance whose
/// container is still running must not vanish from the list — that is how a container nobody knows
/// about is left behind.</item>
/// </list></para>
/// </summary>
public class InstanceRemovalService
{
    private readonly AppDbContext _db;
    private readonly DockerHostService _docker;
    private readonly BackupStore _backups;
    private readonly InstanceService _instances;
    private readonly ILogger<InstanceRemovalService> _log;

    public InstanceRemovalService(
        AppDbContext db, DockerHostService docker, BackupStore backups,
        InstanceService instances, ILogger<InstanceRemovalService> log)
    {
        _db = db; _docker = docker; _backups = backups; _instances = instances; _log = log;
    }

    // The ways, as they travel in the form. Strings rather than the numbers of an enum, for the same
    // reason the sync modes are strings: a value that does not parse must fall to the harmless end,
    // not to whatever happens to be 0.
    public const string ModeUnregister = "unregister";
    public const string ModeKeepData = "keep-data";
    public const string ModeFull = "full";

    /// <summary>
    /// The third way: take a backup first, then remove container and volumes.
    /// <para>Only combined with the full removal, and deliberately not offered alongside "keep the
    /// volumes" as well. A backup earns its wait because the data is about to go; where the volumes
    /// stay, the data stays with them, and a fourth line on that page would ask the operator to
    /// weigh a difference that does not exist.</para>
    /// </summary>
    public const string ModeBackupFirst = "backup-first";

    /// <summary>How long a wait may run before the operator is told it is still running. Not a
    /// deadline — nothing happens when it passes except that somebody is informed. See
    /// <see cref="Instance.PendingRemovalMode"/> for why there is no deadline at all.</summary>
    public static readonly TimeSpan WaitNoticeAfter = TimeSpan.FromHours(6);

    /// <param name="Ok">Whether the outcome is what was asked for.</param>
    /// <param name="Message">What to tell the operator, verbatim.</param>
    /// <param name="Removed">Whether the instance record is gone. False on every refusal: the record
    /// only goes when everything before it worked.</param>
    public sealed record Outcome(bool Ok, string Message, bool Removed);

    /// <summary>The only way open to an instance the cloud did not build: forget it here and leave it
    /// alone out there. The site keeps running; it simply stops being managed.</summary>
    public async Task<Outcome> UnregisterAsync(Instance item, CancellationToken ct = default)
    {
        var name = item.Name;
        _db.Instances.Remove(item);
        await _db.SaveChangesAsync(ct);
        _log.LogWarning("Instance {Name} unregistered; its site was not touched.", name);
        return new Outcome(true, $"\"{name}\" wurde aus der Cloud abgemeldet. Die Website läuft unverändert weiter.", true);
    }

    /// <summary>
    /// Removes the container (and, for <see cref="ModeFull"/>, its named volumes) and then the
    /// record. This is the immediate teardown — the delayed one ends up here too, once its backup
    /// is safe.
    /// </summary>
    /// <param name="expectedContainerId">What the operator was SHOWN. Compared, never used as the
    /// target: a confirmation screen that hands its own answer back as the instruction is how the
    /// wrong container gets removed.</param>
    public async Task<Outcome> TearDownAsync(
        Instance item, string mode, string? expectedContainerId, CancellationToken ct = default)
    {
        var target = await _docker.InspectTeardownAsync(item.ContainerId, ct);
        if (target is null)
            return new Outcome(false,
                "Zu dieser Instanz gibt es auf diesem Host keinen Container. Es wurde nichts entfernt.", false);

        if (!target.CloudManaged)
            return new Outcome(false,
                "Diese Instanz wurde nicht von dieser Cloud angelegt. Ihr Container wird nicht angefasst.", false);

        if (!string.Equals(target.Id, expectedContainerId, StringComparison.Ordinal))
            // Between the question and the answer the instance re-classified, or the page was stale.
            // Removing something the operator was never shown is exactly what must not happen — and
            // for the delayed way the gap is minutes or hours rather than seconds, so this check
            // earns its keep twice over.
            return new Outcome(false,
                "Der Container hat sich geändert, seit die Rückfrage angezeigt wurde. Es wurde nichts entfernt — bitte erneut prüfen.", false);

        var withVolumes = mode == ModeFull;
        _log.LogWarning("Teardown of instance {Name} ({PublicId}): container {Container}, volumes {Volumes}",
            item.Name, item.PublicId, target.Id, withVolumes ? string.Join(", ", target.Volumes) : "(kept)");

        var result = await _docker.RemoveInstanceContainerAsync(target.Id, withVolumes, ct);
        if (!result.Ok) return new Outcome(false, result.Message, false);

        var name = item.Name;
        _db.Instances.Remove(item);
        await _db.SaveChangesAsync(ct);
        return new Outcome(true, $"\"{name}\": {result.Message}", true);
    }

    /// <summary>
    /// Confirms the delayed way: ask the instance for a backup and put the removal on hold until it
    /// is here. Nothing is removed by this call — that is the entire point of it.
    /// </summary>
    public async Task<Outcome> ScheduleWithBackupAsync(
        Instance item, string? expectedContainerId, CancellationToken ct = default)
    {
        // Checked NOW as well as later. Not because the later check could be skipped, but because an
        // operator who cannot have this way should be told so while they are still looking at the
        // page, rather than six hours later in an event log.
        var target = await _docker.InspectTeardownAsync(item.ContainerId, ct);
        if (target is null)
            return new Outcome(false,
                "Zu dieser Instanz gibt es auf diesem Host keinen Container. Es wurde nichts vorgemerkt.", false);
        if (!target.CloudManaged)
            return new Outcome(false,
                "Diese Instanz wurde nicht von dieser Cloud angelegt. Ihr Container wird nicht angefasst.", false);
        if (!string.Equals(target.Id, expectedContainerId, StringComparison.Ordinal))
            return new Outcome(false,
                "Der Container hat sich geändert, seit die Rückfrage angezeigt wurde. Es wurde nichts vorgemerkt — bitte erneut prüfen.", false);

        item.PendingRemovalMode = ModeFull;
        item.PendingRemovalContainerId = target.Id;
        item.PendingRemovalAt = DateTime.UtcNow;
        item.PendingRemovalError = null;
        _instances.Log(item, InstanceEventKind.RemovalPending,
            "Entfernen vorgemerkt — wartet auf das angeforderte Backup.");
        await _db.SaveChangesAsync(ct);

        // Bumps the request id, so an upload answering an older request cannot end this wait.
        await _instances.RequestBackupAsync(item, ct);

        return new Outcome(true,
            $"Für \"{item.Name}\" wurde ein Backup angefordert. Entfernt wird erst, wenn es hier angekommen ist.", false);
    }

    /// <summary>Asks again after a refusal. Same request, new id — an answer to the failed attempt
    /// must not be able to satisfy the new one.</summary>
    public async Task<Outcome> RetryBackupAsync(Instance item, CancellationToken ct = default)
    {
        item.PendingRemovalError = null;
        await _instances.RequestBackupAsync(item, ct);
        return new Outcome(true, $"Für \"{item.Name}\" wurde erneut ein Backup angefordert.", false);
    }

    /// <summary>Takes a waiting removal back. Always available, and the only way out of the wait other
    /// than the backup arriving — which is what makes "it never times out into a deletion" a promise
    /// rather than a trap.</summary>
    public async Task<Outcome> CancelPendingAsync(Instance item, CancellationToken ct = default)
    {
        item.PendingRemovalMode = null;
        item.PendingRemovalContainerId = null;
        item.PendingRemovalAt = null;
        item.PendingRemovalError = null;
        // The backup request goes with it. Leaving it standing would have the site making a backup
        // for a removal that is no longer going to happen.
        item.BackupRequestId = 0;
        item.BackupRequestedAt = null;
        item.BackupRequestError = null;
        item.BackupWaitNotified = false;
        _instances.Log(item, InstanceEventKind.RemovalCancelled,
            "Vorgemerktes Entfernen zurückgenommen. Es wurde nichts entfernt.");
        await _db.SaveChangesAsync(ct);
        return new Outcome(true, $"Das vorgemerkte Entfernen von \"{item.Name}\" wurde zurückgenommen.", false);
    }

    /// <summary>
    /// Carries out one waiting removal, if — and only if — its backup is really here.
    ///
    /// <para>The order is the whole design and it is not negotiable:</para>
    /// <list type="number">
    /// <item><b>Is the file here?</b> Asked of the stored row and of the disk, against the id of THIS
    /// request. No file, no further step — and no clock that eventually says "close enough".</item>
    /// <item><b>Is the container still the one we were shown?</b> Checked before anything is moved,
    /// so a wait that cannot be completed ends without having disturbed the backup.</item>
    /// <item><b>Archive the backup</b>, out of the instance's storage and into a row that has no
    /// foreign key to cascade. If this fails, NOTHING is removed: taking a backup and then losing it
    /// in the same operation is the worst outcome this way could have.</item>
    /// <item><b>Then, and only then, tear down.</b></item>
    /// </list>
    ///
    /// <para>Returns null while there is simply nothing to do yet — the normal answer, once a minute,
    /// for as long as the wait lasts.</para>
    /// </summary>
    public async Task<Outcome?> TryCompletePendingAsync(Instance item, CancellationToken ct = default)
    {
        if (item.PendingRemovalMode is null || item.PendingRemovalError is not null) return null;

        // 1) The gate. Everything else in this method is downstream of this one question.
        var backup = await _instances.ArrivedBackupAsync(item, _backups, ct);
        if (backup is null) return null;

        // 2) Still the same container? Asked before the backup is moved.
        var target = await _docker.InspectTeardownAsync(item.ContainerId, ct);
        if (target is null || !target.CloudManaged
            || !string.Equals(target.Id, item.PendingRemovalContainerId, StringComparison.Ordinal))
        {
            return await FailAsync(item,
                "Das Backup ist da, aber der Container ist nicht mehr der, der vorgemerkt wurde. "
                + "Es wurde nichts entfernt.", ct);
        }

        // 3) The backup is lifted clear of the instance BEFORE the instance goes. Its rows cascade
        //    with the record — on every way, including the one that keeps the volumes — so a backup
        //    still hanging off this instance when it is deleted is a backup that is deleted with it.
        var reason = $"Vor dem vollständigen Entfernen der Instanz am {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC gesichert.";
        var archived = await _backups.ArchiveAsync(backup, item, reason, ct);
        if (archived is null)
            return await FailAsync(item,
                "Das Backup konnte nicht ins Archiv übernommen werden. Es wurde nichts entfernt.", ct);

        // 4) Now the site may go.
        var outcome = await TearDownAsync(item, item.PendingRemovalMode, item.PendingRemovalContainerId, ct);
        if (!outcome.Ok) return await FailAsync(item, outcome.Message, ct);

        _log.LogWarning("Instance {Name} removed after its backup {File} was archived as #{Id}",
            item.Name, archived.FileName, archived.Id);
        return new Outcome(true,
            $"{outcome.Message} Das Backup \"{archived.FileName}\" liegt im Archiv.", true);
    }

    /// <summary>Ends a wait in something an operator can read. The removal stays pending — it is not
    /// silently dropped and it is certainly not carried out — but it stops retrying, because a step
    /// that failed for a reason will fail again for the same reason once a minute for ever.</summary>
    private async Task<Outcome> FailAsync(Instance item, string message, CancellationToken ct)
    {
        item.PendingRemovalError = message;
        _instances.Log(item, InstanceEventKind.RemovalPending, message);
        await _db.SaveChangesAsync(ct);
        _log.LogError("Pending removal of instance {Name} could not be completed: {Message}", item.Name, message);
        return new Outcome(false, message, false);
    }

    /// <summary>Every instance with a removal waiting on a backup. Loaded for the watchdog; the list
    /// is normally empty.</summary>
    public Task<List<Instance>> PendingAsync(CancellationToken ct = default) =>
        _db.Instances.Where(i => i.PendingRemovalMode != null).ToListAsync(ct);
}
