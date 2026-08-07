using Microsoft.AspNetCore.DataProtection;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Encrypts the secrets a profile or the store rolls out — today the SMTP password, tomorrow
/// whatever else gets marked <c>IsSecret</c>.
/// <para>Why this exists: these values sit in one place for ALL profiles and get handed to every
/// instance. Anyone with the database file or a backup of it would otherwise hold the mail
/// credentials of every site. The key ring is the same one that protects the auth cookies and lives
/// on the appdata volume (<c>appdata/keys</c>), so a stolen database on its own is not enough.</para>
/// <para>Consequence worth knowing: losing the key ring makes the stored secrets unreadable. They
/// then come back as empty and have to be entered again — which is the correct failure, because the
/// alternative is storing them in a way that survives theft.</para>
/// </summary>
public class SecretProtector
{
    // Marks a value as produced by this class. Without it there is no way to tell ciphertext from a
    // password that happens to look like base64 — and a wrong guess either double-encrypts or hands
    // out ciphertext as if it were the password.
    private const string Prefix = "enc:v1:";

    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("MatCMS.Cloud.Secrets");

    /// <summary>Encrypts a value. An empty value stays empty — there is nothing to hide.</summary>
    public string? Protect(string? raw) =>
        string.IsNullOrEmpty(raw) ? raw : Prefix + _protector.Protect(raw);

    /// <summary>
    /// Decrypts a stored value. Anything without the marker is returned unchanged, which makes this
    /// safe to run over rows written before encryption existed — they keep working and get encrypted
    /// the next time they are saved.
    /// </summary>
    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;

        try { return _protector.Unprotect(stored[Prefix.Length..]); }
        // A key ring that was thrown away, or a value from another installation: report it as empty
        // rather than throwing, so one unreadable secret cannot break a whole configuration sync.
        catch { return ""; }
    }

    /// <summary>True when the value is already encrypted — used to avoid encrypting twice.</summary>
    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);
}
