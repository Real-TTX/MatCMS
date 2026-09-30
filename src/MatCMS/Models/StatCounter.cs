namespace MatCMS.Models;

/// <summary>One daily visitor-statistics counter: <c>Day × Kind × Key → Count</c> (Admin → Statistik). The kinds
/// and keys are <see cref="MatCMS.Shared.StatKinds"/>, shared with the cloud, which receives these rows as they are.
/// Written by <see cref="MatCMS.Services.StatsFlushService"/>; no single request and no visitor address is ever
/// stored — only counts.</summary>
public class StatCounter
{
    public int Id { get; set; }
    /// <summary>UTC day, <c>yyyy-MM-dd</c>.</summary>
    public string Day { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public long Count { get; set; }
}
