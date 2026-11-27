// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatPlatform;

// 2.4.0: the people of an app (docs/PLATFORM.md "People, permissions and quotas"). MatIdentity sends a person's role, permissions (claim entitlements) and quotas
// (claim mat_quotas) for this app at every sign-in; MatAccess keeps the last values of every person, so the app can ask about anyone, also a person who is not the
// one making the request (a background job, the owner of a shared folder). The app enforces them with its own code.

/// <summary>What MatIdentity said about one person in this app at their last sign-in.</summary>
/// <param name="Subject">The claim <c>sub</c>.</param>
/// <param name="Role">The claim <c>role</c>; null when MatIdentity does not decide the roles of this app (then it decides nothing of the rest either).</param>
/// <param name="Permissions">The claim <c>entitlements</c>: the permissions the person has in this app (those of the role included).</param>
/// <param name="Quotas">The claim <c>mat_quotas</c>: the quotas MatIdentity set; a null value is <em>no limit</em>, a key that is not there the app's own setting.</param>
/// <param name="Seen">When the person last signed in.</param>
public sealed record MatPersonAccess(string Subject, string? Role, IReadOnlyList<string> Permissions, IReadOnlyDictionary<string, long?> Quotas, DateTimeOffset Seen)
{
    /// <summary>MatIdentity decides the role, the permissions and the quotas of this person here (a role came with the sign-in).</summary>
    public bool Decided => Role is not null;
}

/// <summary>The claims of the people (<c>entitlements</c>, <c>mat_quotas</c>) and how they are read.</summary>
public static partial class MatAccessClaims
{
    /// <summary>The permissions of the person in this app (RFC 9068 2.2.3.1): one claim per value, or one claim with a JSON array.</summary>
    public const string Entitlements = "entitlements";

    /// <summary>The quotas MatIdentity set for the person in this app: one claim with a JSON object, <c>{"storage": 53687091200, "ai-tokens": null}</c>.</summary>
    public const string Quotas = "mat_quotas";

    /// <summary>The permissions in <paramref name="principal"/>: well-formed keys, each once.</summary>
    public static IReadOnlyList<string> PermissionsOf(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var keys = new List<string>();
        foreach (var claim in principal.FindAll(Entitlements))
        {
            var value = claim.Value.Trim();
            if (value.StartsWith('['))
            {
                try
                {
                    using var array = JsonDocument.Parse(value);
                    keys.AddRange(array.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                }
                catch (JsonException)
                {
                    // Not a list: nothing of it counts.
                }
            }
            else
            {
                keys.Add(value);
            }
        }

        return [.. keys.Where(IsKey).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The quotas in <paramref name="principal"/>: well-formed keys with a whole number from 0 or null (no limit); anything else is left out.</summary>
    public static IReadOnlyDictionary<string, long?> QuotasOf(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var quotas = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var claim in principal.FindAll(Quotas))
        {
            try
            {
                using var document = JsonDocument.Parse(claim.Value);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var entry in document.RootElement.EnumerateObject().Where(e => IsKey(e.Name)))
                {
                    if (entry.Value.ValueKind == JsonValueKind.Null)
                    {
                        quotas[entry.Name] = null;
                    }
                    else if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt64(out var number) && number >= 0)
                    {
                        quotas[entry.Name] = number;
                    }
                }
            }
            catch (JsonException)
            {
                // Not an object: nothing of it counts.
            }
        }

        return quotas;
    }

    /// <summary>The form of the key of a permission or a quota (as MatIdentity keeps role keys).</summary>
    public static bool IsKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex KeyRegex();
}

/// <summary>
/// The last role, permissions and quotas MatIdentity sent for every person (<c>&lt;DataDirectory&gt;/matplatform/people.json</c>; in memory without a data folder), and
/// the questions an app asks about them. <see cref="MatIdentitySignIn.CompleteSignInAsync"/> keeps them before it hands the person to the app.
/// </summary>
public sealed class MatAccess
{
    public const string FileName = "people.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, MatPersonAccess>? _cache;

    public MatAccess(MatPlatformOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = string.IsNullOrWhiteSpace(options.DataDirectory) ? null : Path.Combine(options.DataDirectory, MatBindingStore.Folder, FileName);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>What MatIdentity said about <paramref name="subject"/> at their last sign-in; null when they have not signed in through MatIdentity.</summary>
    public async Task<MatPersonAccess?> GetAsync(string subject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return (await ReadAsync(ct)).TryGetValue(subject, out var person) ? person : null;
    }

    /// <summary>
    /// Has <paramref name="subject"/> the permission <paramref name="permission"/>? What MatIdentity said when it decides the roles of this app, else
    /// <paramref name="fallback"/> (the app's own rule: MatIdentity is off, or the app manages its roles).
    /// </summary>
    public async Task<bool> HasAsync(string subject, string permission, bool fallback, CancellationToken ct = default)
        => await GetAsync(subject, ct) is { Decided: true } person ? person.Permissions.Contains(permission, StringComparer.Ordinal) : fallback;

    /// <summary>
    /// The limit of the quota <paramref name="key"/> for <paramref name="subject"/>: MatIdentity's value when it set one (null: no limit), else
    /// <paramref name="ownLimit"/> (the app's own setting for this person).
    /// </summary>
    public async Task<long?> LimitAsync(string subject, string key, long? ownLimit, CancellationToken ct = default)
        => await GetAsync(subject, ct) is { Decided: true } person && person.Quotas.TryGetValue(key, out var limit) ? limit : ownLimit;

    /// <summary>MatIdentity set the quota <paramref name="key"/> of <paramref name="subject"/>: the app shows it as fixed on its own page of the person.</summary>
    public async Task<bool> IsFixedAsync(string subject, string key, CancellationToken ct = default)
        => await GetAsync(subject, ct) is { Decided: true } person && person.Quotas.ContainsKey(key);

    /// <summary>MatIdentity decides the role, permissions and quotas of <paramref name="subject"/> here: the app's own page of the person shows them as fixed (<see cref="Describe"/>).</summary>
    public async Task<bool> IsDecidedAsync(string subject, CancellationToken ct = default)
        => await GetAsync(subject, ct) is { Decided: true };

    /// <summary>The sentence for the app's own page of a person whose role, permissions and quotas MatIdentity sets.</summary>
    public static string Describe(string language)
        => language == "en"
            ? "MatIdentity sets this person's role, permissions and quotas; they are changed there."
            : "Rolle, Rechte und Kontingente dieser Person legt MatIdentity fest; geändert werden sie dort.";

    /// <summary>Keeps what MatIdentity sent with the sign-in of <paramref name="person"/> (replacing what was kept before).</summary>
    public Task RememberAsync(MatIdentityPerson person, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (string.IsNullOrEmpty(person.Subject))
        {
            return Task.CompletedTask;
        }

        var access = new MatPersonAccess(
            person.Subject,
            person.Role,
            person.Role is null ? [] : person.Permissions ?? [],
            person.Role is null ? new Dictionary<string, long?>() : person.Quotas ?? new Dictionary<string, long?>(),
            _time.GetUtcNow());
        return ChangeAsync(people => people[person.Subject] = access, ct);
    }

    /// <summary>Forgets <paramref name="subject"/> (the app deleted the person).</summary>
    public Task ForgetAsync(string subject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return ChangeAsync(people => people.Remove(subject), ct);
    }

    private async Task<Dictionary<string, MatPersonAccess>> ReadAsync(CancellationToken ct)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        await _lock.WaitAsync(ct);
        try
        {
            return _cache ??= await LoadAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ChangeAsync(Action<Dictionary<string, MatPersonAccess>> change, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // A copy is changed and written; readers keep the old dictionary until the new one is complete.
            var people = new Dictionary<string, MatPersonAccess>(_cache ?? await LoadAsync(ct), StringComparer.Ordinal);
            change(people);
            if (_path is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(people.Values.OrderBy(p => p.Subject, StringComparer.Ordinal), Json), ct);
                File.Move(temp, _path, overwrite: true);
            }

            _cache = people;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, MatPersonAccess>> LoadAsync(CancellationToken ct)
    {
        if (_path is null || !File.Exists(_path))
        {
            return new Dictionary<string, MatPersonAccess>(StringComparer.Ordinal);
        }

        // A file that cannot be read is an error, not an empty list: the next sign-in of each person writes their values again, but nothing is guessed meanwhile.
        var people = JsonSerializer.Deserialize<List<MatPersonAccess>>(await File.ReadAllTextAsync(_path, ct), Json)
            ?? throw new InvalidDataException($"{_path} is empty.");
        return people.Where(p => !string.IsNullOrEmpty(p.Subject)).ToDictionary(p => p.Subject, StringComparer.Ordinal);
    }
}
