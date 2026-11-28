// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MatPlatform;

/// <summary>A capability the app consumes, as the Connections area shows it.</summary>
public sealed record MatUseView(string Capability, int Version, string Label, bool Optional, MatBindingSource Source, bool ByHand, IReadOnlyList<MatValueView> Values);

/// <summary>A value of a binding as it is shown: a secret only as <see cref="MatSecret"/>.</summary>
public sealed record MatValueView(string Key, string Variable, string Label, string Type, bool Secret, bool Required, object? Value);

/// <summary>A capability the app provides, with the consumers bound to it.</summary>
public sealed record MatOfferView(string Capability, int Version, string Label, bool ByHand, IReadOnlyList<MatBindingInfo> Bindings);

/// <summary>The answer of <c>GET /api/connections</c>: what the Connections area shows (docs/PLATFORM.md "The Connections area").</summary>
public sealed record MatConnectionsView(IReadOnlyList<MatUseView> Uses, IReadOnlyList<MatOfferView> Offers, MatContributions? Contributes);

/// <summary>
/// The bindings of this app, both sides (docs/PLATFORM.md "Bindings"): what it consumes (<see cref="MatBindings"/>), what it provides (an <see cref="IMatBindingHandler"/>
/// of the app or the generic <see cref="MatKeyBindingHandler"/>) and what it contributes. The broker endpoints, <c>GET /api/connections</c> and the Connections area
/// (<see cref="MatConnectionsTagHelper"/>) all go through it.
/// </summary>
public sealed partial class MatConnections
{
    /// <summary>Who made a binding in the Connections area.</summary>
    public const string ManagedByHand = "manual";

    private readonly MatPlatformOptions _options;
    private readonly MatPlatformSettings _settings;
    private readonly MatBindings _bindings;
    private readonly MatBindingStore _store;
    private readonly IEnumerable<IMatBindingHandler> _handlers;

    public MatConnections(MatPlatformOptions options, MatPlatformSettings settings, MatBindings bindings, MatBindingStore store, IEnumerable<IMatBindingHandler> handlers)
    {
        _options = options;
        _settings = settings;
        _bindings = bindings;
        _store = store;
        _handlers = handlers;
    }

    public MatApiDefinition Definition(string language) => _options.Definition(language, new MatAuthInfo(false, null));

    /// <summary>The handler of a capability the app provides: its own, else the generic one when the app has a data folder; null otherwise.</summary>
    public IMatBindingHandler? HandlerFor(string capability)
    {
        if (!Definition(_options.DefaultLanguage).Provides.Any(p => p.Capability == capability))
        {
            return null;
        }

        return _handlers.FirstOrDefault(h => h.Capability == capability)
            ?? (_store.Enabled ? new MatKeyBindingHandler(capability, _store) : null);
    }

    public async Task<MatConnectionsView> ViewAsync(string language, CancellationToken ct = default)
    {
        var definition = Definition(language);
        var uses = new List<MatUseView>();
        foreach (var consumed in definition.Consumes)
        {
            var spec = MatCapabilities.Of(consumed.Capability);
            var binding = await _bindings.GetAsync(consumed.Capability, ct);
            var values = spec.Values.Select(v => new MatValueView(v.Key, v.Variable, v.Label(language), v.Type, v.Secret, v.Required,
                v.Secret ? MatSecret.Of(binding[v.Key]) : binding[v.Key])).ToList();
            uses.Add(new MatUseView(consumed.Capability, consumed.Version, consumed.Label ?? spec.Label(language), consumed.Optional == true,
                binding.Source, spec.ByHand && _bindings.CanBindByHand, values));
        }

        var offers = new List<MatOfferView>();
        foreach (var provided in definition.Provides)
        {
            var handler = HandlerFor(provided.Capability);
            var list = handler is null ? [] : await handler.ListAsync(ct);
            offers.Add(new MatOfferView(provided.Capability, provided.Version, provided.Label ?? MatCapabilities.Of(provided.Capability).Label(language),
                handler is not null, list));
        }

        return new MatConnectionsView(uses, offers, definition.Contributes);
    }

    /// <summary>This app as the provider of a binding: the addresses a broker named, else <c>MAT_PUBLIC_URL</c>/<c>MAT_INTERNAL_URL</c>, else the address of the request.</summary>
    public MatBindingParty Self(HttpContext http, MatBindingParty? named = null)
    {
        var app = Definition(_options.DefaultLanguage).App;
        var requestUrl = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
        return new MatBindingParty(app.Id, app.Name,
            Clean(named?.Url) ?? _settings.AppPublicUrl ?? requestUrl,
            Clean(named?.InternalUrl) ?? _settings.AppInternalUrl);
    }

    /// <summary>The values of a binding as variables (<c>MAT_OFFICE_EDIT_KEY</c>): a secret that was not handed out again as <c>{"isSet": true}</c>, a missing value as "".</summary>
    public static Dictionary<string, object?> Variables(string capability, MatBindResult result)
    {
        var spec = MatCapabilities.Of(capability);
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var value in spec.Values)
        {
            var given = result.Values.TryGetValue(value.Key, out var text) ? text : null;
            variables[value.Variable] = given is not null ? given : value.Secret ? new MatSecret(true) : string.Empty;
        }

        return variables;
    }

    /// <summary>A consumer id: small letters, digits and dashes.</summary>
    public static bool IsId(string? id) => id is not null && IdRegex().IsMatch(id);

    /// <summary>An id from a name typed in the Connections area (<c>My Drive 2</c> → <c>my-drive-2</c>).</summary>
    public static string IdOf(string name)
    {
        var id = NotIdRegex().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return id.Length == 0 ? "app" : id.Length > 60 ? id[..60].TrimEnd('-') : id;
    }

    private static string? Clean(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotIdRegex();
}

/// <summary>The endpoints of the bindings, the Connections area and the list widgets (mapped by <c>MapMatPlatform</c>).</summary>
public static partial class MatConnectionEndpoints
{
    /// <summary>The form of the Connections area posts here (an administrator, with the anti-forgery token).</summary>
    public const string FormPath = "/api/connections/form";

    /// <summary>The cookie that carries the outcome of a form (and a new key, once) back to the Connections area.</summary>
    public const string FlashCookie = "matplatform-connections";

    internal const string FlashPurpose = "MatPlatform.Connections.Flash";

    internal static void Map(IEndpointRouteBuilder app, RouteGroupBuilder api)
    {
        var json = MatApiDefinition.Json;

        api.MapGet("/bindings", async (HttpContext http, MatConnections connections, CancellationToken ct) =>
        {
            if (KeyOnly(http) is { } refused)
            {
                return refused;
            }

            var all = new List<MatBindingInfo>();
            foreach (var provided in connections.Definition("en").Provides)
            {
                if (connections.HandlerFor(provided.Capability) is { } handler)
                {
                    all.AddRange(await handler.ListAsync(ct));
                }
            }

            return Results.Json(new { bindings = all }, json);
        });

        api.MapPut("/bindings/{capability}/{consumer}", async (string capability, string consumer, HttpContext http, MatConnections connections, CancellationToken ct) =>
        {
            if (KeyOnly(http) is { } refused)
            {
                return refused;
            }

            BindBody? body;
            try
            {
                body = await http.Request.ReadFromJsonAsync<BindBody>(json, ct);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                body = null;
            }

            var managedBy = body?.ManagedBy?.Trim();
            if (body is null || !MatConnections.IsId(consumer) || managedBy is null || !MatConnections.IsId(managedBy))
            {
                return Error(string.Empty, "Send {\"managedBy\": \"...\", \"consumer\": {...}} for a consumer id of small letters, digits and dashes.", StatusCodes.Status400BadRequest);
            }

            var provided = connections.Definition("en").Provides.FirstOrDefault(p => p.Capability == capability);
            if (provided is null || connections.HandlerFor(capability) is not { } handler)
            {
                return Error("capability", $"This app does not provide {capability}.", StatusCodes.Status404NotFound);
            }

            var version = body.Version ?? 1;
            if (version > provided.Version)
            {
                return Error("version", $"This app provides {capability} up to version {provided.Version}.", StatusCodes.Status409Conflict);
            }

            var request = new MatBindRequest(capability, version,
                new MatBindingParty(consumer, body.Consumer?.Name ?? consumer, body.Consumer?.Url, body.Consumer?.InternalUrl) { Urls = body.Consumer?.Urls },
                connections.Self(http, body.Provider),
                body.Params ?? new Dictionary<string, string>(),
                managedBy,
                body.Renew == true);
            MatBindResult result;
            try
            {
                result = await handler.BindAsync(request, ct);
            }
            catch (MatBindingRefusedException no)
            {
                return Error(no.Key, no.Message, no.Conflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
            }

            return Results.Json(new { variables = MatConnections.Variables(capability, result) }, json);
        });

        api.MapDelete("/bindings/{capability}/{consumer}", async (string capability, string consumer, string? managedBy, HttpContext http, MatConnections connections, CancellationToken ct) =>
        {
            if (KeyOnly(http) is { } refused)
            {
                return refused;
            }

            return connections.HandlerFor(capability) is { } handler && await handler.UnbindAsync(consumer, managedBy, ct)
                ? Results.NoContent()
                : Error(consumer, "There is no such binding.", StatusCodes.Status404NotFound);
        });

        api.MapGet("/connections", async (HttpContext http, MatConnections connections, MatPlatformOptions options, CancellationToken ct) =>
            Results.Json(await connections.ViewAsync(options.LanguageOf(http), ct), json));

        api.MapGet("/widgets/{id}", async (string id, HttpContext http, MatPlatformOptions options, CancellationToken ct) =>
        {
            var call = new MatPlatformCall(http, options.LanguageOf(http), MatApiAccess.CallerOf(http));
            var known = options.Definition(call.Language, new MatAuthInfo(false, null)).Contributes?.Widgets
                .Any(w => w.Id == id && w.Kind == MatWidgetKinds.List) == true;
            var items = known && http.RequestServices.GetService<IMatPlatformWidgets>() is { } widgets
                ? await widgets.GetWidgetAsync(id, call, ct)
                : null;
            return items is null
                ? Error(id, call.T("Dieses Widget gibt es nicht.", "There is no such widget."), StatusCodes.Status404NotFound)
                : Results.Json(new { items = items.Take(8) }, json);
        });

        // The Connections area: a form of an administrator of the app (who that is, the app says: MatPlatformOptions.ConnectionsAccess).
        app.MapPost(FormPath, HandleFormAsync).AllowAnonymous().DisableAntiforgery();
    }

    private static async Task<IResult> HandleFormAsync(HttpContext http, MatConnections connections, MatBindings bindings, MatPlatformOptions options, IAntiforgery antiforgery, IDataProtectionProvider protection, CancellationToken ct)
    {
        if (options.ConnectionsAccess is null || !await options.ConnectionsAccess(http))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        var form = await http.Request.ReadFormAsync(ct);
        var language = MatConnectionsTagHelper.LanguageOf(http, options);
        string T(string de, string en) => language == "en" ? en : de;
        var capability = form["capability"].ToString();
        var back = form["returnUrl"].ToString();
        if (!back.StartsWith('/') || back.StartsWith("//", StringComparison.Ordinal) || back.StartsWith("/\\", StringComparison.Ordinal))
        {
            back = "/";
        }

        var flash = new Flash { Capability = capability };
        var spec = MatCapabilities.Of(string.IsNullOrEmpty(capability) ? "none.none" : capability);
        switch (form["do"].ToString())
        {
            case "save":
                {
                    var input = spec.Values.ToDictionary(v => v.Key, v => (string?)form["v." + v.Key].ToString(), StringComparer.Ordinal);
                    var problems = connections.Definition(language).Consumes.Any(c => c.Capability == capability)
                        ? await bindings.SaveByHandAsync(capability, input, language, ct)
                        : new Dictionary<string, string> { [string.Empty] = T("Diese App nutzt das nicht.", "This app does not use that.") };
                    flash.Error = problems.Count > 0;
                    flash.Message = problems.Count > 0
                        ? string.Join(" ", problems.Select(p => (spec.Value(p.Key)?.Label(language) is { } label ? label + ": " : string.Empty) + p.Value))
                        : T("Gespeichert.", "Saved.");
                    break;
                }

            case "clear":
                await bindings.ClearByHandAsync(capability, ct);
                flash.Message = T("Getrennt.", "Disconnected.");
                break;

            case "connect":
            case "renew":
                {
                    var renew = form["do"] == "renew";
                    var name = form["name"].ToString().Trim();
                    var consumer = renew ? form["consumer"].ToString() : MatConnections.IdOf(name);
                    if (connections.HandlerFor(capability) is not { } handler || (!renew && name.Length is 0 or > 120))
                    {
                        flash.Error = true;
                        flash.Message = T("Gib den Namen der App ein.", "Enter the name of the app.");
                        break;
                    }

                    var existing = (await handler.ListAsync(ct)).FirstOrDefault(b => b.Consumer == consumer);
                    if (!renew && existing is not null)
                    {
                        flash.Error = true;
                        flash.Message = T($"„{existing.Name}“ ist schon verbunden: stelle seine Zugangsdaten neu aus.", $"\"{existing.Name}\" is connected already: issue its credentials anew.");
                        break;
                    }

                    if (renew && existing is null)
                    {
                        flash.Error = true;
                        flash.Message = T("Diese Verbindung gibt es nicht mehr.", "This connection is gone.");
                        break;
                    }

                    var request = new MatBindRequest(capability, 1, new MatBindingParty(consumer, renew ? existing!.Name : name), connections.Self(http),
                        new Dictionary<string, string>(), renew ? existing!.ManagedBy : MatConnections.ManagedByHand, Renew: true);
                    MatBindResult result;
                    try
                    {
                        result = await handler.BindAsync(request, ct);
                    }
                    catch (MatBindingRefusedException refused)
                    {
                        flash.Error = true;
                        flash.Message = refused.Message;
                        break;
                    }

                    flash.Values = MatConnections.Variables(capability, result)
                        .Where(v => v.Value is string { Length: > 0 })
                        .ToDictionary(v => v.Key, v => (string)v.Value!, StringComparer.Ordinal);
                    flash.Message = T(
                        $"Verbunden: {request.Consumer.Name}. Trage diese Werte dort ein. Geheime Werte werden nur jetzt gezeigt.",
                        $"Connected: {request.Consumer.Name}. Enter these values there. Secret values are shown only now.");
                    break;
                }

            case "remove":
                {
                    var removed = connections.HandlerFor(capability) is { } handler && await handler.UnbindAsync(form["consumer"].ToString(), null, ct);
                    flash.Error = !removed;
                    flash.Message = removed ? T("Verbindung entfernt.", "Connection removed.") : T("Diese Verbindung gibt es nicht mehr.", "This connection is gone.");
                    break;
                }

            default:
                return Results.BadRequest();
        }

        var protector = protection.CreateProtector(FlashPurpose);
        http.Response.Cookies.Append(FlashCookie, protector.Protect(JsonSerializer.Serialize(flash)), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/",
            MaxAge = TimeSpan.FromMinutes(5),
            IsEssential = true,
        });
        return Results.LocalRedirect(back);
    }

    /// <summary>Only a broker with <c>MAT_API_KEY</c> makes and removes bindings; a token of a person or another app does not.</summary>
    private static IResult? KeyOnly(HttpContext http)
        => MatApiAccess.CallerOf(http) == "api-key"
            ? null
            : Error(string.Empty, "Only the key of the app (MAT_API_KEY) may change bindings.", StatusCodes.Status403Forbidden);

    private static IResult Error(string key, string message, int status)
        => Results.Json(new { errors = new Dictionary<string, string> { [key] = message } }, MatApiDefinition.Json, statusCode: status);

    /// <summary>The outcome of a form of the Connections area, for the next page (in a protected cookie, read once).</summary>
    internal sealed class Flash
    {
        public string Capability { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public bool Error { get; set; }

        public Dictionary<string, string>? Values { get; set; }
    }

    private sealed record BindBody(int? Version, string? ManagedBy, MatBindingParty? Consumer, MatBindingParty? Provider, Dictionary<string, string>? Params, bool? Renew);
}
