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
    // Resolved on demand, not injected: CloudBackupService depends on THIS class, and taking it as
    // a constructor dependency would be a cycle the container refuses to build.
    private readonly IServiceProvider _services;
    private readonly IDataProtector _protector;
    private readonly VersionService _version;
    private readonly CloudSyncService _sync;
    private readonly SiteContext _site;
    private readonly CloudState _state;
    private readonly ILogger<CloudService> _log;

    public CloudService(
        AppDbContext db, IHttpClientFactory http, IDataProtectionProvider protection, IServiceProvider services,
        VersionService version, CloudSyncService sync, SiteContext site, CloudState state,
        ILogger<CloudService> log)
    {
        _db = db;
        _http = http;
        _services = services;
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
        var previous = await GetSettingsAsync();
        var keep = string.IsNullOrWhiteSpace(token);
        var newUrl = (url ?? "").Trim().TrimEnd('/');
        var newId = (instanceId ?? "").Trim();

        await UpsertAsync(SettingKeys.CloudUrl, newUrl);
        await UpsertAsync(SettingKeys.CloudInstanceId, newId);
        if (!keep) await UpsertAsync(SettingKeys.CloudToken, Protect(token!.Trim()));
        await _db.SaveChangesAsync();

        // Pointing at a different cloud or a different instance record makes the applied revision and
        // the "seeded once" marks meaningless: two clouds' revision numbers and profile ids are
        // unrelated counters. Without this, a new cloud whose profile happens to sit on the same
        // revision would never be pulled at all, and the UI would report "synchron".
        if (!string.Equals(previous.Url, newUrl, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.InstanceId, newId, StringComparison.Ordinal))
        {
            await _sync.ResetAsync();
        }
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
            // Gespeichert und nicht nur im Zustand gehalten: die CSP wird bei jeder Anfrage gesetzt,
            // auch direkt nach einem Neustart, bevor der erste Herzschlag durch ist.
            // Nur schreiben, wenn sich etwas ändert — und dann auch wirklich speichern. Der Block
            // ringsum füllt sonst nur den Arbeitsspeicher, ein Upsert allein wäre folgenlos.
            var seenPublic = answer?.CloudPublicUrl ?? "";
            var storedPublic = (await _db.SiteSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Key == SettingKeys.CloudPublicUrl))?.Value ?? "";
            if (seenPublic != storedPublic)
            {
                await UpsertAsync(SettingKeys.CloudPublicUrl, seenPublic);
                await _db.SaveChangesAsync();
            }
            _state.ConfigRevision = answer?.ConfigRevision ?? 0;
            _state.AppliedRevision = beat.AppliedRevision;
            _state.SyncError = beat.SyncError;

            // The cloud offers a revision we have not applied → pull and apply it now, in the same
            // cycle, so the next beat already reports the new state.
            if (_state.ConfigRevision > 0 && _state.ConfigRevision != beat.AppliedRevision)
                await PullAndApplyAsync(settings, ct);

            // A backup the cloud asked for. BEFORE the restore below, and that order is the whole
            // point: if the cloud asked for both in the same beat, a backup taken afterwards would
            // preserve the state the restore just imposed — the opposite of what a backup taken
            // before a restore is for.
            if (answer?.Backup is { RequestId: > 0 } wanted)
            {
                var backups = _services.GetService<CloudBackupService>();
                if (backups is not null)
                {
                    _log.LogInformation("Cloud asked for a backup ({Reason}).", wanted.Reason ?? "ohne Angabe");
                    var (ok, error, file) = await backups.TakeAndUploadAsync(wanted, ct);
                    await backups.ReportBackupAsync(wanted.RequestId, ok, error, file, ct);
                }
            }

            // A restore somebody asked for in the cloud. Done here rather than on a schedule of its
            // own because the heartbeat is already the channel through which this site learns what
            // is wanted of it — and because it means a restore begins within a minute of the click.
            if (answer?.Restore is { BackupId: > 0 } restore)
            {
                var backups = _services.GetService<CloudBackupService>();
                if (backups is not null)
                {
                    _log.LogWarning("Cloud asked for backup {File} to be restored — this overwrites the site.", restore.FileName);
                    var (ok, error) = await backups.RestoreAsync(restore, ct);
                    await backups.ReportRestoreAsync(restore.BackupId, ok, error, ct);
                }
            }
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

        InstanceConfig? config;
        try
        {
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

            config = await res.Content.ReadFromJsonAsync<InstanceConfig>(ct);
        }
        catch (Exception ex)
        {
            // An unreachable cloud is a normal condition, not a 500: the admin clicked "apply now"
            // and must get a flash message like every other handler on that page.
            _log.LogInformation(ex, "Fetching the cloud configuration failed");
            var error = $"Die Cloud war nicht erreichbar: {ex.Message}";
            _state.SyncError = error;
            return new(false, 0, error, [], []);
        }

        if (config is null) return new(false, 0, "Leere Konfiguration erhalten.", [], []);

        var result = await _sync.ApplyAsync(config,
            (key, token) => FetchPluginAsync(settings, key, token), ct);

        _state.AppliedRevision = result.Ok ? result.Revision : await _sync.AppliedRevisionAsync(ct);
        _state.SyncError = result.Error;
        _state.LastSyncUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>
    /// Applies only the items an operator ticked in the preview. <paramref name="selection"/> holds
    /// <c>kind:id</c> keys exactly as the preview report produced them.
    /// <para>The configuration is narrowed down FIRST and then run through the normal applier, so the
    /// decision for each item is still made by the one piece of code that makes it everywhere else.
    /// A payload with nothing selected becomes null — which the applier reads as "don't touch this",
    /// the same as a profile that does not sync it.</para>
    /// </summary>
    public async Task<CloudSyncService.SyncResult> ApplySelectionAsync(
        IReadOnlyCollection<string> selection, CancellationToken ct = default)
    {
        if (selection.Count == 0)
            return new(false, 0, "Es war nichts ausgewählt.", [], []);

        var settings = await GetSettingsAsync();
        if (!settings.Configured)
            return new(false, 0, "Keine Cloud-Verbindung konfiguriert.", [], []);

        var (config, error) = await FetchConfigAsync(settings, ct);
        if (config is null) return new(false, 0, error, [], []);

        var wanted = selection.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Ids must be normalised exactly as the applier reports them, or a value that only differs in
        // whitespace is previewed as "component:hero", posted back as "component:hero", and then
        // looked up as "component: hero " — silently dropping the item the operator ticked.
        bool Picked(string kind, string? id) => wanted.Contains($"{kind}:{(id ?? "").Trim()}");
        bool PickedComponent(string? type) =>
            wanted.Contains($"component:{(type ?? "").Trim().ToLowerInvariant()}");

        config.Settings = config.Settings?
            .Where(kv => Picked("setting", kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        config.Users = config.Users?.Where(u => Picked("user", u.Username)).ToList();
        config.Components = config.Components?.Where(c => PickedComponent(c.Type)).ToList();
        config.Templates = config.Templates?.Where(t => Picked("template", t.Name)).ToList();
        config.Plugins = config.Plugins?.Where(p => Picked("plugin", p.Key)).ToList();

        // Switching the live design is its own row with its own kind ("activate"), so it is ticked —
        // or not — independently of rolling that template out.
        var activate = !string.IsNullOrWhiteSpace(config.ActivateTemplate)
                       && Picked("activate", config.ActivateTemplate);
        if (!activate) config.ActivateTemplate = null;

        // A payload nobody picked from is "not synced" rather than "synced as empty" — EXCEPT for
        // templates when an activation was ticked: the applier performs the switch inside
        // ApplyTemplatesAsync, which it skips entirely for a null section. An empty list makes it run
        // with nothing to roll out and still activate.
        if (config.Settings?.Count == 0) config.Settings = null;
        if (config.Users?.Count == 0) config.Users = null;
        if (config.Components?.Count == 0) config.Components = null;
        if (config.Templates?.Count == 0 && !activate) config.Templates = null;
        if (activate) config.Templates ??= [];
        if (config.Plugins?.Count == 0) config.Plugins = null;

        var result = await _sync.ApplySelectionAsync(config,
            (key, token) => FetchPluginAsync(settings, key, token), ct);

        _state.SyncError = result.Error;
        _state.LastSyncUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>Fetches the profile configuration. Never throws: an unreachable cloud is a normal
    /// condition and every caller here turns it into a message, not a 500.</summary>
    private async Task<(InstanceConfig? config, string? error)> FetchConfigAsync(
        CloudSettings settings, CancellationToken ct)
    {
        try
        {
            var client = CreateClient(settings);
            var res = await client.GetAsync($"{settings.Url}/api/instances/{settings.InstanceId}/config", ct);
            if (!res.IsSuccessStatusCode)
                return (null, res.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "Die Instanz ist in der Cloud noch nicht freigegeben."
                    : $"Konfiguration konnte nicht geladen werden (HTTP {(int)res.StatusCode}).");

            var config = await res.Content.ReadFromJsonAsync<InstanceConfig>(ct);
            return config is null ? (null, "Leere Konfiguration erhalten.") : (config, null);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Fetching the cloud configuration failed");
            return (null, $"Die Cloud war nicht erreichbar: {ex.Message}");
        }
    }

    /// <summary>Fetches the configuration and reports what applying it WOULD do — nothing is written
    /// and no state is touched, so this stays safe to click on a live site.</summary>
    public async Task<CloudSyncService.SyncResult> PreviewAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync();
        if (!settings.Configured)
            return new(false, 0, "Keine Cloud-Verbindung konfiguriert.", [], []);

        var (config, error) = await FetchConfigAsync(settings, ct);
        if (config is null) return new(false, 0, error, [], []);

        return await _sync.PreviewAsync(config, ct);
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
            SyncReport = await _sync.LastReportAsync(ct),
            SyncRunAt = await _sync.LastRunAtAsync(ct)
        };
    }

    /// <summary>
    /// Hands one message to the cloud for delivery. Used when the profile said this site does not
    /// send mail itself.
    /// <para>"Queued" is the answer to hope for and it does NOT mean delivered: the cloud spools
    /// the message and a worker sends it. That is the point — a visitor's form submission must not
    /// wait on somebody else's SMTP server, and a delivery that fails is then retried rather than
    /// lost. There is no sender field: the cloud sends with its own address and only takes the
    /// reply-to from here.</para>
    /// </summary>
    public async Task<(bool ok, string? error)> SendMailAsync(
        IEnumerable<string> to, string subject, string body, string? replyTo, bool isHtml = false,
        CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync();
        if (!settings.Configured) return (false, "Diese Website ist mit keiner Cloud verbunden.");

        try
        {
            var client = CreateClient(settings);
            var payload = new MailRequest
            {
                To = to.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
                Subject = subject,
                Body = body,
                ReplyTo = replyTo,
                IsHtml = isHtml,
            };
            using var res = await client.PostAsJsonAsync(
                $"{settings.Url}/api/instances/{settings.InstanceId}/mail", payload, ct);

            if (!res.IsSuccessStatusCode)
                return (false, $"Die Cloud hat die Nachricht abgelehnt (HTTP {(int)res.StatusCode}).");

            var answer = await res.Content.ReadFromJsonAsync<MailResponse>(cancellationToken: ct);
            if (answer is null) return (false, "Die Cloud hat nicht geantwortet.");
            return (answer.Queued, answer.Queued ? null : answer.Error ?? "Unbekannter Grund.");
        }
        catch (Exception ex)
        {
            // Never thrown at the caller: a mail problem must not break what the visitor was doing.
            _log.LogWarning(ex, "Handing mail to the cloud failed");
            return (false, ex.Message);
        }
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
