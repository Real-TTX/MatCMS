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
/// </summary>
public class DeleteModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DockerHostService _docker;
    private readonly ILogger<DeleteModel> _log;

    public DeleteModel(AppDbContext db, DockerHostService docker, ILogger<DeleteModel> log)
    {
        _db = db; _docker = docker; _log = log;
    }

    // The ways, as they travel in the form. Strings rather than an enum's numbers for the same
    // reason the sync modes are strings: a value that does not parse must fall to the harmless end,
    // not to whatever happens to be 0.
    public const string ModeUnregister = "unregister";
    public const string ModeKeepData = "keep-data";
    public const string ModeFull = "full";

    public Instance Item { get; private set; } = new();

    /// <summary>What a teardown would actually touch, as the daemon reports it. Null when the
    /// container is not on this host — which is the normal state of a remote instance.</summary>
    public DockerHostService.TeardownTarget? Target { get; private set; }

    /// <summary>Whether the destructive ways may be offered at all. Both halves are required: the
    /// container has to be here AND has to be ours.</summary>
    public bool CanTearDown => Target is { CloudManaged: true };

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

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var item = await _db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return RedirectToPage("Index");
        Item = item;
        Target = await _docker.InspectTeardownAsync(item.ContainerId, HttpContext.RequestAborted);
        BackupCount = await _db.CloudBackups.CountAsync(b => b.InstanceId == id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, string? mode, string? containerId)
    {
        var item = await _db.Instances.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return RedirectToPage("Index");

        // Anything unrecognised is treated as the harmless way rather than guessed at. There is no
        // sensible "did you mean full removal?".
        mode = mode switch
        {
            ModeFull => ModeFull,
            ModeKeepData => ModeKeepData,
            _ => ModeUnregister
        };

        if (mode == ModeUnregister) return await UnregisterAsync(item);

        // Everything below removes a container, so the target is resolved from the RECORD and the
        // daemon — never from the form — and then checked against what the operator was shown.
        var target = await _docker.InspectTeardownAsync(item.ContainerId, HttpContext.RequestAborted);
        if (target is null)
        {
            TempData["FlashError"] = "Zu dieser Instanz gibt es auf diesem Host keinen Container. Es wurde nichts entfernt.";
            return RedirectToPage(new { id });
        }
        if (!target.CloudManaged)
        {
            TempData["FlashError"] = "Diese Instanz wurde nicht von dieser Cloud angelegt. Ihr Container wird nicht angefasst.";
            return RedirectToPage(new { id });
        }
        if (!string.Equals(target.Id, containerId, StringComparison.Ordinal))
        {
            // Between the question and the answer the instance re-classified, or the page was stale.
            // Removing something the operator was never shown is exactly what must not happen.
            TempData["FlashError"] = "Der Container hat sich geändert, seit die Rückfrage angezeigt wurde. Es wurde nichts entfernt — bitte erneut prüfen.";
            return RedirectToPage(new { id });
        }

        var withVolumes = mode == ModeFull;
        _log.LogWarning("Teardown of instance {Name} ({PublicId}) requested: container {Container}, volumes {Volumes}",
            item.Name, item.PublicId, target.Id, withVolumes ? string.Join(", ", target.Volumes) : "(kept)");

        var result = await _docker.RemoveInstanceContainerAsync(target.Id, withVolumes, HttpContext.RequestAborted);
        if (!result.Ok)
        {
            // The record stays. An instance whose container is still running must not vanish from the
            // list — that is how a container nobody knows about is left behind.
            TempData["FlashError"] = result.Message;
            return RedirectToPage(new { id });
        }

        var name = item.Name;
        _db.Instances.Remove(item);
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"\"{name}\": {result.Message}";
        return RedirectToPage("Index");
    }

    /// <summary>The only way open to an instance the cloud did not build: forget it here and leave it
    /// alone out there. The site keeps running; it simply stops being managed.</summary>
    private async Task<IActionResult> UnregisterAsync(Instance item)
    {
        var name = item.Name;
        _db.Instances.Remove(item);
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"\"{name}\" wurde aus der Cloud abgemeldet. Die Website läuft unverändert weiter.";
        return RedirectToPage("Index");
    }
}
