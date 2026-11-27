// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using Microsoft.Extensions.Configuration;

namespace MatPlatform;

/// <summary>Where the values of a binding come from: nowhere, the environment variables (a broker such as Mattiverse, or the compose file) or the Connections area.</summary>
public enum MatBindingSource
{
    None,
    Variables,
    ByHand,
}

/// <summary>A capability this app consumes, as it is bound now: the source and the values by their keys (<see cref="MatCapabilities.Url"/>, ...).</summary>
public sealed record MatBinding(string Capability, MatBindingSource Source, IReadOnlyDictionary<string, string> Values)
{
    public bool IsBound => Source != MatBindingSource.None;

    /// <summary>The value with this key, or null.</summary>
    public string? this[string key] => Values.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>The public address of the provider (a key capability).</summary>
    public string? Url => this[MatCapabilities.Url];

    /// <summary>The address of the provider on the shared network, if there is one.</summary>
    public string? InternalUrl => this[MatCapabilities.InternalUrl];

    /// <summary>The address for calls from this server: the internal one when there is one.</summary>
    public string? ServerUrl => InternalUrl ?? Url;

    /// <summary>The key of the binding (a key capability).</summary>
    public string? Key => this[MatCapabilities.Key];

    public static MatBinding None(string capability) => new(capability, MatBindingSource.None, new Dictionary<string, string>());
}

/// <summary>
/// The capabilities this app consumes, bound (docs/PLATFORM.md "Bindings"): from the environment variables when any of them is set (a broker or the compose file;
/// the app then shows the values as fixed), else from what an administrator entered in the Connections area, else not bound. Inject it where the app calls the
/// provider: <c>var office = await bindings.GetAsync("office.edit"); if (office.IsBound) { ... office.ServerUrl ... office.Key ... }</c>.
/// </summary>
public sealed class MatBindings
{
    private readonly IConfiguration _configuration;
    private readonly MatBindingStore _store;

    public MatBindings(IConfiguration configuration, MatBindingStore store)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(store);
        _configuration = configuration;
        _store = store;
    }

    /// <summary>True when an administrator may enter values by hand (the app has a data folder).</summary>
    public bool CanBindByHand => _store.Enabled;

    /// <summary>The binding of <paramref name="capability"/>: variables first, then the values entered by hand.</summary>
    public async Task<MatBinding> GetAsync(string capability, CancellationToken ct = default)
    {
        var fromVariables = FromVariables(capability);
        if (fromVariables.IsBound)
        {
            return fromVariables;
        }

        var spec = MatCapabilities.Of(capability);
        if (!spec.ByHand || await _store.UsedAsync(capability, ct) is not { } values || !Complete(spec, values))
        {
            return MatBinding.None(capability);
        }

        return new MatBinding(capability, MatBindingSource.ByHand, values);
    }

    /// <summary>The binding as the environment variables give it (none when no variable of the capability is set). Read on every call: no restart is needed in tests.</summary>
    public MatBinding FromVariables(string capability)
    {
        var spec = MatCapabilities.Of(capability);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in spec.Values)
        {
            var text = _configuration[value.Variable]?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                values[value.Key] = value.Type == "url" ? text.TrimEnd('/') : text;
            }
        }

        return values.Count == 0 || !Complete(spec, values)
            ? MatBinding.None(capability)
            : new MatBinding(capability, MatBindingSource.Variables, values);
    }

    /// <summary>True while the variables set the capability: its values are fixed, in the Connections area and in <c>/api/configure</c>.</summary>
    public bool IsFixed(string capability) => FromVariables(capability).IsBound;

    /// <summary>
    /// Keeps the values an administrator entered (by the keys of the values; an empty secret keeps the stored one). Returns the problems by key; nothing is kept
    /// when there is one. Refused while the variables set the capability.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> SaveByHandAsync(string capability, IReadOnlyDictionary<string, string?> input, string language, CancellationToken ct = default)
    {
        var spec = MatCapabilities.Of(capability);
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!spec.ByHand || !_store.Enabled || IsFixed(capability))
        {
            problems[string.Empty] = language == "en" ? "These values cannot be entered here." : "Diese Werte lassen sich hier nicht eintragen.";
            return problems;
        }

        var stored = await _store.UsedAsync(capability, ct) ?? new Dictionary<string, string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in spec.Values)
        {
            var text = input.TryGetValue(value.Key, out var given) ? given?.Trim() ?? string.Empty : string.Empty;
            if (value.Secret && text.Length == 0 && stored.TryGetValue(value.Key, out var kept))
            {
                text = kept;
            }

            if (value.Type == "url" && text.Length > 0)
            {
                text = text.TrimEnd('/');
                if (!(Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
                {
                    problems[value.Key] = language == "en" ? "An address starting with http:// or https://." : "Eine Adresse, die mit http:// oder https:// beginnt.";
                    continue;
                }
            }

            if (text.Length > 2000)
            {
                problems[value.Key] = language == "en" ? "Too long." : "Zu lang.";
            }
            else if (value.Required && text.Length == 0)
            {
                problems[value.Key] = language == "en" ? "Required." : "Pflichtfeld.";
            }
            else if (text.Length > 0)
            {
                values[value.Key] = text;
            }
        }

        if (problems.Count == 0)
        {
            await _store.SetUsedAsync(capability, values, ct);
        }

        return problems;
    }

    /// <summary>Forgets the values entered by hand.</summary>
    public Task<bool> ClearByHandAsync(string capability, CancellationToken ct = default)
        => _store.Enabled ? _store.ClearUsedAsync(capability, ct) : Task.FromResult(false);

    private static bool Complete(MatCapabilitySpec spec, IReadOnlyDictionary<string, string> values)
        => spec.Values.Where(v => v.Required).All(v => values.TryGetValue(v.Key, out var text) && !string.IsNullOrEmpty(text));
}
