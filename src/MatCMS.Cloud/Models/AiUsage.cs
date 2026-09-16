namespace MatCMS.Cloud.Models;

/// <summary>
/// Per-instance, per-month AI token consumption — the ledger the relay checks against the profile's
/// <see cref="Profile.AiMonthlyTokenBudget"/> and adds to after each call. One row per (instance,
/// period). Cascades with the instance.
/// </summary>
public class AiUsage
{
    public int Id { get; set; }

    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }

    /// <summary>Billing period as "yyyy-MM" (UTC), so a month's usage sums to one row.</summary>
    public string Period { get; set; } = "";

    /// <summary>Prompt + completion tokens spent this period.</summary>
    public int Tokens { get; set; }

    /// <summary>How many relayed calls this period (for the operator's overview).</summary>
    public int Calls { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
