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
    public const int DefaultPortFrom = 9201;
    public const int DefaultPortTo = 9299;

    public bool Enabled => _cloud.Flag(SettingKeys.HostingEnabled);

    /// <summary>True nur, wenn Matcad AUSDRÜCKLICH gewählt wurde. Nicht gesetzt heißt: kein Matcad.
    /// <para>Andersherum wäre es die falsche Vorgabe — eine frisch aufgesetzte Cloud hätte dann einen
    /// Weg voreingestellt, der ohne Adresse und Zugangsschlüssel gar nicht funktionieren kann.</para>
    /// </summary>
    public bool UsesMatcad => _cloud.Get(SettingKeys.HostingMode) == "matcad";

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

    // --- Anlegen ---------------------------------------------------------------------------------

    /// <param name="Name">Anzeigename; daraus wird der Container- und Volume-Name abgeleitet.</param>
    /// <param name="Domain">Nur ohne Matcad nötig — dort trägt sie der Betreiber selbst ein.</param>
    public sealed record CreateRequest(string Name, string? Domain, string ImageTag, string JoinCode);

    public sealed record CreateResult(bool Ok, string? Error, string? ContainerId, int? Port, string? ContainerName);

    public const string DefaultNamePattern = "matcms-instance-{name}";

    /// <summary>
    /// Aus einem Anzeigenamen ein Bezeichner nach Docker-Sitte: klein, Bindestriche statt allem
    /// anderen. "My Homepage.com" wird zu "my-homepage-com".
    /// <para>Klein geschrieben, weil docker compose Projektnamen selbst kleinschreibt — ein Stack in
    /// gemischter Schreibweise wäre genau das, was Compose nie erzeugen würde, und läse sich neben
    /// den übrigen Stacks wie ein Fremdkörper.</para>
    /// </summary>
    public static string Normalise(string name)
    {
        var parts = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Join('-', parts);
        return joined.Length == 0 ? "instanz" : joined;
    }

    /// <summary>Der Name des Stacks: das eingestellte Muster mit eingesetztem Namen. Er ist zugleich
    /// Container- und Volume-Name, damit in jeder Oberfläche dasselbe steht.</summary>
    public string StackName(string displayName)
    {
        var pattern = _cloud.Get(SettingKeys.HostingNamePattern);
        if (string.IsNullOrWhiteSpace(pattern)) pattern = DefaultNamePattern;
        var slug = Normalise(displayName);
        // {name} ist die Schreibweise; $NAME wird noch verstanden, weil es kurz die erste war.
        // Ein Muster OHNE Platzhalter ergäbe für jede Website denselben Namen — dann wird angehängt
        // statt ersetzt, sonst kollidiert die zweite Instanz mit der ersten.
        if (pattern.Contains("{name}", StringComparison.OrdinalIgnoreCase))
            return pattern.Replace("{name}", slug, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (pattern.Contains("$NAME"))
            return pattern.Replace("$NAME", slug).ToLowerInvariant();
        return (pattern.TrimEnd('-') + "-" + slug).ToLowerInvariant();
    }

    /// <summary>
    /// Legt einen neuen MatCMS-Container an und startet ihn.
    ///
    /// <para>Scheitert etwas nach dem Erzeugen, wird der halb gebaute Container wieder entfernt. Ein
    /// gestoppter Rest mit richtigem Namen wäre schlimmer als gar keiner: der nächste Versuch mit
    /// demselben Namen liefe darauf auf.</para>
    ///
    /// <para>Das Volume bleibt bewusst stehen. Es enthält ab der ersten Sekunde Daten der Website,
    /// und etwas zu löschen, das Inhalte tragen könnte, ist keine Aufräumarbeit für einen
    /// Fehlerpfad.</para>
    /// </summary>
    public async Task<CreateResult> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        if (!Enabled) return new(false, "Hosting ist in den Einstellungen nicht eingeschaltet.", null, null, null);

        var client = _docker.ClientOrNull;
        if (client is null) return new(false, "Docker ist nicht erreichbar.", null, null, null);

        var stack = StackName(req.Name);
        var containerName = stack;
        var volumeName = stack + "-data";

        if (!UsesMatcad && string.IsNullOrWhiteSpace(req.Domain))
            return new(false, "Ohne Matcad muss eine Domain angegeben werden.", null, null, null);

        var port = await NextFreePortAsync(ct);
        if (port is null) return new(false, "Kein freier Port im eingestellten Bereich.", null, null, null);

        var image = "ghcr.io/real-ttx/matcms:" + (string.IsNullOrWhiteSpace(req.ImageTag) ? "latest" : req.ImageTag.Trim());
        string? createdId = null;
        try
        {
            // Erst ziehen. Ohne das schlüge das Erzeugen mit einer Meldung fehl, die nach einem
            // Fehler im Aufruf aussieht statt nach einem fehlenden Image.
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image }, null, new Progress<JSONMessage>(), ct);

            var labels = new Dictionary<string, string>
            {
                [DockerHostService.ManagedLabel] = "true",
                // Die Labels, die Compose selbst schreibt. Jede Verwaltungsoberfläche — Docker
                // Desktop, Dockhand, Portainer — gruppiert danach, ohne von uns etwas zu wissen.
                // Deshalb dieser Weg und nicht das API eines bestimmten Werkzeugs.
                ["com.docker.compose.project"] = stack,
                ["com.docker.compose.service"] = "web",
                ["com.docker.compose.container-number"] = "1",
                ["com.docker.compose.oneoff"] = "False",
                // working_dir und config_files bleiben WEG: sie zeigen auf die Datei, aus der ein
                // Stack entstand. Ohne Datei dort wäre das eine Lüge gegenüber docker compose.
            };
            if (UsesMatcad)
            {
                // Matcad liest diese Labels und richtet die Route selbst ein.
                labels["matcad.enable"] = "true";
                if (!string.IsNullOrWhiteSpace(req.Domain)) labels["matcad.host"] = req.Domain!.Trim();
                labels["matcad.port"] = "8080";
            }

            var create = new CreateContainerParameters
            {
                Name = containerName,
                Image = image,
                Labels = labels,
                Env = new List<string>
                {
                    // Damit sie sich selbst anmeldet und ihr Profil bekommt.
                    "MatCms__Cloud__Url=" + (_cloud.Get(SettingKeys.CanonicalUrl) ?? ""),
                    "MatCms__Cloud__JoinCode=" + req.JoinCode,
                    // Sie startet hinter einem Proxy — ohne das baut sie http-Adressen und wäre in
                    // der Cloud weder einbettbar noch richtig verlinkt.
                    "MatCms__Proxy__TrustAll=true",
                },
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        ["8080/tcp"] = new List<PortBinding> { new() { HostPort = port.Value.ToString() } }
                    },
                    Binds = new List<string> { volumeName + ":/app/appdata" },
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                },
            };

            var created = await client.Containers.CreateContainerAsync(create, ct);
            createdId = created.ID;
            await client.Containers.StartContainerAsync(createdId, new ContainerStartParameters(), ct);

            _log.LogInformation("Instanz {Name} angelegt: Container {Id} auf Port {Port}.", containerName, createdId, port);
            return new(true, null, createdId, port, containerName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Anlegen von {Name} fehlgeschlagen.", containerName);
            if (createdId is not null)
            {
                try
                {
                    await client.Containers.RemoveContainerAsync(createdId,
                        new ContainerRemoveParameters { Force = true }, CancellationToken.None);
                }
                catch (Exception cleanup)
                {
                    _log.LogWarning(cleanup, "Der halb gebaute Container {Id} blieb stehen.", createdId);
                }
            }
            return new(false, ex.Message, null, null, null);
        }
    }
}