namespace MatCMS.Models;

/// <summary>
/// A key another PROGRAM uses to call the site's API (<c>/api/v1/{plugin}/…</c>, the plugins' <c>IPluginBuilder.Api</c> routes): an ERP that fills the shop,
/// a script that reads orders. Not a person's login — it has no password, no cookie and no admin rights; what it may do is exactly <see cref="Rights"/>.
/// <para>Stored as SHA-256 only and shown once when it is made; <see cref="Prefix"/> is the start of the key, kept so the list can say which one is
/// which. Revoking keeps the row (an audit trail of what could write to the shop), deleting removes it.</para>
/// </summary>
public class SiteApiKey
{
    public int Id { get; set; }

    /// <summary>What the operator calls it ("ERP Lager", "Warenwirtschaft").</summary>
    public string Name { get; set; } = "";

    /// <summary>The first characters of the key (<c>msk_ab12cd</c>), for the list.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>SHA-256 of the whole key, lower-case hex.</summary>
    public string Hash { get; set; } = "";

    /// <summary>
    /// The rights, comma-separated, each <c>{plugin}:{read|write|personal}</c> (<c>shop:write,shop:personal</c>). <c>write</c> includes reading;
    /// <c>personal</c> (orders, customers) is never implied by another.
    /// </summary>
    public string Rights { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public long Uses { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public bool IsUsable(DateTime now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}
