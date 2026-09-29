using System.Security.Cryptography;
using System.Threading.Channels;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services.Proxy;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services.Nodes;

/// <summary>
/// Moving an instance between hosts (Hosting increment 5) — one implementation behind the Hosting tab, the
/// operator API and the MCP tool. Hosts are "Dieser Host" (null) or a node; every step goes through
/// <see cref="NodeService.RunOnAsync"/>, so a move local → node, node → node and node → local is one code path.
/// <para>The data volume is copied 1:1 (see <see cref="DockerHostService.ExportDataAsync"/> for why not
/// backup/restore): stop the source → export → the target creates the same container with the copied data and
/// starts it → wait until the INSTANCE beats from the target → move the proxy route → retire (or, if asked,
/// remove) the source.</para>
/// <para>The rule every failure path obeys: the same cloud identity must never run twice. The source is only
/// started again after the target container is verifiably gone; if the target cannot be removed, the source
/// stays stopped and the move says so.</para>
/// </summary>
public class MigrationService
{
    private readonly AppDbContext _db;
    private readonly NodeService _nodes;
    private readonly InstanceService _instances;
    private readonly ProxyService _proxy;
    private readonly DockerHostService _docker;
    private readonly MigrationQueue _queue;
    private readonly ILogger<MigrationService> _log;

    public MigrationService(AppDbContext db, NodeService nodes, InstanceService instances, ProxyService proxy,
        DockerHostService docker, MigrationQueue queue, ILogger<MigrationService> log)
    {
        _db = db; _nodes = nodes; _instances = instances; _proxy = proxy; _docker = docker; _queue = queue; _log = log;
    }

    /// <summary>How long the target gets to prove it runs: its first beat (every ~60 s) after a cold start.</summary>
    public static readonly TimeSpan VerifyTimeout = TimeSpan.FromMinutes(5);

    public static string HostName(Node? n) => n?.Name ?? "Dieser Host";

    /// <summary>Checks and queues a move. Nothing is touched yet — the worker runs it in the background.</summary>
    /// <param name="target">Null = "Dieser Host".</param>
    public async Task<(InstanceMigration? Migration, string? Error)> StartAsync(Instance inst, Node? target, bool removeSource,
        string? requestedBy, CancellationToken ct = default)
    {
        if (!HostingActionsService.CanAct(inst))
            return (null, "Die Instanz läuft weder auf dem Docker-Host dieser Cloud noch auf einem verbundenen Node.");
        // Moving retires or removes the old container — only for one this cloud built (the same rule as teardown).
        if (!inst.CloudManaged)
            return (null, "Diese Instanz wurde nicht von dieser Cloud angelegt — ihr Container wird nicht umgezogen.");
        if (await _db.InstanceMigrations.AnyAsync(m => m.InstanceId == inst.Id && m.State == "running", ct))
            return (null, "Für diese Instanz läuft schon ein Umzug.");
        if (target?.Id == inst.NodeId)
            return (null, "Die Instanz läuft bereits dort.");

        var from = inst.NodeId is { } fid ? await _db.Nodes.FindAsync(new object[] { fid }, ct) : null;
        if (inst.NodeId is not null && from is null) return (null, "Der Quell-Node ist nicht mehr eingetragen.");
        foreach (var n in new[] { from, target })
        {
            if (n is null)
            {
                if (!await _docker.IsReachableAsync(ct)) return (null, "Der Docker-Host dieser Cloud ist nicht erreichbar.");
            }
            else if (!n.IsOnline(DateTime.UtcNow)) return (null, $"Node „{n.Name}“ ist nicht verbunden.");
        }

        var m = new InstanceMigration
        {
            InstanceId = inst.Id, FromNodeId = from?.Id, ToNodeId = target?.Id,
            FromName = HostName(from), ToName = HostName(target), RemoveSource = removeSource,
            SourceContainerId = inst.ContainerId, RequestedBy = requestedBy, Step = "queued",
            TransferId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
        };
        m.Log = Line($"Umzug von „{m.FromName}“ nach „{m.ToName}“ angefordert{(removeSource ? " (alte Kopie wird danach entfernt)" : "")}.");
        _db.InstanceMigrations.Add(m);
        _instances.Log(inst, InstanceEventKind.MigrationStarted, $"Umzug nach „{m.ToName}“ gestartet — die Website ist dabei kurz offline.");
        // The site is about to go dark on purpose: no outage mail for that.
        inst.OfflineNotified = true;
        await _db.SaveChangesAsync(ct);
        _queue.Enqueue(m.Id);
        return (m, null);
    }

    private static string Line(string s) => $"{DateTime.UtcNow:HH:mm:ss} {s}\n";

    private async Task StepAsync(InstanceMigration m, string step, string line, CancellationToken ct)
    {
        m.Step = step;
        m.Log += Line(line);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Runs one queued move to its end (worker). Never throws; every outcome lands on the row.</summary>
    public async Task RunAsync(int migrationId, CancellationToken ct)
    {
        var m = await _db.InstanceMigrations.FirstOrDefaultAsync(x => x.Id == migrationId, ct);
        if (m is null || !m.Running) return;
        var inst = await _db.Instances.FirstOrDefaultAsync(i => i.Id == m.InstanceId, ct);
        var from = m.FromNodeId is { } f ? await _db.Nodes.FindAsync(new object[] { f }, ct) : null;
        var to = m.ToNodeId is { } t ? await _db.Nodes.FindAsync(new object[] { t }, ct) : null;
        if (inst is null || (m.FromNodeId is not null && from is null) || (m.ToNodeId is not null && to is null) || m.SourceContainerId is null)
        {
            await FinishAsync(m, inst, "failed", "Instanz, Node oder Container ist nicht mehr da — nichts verändert.", ct);
            return;
        }

        try
        {
            // 1) Stop the source. From here on the site is offline — and a failure must bring it back.
            await StepAsync(m, "stop", $"Quelle stoppen ({m.FromName}) …", ct);
            var r = await _nodes.RunOnAsync(from, NodeJobKinds.Power, new ContainerJob(m.SourceContainerId, "stop"), inst.Id, TimeSpan.FromMinutes(2), ct);
            if (!r.Ok) { await FinishAsync(m, inst, "failed", "Quelle konnte nicht gestoppt werden: " + r.Message + " — nichts verändert.", ct); return; }
            m.SourceStopped = true;

            // 2) Export the data volume (source → cloud).
            await StepAsync(m, "export", "Daten exportieren …", ct);
            r = await _nodes.RunOnAsync(from, NodeJobKinds.Export, new ExportJob(m.SourceContainerId, m.TransferId), inst.Id, TimeSpan.FromMinutes(60), ct);
            var info = r.Ok ? NodeJobExecutor.Deserialize<DockerHostService.ExportInfo>(r.ResultJson) : null;
            if (info is null) { await RollbackAsync(m, inst, from, to, "Export fehlgeschlagen: " + r.Message, ct); return; }
            m.Bytes = info.Bytes;
            m.Log += Line($"{r.Message} Container „{info.Name}“, Datenträger „{info.Volume}“, Image {info.Image}.");

            // 3) Import on the target (cloud → target): the same container, with the copied data, started.
            await StepAsync(m, "import", $"Auf „{m.ToName}“ anlegen und Daten übernehmen …", ct);
            var (pf, pt) = to is null ? PortRangeLocal() : (to.PortFrom, to.PortTo);
            var spec = new DockerHostService.InstanceContainerSpec(info.Name, info.Volume, info.Image, info.Env, pf, pt);
            r = await _nodes.RunOnAsync(to, NodeJobKinds.Import, new ImportJob(spec, m.TransferId), inst.Id, TimeSpan.FromMinutes(60), ct);
            var created = r.Ok ? NodeJobExecutor.Deserialize<DockerHostService.CreateContainerResult>(r.ResultJson) : null;
            if (created?.ContainerId is null) { await RollbackAsync(m, inst, from, to, "Übernahme auf dem Ziel fehlgeschlagen: " + r.Message, ct); return; }
            m.TargetStarted = true;
            m.TargetContainerId = created.ContainerId;
            m.Log += Line(r.Message);
            await _db.SaveChangesAsync(ct);
            FileNodeTransfer.Delete(m.TransferId);   // the copy has arrived; no reason to keep the whole site on the cloud's disk

            // 4) Verify: not "the container started" but "the INSTANCE beats from the target". Only that proves the
            //    copied site boots and its cloud link survived.
            await StepAsync(m, "verify", "Warten, bis sich die Website vom Ziel meldet …", ct);
            var startedAt = DateTime.UtcNow;
            if (!await WaitForTargetAsync(m, inst.Id, startedAt, ct))
            {
                await RollbackAsync(m, inst, from, to, $"Die Website hat sich innerhalb von {VerifyTimeout.TotalMinutes:0} Minuten nicht vom Ziel gemeldet.", ct);
                return;
            }
            await _db.Entry(inst).ReloadAsync(ct);
            m.Log += Line($"Website meldet sich von „{m.ToName}“ (Port {inst.LocalPort}).");

            // 5) Proxy: the route leaves with the site. A failure here does not undo the move — the site runs.
            if (inst.ProxyDomain is { } domain)
            {
                await StepAsync(m, "proxy", $"Domain „{domain}“ umziehen …", ct);
                var pr = await _proxy.MoveRouteAsync(inst, from, ct);
                m.Log += Line(pr.Ok ? pr.Message + " DNS muss auf den neuen Host zeigen." : "WARNUNG: Domain nicht umgezogen: " + pr.Message + " — im Hosting-Tab erneut veröffentlichen.");
            }

            // 6) The old copy: retired by default (a way back), removed only when asked for.
            await StepAsync(m, "source", m.RemoveSource ? "Alte Kopie entfernen …" : "Alte Kopie stilllegen …", ct);
            r = m.RemoveSource
                ? await _nodes.RunOnAsync(from, NodeJobKinds.Remove, new ContainerJob(m.SourceContainerId, RemoveVolumes: true), inst.Id, TimeSpan.FromMinutes(5), ct)
                : await _nodes.RunOnAsync(from, NodeJobKinds.Retire, new ContainerJob(m.SourceContainerId), inst.Id, TimeSpan.FromMinutes(2), ct);
            m.Log += Line(r.Ok ? r.Message : "WARNUNG: " + r.Message + " — die alte Kopie ist gestoppt, bitte von Hand aufräumen.");

            await FinishAsync(m, inst, "succeeded", $"Umzug nach „{m.ToName}“ abgeschlossen.", ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Migration {Id} crashed", m.Id);
            try { await RollbackAsync(m, inst, from, to, "Unerwarteter Fehler: " + ex.Message, CancellationToken.None); }
            catch (Exception ex2) { _log.LogError(ex2, "Rollback of migration {Id} crashed", m.Id); }
        }
        finally { FileNodeTransfer.Delete(m.TransferId); }
    }

    private (int, int) PortRangeLocal()
    {
        // Same source as HostingService.PortRange, read directly to avoid a service cycle.
        var cloud = _db.CloudSettings.AsNoTracking().ToDictionary(s => s.Key, s => s.Value);
        var from = cloud.TryGetValue(SettingKeys.HostingPortFrom, out var fs) && int.TryParse(fs, out var fi) ? fi : HostingService.DefaultPortFrom;
        var to = cloud.TryGetValue(SettingKeys.HostingPortTo, out var ts) && int.TryParse(ts, out var ti) ? ti : HostingService.DefaultPortTo;
        return from <= to ? (from, to) : (to, from);
    }

    private async Task<bool> WaitForTargetAsync(InstanceMigration m, int instanceId, DateTime startedAt, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + VerifyTimeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            var i = await _db.Instances.AsNoTracking().FirstOrDefaultAsync(x => x.Id == instanceId, ct);
            if (i is null) return false;
            var onTarget = m.ToNodeId is null ? i.Hosting == InstanceHosting.Local : i.Hosting == InstanceHosting.Node && i.NodeId == m.ToNodeId;
            if (onTarget && i.LastHeartbeatUtc > startedAt && i.ContainerId is { } cid
                && DockerHostService.IdMatches(m.TargetContainerId!, cid.Trim().ToLowerInvariant()))
                return true;
        }
        return false;
    }

    /// <summary>Back to where it was: target container (and its copied volume) gone FIRST, then the source started.
    /// If the target cannot be removed, the source stays stopped — never two containers with one identity.</summary>
    private async Task RollbackAsync(InstanceMigration m, Instance inst, Node? from, Node? to, string why, CancellationToken ct)
    {
        m.Log += Line("FEHLER: " + why);
        if (m.TargetStarted && m.TargetContainerId is not null)
        {
            await StepAsync(m, "rollback", "Ziel-Container wieder entfernen …", ct);
            var rt = await _nodes.RunOnAsync(to, NodeJobKinds.Remove, new ContainerJob(m.TargetContainerId, RemoveVolumes: true), inst.Id, TimeSpan.FromMinutes(5), ct);
            if (!rt.Ok)
            {
                await FinishAsync(m, inst, "failed", $"Ziel-Container konnte nicht entfernt werden ({rt.Message}). Die Quelle bleibt gestoppt, damit die Website nicht doppelt läuft — bitte prüfen.", ct);
                return;
            }
            m.Log += Line(rt.Message);
            m.TargetStarted = false;
        }
        if (m.SourceStopped)
        {
            await StepAsync(m, "rollback", "Quelle wieder starten …", ct);
            var rs = await _nodes.RunOnAsync(from, NodeJobKinds.Power, new ContainerJob(m.SourceContainerId!, "start"), inst.Id, TimeSpan.FromMinutes(2), ct);
            if (!rs.Ok)
            {
                await FinishAsync(m, inst, "failed", "Quelle konnte nicht wieder gestartet werden: " + rs.Message, ct);
                return;
            }
            m.Log += Line(rs.Message);
        }
        await FinishAsync(m, inst, "rolled-back", "Umzug zurückgerollt — die Website läuft wieder an der alten Stelle.", ct);
    }

    private async Task FinishAsync(InstanceMigration m, Instance? inst, string state, string message, CancellationToken ct)
    {
        m.State = state;
        m.Step = state;
        m.Log += Line(message);
        m.FinishedAt = DateTime.UtcNow;
        if (inst is not null)
        {
            _instances.Log(inst, state == "succeeded" ? InstanceEventKind.MigrationSucceeded : InstanceEventKind.MigrationFailed, message);
            // Back to normal outage monitoring.
            inst.OfflineNotified = false;
        }
        await _db.SaveChangesAsync(CancellationToken.None);
        FileNodeTransfer.Delete(m.TransferId);
    }

    /// <summary>After a restart of the cloud: a move that was running is over. Where the source was stopped and the
    /// target never started, the source is brought back; anything else is marked for a human.</summary>
    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        var open = await _db.InstanceMigrations.Where(m => m.State == "running").ToListAsync(ct);
        foreach (var m in open)
        {
            var inst = await _db.Instances.FirstOrDefaultAsync(i => i.Id == m.InstanceId, ct);
            var from = m.FromNodeId is { } f ? await _db.Nodes.FindAsync(new object[] { f }, ct) : null;
            var to = m.ToNodeId is { } t ? await _db.Nodes.FindAsync(new object[] { t }, ct) : null;
            if (inst is null) { await FinishAsync(m, null, "failed", "Cloud neu gestartet; Instanz nicht mehr da.", ct); continue; }
            if (m.Step == "queued") { _queue.Enqueue(m.Id); continue; }   // never started — just run it now
            // Past verification the site RUNS on the target and may already hold new content there — rolling back
            // would throw that away. The move stands; only the tidy-up after it is left for a human.
            if (m.Step is "proxy" or "source")
            {
                await FinishAsync(m, inst, "succeeded", "Die Cloud wurde nach dem erfolgreichen Umzug neu gestartet — Domain und alte Kopie bitte prüfen.", ct);
                continue;
            }
            try { await RollbackAsync(m, inst, from, to, "Die Cloud wurde während des Umzugs neu gestartet.", ct); }
            catch (Exception ex) { await FinishAsync(m, inst, "failed", "Cloud neu gestartet, Zurückrollen fehlgeschlagen: " + ex.Message, ct); }
        }
    }

    public Task<List<InstanceMigration>> HistoryAsync(int instanceId, int take = 10, CancellationToken ct = default) =>
        _db.InstanceMigrations.AsNoTracking().Where(m => m.InstanceId == instanceId).OrderByDescending(m => m.Id).Take(take).ToListAsync(ct);

    public static object Json(InstanceMigration m) => new
    {
        id = m.Id, state = m.State, step = m.Step, from = m.FromName, to = m.ToName, removeSource = m.RemoveSource,
        bytes = m.Bytes, requestedBy = m.RequestedBy, startedAt = m.StartedAt, finishedAt = m.FinishedAt,
        log = m.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries),
    };
}

/// <summary>The queue between "move requested" (a request) and "move running" (the worker). Singleton.</summary>
public sealed class MigrationQueue
{
    private readonly Channel<int> _ch = Channel.CreateUnbounded<int>();
    public void Enqueue(int id) => _ch.Writer.TryWrite(id);
    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) => _ch.Reader.ReadAllAsync(ct);
}

/// <summary>Runs moves one at a time — each copies a whole data volume through the cloud; doing several at once
/// would only compete for the same disk and link.</summary>
public sealed class MigrationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly MigrationQueue _queue;
    private readonly ILogger<MigrationWorker> _log;

    public MigrationWorker(IServiceScopeFactory scopes, MigrationQueue queue, ILogger<MigrationWorker> log)
    {
        _scopes = scopes; _queue = queue; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the app a moment (migrations applied, agents reconnecting) before touching interrupted moves.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MigrationService>().RecoverInterruptedAsync(stoppingToken);
        }
        catch (Exception ex) { _log.LogError(ex, "Recovering interrupted migrations failed"); }

        await foreach (var id in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MigrationService>().RunAsync(id, stoppingToken);
            }
            catch (Exception ex) { _log.LogError(ex, "Migration {Id} failed", id); }
        }
    }
}
