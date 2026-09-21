namespace MatCMS.Cloud.Models;

/// <summary>
/// A registered OAuth client for the Authorization Code flow — a native "Sign in with your MatCMS Cloud"
/// integration such as a ChatGPT custom GPT action. Distinct from the Device Grant (browserless clients):
/// here the client has a redirect and a secret. On authorization the operator consents at
/// <c>/oauth/c/authorize</c>, the client exchanges the code at <c>/oauth/token</c> with its secret, and
/// receives a full-access operator API key (the same <see cref="ApiKey"/> shape, revocable in the admin).
/// <para>Stored the same way as every other handed-out secret: the client secret as <b>SHA-256 only</b>,
/// shown in the clear once at creation, with a short <see cref="SecretPrefix"/> for the list.</para>
/// </summary>
public class OAuthClient
{
    public int Id { get; set; }

    /// <summary>Admin label, e.g. "ChatGPT". Free text for the operator's overview.</summary>
    public string Name { get; set; } = "";

    /// <summary>The public client identifier (<c>mcc_…</c>), sent in the authorize request and the token
    /// exchange. Public — it is not a secret.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>SHA-256 of the client secret. The raw value is shown once and cannot be recovered.</summary>
    public string ClientSecretHash { get; set; } = "";

    /// <summary>Leading characters of the raw secret — enough to recognise it, never to authenticate with.</summary>
    public string SecretPrefix { get; set; } = "";

    /// <summary>Allowed redirect URIs, one per line. An authorize/token request whose <c>redirect_uri</c>
    /// is not exactly one of these is refused — the whole trust of the flow rests on this allowlist.</summary>
    public string RedirectUris { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Set when revoked; a revoked client fails authorization but stays in the list for the audit
    /// trail (a client that can mint an operator key is worth keeping a record of).</summary>
    public DateTime? RevokedAt { get; set; }

    public bool Revoked => RevokedAt is not null;
}
