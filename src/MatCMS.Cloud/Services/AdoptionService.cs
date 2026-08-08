using MatCMS.Shared;
using System.Net.Http.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Cloud-initiated adoption: the operator supplies an existing instance's URL plus one of ITS admin
/// accounts, and the cloud pushes the link over in one call. This is the counterpart to join-code
/// enrollment — it suits instances that already exist, while the join code suits rollouts and any
/// site the cloud cannot reach.
/// <para>The credentials are used for exactly this one handshake and are never stored. The instance
/// verifies them against its own user table (admin role required) before accepting the link, so the
/// endpoint cannot be used to hijack a site by anyone who does not already have admin on it.</para>
/// </summary>
public class AdoptionService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly InstanceService _instances;
    private readonly CloudContext _cloud;
    private readonly ILogger<AdoptionService> _log;

    public AdoptionService(
        AppDbContext db, IHttpClientFactory http, InstanceService instances,
        CloudContext cloud, ILogger<AdoptionService> log)
    {
        _db = db;
        _http = http;
        _instances = instances;
        _cloud = cloud;
        _log = log;
    }

    /// <summary>What it answers with, so the cloud can label the instance straight away.</summary>
    private sealed record LinkResponse(string? SiteName, string? Version, string? ContainerId, string? Url);

    public sealed record AdoptResult(Instance? Instance, string? Error);

    /// <summary>
    /// Creates the instance record, hands the link to the site, and rolls the record back if the
    /// handover fails — an instance that never accepted the link must not linger in the list looking
    /// like it is merely offline.
    /// </summary>
    public async Task<AdoptResult> AdoptAsync(
        string instanceUrl, string username, string password, int? profileId,
        HttpRequest? currentRequest, CancellationToken ct = default)
    {
        var url = (instanceUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) return new(null, "Bitte die URL der Instanz angeben.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != "http" && parsed.Scheme != "https"))
            return new(null, "Die URL muss mit http:// oder https:// beginnen.");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new(null, "Bitte Benutzer und Passwort eines Administrators der Instanz angeben.");

        var cloudUrl = _cloud.CanonicalBaseUrl(currentRequest);
        if (string.IsNullOrWhiteSpace(cloudUrl))
            return new(null, "Die öffentliche URL dieser Cloud ist nicht gesetzt (Einstellungen → Allgemein).");

        var (instance, token) = await _instances.CreateForAdoptionAsync(parsed.Host, profileId);

        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Cloud");

            var res = await client.PostAsJsonAsync($"{url}/api/cloud/link",
                // The shared contract type, not a local copy: a field added to LinkRequest must
                // actually be sent, and a private record with the same shape would silently not.
                new LinkRequest
                {
                    Username = username.Trim(), Password = password, CloudUrl = cloudUrl,
                    InstanceId = instance.PublicId, Token = token
                }, ct);

            if (!res.IsSuccessStatusCode)
            {
                await RollbackAsync(instance, ct);
                return new(null, res.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "Benutzer oder Passwort wurde von der Instanz abgelehnt.",
                    System.Net.HttpStatusCode.NotFound =>
                        "Die Instanz kennt den Endpunkt nicht — sie läuft vermutlich auf einer älteren MatCMS-Version.",
                    _ => $"Die Instanz antwortete mit HTTP {(int)res.StatusCode}."
                });
            }

            var info = await res.Content.ReadFromJsonAsync<LinkResponse>(ct);
            if (!string.IsNullOrWhiteSpace(info?.SiteName)) instance.Name = info!.SiteName!.Trim();
            instance.Url = string.IsNullOrWhiteSpace(info?.Url) ? url : info!.Url!.Trim();
            instance.Version = info?.Version;
            instance.ContainerId = info?.ContainerId;
            await _instances.ClassifyAsync(instance, ct);
            _instances.Log(instance, InstanceEventKind.Connected, "Instanz wurde von der Cloud aus verbunden.");
            await _db.SaveChangesAsync(ct);

            return new(instance, null);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Adoption of {Url} failed", url);
            await RollbackAsync(instance, ct);
            return new(null, $"Die Instanz war nicht erreichbar: {ex.Message}");
        }
    }

    private async Task RollbackAsync(Instance instance, CancellationToken ct)
    {
        _db.Instances.Remove(instance);
        await _db.SaveChangesAsync(ct);
    }
}
