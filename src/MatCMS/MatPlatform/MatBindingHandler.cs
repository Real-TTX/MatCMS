// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MatPlatform;

/// <summary>One side of a binding: the id of the app (<c>matdrive</c>), its name, its public address and its address on the shared network.</summary>
public sealed record MatBindingParty(string Id, string Name, string? Url = null, string? InternalUrl = null)
{
    /// <summary>Every public address of the app, <see cref="Url"/> first (a route under the domain and this host with its port): what <c>identity.oidc</c> registers.</summary>
    public IReadOnlyList<string>? Urls { get; init; }

    /// <summary><see cref="Urls"/>, else <see cref="Url"/> alone; without a trailing slash, each once.</summary>
    public IReadOnlyList<string> Addresses()
        => (Urls is { Count: > 0 } urls ? urls : Url is { Length: > 0 } url ? [url] : [])
            .Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim().TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>What a broker (or an administrator by hand) asks of a provider: bind this consumer for this capability.</summary>
/// <param name="Capability">The capability.</param>
/// <param name="Version">The version the consumer speaks.</param>
/// <param name="Consumer">Who is bound.</param>
/// <param name="Provider">This app as the broker reaches and publishes it (its addresses), so the provider can name them in the values.</param>
/// <param name="Params">What the consumer passes (from its <c>consumes</c>).</param>
/// <param name="ManagedBy"><c>mattiverse</c> (a broker) or <c>manual</c>.</param>
/// <param name="Renew">Make a new secret even when there is one.</param>
public sealed record MatBindRequest(
    string Capability,
    int Version,
    MatBindingParty Consumer,
    MatBindingParty Provider,
    IReadOnlyDictionary<string, string> Params,
    string ManagedBy,
    bool Renew)
{
    /// <summary>A parameter of the consumer, or <paramref name="fallback"/> when it did not pass one.</summary>
    public string Param(string name, string fallback) => Params.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
}

/// <summary>
/// The values of a binding by their keys (<see cref="MatCapabilitySpec.Values"/>). A secret is in it only when it was made now; a secret that exists already and is not
/// handed out again is <c>null</c> (sent as <c>{"isSet": true}</c>).
/// </summary>
public sealed record MatBindResult(IReadOnlyDictionary<string, string?> Values);

/// <summary>
/// A provider refuses a binding (2.2.0): a parameter that is missing or wrong (`400`), or something that is in the way and must not be taken over (`409`, a route someone
/// made by hand for the same host). Thrown from <see cref="IMatBindingHandler.BindAsync"/>; MatPlatform answers a broker with `{"errors": {"<key>": "<message>"}}` and shows the
/// message in the Connections area. The message is for a person.
/// </summary>
public sealed class MatBindingRefusedException(string key, string message, bool conflict = false) : Exception(message)
{
    /// <summary>The key of the error answer (`host`, `params`, ...).</summary>
    public string Key { get; } = key;

    /// <summary>True: `409` (something is in the way); false: `400` (the request lacks something).</summary>
    public bool Conflict { get; } = conflict;
}

/// <summary>A binding as a provider lists it.</summary>
public sealed record MatBindingInfo(string Capability, string Consumer, string Name, string ManagedBy, DateTimeOffset? Since = null, DateTimeOffset? LastUsed = null);

/// <summary>
/// What a binding is for a provider whose binding is more than a key: an OpenID Connect client (MatIdentity), a relay user (MatSMTP), a host with its open files
/// (MatOffice). Register it as <c>services.AddScoped&lt;IMatBindingHandler, MyHandler&gt;()</c>. A capability the app provides without a handler of its own gets the
/// generic one (<see cref="MatKeyBindingHandler"/>): a key per consumer, kept by MatPlatform.
/// </summary>
public interface IMatBindingHandler
{
    /// <summary>The capability it handles (one of the app's <c>provides</c>).</summary>
    string Capability { get; }

    Task<IReadOnlyList<MatBindingInfo>> ListAsync(CancellationToken ct);

    /// <summary>Makes or updates the binding of the consumer (idempotent: the same request twice is one binding) and answers its values.</summary>
    Task<MatBindResult> BindAsync(MatBindRequest request, CancellationToken ct);

    /// <summary>Removes the binding of <paramref name="consumer"/>: only one made by <paramref name="managedBy"/>, any when it is null. False when there was none.</summary>
    Task<bool> UnbindAsync(string consumer, string? managedBy, CancellationToken ct);
}

/// <summary>
/// The binding of a key capability, with no code of the app (docs/PLATFORM.md "Bindings"): MatPlatform makes a key for the consumer, keeps its hash in the data folder
/// (<see cref="MatBindingStore"/>) and answers the addresses of the provider and the key. The consumer calls <c>/api/provides/{capability}/...</c> with
/// <c>Authorization: Bearer &lt;key&gt;</c>; <see cref="MatBindingAccess.Require"/> checks it.
/// </summary>
public sealed class MatKeyBindingHandler(string capability, MatBindingStore store) : IMatBindingHandler
{
    public string Capability { get; } = capability;

    public async Task<IReadOnlyList<MatBindingInfo>> ListAsync(CancellationToken ct)
        => [.. (await store.ProvidedAsync(Capability, ct)).Select(b => new MatBindingInfo(b.Capability, b.Consumer, b.Name, b.ManagedBy, b.Since, b.LastUsed))];

    public async Task<MatBindResult> BindAsync(MatBindRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (_, key) = await store.UpsertProvidedAsync(Capability, request.Consumer, request.ManagedBy, request.Renew, ct);
        return new MatBindResult(new Dictionary<string, string?>
        {
            [MatCapabilities.Url] = request.Provider.Url,
            [MatCapabilities.InternalUrl] = request.Provider.InternalUrl,
            [MatCapabilities.Key] = key,
        });
    }

    public Task<bool> UnbindAsync(string consumer, string? managedBy, CancellationToken ct)
        => store.RemoveProvidedAsync(Capability, consumer, managedBy, ct);
}

/// <summary>Who calls <c>/api/provides/{capability}/...</c>: the consumer of a binding made by <see cref="MatKeyBindingHandler"/>.</summary>
public static class MatBindingAccess
{
    /// <summary>Where the id of the calling consumer is kept in <see cref="HttpContext.Items"/>.</summary>
    public const string ConsumerKey = "matplatform:consumer";

    /// <summary>
    /// An endpoint filter for the endpoints of a key capability: <c>401</c> unless the request carries <c>Authorization: Bearer &lt;key&gt;</c> of a binding of
    /// <paramref name="capability"/>. The consumer's id lands in <see cref="HttpContext.Items"/> (<see cref="ConsumerOf"/>).
    /// <c>app.MapGroup("/api/provides/files.store").AddEndpointFilter(MatBindingAccess.Require("files.store"))</c>.
    /// </summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        return async (context, next) =>
        {
            var http = context.HttpContext;
            var header = http.Request.Headers.Authorization.ToString();
            var key = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
            var binding = await http.RequestServices.GetRequiredService<MatBindingStore>().AuthenticateAsync(capability, key, http.RequestAborted);
            if (binding is null)
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Json(
                    new { errors = new Dictionary<string, string> { [string.Empty] = "The key of a binding is required." } },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            http.Items[ConsumerKey] = binding.Consumer;
            return await next(context);
        };
    }

    /// <summary>The consumer that <see cref="Require"/> let in, or null.</summary>
    public static string? ConsumerOf(HttpContext http) => http?.Items[ConsumerKey] as string;
}
