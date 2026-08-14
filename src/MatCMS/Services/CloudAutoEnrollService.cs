using MatCMS.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Meldet eine frisch gestartete Website bei der Cloud an, wenn Adresse und Join-Code als
/// Umgebungsvariablen mitgegeben wurden.
///
/// <para>Das ist die fehlende Hälfte des Provisionings: eine Cloud kann einen Container erzeugen,
/// aber ohne das hier stünde er unverbunden da und jemand müsste sich einloggen und den Code von Hand
/// eintippen — womit das Anlegen aus der Cloud seinen Sinn verlöre.</para>
///
/// <para>Genau EINMAL, und nur solange keine Verbindung besteht: die Kennung der Instanz ist der
/// Prüfstein. Ein Container, der neu startet, meldet sich also nicht ein zweites Mal an, und eine
/// von Hand umgehängte Website wird nicht durch eine alte Umgebungsvariable zurückgerissen.</para>
///
/// <para>Als Hintergrunddienst und nicht im Startpfad: die Cloud ist beim ersten Start des Containers
/// womöglich noch gar nicht erreichbar, und eine Website, die deswegen nicht hochkommt, wäre der
/// schlechtere Tausch. Es wird in Abständen erneut versucht.</para>
/// </summary>
public class CloudAutoEnrollService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<CloudAutoEnrollService> _log;

    public CloudAutoEnrollService(IServiceScopeFactory scopes, IConfiguration config, ILogger<CloudAutoEnrollService> log)
    {
        _scopes = scopes;
        _config = config;
        _log = log;
    }

    /// <summary>Zwischen zwei Versuchen. Kurz genug, dass eine gerade startende Cloud schnell gefunden
    /// wird, lang genug, dass ein falscher Code kein Dauerfeuer erzeugt.</summary>
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var url = (_config["MatCms:Cloud:Url"] ?? "").Trim();
        var code = (_config["MatCms:Cloud:JoinCode"] ?? "").Trim();
        if (url.Length == 0 || code.Length == 0) return;   // nichts vorgegeben, nichts zu tun

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var connected = await db.SiteSettings.AsNoTracking()
                    .AnyAsync(s => s.Key == SettingKeys.CloudInstanceId && s.Value != "", stoppingToken);
                if (connected) return;   // schon verbunden — hier gibt es nichts mehr zu tun

                var cloud = scope.ServiceProvider.GetRequiredService<CloudService>();
                var (ok, error) = await cloud.RegisterAsync(url, code, stoppingToken);
                if (ok)
                {
                    _log.LogInformation("Automatisch bei der Cloud unter {Url} angemeldet.", url);
                    return;
                }
                // Kein Abbruch: beim ersten Start eines frisch erzeugten Containers ist die Cloud oft
                // noch nicht so weit. Ein dauerhaft falscher Code fällt in der Cloud auf, nicht hier.
                _log.LogWarning("Automatische Anmeldung bei {Url} noch nicht möglich: {Error}", url, error);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Automatische Anmeldung fehlgeschlagen.");
            }

            try { await Task.Delay(Retry, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
