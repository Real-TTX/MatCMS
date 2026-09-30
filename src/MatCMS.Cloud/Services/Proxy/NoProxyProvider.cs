namespace MatCMS.Cloud.Services.Proxy;

/// <summary>
/// No reverse proxy — today's default mode. The instance is reached on its published host port; a domain
/// published here is only RECORDED (and pinned as the instance's address), because routing it is somebody
/// else's job (an external proxy, a DNS entry the operator runs). That is still worth doing: without it the
/// domain an operator typed at provisioning was thrown away.
/// </summary>
public sealed class NoProxyProvider : IProxyProvider
{
    public string Kind => ProxyKinds.None;
    public bool ManagesRoutes => false;

    public Task<ProxyResult> TestAsync(CancellationToken ct = default) => Task.FromResult(new ProxyResult(true));

    public Task<ProxyResult> UpsertAsync(string? existingId, string routeKey, string name, string host, string upstream, bool rewriteHost = false, CancellationToken ct = default)
        => Task.FromResult(new ProxyResult(true));

    public Task<ProxyResult> DeleteAsync(string routeId, CancellationToken ct = default) => Task.FromResult(new ProxyResult(true));

    public Task<bool?> ExistsAsync(string routeId, CancellationToken ct = default) => Task.FromResult<bool?>(null);
}
