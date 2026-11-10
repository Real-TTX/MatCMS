using System.Security.Cryptography;
using System.Text;

namespace MatCMS.Shared;

/// <summary>
/// The signature of a package from the MatCMS Store (the store server on matcms.de, <c>docs/official-store.md</c>). The store distributes code —
/// native plugins run with every right of the process — so whatever a cloud or a site installs FROM it must carry a signature made with a key
/// the store server never holds: the private key lives with the store's operator, the public keys ship in the images (<see cref="StoreKeys"/>).
/// A cloud that passes a package on, a proxy, a cache — none of them can change a byte without the check failing.
/// <para>What is signed is a short text, not the zip: <c>matcms-store-v1</c>, the kind, the key, the version and the SHA-256 of the package, one per
/// line. So the signer needs only those four values (the store's review page shows them), and the check needs only the package and its catalogue
/// entry. ECDSA P-256 over SHA-256, signature in IEEE P1363 form (64 bytes, what <see cref="ECDsa.SignData(byte[], HashAlgorithmName)"/> gives),
/// base64.</para>
/// <para>Dependency-free like the rest of this library: the store plugin keeps its own copy of <see cref="Text"/> (a plugin is written against the
/// plugin API only), and an end-to-end test makes both agree.</para>
/// </summary>
public static class StoreSignature
{
    /// <summary>The first line of every signed text; a new scheme gets a new name, never a changed meaning.</summary>
    public const string Scheme = "matcms-store-v1";

    /// <summary>The text that is signed. Kind and key are compared lower-case, the hash as lower-case hex.</summary>
    public static string Text(string kind, string key, string version, string sha256Hex) =>
        string.Join('\n', Scheme, (kind ?? "").Trim().ToLowerInvariant(), (key ?? "").Trim().ToLowerInvariant(), (version ?? "").Trim(),
            (sha256Hex ?? "").Trim().ToLowerInvariant());

    /// <summary>SHA-256 of a package as lower-case hex.</summary>
    public static string Sha256Hex(byte[] package) => Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();

    /// <summary>SHA-256 of a package read from a stream, as lower-case hex.</summary>
    public static string Sha256Hex(Stream package) => Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();

    /// <summary>The id of a public key: the first 16 hex digits of the SHA-256 of its SubjectPublicKeyInfo.</summary>
    public static string KeyId(byte[] subjectPublicKeyInfo) => Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo))[..16].ToLowerInvariant();

    /// <summary>Signs a text with a private key. For the signing tool and tests — never on a server.</summary>
    public static string Sign(string text, ECDsa privateKey) =>
        Convert.ToBase64String(privateKey.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256));

    /// <summary>
    /// True when <paramref name="signature"/> is a valid signature of <paramref name="text"/> by the key <paramref name="keyId"/> — one of
    /// <paramref name="keys"/> (key id → base64 SubjectPublicKeyInfo), by default the keys that ship with this version (<see cref="StoreKeys.Known"/>).
    /// Anything malformed is simply "not valid"; this never throws.
    /// </summary>
    public static bool Verify(string text, string? signature, string? keyId, IReadOnlyDictionary<string, string>? keys = null)
    {
        keys ??= StoreKeys.Known;
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(keyId) || !keys.TryGetValue(keyId.Trim().ToLowerInvariant(), out var spki))
            return false;
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            return ec.VerifyData(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Checks a package against its catalogue entry: the hash must match and the signature must be valid for exactly these values.</summary>
    public static bool VerifyPackage(byte[] package, string kind, string key, string version, string? sha256Hex, string? signature, string? keyId,
        IReadOnlyDictionary<string, string>? keys = null)
    {
        var actual = Sha256Hex(package);
        if (!string.IsNullOrEmpty(sha256Hex) && !string.Equals(actual, sha256Hex.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return Verify(Text(kind, key, version, actual), signature, keyId, keys);
    }
}

/// <summary>
/// The public keys of the MatCMS Store (key id → base64 SubjectPublicKeyInfo of an ECDSA P-256 key). A new key — the old one lost or retired —
/// is a new entry here and therefore a new version of MatCMS and of the cloud; an old entry stays as long as packages signed with it are in use.
/// The private key is NOT in this repository: it lives with the store's operator (<c>tools/StoreSign</c>, <c>%APPDATA%\MatCMS\store-signing</c>).
/// </summary>
public static class StoreKeys
{
    /// <summary>The keys this version trusts.</summary>
    public static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>
    {
        // KEYS-BEGIN (tools/StoreSign keygen prints the line to add)
        // Store key of MATTHIX, created 2026-11-10 (private key with the store operator).
        ["0b25964caefce574"] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIyUpFf6y4xRjx9CCR/iz8QKx1qB9B1x74chmIvusKfStQtQTG8LBFiEGUY3y+gkpcw4X5PmmaJ1RCwGoAk2vcg==",
        // KEYS-END
    };
}
