using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Services.Plugins;

/// <summary>
/// The plugins' API for other programs: <c>/api/v1/{plugin}/{path}</c> runs the route a plugin declared with <see cref="IPluginBuilder.Api"/>, and
/// <c>GET /api/v1</c> lists what the presented key may call. Every call needs a site API key (<see cref="SiteApiKeyService"/>) as
/// <c>Authorization: Bearer msk_…</c> (or <c>X-Api-Key</c>) with the plugin's right for the route's <see cref="ApiAccess"/>.
/// <para>Deliberately NOT the public route machinery (<see cref="PluginRouteTransformer"/>/<c>PluginPage</c>): no layout, no cookies, no member, no
/// antiforgery, no culture prefix — and a plugin's handler never sees the key (the Authorization header is taken out), only its name
/// (<see cref="RequestData.ApiCaller"/>). Answers are JSON, errors <c>{"error": "…"}</c> with a status that says what went wrong.</para>
/// </summary>
public static class PluginApiEndpoint
{
    /// <summary>Calls per key and minute. An ERP syncing stock sends batches (one call, many lines), not one call per article.</summary>
    public const int CallsPerMinute = 600;

    private static readonly ConcurrentDictionary<int, (long Minute, int Count)> Calls = new();

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1", IndexAsync).DisableAntiforgery();
        app.Map("/api/v1/{plugin}/{**rest}", HandleAsync).DisableAntiforgery();
    }

    private static IResult Error(int status, string message, HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { error = message }, statusCode: status);
    }

    /// <summary>The key a request presents: <c>Authorization: Bearer …</c>, else <c>X-Api-Key</c>.</summary>
    private static string Presented(HttpRequest r)
    {
        var auth = r.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth[7..].Trim();
        return r.Headers["X-Api-Key"].ToString().Trim();
    }

    private static async Task<(MatCMS.Models.SiteApiKey? Key, IResult? Error)> AuthenticateAsync(HttpContext http, SiteApiKeyService keys)
    {
        var presented = Presented(http.Request);
        if (presented.Length == 0)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return (null, Error(401, "Kein API-Schlüssel. Senden Sie ihn als „Authorization: Bearer msk_…“.", http));
        }
        var key = await keys.FindAsync(presented, http.RequestAborted);
        if (key is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            return (null, Error(401, "Der API-Schlüssel ist unbekannt, widerrufen oder abgelaufen.", http));
        }
        var minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
        var c = Calls.AddOrUpdate(key.Id, _ => (minute, 1), (_, e) => e.Minute == minute ? (minute, e.Count + 1) : (minute, 1));
        if (c.Count > CallsPerMinute)
        {
            http.Response.Headers.RetryAfter = (60 - DateTime.UtcNow.Second).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return (null, Error(429, $"Zu viele Aufrufe (höchstens {CallsPerMinute} pro Minute und Schlüssel). Mehrere Zeilen gehen in einem Aufruf.", http));
        }
        await keys.TouchAsync(key.Id, http.RequestAborted);
        return (key, null);
    }

    /// <summary><c>GET /api/v1</c>: who this key is and every route of every loaded plugin, with whether this key may call it.</summary>
    private static async Task<IResult> IndexAsync(HttpContext http, SiteApiKeyService keys, PluginRegistry registry)
    {
        var (key, error) = await AuthenticateAsync(http, keys);
        if (error is not null) return error;
        var routes = new JsonArray();
        foreach (var r in registry.ApiRoutes.OrderBy(r => r.Owner, StringComparer.Ordinal).ThenBy(r => r.Pattern.Template, StringComparer.Ordinal))
            routes.Add(new JsonObject
            {
                ["plugin"] = r.Owner, ["method"] = r.Method, ["path"] = "/api/v1/" + r.Owner + r.Pattern.Template,
                ["access"] = SiteApiKeyService.Word(r.Options.Access), ["summary"] = r.Options.Summary,
                ["allowed"] = SiteApiKeyService.Allows(key!.Rights, r.Owner, r.Options.Access)
            });
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(new JsonObject
        {
            ["key"] = new JsonObject { ["name"] = key!.Name, ["rights"] = new JsonArray(key.Rights.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode)x).ToArray()) },
            ["routes"] = routes
        });
    }

    private static async Task<IResult> HandleAsync(HttpContext http, string plugin, string? rest, SiteApiKeyService keys, PluginRegistry registry)
    {
        var (key, error) = await AuthenticateAsync(http, keys);
        if (error is not null) return error;

        var method = http.Request.Method.ToUpperInvariant();
        var path = "/" + (rest ?? "").Trim('/');
        var (route, values, otherMethod) = registry.MatchApi(plugin, path, method);
        if (route is null)
            return otherMethod
                ? Error(405, $"{method} gibt es für {path} nicht.", http)
                : Error(404, registry.ApiRoutes.Any(r => r.Owner == plugin) ? $"Unbekannter Endpunkt: {path}." : $"Das Plugin „{plugin}“ bietet keine API (oder ist ausgeschaltet).", http);
        if (!SiteApiKeyService.Allows(key!.Rights, plugin, route.Options.Access))
            return Error(403, $"Dieser Schlüssel hat für „{plugin}“ nicht das Recht „{SiteApiKeyService.Word(route.Options.Access)}“.", http);

        byte[] body;
        try { body = method is "GET" ? Array.Empty<byte>() : await ReadBodyAsync(http, RequestData.MaxApiBody); }
        catch (RequestBodyTooLargeException) { return Error(413, $"Der Body ist zu groß (höchstens {RequestData.MaxApiBody / 1024 / 1024} MB).", http); }

        var query = http.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
        // The key is the caller's secret, not the plugin's: the header that carries it never reaches the handler.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in http.Request.Headers)
            if (!string.Equals(kv.Key, "Authorization", StringComparison.OrdinalIgnoreCase) && !string.Equals(kv.Key, "X-Api-Key", StringComparison.OrdinalIgnoreCase))
                headers[kv.Key] = kv.Value.ToString();
        var request = new RequestData
        {
            Method = method, Path = path, Query = query, Route = new Dictionary<string, string>(values!, StringComparer.OrdinalIgnoreCase),
            Headers = headers, Body = body, ClientIp = http.Connection.RemoteIpAddress?.ToString() ?? "", ApiCaller = key.Name
        };
        var ctx = new PluginRequestContext(route.Info, request, new PluginUser(), PluginRequestContext.CurrentLocale(), null, "", http.RequestServices, registry);

        PluginResult result;
        try { result = await route.Handler(ctx, http.RequestAborted); }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            registry.AddLog(route.Owner, $"API {method} {path} ({key.Name}): {ex.Message}");
            return Error(500, "Interner Fehler im Plugin. Der Betreiber findet ihn im Protokoll des Plugins.", http);
        }

        http.Response.Headers.CacheControl = "no-store";
        return result.Kind switch
        {
            PluginResult.ResultKind.Json => Results.Content(result.Body?.ToJsonString() ?? "null", "application/json; charset=utf-8", statusCode: result.StatusCode),
            PluginResult.ResultKind.Text => Results.Content(result.Content, result.ContentType, statusCode: result.StatusCode),
            PluginResult.ResultKind.File => Results.File(result.Bytes, result.ContentType, string.IsNullOrEmpty(result.Target) ? null : result.Target),
            PluginResult.ResultKind.Status => result.StatusCode >= 400 ? Error(result.StatusCode, "Fehler " + result.StatusCode + ".", http) : Results.StatusCode(result.StatusCode),
            // A page, a fragment or a redirect is an answer for a browser, not for a program.
            _ => Error(500, "Der Endpunkt hat keine API-Antwort gegeben.", http)
        };
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext http, int max)
    {
        if (http.Request.ContentLength > max) throw new RequestBodyTooLargeException();
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > max) throw new RequestBodyTooLargeException();
        }
        return ms.ToArray();
    }
}
