using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;

namespace MatCMS.Cloud.Services.Nodes;

/// <summary>
/// The node-agent: <c>dotnet MatCMS.Cloud.dll --node-agent</c> — the cloud image in a second mode, entered
/// BEFORE the web app is built (like the <c>--self-update</c> helper), so an agent never opens a database,
/// never migrates, never serves a page. It only talks to its own Docker daemon and, outbound, to the cloud.
/// <para>Configuration (the only three things a host has to be given, printed by the cloud when the node is
/// created): <c>MatCmsNode__CloudUrl</c>, <c>MatCmsNode__NodeId</c>, <c>MatCmsNode__Token</c>. The Docker
/// endpoint defaults to the mounted socket. Everything else (proxy, ports) is configured in the cloud and
/// arrives with the jobs.</para>
/// <para>Loop: beat → run the handed-out jobs concurrently → beat again as soon as one finishes. Idle, the
/// beat is a long poll the cloud answers the moment it has a job — near-instant actions without ever
/// accepting a connection.</para>
/// </summary>
public static class NodeAgentRunner
{
    public static async Task<int> RunAsync(CancellationToken stop = default)
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var cloudUrl = (config["MatCmsNode:CloudUrl"] ?? "").Trim().TrimEnd('/');
        var nodeId = (config["MatCmsNode:NodeId"] ?? "").Trim();
        var token = (config["MatCmsNode:Token"] ?? "").Trim();
        if (cloudUrl.Length == 0 || nodeId.Length == 0 || token.Length == 0)
        {
            Console.Error.WriteLine("MatCmsNode__CloudUrl, MatCmsNode__NodeId und MatCmsNode__Token müssen gesetzt sein.");
            return 2;
        }

        // Same key DockerHostService reads in the cloud; on a node the mounted socket is the obvious default.
        var dockerConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MatCmsCloud:Docker:Endpoint"] = "unix:///var/run/docker.sock" })
            .AddEnvironmentVariables().Build();
        using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
        var log = loggers.CreateLogger("NodeAgent");
        // A FRESH engine (and Docker client) per beat and per job — see DockerHostService.Dispose: a client whose
        // connection pool got stuck once would otherwise silence the agent for good.
        DockerHostService NewDocker() => new(dockerConfig, loggers.CreateLogger<DockerHostService>());

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "local";
        var plus = version.IndexOf('+'); if (plus > 0) version = version[..plus];

        // The long poll holds up to 25 s; everything above that is a dead connection.
        using var cloud = new HttpClient { Timeout = NodeProtocol.LongPoll + TimeSpan.FromSeconds(20) };
        cloud.DefaultRequestHeaders.Add(NodeProtocol.TokenHeader, token);
        using var proxyHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Moving a site streams its whole data volume — no fixed timeout; the job's own cancellation ends it.
        using var transferHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        transferHttp.DefaultRequestHeaders.Add(NodeProtocol.TokenHeader, token);
        var transfer = new HttpNodeTransfer(transferHttp, cloudUrl, nodeId);
        var url = $"{cloudUrl}/api/nodes/{Uri.EscapeDataString(nodeId)}/heartbeat";

        var reports = new ConcurrentQueue<NodeJobReport>();
        var wake = new CancellationTokenSource();
        var backoff = TimeSpan.FromSeconds(2);
        log.LogInformation("MatCMS-Node-Agent {Version} → {Cloud} (Node {Node}).", version, cloudUrl, nodeId);

        while (!stop.IsCancellationRequested)
        {
            // Bounded: a daemon call that never returns must not silence the agent — it then beats WITHOUT an
            // inventory and reports why, instead of disappearing from the cloud.
            using var docker = NewDocker();
            using var dockerCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            dockerCts.CancelAfter(TimeSpan.FromSeconds(20));
            string? dockerVersion = null, hostName = null, dockerError;
            try { (dockerVersion, hostName, dockerError) = await docker.DaemonInfoAsync(dockerCts.Token); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { dockerError = "Docker-Daemon antwortet nicht (20 s)."; }
            List<NodeContainer>? containers = null;
            if (dockerError is null)
            {
                try
                {
                    containers = (await docker.ListMatCmsContainersAsync(dockerCts.Token) ?? new())
                        .Select(c => new NodeContainer { Id = c.Id, Name = c.Name, Image = c.Image, State = c.State, PublishedPort = c.PublishedPort, CloudManaged = c.CloudManaged })
                        .ToList();
                }
                catch (Exception ex) { dockerError = ex.Message; }
            }

            // Taken BEFORE draining: a job finishing after this point cancels exactly this token; one finishing
            // before it has already queued its report, which the drain below picks up. No gap either way.
            var wakeToken = Volatile.Read(ref wake).Token;
            var sending = new List<NodeJobReport>();
            while (reports.TryDequeue(out var r)) sending.Add(r);
            var req = new NodeHeartbeatRequest
            {
                ProtocolVersion = NodeProtocol.Version, AgentVersion = version, HostName = hostName ?? Environment.MachineName,
                DockerVersion = dockerVersion, DockerError = dockerError, Containers = containers, Reports = sending,
                // Only an idle agent may be held: with reports in flight, or a job about to finish, the cloud
                // must answer at once.
                Wait = sending.Count == 0,
            };

            // A finishing job cancels an idle long poll so its report goes out immediately. Only an idle beat
            // (no reports inside) is ever cancelled, so nothing can be lost by cancelling it.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, req.Wait ? wakeToken : CancellationToken.None);
            try
            {
                using var resp = await cloud.PostAsJsonAsync(url, req, linked.Token);
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    log.LogWarning("Die Cloud weist diesen Node ab ({Status}) — gesperrt oder Token falsch. Neuer Versuch in 60 s.", (int)resp.StatusCode);
                    foreach (var r in sending) reports.Enqueue(r);
                    await Task.Delay(TimeSpan.FromSeconds(60), stop);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadFromJsonAsync<NodeHeartbeatResponse>(cancellationToken: linked.Token);
                backoff = TimeSpan.FromSeconds(2);

                foreach (var job in body?.Jobs ?? new())
                {
                    log.LogInformation("Auftrag {Id}: {Kind}", job.Id, job.Kind);
                    _ = Task.Run(async () =>
                    {
                        using var jobDocker = NewDocker();
                        var rep = await NodeJobExecutor.ExecuteAsync(job, jobDocker, proxyHttp, transfer, stop);
                        log.LogInformation("Auftrag {Id} {Result}: {Message}", job.Id, rep.Ok ? "ok" : "FEHLER", rep.Message);
                        reports.Enqueue(rep);
                        var old = Interlocked.Exchange(ref wake, new CancellationTokenSource());
                        old.Cancel();
                    }, stop);
                }
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                // woken by a finished job (or the HTTP timeout) — beat again right away
                if (!req.Wait) foreach (var r in sending) reports.Enqueue(r);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                // The cloud may or may not have processed the reports; it folds a report only into a job that is
                // still running, so sending one twice is harmless — losing one is not.
                foreach (var r in sending) reports.Enqueue(r);
                log.LogWarning("Cloud nicht erreichbar: {Message} — neuer Versuch in {Delay} s.", ex.Message, (int)backoff.TotalSeconds);
                await Task.Delay(backoff, stop).ContinueWith(_ => { });
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
        }
        return 0;
    }
}
