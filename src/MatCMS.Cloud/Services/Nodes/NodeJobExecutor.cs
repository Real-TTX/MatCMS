using System.Text.Json;
using MatCMS.Cloud.Services.Proxy;

namespace MatCMS.Cloud.Services.Nodes;

/// <summary>
/// Executes one job ON the node, with the same engine the cloud uses for its own host — so every guard
/// (<c>LooksLikeMatCms</c>, the managed label, the rollback of an update) holds on the node itself, not only in
/// the cloud that asked. No database, no ASP.NET: this runs inside the agent.
/// </summary>
public static class NodeJobExecutor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(object o) => JsonSerializer.Serialize(o, Json);
    public static T? Deserialize<T>(string? s) => string.IsNullOrEmpty(s) ? default : JsonSerializer.Deserialize<T>(s, Json);

    public static async Task<NodeJobReport> ExecuteAsync(NodeJobOffer job, DockerHostService docker, HttpClient proxyHttp, INodeTransfer transfer, CancellationToken ct)
    {
        var report = new NodeJobReport { JobId = job.Id };
        try
        {
            switch (job.Kind)
            {
                case NodeJobKinds.Details:
                {
                    var p = Deserialize<ContainerJob>(job.PayloadJson)!;
                    var d = await docker.GetContainerDetailsAsync(p.ContainerId, ct);
                    report.Ok = d is not null;
                    report.Message = d is null ? "Container auf dem Node nicht gefunden." : "";
                    report.ResultJson = d is null ? null : Serialize(d);
                    break;
                }
                case NodeJobKinds.Logs:
                {
                    var p = Deserialize<ContainerJob>(job.PayloadJson)!;
                    var (ok, text) = await docker.GetContainerLogsAsync(p.ContainerId, p.Tail, ct);
                    report.Ok = ok;
                    report.Message = ok ? "" : text;
                    report.ResultJson = ok ? Serialize(new { logs = text }) : null;
                    break;
                }
                case NodeJobKinds.Power:
                {
                    var p = Deserialize<ContainerJob>(job.PayloadJson)!;
                    var r = p.Action switch
                    {
                        "start" => await docker.StartContainerAsync(p.ContainerId, ct),
                        "stop" => await docker.StopContainerAsync(p.ContainerId, ct),
                        "restart" => await docker.RestartContainerAsync(p.ContainerId, ct),
                        _ => new DockerHostService.ContainerActionResult(false, $"Unbekannte Aktion „{p.Action}“."),
                    };
                    report.Ok = r.Ok; report.Message = r.Message;
                    break;
                }
                case NodeJobKinds.Update:
                {
                    var p = Deserialize<ContainerJob>(job.PayloadJson)!;
                    var r = await docker.UpdateContainerAsync(p.ContainerId, ct);
                    report.Ok = r.Ok; report.Message = r.Message;
                    break;
                }
                case NodeJobKinds.Create:
                {
                    var spec = Deserialize<DockerHostService.InstanceContainerSpec>(job.PayloadJson)!;
                    var r = await docker.CreateInstanceContainerAsync(spec, ct);
                    report.Ok = r.Ok;
                    report.Message = r.Ok ? $"Container {r.ContainerName} auf Port {r.Port} angelegt." : r.Error;
                    report.ResultJson = Serialize(r);
                    break;
                }
                case NodeJobKinds.Proxy:
                {
                    var op = Deserialize<ProxyOp>(job.PayloadJson)!;
                    var r = await ProxyEngine.ExecuteAsync(op, proxyHttp, docker, ct);
                    report.Ok = r.Ok; report.Message = r.Message;
                    report.ResultJson = Serialize(r);
                    break;
                }
                case NodeJobKinds.Export:
                {
                    var p = Deserialize<ExportJob>(job.PayloadJson)!;
                    var (ok, msg, info) = await docker.ExportDataAsync(p.ContainerId, (s, c) => transfer.UploadAsync(p.TransferId, s, c), ct);
                    report.Ok = ok; report.Message = msg;
                    report.ResultJson = info is null ? null : Serialize(info);
                    break;
                }
                case NodeJobKinds.Import:
                {
                    var p = Deserialize<ImportJob>(job.PayloadJson)!;
                    var r = await docker.CreateInstanceContainerAsync(p.Spec, ct, c => transfer.DownloadAsync(p.TransferId, c));
                    report.Ok = r.Ok;
                    report.Message = r.Ok ? $"Container {r.ContainerName} mit übertragenen Daten auf Port {r.Port} gestartet." : r.Error;
                    report.ResultJson = Serialize(r);
                    break;
                }
                case NodeJobKinds.Retire:
                {
                    var p = Deserialize<ContainerJob>(job.PayloadJson)!;
                    var r = await docker.RetireContainerAsync(p.ContainerId, ct);
                    report.Ok = r.Ok; report.Message = r.Message;
                    break;
                }
                case NodeJobKinds.TeardownInfo:
                {
                    var p = Deserialize<ContainerJob>(job.PayloadJson)!;
                    var t = await docker.InspectTeardownAsync(p.ContainerId, ct);
                    report.Ok = true;
                    report.ResultJson = t is null ? null : Serialize(t);
                    break;
                }
                case NodeJobKinds.Remove:
                {
                    // The node re-checks the managed label itself (RemoveInstanceContainerAsync) — the cloud's
                    // confirmation alone never deletes a container.
                    var p = Deserialize<ContainerJob>(job.PayloadJson)!;
                    var r = await docker.RemoveInstanceContainerAsync(p.ContainerId, p.RemoveVolumes, ct);
                    report.Ok = r.Ok; report.Message = r.Message;
                    report.ResultJson = Serialize(r);
                    break;
                }
                case NodeJobKinds.ImagesList:
                {
                    var list = await docker.ListMatCmsImagesAsync(ct);
                    report.Ok = list is not null;
                    report.Message = list is null ? "Docker-Daemon des Nodes nicht erreichbar." : "";
                    report.ResultJson = list is null ? null : Serialize(list);
                    break;
                }
                case NodeJobKinds.ImagesPrune:
                {
                    var r = await docker.PruneMatCmsImagesAsync(ct);
                    report.Ok = true; report.Message = $"{r.Removed} Image(s) entfernt.";
                    report.ResultJson = Serialize(r);
                    break;
                }
                case NodeJobKinds.AgentUpdate:
                {
                    // The agent's own container — the same detection the cloud uses for its self-update.
                    var self = SelfContainer.Current;
                    if (self is null) { report.Ok = false; report.Message = "Der Agent läuft nicht erkennbar in einem Container."; break; }
                    var r = await docker.SpawnContainerUpdateHelperAsync(self, ct);
                    report.Ok = r.Ok; report.Message = r.Message;
                    break;
                }
                default:
                    report.Ok = false;
                    report.Message = $"Auftrag „{job.Kind}“ kennt dieser Node nicht (Agent-Version zu alt?).";
                    break;
            }
        }
        catch (Exception ex)
        {
            report.Ok = false;
            report.Message = "Auftrag fehlgeschlagen: " + ex.Message;
        }
        return report;
    }
}
