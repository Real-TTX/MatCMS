namespace MatCMS.Cloud.Models;

/// <summary>Key/value configuration row - same pattern as MatCMS's SiteSettings. Keys are centralised
/// in <see cref="Services.SettingKeys"/>; each settings form saves only its own keys.</summary>
public class CloudSetting
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}
