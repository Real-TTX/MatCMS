using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Instances;

/// <summary>
/// Taking an instance out of the cloud — on its own page, because this is the one action here that
/// can destroy a customer's site and the operator has to be told what each way actually does.
///
/// <para><b>The distinction this page exists for:</b> the cloud may only tear down what it BUILT
/// itself. An instance that joined with a code runs on somebody else's machine; the cloud may forget
/// it, but reaching into it is not the cloud's business. <see cref="Instance.CloudManaged"/> is that
/// answer — the label the cloud stamps on its own containers, re-read from the daemon on every
/// heartbeat — and it decides which ways this page even offers. A way that is not offered cannot be
/// clicked by accident.</para>
///
/// <para><b>The target is never taken from the page.</b> The form carries the container id only so
/// the POST can check that it is still describing the same thing the operator was shown; the id it
/// acts on comes from the instance record and is verified against the daemon again. A confirmation
/// screen that hands its own answer back as the instruction is how the wrong container gets removed.
/// If the two disagree — the site moved, the container was replaced — nothing happens and the
/// operator is asked to look again.</para>
///
/// <para><b>What the page does NOT do any more is the removing.</b> That lives in
/// <see cref="InstanceRemovalService"/>, because the third way is finished minutes later by the
/// watchdog and two implementations of "delete a customer's site" would be two chances to get the
/// guards subtly different.</para>
/// </summary>
public class DeleteModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DockerHostService _docker;
    private readonly InstanceRemovalService _removals;

    public DeleteModel(AppDbContext db, DockerHostService docker, InstanceRemovalService removals)
    {
        _db = db; _docker = docker; _removals = removals;
    }

    // Re-exposed so the view can name them without reaching into the service namespace.
    public const string ModeUnregister = InstanceRemovalService.ModeUnregister;
    public const string ModeKeepData = InstanceRemovalService.ModeKeepData;
    public const string ModeFull = InstanceRemovalService.ModeFull;
    public const string ModeBackupFirst = InstanceRemovalService.ModeBackupFirst;

    public Instance Item { get; private set; } = new();

    /// <summary>What a teardown would actually touch, as the daemon reports it. Null when the
    /// container is not on this host — which is the normal state of a remote instance.</summary>
    public DockerHostService.TeardownTarget? Target { get; private set; }

    /// <summary>Whether the destructive ways may be offered at all. Both halves are required: the
    /// container has to be here AND has to be ours.</summary>
    public bool CanTearDown => Target is { CloudManaged: true };

    /// <summary>
    /// Whether the instance can answer a backup request at all. An older one does not know the field
    /// and would ignore it, so the way would wait for ever on a healthy site.
    /// <para>Offering it anyway and letting it hang would be the worst of both: the operator believes
    /// a removal is under way, and it never is. A way that cannot work is not shown.</para>
    /// </summary>
    public bool CanBackupFirst => CanTearDown && !InstanceService.IsOutdatedProtocol(Item);

    /// <summary>
    /// How many backups this instance has lying in the cloud. Shown because they go with it: the
    /// rows hang off the instance and cascade away when it is deleted, whichever way is chosen —
    /// including the one that promises to keep the data.
    ///
    /// <para>"Datenträger behalten" would otherwise read as "everything is still there" while the
    /// last safety net quietly went with the record. Saying the number is the difference between an
    /// informed decision and a surprise.</para>
    /// </summary>
    public int BackupCount { get; private set; }

    /// <summary>The backup that has arrived for the outstanding request, if one has. What the page
    /// shows instead of "we asked" — because that is the whole difference this way is built on.</summary>
    public CloudBackup? ArrivedBackup { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var item = await _db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return RedirectToPage("Index");
        Item = item;
        Target = await _docker.InspectTeardownAsync(item.ContainerId, HttpContext.RequestAborted);
        BackupCount = await _db.CloudBackups.CountAsync(b => b.InstanceId == id);

        // Only interesting while something is waiting on it; asked here so the wait can say what it
        // is actually waiting for rather than only that it is waiting.
        if (item.RemovalPending)
            ArrivedBackup = await _db.CloudBackups.AsNoTracking()
                .Where(b => b.InstanceId == item.Id && b.RequestId == item.BackupRequestId)
                .OrderByDescending(b => b.UploadedAt)
                .FirstOrDefaultAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, string? mode, string? containerId)
    {
        var item = await _db.Instances.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return RedirectToPage("Index");

        // A removal already waiting must not be overtaken by a second answer to the same question —
        // a double submit, or a stale tab, would otherwise remove the site without its backup.
        if (item.RemovalPending)
        {
            TempData["FlashError"] = "Für diese Instanz ist bereits ein Entfernen vorgemerkt.";
            return RedirectToPage(new { id });
        }

        // Anything unrecognised is treated as the harmless way rather than guessed at. There is no
        // sensible "did you mean full removal?".
        mode = mode switch
        {
            ModeFull => ModeFull,
            ModeKeepData => ModeKeepData,
            ModeBackupFirst => ModeBackupFirst,
            _ => ModeUnregister
        };

        var outcome = mode switch
        {
            ModeUnregister => await _removals.UnregisterAsync(item, HttpContext.RequestAborted),
            ModeBackupFirst => await _removals.ScheduleWithBackupAsync(item, containerId, HttpContext.RequestAborted),
            _ => await _removals.TearDownAsync(item, mode, containerId, HttpContext.RequestAborted),
        };

        if (!outcome.Ok)
        {
            // The record stays. An instance whose container is still running must not vanish from the
            // list — that is how a container nobody knows about is left behind.
            TempData["FlashError"] = outcome.Message;
            return RedirectToPage(new { id });
        }

        TempData["Flash"] = outcome.Message;
        // A removal that is only PENDING keeps its page: there is a wait to look at, and being sent
        // back to the list would read as "done".
        return outcome.Removed ? RedirectToPage("Index") : RedirectToPage(new { id });
    }

    /// <summary>Asks the instance again after it refused or failed. New request id, so a late answer
    /// to the old one cannot end this wait.</summary>
    public async Task<IActionResult> OnPostRetryBackupAsync(int id)
    {
        var item = await _db.Instances.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return RedirectToPage("Index");
        if (!item.RemovalPending) return RedirectToPage(new { id });

        var outcome = await _removals.RetryBackupAsync(item, HttpContext.RequestAborted);
        TempData["Flash"] = outcome.Message;
        return RedirectToPage(new { id });
    }

    /// <summary>The way out of the wait. Always available — it is what makes "this never times out
    /// into a deletion" a promise instead of a trap.</summary>
    public async Task<IActionResult> OnPostCancelPendingAsync(int id)
    {
        var item = await _db.Instances.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return RedirectToPage("Index");

        var outcome = await _removals.CancelPendingAsync(item, HttpContext.RequestAborted);
        TempData["Flash"] = outcome.Message;
        return RedirectToPage(new { id });
    }
}
