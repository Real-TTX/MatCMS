namespace MatCMS.Cloud.Models;

/// <summary>
/// A pending OAuth 2.0 Device Authorization Grant (RFC 8628). A browserless client (a CLI, an agent,
/// or ChatGPT) starts the flow at <c>/oauth/device_authorization</c>, shows the human-typeable
/// <see cref="UserCode"/>, and polls <c>/oauth/token</c> with the secret <see cref="DeviceCodeHash">device code</see>
/// while the operator confirms it at <c>/device</c>.
/// <para>Persisted (not in <c>IMemoryCache</c> like the short-lived SSO codes in <see cref="Services.OAuthCodes"/>):
/// a device poll window is minutes long, and a cloud restart mid-authorisation must not silently drop a
/// pending grant. The device code is stored as <b>SHA-256 only</b> — the same "a secret the cloud hands
/// out is kept as its hash" rule as instance tokens and API keys — so a leaked database row cannot be
/// polled with. On approval NOTHING secret is stored: the operator API key is minted at the moment the
/// client redeems the code (<see cref="Services.DeviceCodes.RedeemAsync"/>) and handed back exactly once.</para>
/// </summary>
public class DeviceCode
{
    public int Id { get; set; }

    /// <summary>SHA-256 of the raw <c>device_code</c> the client polls with. The raw value is returned only
    /// to the client that started the flow and never stored.</summary>
    public string DeviceCodeHash { get; set; } = "";

    /// <summary>The short, human-typeable code shown to the operator (e.g. <c>ABCD-EFGH</c>), entered at
    /// <c>/device</c>. Drawn from an alphabet without 0/O/1/I for the same reason as the profile join code.</summary>
    public string UserCode { get; set; } = "";

    /// <summary>A label for the client that started the flow (its <c>client_id</c>, e.g. "chatgpt" or a CLI
    /// name), used only to name the minted key so the operator can recognise it in the API-key list.</summary>
    public string ClientLabel { get; set; } = "";

    public DeviceCodeStatus Status { get; set; } = DeviceCodeStatus.Pending;

    /// <summary>The operator who approved (or denied) the request at <c>/device</c>.</summary>
    public int? ApprovedByUserId { get; set; }

    /// <summary>The operator API key minted on redemption — the audit link between the device grant and the
    /// key it produced (the key itself lives in <see cref="ApiKey"/> and is revocable in the admin).</summary>
    public int? MintedApiKeyId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    /// <summary>Last poll time — used to enforce the RFC 8628 <c>interval</c> (a faster poll gets
    /// <c>slow_down</c>).</summary>
    public DateTime? LastPolledAt { get; set; }

    public int IntervalSeconds { get; set; } = 5;
}

/// <summary>Pending → the operator has not decided; Approved → confirmed, awaiting the client's next poll;
/// Denied → the operator declined; Consumed → the key was minted and handed to the client (single use).</summary>
public enum DeviceCodeStatus
{
    Pending = 0,
    Approved = 1,
    Denied = 2,
    Consumed = 3,
}
