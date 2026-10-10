using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services.Nodes;

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
    private readonly AppDbContext _db;
    private readonly NodeService _nodes;
    private readonly Proxy.ProxyService _proxy;

    public HostingService(CloudContext cloud, DockerHostService docker, AppDbContext db, NodeService nodes, Proxy.ProxyService proxy)
    {
        _cloud = cloud;
        _docker = docker;
        _db = db;
        _nodes = nodes;
        _proxy = proxy;
    }

    /// <summary>Vorgabe, wenn kein Bereich eingestellt ist.</summary>
    public const int DefaultPortFrom = 9201;
    public const int DefaultPortTo = 9299;

    public bool Enabled => _cloud.Flag(SettingKeys.HostingEnabled);

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
    public Task<int?> NextFreePortAsync(CancellationToken ct = default)
    {
        var (from, to) = PortRange;
        return _docker.NextFreePortAsync(from, to, ct);
    }

    /// <summary>Alle auf dem Host veröffentlichten Ports, oder null wenn der Daemon nicht erreichbar
    /// ist. Null und leer sind hier zwei verschiedene Antworten.</summary>
    public Task<HashSet<int>?> UsedPortsAsync(CancellationToken ct = default) => _docker.UsedPortsAsync(ct);

    // --- Anlegen ---------------------------------------------------------------------------------

    /// <param name="Name">Anzeigename; daraus wird der Container- und Volume-Name abgeleitet.</param>
    /// <param name="Domain">Optional. Published after creation by <c>ProxyService</c> through the configured
    /// provider (route + certificate), or only recorded when no proxy is configured.</param>
    /// <param name="NodeId">Null = on this cloud's own Docker host.</param>
    public sealed record CreateRequest(string Name, string? Domain, string ImageTag, string JoinCode, int? NodeId = null);

    /// <param name="Node">Where it was created (null = this host) — the domain is published there too.</param>
    public sealed record CreateResult(bool Ok, string? Error, string? ContainerId, int? Port, string? ContainerName, Models.Node? Node = null);

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
        // Docker/compose stack names allow ONLY [a-z0-9-]. char.IsLetterOrDigit is true for 'ä'/'ö'/'ü'/'ß'
        // (they are Unicode letters), so they used to slip through and produce an INVALID stack name —
        // "Der Käseschneider" became "der-käseschneider" and Docker rejected it. Transliterate the German
        // umlauts, then keep STRICTLY ASCII a-z0-9 (every other character becomes a separator), so nothing
        // outside the allowed set can ever reach the stack name.
        var lowered = name.ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var parts = new string(lowered.Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : ' ').ToArray())
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

    /// <summary>The name part of a stack/container name — the reverse of <see cref="StackName"/>: with the pattern
    /// "matcms-instance-{name}", "matcms-instance-kunde-a" gives "kunde-a". Null when the container does not follow
    /// the pattern (started by hand, or the pattern changed since).</summary>
    public static string? SlugFromStack(string? containerName, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(containerName)) return null;
        if (string.IsNullOrWhiteSpace(pattern)) pattern = DefaultNamePattern;
        var p = pattern.ToLowerInvariant().Replace("$name", "{name}");
        var at = p.IndexOf("{name}", StringComparison.Ordinal);
        string prefix, suffix;
        if (at < 0) { prefix = p.TrimEnd('-') + "-"; suffix = ""; }
        else { prefix = p[..at]; suffix = p[(at + 6)..]; }
        var c = containerName.ToLowerInvariant();
        if (!c.StartsWith(prefix, StringComparison.Ordinal) || !c.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var slug = c[prefix.Length..(c.Length - suffix.Length)].Trim('-');
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>
    /// Legt einen neuen MatCMS-Container an und startet ihn — auf dem Docker-Host dieser Cloud oder, mit
    /// <see cref="CreateRequest.NodeId"/>, auf einem verbundenen Node. Beide Wege laufen durch DIESELBE
    /// Engine-Methode (<see cref="DockerHostService.CreateInstanceContainerAsync"/>): hier im Prozess, auf dem
    /// Node als Auftrag. Label, Portwahl und das Aufräumen eines halb gebauten Containers liegen deshalb dort.
    /// </summary>
    public async Task<CreateResult> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        if (!Enabled) return new(false, "Hosting ist in den Einstellungen nicht eingeschaltet.", null, null, null);

        Models.Node? node = null;
        if (req.NodeId is { } nid)
        {
            node = await _db.Nodes.FindAsync(new object[] { nid }, ct);
            if (node is null) return new(false, "Node nicht gefunden.", null, null, null);
        }
        else if (_docker.ClientOrNull is null) return new(false, "Docker ist nicht erreichbar.", null, null, null);

        var stack = StackName(req.Name);
        var image = "ghcr.io/real-ttx/matcms:" + (string.IsNullOrWhiteSpace(req.ImageTag) ? "latest" : req.ImageTag.Trim());
        var (from, to) = node is null ? PortRange : (node.PortFrom, node.PortTo);
        var spec = new DockerHostService.InstanceContainerSpec(stack, stack + "-data", image, new List<string>
        {
            // Damit sie sich selbst anmeldet und ihr Profil bekommt. Auf einem Node muss diese Adresse von
            // DORT erreichbar sein — deshalb die öffentliche Adresse der Cloud, nie ein interner Name.
            "MatCms__Cloud__Url=" + (_cloud.Get(SettingKeys.CanonicalUrl) ?? ""),
            "MatCms__Cloud__JoinCode=" + req.JoinCode,
            // Sie startet hinter einem Proxy — ohne das baut sie http-Adressen und wäre in der Cloud weder
            // einbettbar noch richtig verlinkt.
            "MatCms__Proxy__TrustAll=true",
        }, from, to);

        // No domain requirement: the container runs and is reachable on its host port either way, and a domain
        // given at provisioning is published by ProxyService right after (route or record).
        DockerHostService.CreateContainerResult r;
        if (node is null) r = await _docker.CreateInstanceContainerAsync(spec, ct);
        else
        {
            // Pulling an image on a fresh host can take a while.
            var job = await _nodes.RunAsync(node, NodeJobKinds.Create, spec, null, TimeSpan.FromMinutes(5), ct);
            r = job.Finished
                ? NodeJobExecutor.Deserialize<DockerHostService.CreateContainerResult>(job.ResultJson) ?? new(false, job.Message, null, null, null)
                : new(false, job.Message, null, null, null);
        }
        return new(r.Ok, r.Error, r.ContainerId, r.Port, r.ContainerName, node);
    }

    public sealed record ProvisionResult(bool Ok, string Message, string? ContainerName = null, int? Port = null, bool DomainFailed = false);

    /// <summary>
    /// Provisioning as ONE step for the UI, the API and the MCP tool: container (here or on a node), then the
    /// domain on the same host. No instance row is created here — the site appears when IT enrolls with its
    /// profile's join code; two ways a record comes into being would be two truths. A route set up now is
    /// adopted on that first beat.
    /// </summary>
    public async Task<ProvisionResult> ProvisionAsync(string? name, int profileId, string? domain, string? imageTag, int? nodeId,
        bool pushCanonical, CancellationToken ct = default)
    {
        if (!Enabled) return new(false, "Hosting ist in den Einstellungen nicht eingeschaltet.");
        if (string.IsNullOrWhiteSpace(name)) return new(false, "Bitte einen Namen angeben.");
        // The join code comes from the PROFILE — it is how the new site lands there and gets its templates,
        // plugins and users.
        var profile = await _db.Profiles.FindAsync(new object[] { profileId }, ct);
        if (profile is null) return new(false, "Bitte ein Profil wählen.");
        if (!string.IsNullOrWhiteSpace(domain) && Proxy.ProxyService.NormaliseDomain(domain) is null)
            return new(false, "Keine gültige Domain (nur ein Hostname, z. B. shop.example.de).");
        // The new container finds the cloud ONLY through this address (MatCms__Cloud__Url). Without it the
        // site runs but never enrolls — a container nobody sees, so refuse before creating it.
        if (string.IsNullOrWhiteSpace(_cloud.Get(SettingKeys.CanonicalUrl)))
            return new(false, "Die öffentliche Adresse der Cloud fehlt (Einstellungen → Allgemein) — ohne sie kann sich die neue Instanz nicht anmelden.");

        var result = await CreateAsync(new CreateRequest(name.Trim(), domain?.Trim(), imageTag ?? "", profile.JoinCode, nodeId), ct);
        if (!result.Ok) return new(false, $"Anlegen fehlgeschlagen: {result.Error}");

        var where = result.Node is null ? "" : $" auf Node „{result.Node.Name}“";
        var msg = $"„{result.ContainerName}“ läuft{where} auf Port {result.Port}. Sie meldet sich in den nächsten Minuten selbst an.";
        // Routes right away: the automatic host address when the host has the feature on, and the customer domain
        // when one was given. Both are adopted by the instance's first beat.
        if (result.ContainerId is not null && result.ContainerName is not null)
        {
            var pr = await _proxy.ProvisionRoutesAsync(result.Node, result.ContainerId, result.ContainerName, result.Port,
                name.Trim(), domain, pushCanonical, ct);
            if (!pr.Ok)
                return new(true, msg + $" Die Domain wurde NICHT eingerichtet: {pr.Message} — im Hosting-Tab der Instanz erneut veröffentlichen.",
                    result.ContainerName, result.Port, DomainFailed: true);
            if (pr.Message.Length > 0) msg += " " + pr.Message;
        }
        return new(true, msg, result.ContainerName, result.Port);
    }
}
