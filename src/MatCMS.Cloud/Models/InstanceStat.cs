namespace MatCMS.Cloud.Models;

/// <summary>One daily visitor-statistics counter of an instance, as the site sent it on its heartbeat
/// (<see cref="MatCMS.Shared.HeartbeatRequest.Stats"/>): <c>Day × Kind × Key → Count</c>, the same shape the site
/// stores. A day is always replaced as a whole, never added to — the site sends the day as it has it NOW, so a
/// repeat is harmless. Goes with the instance (cascade).</summary>
public class InstanceStat
{
    public int Id { get; set; }
    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }
    /// <summary>UTC day, <c>yyyy-MM-dd</c>.</summary>
    public string Day { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public long Count { get; set; }
}
