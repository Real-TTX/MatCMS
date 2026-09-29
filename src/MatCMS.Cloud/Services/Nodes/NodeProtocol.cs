namespace MatCMS.Cloud.Services.Nodes;

/// <summary>
/// The wire contract between the cloud and a node-agent (Hosting increment 4). Deliberately NOT in
/// <c>MatCMS.Shared</c>: both ends are this project (the agent is the cloud image in <c>--node-agent</c> mode),
/// and MatCMS instances never see it — so it moves without touching <c>CloudProtocol.Version</c>.
/// <para>The agent always connects OUT: <c>POST /api/nodes/{publicId}/heartbeat</c>, token in
/// <see cref="TokenHeader"/>. The cloud answers with jobs; with nothing to hand out it holds the request up to
/// <see cref="LongPoll"/> and answers the moment a job is enqueued.</para>
/// </summary>
public static class NodeProtocol
{
    public const int Version = 1;
    public const string TokenHeader = "X-MatCMS-Node-Token";
    public static readonly TimeSpan LongPoll = TimeSpan.FromSeconds(25);
}

public sealed class NodeHeartbeatRequest
{
    public int ProtocolVersion { get; set; }
    public string? AgentVersion { get; set; }
    public string? HostName { get; set; }
    public string? DockerVersion { get; set; }
    public string? DockerError { get; set; }

    /// <summary>The MatCMS containers on the host; null = could not be listed (keep the last inventory).</summary>
    public List<NodeContainer>? Containers { get; set; }

    public List<NodeJobReport> Reports { get; set; } = new();

    /// <summary>True = nothing to report, the agent is idle: the cloud may hold the request (long poll).</summary>
    public bool Wait { get; set; }
}

public sealed class NodeContainer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string State { get; set; } = "";
    public int? PublishedPort { get; set; }
    public bool CloudManaged { get; set; }
}

public sealed class NodeJobReport
{
    public long JobId { get; set; }
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? ResultJson { get; set; }
}

public sealed class NodeHeartbeatResponse
{
    public int ProtocolVersion { get; set; } = NodeProtocol.Version;
    public List<NodeJobOffer> Jobs { get; set; } = new();
}

public sealed class NodeJobOffer
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}

/// <summary>The job kinds an agent executes. Anything else is refused on the node.</summary>
public static class NodeJobKinds
{
    public const string Details = "container.details";
    public const string Logs = "container.logs";
    public const string Power = "container.power";
    public const string Update = "container.update";
    public const string Create = "instance.create";
    public const string Proxy = "proxy";

    // Increment 5: moving and removing.
    public const string Export = "data.export";
    public const string Import = "instance.import";
    public const string Retire = "container.retire";
    public const string TeardownInfo = "container.teardownInfo";
    public const string Remove = "container.remove";
}

/// <summary>Payload of the container.* jobs.</summary>
/// <param name="Action">container.power: start | stop | restart.</param>
/// <param name="RemoveVolumes">container.remove: also the named volumes (read off the container, never derived).</param>
public sealed record ContainerJob(string ContainerId, string? Action = null, int Tail = 200, bool RemoveVolumes = false);

/// <summary>data.export: tar the stopped container's data and upload it under <paramref name="TransferId"/>.</summary>
public sealed record ExportJob(string ContainerId, string TransferId);

/// <summary>instance.import: create <paramref name="Spec"/>, seed its volume from <paramref name="TransferId"/>, start it.</summary>
public sealed record ImportJob(DockerHostService.InstanceContainerSpec Spec, string TransferId);

/// <summary>
/// Where the data of a move travels. The agent streams it over HTTP to/from the cloud (still outbound only:
/// the agent uploads and downloads, the cloud never connects to a host); for "Dieser Host" the cloud reads and
/// writes the same file directly.
/// </summary>
public interface INodeTransfer
{
    Task<long> UploadAsync(string transferId, Stream data, CancellationToken ct);
    Task<Stream> DownloadAsync(string transferId, CancellationToken ct);
}

/// <summary>The cloud side: one file per transfer under appdata/transfers, deleted when the move ends.</summary>
public sealed class FileNodeTransfer : INodeTransfer
{
    public static string Dir => Path.Combine(Directory.GetCurrentDirectory(), "appdata", "transfers");

    /// <summary>Only the random ids the cloud mints — never a path.</summary>
    public static string PathFor(string transferId)
    {
        if (transferId.Length is < 16 or > 80 || !transferId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Ungültige Transfer-ID.");
        return Path.Combine(Dir, transferId + ".tar");
    }

    public async Task<long> UploadAsync(string transferId, Stream data, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var path = PathFor(transferId);
        await using var file = File.Create(path);
        await data.CopyToAsync(file, 1 << 20, ct);
        return file.Length;
    }

    public Task<Stream> DownloadAsync(string transferId, CancellationToken ct) =>
        Task.FromResult<Stream>(File.OpenRead(PathFor(transferId)));

    public static void Delete(string? transferId)
    {
        if (string.IsNullOrEmpty(transferId)) return;
        try { File.Delete(PathFor(transferId)); } catch { }
    }
}

/// <summary>The agent side: the same transfer, streamed over HTTP with the node token.</summary>
public sealed class HttpNodeTransfer : INodeTransfer
{
    private readonly HttpClient _http;
    private readonly string _base;

    public HttpNodeTransfer(HttpClient http, string cloudUrl, string nodeId)
    {
        _http = http;
        _base = $"{cloudUrl}/api/nodes/{Uri.EscapeDataString(nodeId)}/transfers/";
    }

    public async Task<long> UploadAsync(string transferId, Stream data, CancellationToken ct)
    {
        var counting = new CountingStream(data);
        using var content = new StreamContent(counting, 1 << 20);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        using var resp = await _http.PutAsync(_base + Uri.EscapeDataString(transferId), content, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Upload zur Cloud abgelehnt ({(int)resp.StatusCode}).");
        return counting.Count;
    }

    public async Task<Stream> DownloadAsync(string transferId, CancellationToken ct)
    {
        var resp = await _http.GetAsync(_base + Uri.EscapeDataString(transferId), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Download von der Cloud abgelehnt ({(int)resp.StatusCode}).");
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); Count += n; return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var n = await inner.ReadAsync(buffer, ct); Count += n; return n; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
