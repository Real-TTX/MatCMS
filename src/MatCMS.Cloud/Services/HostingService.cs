using Docker.DotNet;
using Docker.DotNet.Models;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Was die Cloud braucht, BEVOR sie eine Instanz anlegt: darf sie überhaupt, und welcher Port ist
/// frei.
///
/// <para>Bewusst ohne jede erzeugende Methode. Die Portsuche ist reines Lesen und lässt sich dadurch
/// prüfen, ohne dass ein Container entsteht — und sie ist der Teil, an dem ein Fehler am teuersten
/// wäre: ein doppelt vergebener Port lässt den neuen Container gar nicht erst starten, ein zu
/// großzügig gewählter kollidiert später mit etwas, das gerade nicht lief.</para>
/// </summary>
public class HostingService
{
    private readonly CloudContext _cloud;
    private readonly DockerHostService _docker;
    private readonly ILogger<HostingService> _log;

    public HostingService(CloudContext cloud, DockerHostService docker, ILogger<HostingService> log)
    {
        _cloud = cloud;
        _docker = docker;
        _log = log;
    }

    /// <summary>Vorgabe, wenn kein Bereich eingestellt ist.</summary>
    public const int DefaultPortFrom = 9110;
    public const int DefaultPortTo = 9199;

    public bool Enabled => _cloud.Flag(SettingKeys.HostingEnabled);

    /// <summary>True, wenn der Betreiber Matcad die Route überlassen will.</summary>
    public bool UsesMatcad => _cloud.Get(SettingKeys.HostingMode) != "docker";

    public (int From, int To) PortRange
    {
        get
        {
            var from = int.TryParse(_cloud.Get(SettingKeys.HostingPortFrom), out var f) ? f : DefaultPortFrom;
            var to = int.TryParse(_cloud.Get(SettingKeys.HostingPortTo), out var t) ? t : DefaultPortTo;
            // Verdreht eingegeben wird getauscht statt abgelehnt: die Absicht ist eindeutig, und ein
            // leerer Bereich hieße, dass nie ein Port gefunden wird.
            return from <= to ? (from, to) : (to, from);
        }
    }

    /// <summary>
    /// Der nächste freie Port aus dem eingestellten Bereich, oder null wenn keiner mehr frei ist.
    ///
    /// <para>Gefragt wird der Docker-Daemon, nicht die eigene Datenbank: belegt ist ein Port auch
    /// dann, wenn ihn etwas anderes als eine MatCMS-Instanz hält — und selbst eine GESTOPPTE Instanz
    /// zählt, weil sie ihn beim nächsten Start wieder beansprucht. Deshalb <c>All = true</c>.</para>
    ///
    /// <para>Ohne erreichbaren Daemon kommt null zurück und nicht der erste Port des Bereichs: eine
    /// Vermutung wäre hier schlechter als ein ehrliches "weiß ich nicht", weil sie erst beim Starten
    /// des Containers auffliegt.</para>
    /// </summary>
    public async Task<int?> NextFreePortAsync(CancellationToken ct = default)
    {
        var used = await UsedPortsAsync(ct);
        if (used is null) return null;

        var (from, to) = PortRange;
        for (var port = from; port <= to; port++)
        {
            if (!used.Contains(port)) return port;
        }
        _log.LogWarning("Kein freier Port zwischen {From} und {To} — {Count} sind belegt.", from, to, used.Count);
        return null;
    }

    /// <summary>Alle auf dem Host veröffentlichten Ports, oder null wenn der Daemon nicht erreichbar
    /// ist. Null und leer sind hier zwei verschiedene Antworten.</summary>
    public async Task<HashSet<int>?> UsedPortsAsync(CancellationToken ct = default)
    {
        var client = _docker.ClientOrNull;
        if (client is null) return null;

        try
        {
            var list = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
            return list
                .SelectMany(c => c.Ports ?? new List<Port>())
                .Where(p => p.PublicPort > 0)
                .Select(p => (int)p.PublicPort)
                .ToHashSet();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Belegte Ports konnten nicht ermittelt werden.");
            return null;
        }
    }
}
