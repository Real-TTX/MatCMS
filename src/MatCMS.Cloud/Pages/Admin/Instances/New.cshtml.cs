using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Instances;

/// <summary>
/// Eine neue Instanz anlegen — die Seite, die den Erzeuger auslöst.
///
/// <para>Erreichbar nur, wenn Hosting eingeschaltet ist. Wer die Adresse direkt aufruft, landet
/// wieder in der Liste: eine Seite, deren einzige Schaltfläche sicher scheitert, ist keine.</para>
/// </summary>
public class NewModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly HostingService _hosting;
    private readonly ReleaseWatcher _releases;
    private readonly MatCMS.Cloud.Services.Proxy.ProxyService _proxy;

    public NewModel(AppDbContext db, HostingService hosting, ReleaseWatcher releases, MatCMS.Cloud.Services.Proxy.ProxyService proxy)
    {
        _db = db;
        _hosting = hosting;
        _releases = releases;
        _proxy = proxy;
    }

    public List<Profile> Profiles { get; private set; } = [];
    /// <summary>True when a real proxy (Matcad/Caddy) will route the domain; false = it is only recorded.</summary>
    public bool ProxyRoutes => _proxy.Provider().ManagesRoutes;
    public int? NextPort { get; private set; }

    /// <summary>Die neueste bekannte Version als Vorschlag. "latest" bleibt möglich, aber ein
    /// festgenagelter Stand ist die ehrlichere Vorgabe: er sagt, was gerade entsteht.</summary>
    public string? LatestVersion => _releases.LatestVersion;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!_hosting.Enabled) return RedirectToPage("Index");
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Profiles = await _db.Profiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        NextPort = await _hosting.NextFreePortAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostAsync(string? name, int profileId, string? domain, string? imageTag)
    {
        if (!_hosting.Enabled) return RedirectToPage("Index");

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["FlashError"] = "Bitte einen Namen angeben.";
            return RedirectToPage();
        }

        // Der Join-Code kommt vom PROFIL — er ist der Weg, auf dem die neue Instanz dort landet und
        // ihre Templates, Plugins und Benutzer bekommt.
        var profile = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId);
        if (profile is null)
        {
            TempData["FlashError"] = "Bitte ein Profil wählen.";
            return RedirectToPage();
        }

        var result = await _hosting.CreateAsync(
            new HostingService.CreateRequest(name.Trim(), domain?.Trim(), imageTag ?? "", profile.JoinCode),
            HttpContext.RequestAborted);

        if (!result.Ok)
        {
            TempData["FlashError"] = $"Anlegen fehlgeschlagen: {result.Error}";
            return RedirectToPage();
        }

        // Die Instanz taucht in der Liste auf, sobald SIE sich meldet — nicht schon jetzt. Hier wird
        // kein Datensatz angelegt: die Anmeldung ist der Moment, in dem eine Instanz existiert, und
        // zwei Wege, wie ein Eintrag entsteht, wären zwei Wahrheiten.
        var msg = $"„{result.ContainerName}“ läuft auf Port {result.Port}. Sie meldet sich in den nächsten Minuten selbst an.";
        if (!string.IsNullOrWhiteSpace(domain) && result.ContainerId is not null && result.ContainerName is not null)
        {
            // The route only needs the container, so it is created now; the instance adopts it when it enrolls.
            var pr = await _proxy.PublishForNewContainerAsync(result.ContainerId, result.ContainerName, result.Port,
                domain, pushCanonical: true, HttpContext.RequestAborted);
            if (!pr.Ok)
            {
                TempData["FlashError"] = msg + $" Die Domain wurde NICHT eingerichtet: {pr.Message} — im Hosting-Tab der Instanz erneut veröffentlichen.";
                return RedirectToPage("Index");
            }
            msg += " " + pr.Message;
        }
        TempData["Flash"] = msg;
        return RedirectToPage("Index");
    }
}
