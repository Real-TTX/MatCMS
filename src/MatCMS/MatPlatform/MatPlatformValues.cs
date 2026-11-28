// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace MatPlatform;

// What an app supplies to the platform contract besides its definition: the values. Each interface is optional; one class may implement all of them
// (register it with AddMatPlatform<TApp> or AddMatPlatformValues<TApp>). The endpoints of MapMatPlatform call them (docs/PLATFORM.md).

/// <summary>One call of the platform API: the request, the language of its texts and who calls.</summary>
/// <param name="Http">The request.</param>
/// <param name="Language">The language the caller asked for with <c>Accept-Language</c> (<c>de</c>, <c>en</c>), else the app's.</param>
/// <param name="Caller">Who calls, for the activity log: <c>api-key</c>, the subject or client of a token; <c>anonymous</c> on <c>/api/health</c>.</param>
public sealed record MatPlatformCall(HttpContext Http, string Language, string Caller)
{
    /// <summary>The German or the English text, by <see cref="Language"/>.</summary>
    public string T(string de, string en) => Language == "en" ? en : de;
}

/// <summary>The values of the tiles (<c>GET /api/state</c>).</summary>
public interface IMatPlatformState
{
    /// <summary>One entry per key of <see cref="MatApiDefinition.State"/> (plus helpers such as the maximum of a meter).</summary>
    Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct);
}

/// <summary>The values of the few settings (<c>GET</c> and <c>PUT /api/configure</c>).</summary>
public interface IMatPlatformConfiguration
{
    /// <summary>
    /// One entry per key of <see cref="MatApiDefinition.Configure"/>. A secret may be returned as <see cref="MatSecret"/>; whatever is returned for a field of the type
    /// <c>secret</c> is turned into <c>{"isSet": bool}</c> before it is sent, so a secret never leaves the app.
    /// </summary>
    Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct);

    /// <summary>
    /// Checks and saves the values that are in <paramref name="values"/> (only the keys that change; a secret that is not there stays, <c>""</c> clears it)
    /// when all are valid, and writes the change to the app's activity log with <see cref="MatPlatformCall.Caller"/>. Returns the errors per key otherwise
    /// (nothing saved); an empty answer means saved.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct);
}

/// <summary>The actions of the kind <c>post</c> (<c>POST /api/actions/{id}</c>).</summary>
public interface IMatPlatformActions
{
    /// <summary>Runs the action; <c>Ok = false</c> when it cannot run now (409), null when the app does not know it (404).</summary>
    Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct);
}

/// <summary>The checks of <c>GET /api/health</c>. Fast and cheap: no outside call that can hang.</summary>
public interface IMatPlatformHealth
{
    /// <summary>The checks, each <c>ok</c>, <c>degraded</c> or <c>down</c>; the status of the app is the worst of them.</summary>
    Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct);
}

/// <summary>2.4.0: what a person uses of one quota, and the app's own limit for them (null: no limit).</summary>
public sealed record MatQuotaUsage(long Used, long? Limit);

/// <summary>2.4.0: what a person uses of the app's quotas (<c>GET /api/quotas/{subject}</c>); MatPlatform puts MatIdentity's value over the app's own limit.</summary>
public interface IMatPlatformQuotas
{
    /// <summary>
    /// One entry per quota of the definition (a missing one counts as nothing used and the app's limit as none): what <paramref name="subject"/> uses and the app's
    /// own limit for them (its default or its setting for this person); null when the app does not know the person (404).
    /// </summary>
    Task<IReadOnlyDictionary<string, MatQuotaUsage>?> GetUsageAsync(string subject, MatPlatformCall call, CancellationToken ct);
}

/// <summary>2.4.0: the answer of <c>GET /api/quotas/{subject}</c>.</summary>
public sealed record MatQuotaReport(string Subject, IReadOnlyDictionary<string, MatQuotaReportValue> Values);

/// <summary>
/// One quota of a person: what is used, the limit that applies now (MatIdentity's when <see cref="Fixed"/>, else the app's own; null: no limit; always written) and
/// the period of the quota.
/// </summary>
public sealed record MatQuotaReportValue(long Used, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] long? Limit, bool Fixed, string? Period = null);

/// <summary>The rows of the widgets of the kind <c>list</c> (<c>GET /api/widgets/{id}</c>).</summary>
public interface IMatPlatformWidgets
{
    /// <summary>Up to eight rows of the widget, newest or most important first; null when the app does not know it (404).</summary>
    Task<IReadOnlyList<MatWidgetItem>?> GetWidgetAsync(string id, MatPlatformCall call, CancellationToken ct);
}
