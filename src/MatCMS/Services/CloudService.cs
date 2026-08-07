using System.Net.Http.Json;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Services;

/// <summary>
/// Live state of the cloud link. A singleton so the admin UI and the sidebar can read the last
/// result without re-querying the cloud, and so a failure is visible instead of silent.
/// </summary>
public class CloudState
{
    public DateTime? LastAttemptUtc { get; set; }
    public bool Connected { get; set; }
    public string? LastError { get; set; }

    /// <summary>"Pending" while the cloud operator has not accepted this instance yet.</summary>
    public string? Status { get; set; }

    /// <summary>Newest published MatCMS release as reported by the cloud (it polls the registry
    /// centrally, so this instance never has to).</summary>
    public string? LatestVersion { get; set; }

    public bool UpdateAvailable { get; set; }

    /// <summary>True when the cloud found our container on its own Docker daemon and could perform
    /// the update itself. Purely informational here — the update is triggered in the cloud.</summary>
    public bool CloudCanUpdate { get; set; }

    /// <summary>The name the cloud knows this instance by.</summary>
    public string? DisplayName { get; set; }

    public string? ProfileName { get; set; }

    /// <summary>Revision the cloud currently offers vs. what we have applied. Equal = in sync.</summary>
    public int ConfigRevision { get; set; }
    public int AppliedRevision { get; set; }
    public string? SyncError { get; set; }
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// Base URL this site was last actually reached at, captured from incoming requests. Reported to
    /// the cloud when no canonical URL is configured — which is the normal state of a site nobody set
    /// one on, and without it the cloud has no address to show a preview for.
    /// <para>In-memory on purpose: it costs nothing per request and refills the moment somebody opens
    /// the site, so it never needs a write to the database.</para>
    /// </summary>
    public string? ObservedBaseUrl { get; set; }

    public bool IsPending => string.Equals(Status, "Pending", StringComparison.OrdinalIgnoreCase);
    public bool OutOfSync => ConfigRevision > 0 && AppliedRevision != ConfigRevision;
}

/// <summary>
/// Reads/writes the cloud link configuration, performs the heartbeat and pulls configuration when
/// the cloud reports a new revision. The token is stored DataProtection-encrypted (same key ring as
/// the auth cookies, persisted on the appdata volume), so a leaked database or backup does not hand
/// over the cloud credential.
/// </summary>
public class CloudService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IDataProtector _protector;
    private readonly VersionService _version;
    private readonly CloudSyncService _sync;
    private readonly SiteContext _site;
    private readonly CloudState _state;
    private readonly ILogger<CloudService> _log;

    public CloudService(
        AppDbContext db, IHttpClientFactory http, IDataProtectionProvider protection,
        VersionService version, CloudSyncService sync, SiteContext site, CloudState state,
        ILogger<CloudService> log)
    {
        _db = db;
        _http = http;
        _protector = protection.CreateProtector("MatCMS.CloudToken");
        _version = version;
        _sync = sync;
        _site = site;
        _state = state;
        _log = log;
    }

    public sealed record CloudSettings(string Url, string InstanceId, string Token)
    {
        /// <summary>All three parts are required — a half-configured link is treated as "off".</summary>
        public bool Configured =>
            !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(InstanceId) && !string.IsNullOrWhiteSpace(Token);
    }

    public async Task<CloudSettings> GetSettingsAsync()
    {
        var keys = SettingKeys.Cloud;
        var map = await _db.SiteSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        string G(string k) => map.TryGetValue(k, out var v) ? (v ?? "") : "";

        return new CloudSettings(
            G(SettingKeys.CloudUrl).Trim().TrimEnd('/'),
            G(SettingKeys.CloudInstanceId).Trim(),
            Unprotect(G(SettingKeys.CloudToken)));
    }

    // --- Enrollment ---------------------------------------------------------

    /// <summary>
    /// Instance-initiated enrollment: we present a profile's join code and store the id + token we
    /// get back. This is the direction that works behind NAT — nothing has to reach this site.
    /// </summary>
    public async Task<(bool ok, string? error)> RegisterAsync(
        string? cloudUrl, string? joinCode, CancellationToken ct = default)
    {
        var url = (cloudUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) return (false, "Bitte die Cloud-URL angeben.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != "http" && parsed.Scheme != "https"))
            return (false, "Die Cloud-URL muss mit http:// oder https:// beginnen.");
        if (string.IsNullOrWhiteSpace(joinCode)) return (false, "Bitte den Join-Code angeben.");

        try
        {
            var beat = await BuildBeatAsync(ct);
            var request = new RegisterRequest
            {
                JoinCode = joinCode.Trim(),
                ProtocolVersion = CloudProtocol.Version,
                Version = beat.Version,
                SiteName = beat.SiteName,
                Url = beat.Url,
                HostName = beat.HostName,
                ContainerId = beat.ContainerId,
                ImageRef = beat.ImageRef
            };

            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Instance");

            var res = await client.PostAsJsonAsync($"{url}/api/instances/register", request, ct);
            if (!res.IsSuccessStatusCode)
                return (false, res.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Der Join-Code wurde von der Cloud abgelehnt."
                    : $"Die Cloud antwortete mit HTTP {(int)res.StatusCode}.");

            var answer = await res.Content.ReadFromJsonAsync<RegisterResponse>(ct);
            if (answer is null || string.IsNullOrWhiteSpace(answer.InstanceId) || string.IsNullOrWhiteSpace(answer.Token))
                return (false, "Die Cloud hat keine gültigen Verbindungsdaten geliefert.");

            await StoreLinkAsync(url, answer.InstanceId, answer.Token, ct);
            await SendHeartbeatAsync(ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Cloud registration failed");
            return (false, $"Die Cloud war nicht erreichbar: {ex.Message}");
        }
    }

    /// <summary>
    /// Cloud-initiated adoption, called from the <c>/api/cloud/link</c> endpoint after the supplied
    /// admin credentials have been verified. Stores the link and beats immediately so the cloud sees
    /// a live instance the moment the handshake returns.
    /// </summary>
    public async Task AcceptLinkAsync(string cloudUrl, string instanceId, string token, CancellationToken ct = default)
    {
        await StoreLinkAsync(cloudUrl.Trim().TrimEnd('/'), instanceId.Trim(), token.Trim(), ct);
        await SendHeartbeatAsync(ct);
    }

    /// <summary>Writes the link and resets the sync state — a new cloud or profile makes the old
    /// applied revision meaningless.</summary>
    private async Task StoreLinkAsync(string url, string instanceId, string token, CancellationToken ct)
    {
        await UpsertAsync(SettingKeys.CloudUrl, url);
        await UpsertAsync(SettingKeys.CloudInstanceId, instanceId);
        await UpsertAsync(SettingKeys.CloudToken, Protect(token));
        await _db.SaveChangesAsync(ct);
        await _sync.ResetAsync(ct);
    }

    /// <summary>Stores the link exactly as given (manual entry / advanced path). An empty token keeps
    /// the stored one, so re-saving the URL does not wipe the credential.</summary>
    public async Task SaveSettingsAsync(string? url, string? instanceId, string? token)
    {
        var keep = string.IsNullOrWhiteSpace(token);
        await UpsertAsync(SettingKeys.CloudUrl, (url ?? "").Trim().TrimEnd('/'));
        await UpsertAsync(SettingKeys.CloudInstanceId, (instanceId ?? "").Trim());
        if (!keep) await UpsertAsync(SettingKeys.CloudToken, Protect(token!.Trim()));
        await _db.SaveChangesAsync();
    }

    /// <summary>Tells the cloud we are leaving (best effort) and clears the local link, so the cloud
    /// marks us offline at once instead of waiting out its dead-man timeout.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync();
        if (settings.Configured)
        {
            try
            {
                var client = CreateClient(settings);
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{settings.Url}/api/instances/{settings.InstanceId}/disconnect");
                await client.SendAsync(req, ct);
            }
            catch (Exception ex)
            {
                // The cloud being unreachable must never block a local disconnect — its timeout
                // covers this case anyway.
                _log.LogInformation(ex, "Cloud disconnect notice failed (link is cleared regardless)");
            }
        }

        await UpsertAsync(SettingKeys.CloudUrl, "");
        await UpsertAsync(SettingKeys.CloudInstanceId, "");
        await UpsertAsync(SettingKeys.CloudToken, "");
        await _db.SaveChangesAsync(ct);
        await _sync.ResetAsync(ct);

        _state.Connected = false;
        _state.LastError = null;
        _state.Status = null;
        _state.UpdateAvailable = false;
        _state.CloudCanUpdate = false;
        _state.LatestVersion = null;
        _state.DisplayName = null;
        _state.ProfileName = null;
        _state.ConfigRevision = 0;
        _state.AppliedRevision = 0;
        _state.SyncError = null;
    }

    // --- Heartbeat + sync ---------------------------------------------------

    /// <summary>Builds and sends one heartbeat, folding the answer into <see cref="CloudState"/> and
    /// pulling the configuration when the cloud reports a revision we have not applied. Never throws
    /// — a broken link must never take the site down.</summary>
    public async Task SendHeartbeatAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync();
        if (!settings.Configured)
        {
            _state.Connected = false;
            return;
        }

        _state.LastAttemptUtc = DateTime.UtcNow;
        try
        {
            var beat = await BuildBeatAsync(ct);
            var client = CreateClient(settings);
            var res = await client.PostAsJsonAsync(
                $"{settings.Url}/api/instances/{settings.InstanceId}/heartbeat", beat, ct);

            if (!res.IsSuccessStatusCode)
            {
                _state.Connected = false;
                _state.LastError = res.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "Instanz-ID oder Token wird von der Cloud abgelehnt.",
                    System.Net.HttpStatusCode.Forbidden => "Diese Instanz wurde in der Cloud abgelehnt.",
                    _ => $"Cloud antwortete mit HTTP {(int)res.StatusCode}."
                };
                return;
            }

            var answer = await res.Content.ReadFromJsonAsync<HeartbeatResponse>(ct);
            _state.Connected = true;
            _state.LastError = null;
            _state.Status = answer?.Status;
            _state.LatestVersion = answer?.LatestVersion;
            _state.UpdateAvailable = answer?.UpdateAvailable ?? false;
            _state.CloudCanUpdate = answer?.CloudCanUpdate ?? false;
            _state.DisplayName = answer?.DisplayName;
            _state.ProfileName = answer?.ProfileName;
            _state.ConfigRevision = answer?.ConfigRevision ?? 0;
            _state.AppliedRevision = beat.AppliedRevision;
            _state.SyncError = beat.SyncError;

            // The cloud offers a revision we have not applied → pull and apply it now, in the same
            // cycle, so the next beat already reports the new state.
            if (_state.ConfigRevision > 0 && _state.ConfigRevision != beat.AppliedRevision)
                await PullAndApplyAsync(settings, ct);
        }
        catch (Exception ex)
        {
            _state.Connected = false;
            _state.LastError = ex.Message;
            _log.LogInformation(ex, "Cloud heartbeat failed");
        }
    }

    /// <summary>Fetches the profile configuration and hands it to the applier. Exposed so the admin
    /// UI can offer an explicit "apply now" instead of waiting for the next beat.</summary>
    public async Task<CloudSyncService.SyncResult> PullAndApplyAsync(
        CloudSettings? settings = null, CancellationToken ct = default)
    {
        settings ??= await GetSettingsAsync();
        if (!settings.Configured)
            return new(false, 0, "Keine Cloud-Verbindung konfiguriert.", [], []);

        var client = CreateClient(settings);
        var res = await client.GetAsync($"{settings.Url}/api/instances/{settings.InstanceId}/config", ct);
        if (!res.IsSuccessStatusCode)
        {
            var error = res.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "Die Instanz ist in der Cloud noch nicht freigegeben."
                : $"Konfiguration konnte nicht geladen werden (HTTP {(int)res.StatusCode}).";
            _state.SyncError = error;
            return new(false, 0, error, [], []);
        }

        var config = await res.Content.ReadFromJsonAsync<InstanceConfig>(ct);
        if (config is null) return new(false, 0, "Leere Konfiguration erhalten.", [], []);

        var result = await _sync.ApplyAsync(config,
            (key, token) => FetchPluginAsync(settings, key, token), ct);

        _state.AppliedRevision = result.Ok ? result.Revision : await _sync.AppliedRevisionAsync(ct);
        _state.SyncError = result.Error;
        _state.LastSyncUtc = DateTime.UtcNow;
        return result;
    }

    private async Task<byte[]?> FetchPluginAsync(CloudSettings settings, string key, CancellationToken ct)
    {
        var client = CreateClient(settings);
        var res = await client.GetAsync($"{settings.Url}/api/instances/{settings.InstanceId}/plugin/{Uri.EscapeDataString(key)}", ct);
        return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync(ct) : null;
    }

    private async Task<HeartbeatRequest> BuildBeatAsync(CancellationToken ct)
    {
        // The public URL has no fallback outside a request (the heartbeat runs on a timer), so an
        // unconfigured canonical URL simply travels as null and the cloud shows no "open site" link.
        var canonical = await _db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.CanonicalUrl)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        return new HeartbeatRequest
        {
            ProtocolVersion = CloudProtocol.Version,
            Version = _version.Current,
            // Via SiteContext, not the raw setting: an unset site name has a sensible fallback there,
            // whereas the raw value is empty and the cloud would list the site as "Neue Instanz".
            SiteName = _site.SiteName,
            // Canonical URL wins; otherwise report where the site was last actually reached, so the
            // cloud has something to link to and preview even on a site nobody configured a URL on.
            Url = string.IsNullOrWhiteSpace(canonical) ? _state.ObservedBaseUrl : canonical,
            HostName = Environment.MachineName,
            ContainerId = ContainerIdentity.Current,
            // We cannot read our own image from inside the container; the operator can surface it
            // via compose (MATCMS_IMAGE). Purely informational for the cloud's instance list.
            ImageRef = Environment.GetEnvironmentVariable("MATCMS_IMAGE"),
            PageCount = await _db.Pages.CountAsync(ct),
            PluginCount = await _db.Plugins.CountAsync(ct),
            UserCount = await _db.Users.CountAsync(ct),
            AppliedRevision = await _sync.AppliedRevisionAsync(ct),
            SyncError = await _sync.LastErrorAsync(ct),
            SyncReport = await _sync.LastReportAsync(ct)
        };
    }

    private HttpClient CreateClient(CloudSettings settings)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Instance");
        client.DefaultRequestHeaders.Add(CloudProtocol.TokenHeader, settings.Token);
        return client;
    }

    private async Task UpsertAsync(string key, string value)
    {
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else row.Value = value;
    }

    private string Protect(string raw) =>
        string.IsNullOrEmpty(raw) ? "" : _protector.Protect(raw);

    /// <summary>Decrypts a stored token. A value that is not valid ciphertext (hand-edited row, or
    /// a key ring that was thrown away) yields "" rather than an exception, which shows up in the UI
    /// as "not connected" — recoverable by connecting again.</summary>
    private string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try { return _protector.Unprotect(stored); }
        catch { return ""; }
    }
}

/// <summary>
/// Sends the heartbeat once a minute. Re-reads the settings every cycle, so connecting or
/// disconnecting in the admin UI takes effect live — no restart.
/// </summary>
public class CloudConnectionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CloudConnectionService> _log;

    public CloudConnectionService(IServiceScopeFactory scopes, ILogger<CloudConnectionService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting (seeding, plugin run) before the first beat.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CloudService>()
                    .SendHeartbeatAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "Cloud heartbeat cycle failed"); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
