// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MatPlatform;

/// <summary>A binding this app made for a consumer of a key capability (<see cref="MatKeyBindingHandler"/>): who, since when, and only the hash of its key.</summary>
public sealed class MatProvidedBinding
{
    public string Capability { get; set; } = string.Empty;

    /// <summary>The id of the consumer (<c>matdrive</c>).</summary>
    public string Consumer { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Who made it: <c>mattiverse</c> (a broker) or <c>manual</c> (an administrator in the Connections area).</summary>
    public string ManagedBy { get; set; } = string.Empty;

    /// <summary>SHA-256 of the key (hex).</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>The first characters of the key, to recognise it.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    public DateTimeOffset Since { get; set; }

    public DateTimeOffset? LastUsed { get; set; }

    public string? ConsumerUrl { get; set; }

    public string? ConsumerInternalUrl { get; set; }
}

/// <summary>
/// What MatPlatform keeps of the bindings, in one small file in the data folder of the app (<see cref="MatPlatformOptions.DataDirectory"/>, <c>matplatform/bindings.json</c>):
/// the bindings it made for consumers of its key capabilities (keys as hashes) and the values an administrator entered by hand for what it consumes (secrets protected
/// with ASP.NET Core Data Protection). Without a data folder nothing is kept: bindings come from variables only.
/// </summary>
public sealed class MatBindingStore
{
    public const string Folder = "matplatform";
    public const string FileName = "bindings.json";
    public const string KeyStart = "mat_";
    private const string Purpose = "MatPlatform.Bindings";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string? _path;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private StoreFile? _cache;

    public MatBindingStore(MatPlatformOptions options, IDataProtectionProvider protection)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protection);
        _path = string.IsNullOrWhiteSpace(options.DataDirectory) ? null : Path.Combine(options.DataDirectory, Folder, FileName);
        _protector = protection.CreateProtector(Purpose);
    }

    /// <summary>True when the app named a data folder: bindings by hand are possible.</summary>
    public bool Enabled => _path is not null;

    // ---- provided (the generic key capabilities) -------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<MatProvidedBinding>> ProvidedAsync(string capability, CancellationToken ct = default)
        => (await ReadAsync(ct)).Provided.Where(b => b.Capability == capability).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Makes or updates the binding of <paramref name="consumer"/>; a new key when there is none or <paramref name="renew"/>. Returns the key when one was made.</summary>
    public Task<(MatProvidedBinding Binding, string? Key)> UpsertProvidedAsync(string capability, MatBindingParty consumer, string managedBy, bool renew, CancellationToken ct = default)
        => ChangeAsync(file =>
        {
            var binding = file.Provided.FirstOrDefault(b => b.Capability == capability && b.Consumer == consumer.Id);
            string? key = null;
            if (binding is null)
            {
                binding = new MatProvidedBinding { Capability = capability, Consumer = consumer.Id, Since = DateTimeOffset.UtcNow };
                file.Provided.Add(binding);
                renew = true;
            }

            binding.Name = string.IsNullOrWhiteSpace(consumer.Name) ? consumer.Id : consumer.Name.Trim();
            binding.ManagedBy = managedBy;
            binding.ConsumerUrl = consumer.Url;
            binding.ConsumerInternalUrl = consumer.InternalUrl;
            if (renew)
            {
                key = NewKey();
                binding.KeyHash = Hash(key);
                binding.KeyPrefix = key[..(KeyStart.Length + 6)];
            }

            return (binding, key);
        }, ct);

    /// <summary>Removes a binding (only one made by <paramref name="managedBy"/>, unless it is null). False when there was none.</summary>
    public Task<bool> RemoveProvidedAsync(string capability, string consumer, string? managedBy, CancellationToken ct = default)
        => ChangeAsync(file => file.Provided.RemoveAll(b => b.Capability == capability && b.Consumer == consumer && (managedBy is null || b.ManagedBy == managedBy)) > 0, ct);

    /// <summary>The consumer whose key <paramref name="key"/> is, for <paramref name="capability"/>; notes when it was used. Null for any other key.</summary>
    public async Task<MatProvidedBinding?> AuthenticateAsync(string capability, string? key, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrEmpty(key) || !key.StartsWith(KeyStart, StringComparison.Ordinal) || key.Length > 200)
        {
            return null;
        }

        var hash = Encoding.ASCII.GetBytes(Hash(key));
        var match = (await ReadAsync(ct)).Provided.FirstOrDefault(b => b.Capability == capability
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(b.KeyHash), hash));
        if (match is not null && (match.LastUsed is null || match.LastUsed < DateTimeOffset.UtcNow.AddMinutes(-5)))
        {
            await ChangeAsync(file =>
            {
                var stored = file.Provided.FirstOrDefault(b => b.Capability == capability && b.Consumer == match.Consumer);
                if (stored is not null)
                {
                    stored.LastUsed = DateTimeOffset.UtcNow;
                }

                return true;
            }, ct);
        }

        return match;
    }

    // ---- used (values entered by hand) ----------------------------------------------------------------------------------------------

    /// <summary>The values entered by hand for <paramref name="capability"/> (by the keys of its values), or null.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> UsedAsync(string capability, CancellationToken ct = default)
    {
        if (!(await ReadAsync(ct)).Used.TryGetValue(capability, out var stored))
        {
            return null;
        }

        var spec = MatCapabilities.Of(capability);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in stored)
        {
            values[key] = spec.Value(key)?.Secret == true ? Unprotect(value) : value;
        }

        return values;
    }

    /// <summary>Keeps the values entered by hand (by the keys of the values; a secret is protected).</summary>
    public Task SetUsedAsync(string capability, IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        var spec = MatCapabilities.Of(capability);
        var stored = values.Where(v => !string.IsNullOrEmpty(v.Value))
            .ToDictionary(v => v.Key, v => spec.Value(v.Key)?.Secret == true ? _protector.Protect(v.Value) : v.Value, StringComparer.Ordinal);
        return ChangeAsync(file => file.Used[capability] = stored, ct);
    }

    public Task<bool> ClearUsedAsync(string capability, CancellationToken ct = default)
        => ChangeAsync(file => file.Used.Remove(capability), ct);

    // ---- the file --------------------------------------------------------------------------------------------------------------------

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string NewKey()
        => KeyStart + Convert.ToBase64String(RandomNumberGenerator.GetBytes(30)).Replace('+', 'x').Replace('/', 'y').TrimEnd('=');

    private string Unprotect(string value)
    {
        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            // The keys of Data Protection were lost: the value has to be entered again.
            return string.Empty;
        }
    }

    private async Task<StoreFile> ReadAsync(CancellationToken ct)
    {
        if (_path is null)
        {
            return new StoreFile();
        }

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

    private async Task<T> ChangeAsync<T>(Func<StoreFile, T> change, CancellationToken ct)
    {
        if (_path is null)
        {
            throw new InvalidOperationException("MatPlatform keeps no bindings: the app named no DataDirectory (MatPlatformOptions).");
        }

        await _lock.WaitAsync(ct);
        try
        {
            var file = _cache ?? await LoadAsync(ct);
            var result = change(file);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(file, Json), ct);
            File.Move(temp, _path, overwrite: true);
            _cache = file;
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<StoreFile> LoadAsync(CancellationToken ct)
    {
        if (_path is null || !File.Exists(_path))
        {
            return new StoreFile();
        }

        // A file that cannot be read is an error, not an empty store: overwriting it would lose every binding.
        return JsonSerializer.Deserialize<StoreFile>(await File.ReadAllTextAsync(_path, ct), Json)
            ?? throw new InvalidDataException($"{_path} is empty.");
    }

    private sealed class StoreFile
    {
        public List<MatProvidedBinding> Provided { get; set; } = [];

        public Dictionary<string, Dictionary<string, string>> Used { get; set; } = new(StringComparer.Ordinal);
    }
}
