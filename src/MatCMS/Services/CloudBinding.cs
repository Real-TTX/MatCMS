namespace MatCMS.Services;

/// <summary>
/// The binding of the capability <c>cms.cloud</c> (MatDesign docs/PLATFORM.md "Bindings", a key capability): a broker such as MatOS bound
/// this site to a MatCMS.Cloud and passed <c>MAT_CMS_CLOUD_URL</c> (the cloud's public address), <c>MAT_CMS_CLOUD_INTERNAL_URL</c> (where this
/// server reaches it, on the shared Docker network) and <c>MAT_CMS_CLOUD_KEY</c> (the key the cloud made for this site). The site then joins
/// the cloud by itself (<see cref="CloudService.EnsureBindingAsync"/>). Null without the key or an address: no binding, nothing changes.
/// </summary>
public sealed record CloudBinding(string? PublicUrl, string ServerUrl, string Key)
{
    public const string UrlVariable = "MAT_CMS_CLOUD_URL";
    public const string InternalUrlVariable = "MAT_CMS_CLOUD_INTERNAL_URL";
    public const string KeyVariable = "MAT_CMS_CLOUD_KEY";

    /// <summary>Where a bound site joins: the cloud's endpoint of <c>cms.cloud</c>, the key as the bearer token.</summary>
    public const string RegisterPath = "/api/provides/cms.cloud/register";

    public static CloudBinding? From(IConfiguration configuration)
    {
        var key = Clean(configuration[KeyVariable]);
        var url = Clean(configuration[UrlVariable]);
        var server = Clean(configuration[InternalUrlVariable]) ?? url;
        return key is null || server is null ? null : new CloudBinding(url, server, key);
    }

    /// <summary>True when <paramref name="url"/> (the cloud a link points at) is this binding's cloud, by either address.</summary>
    public bool Names(string? url)
    {
        var u = (url ?? "").Trim().TrimEnd('/');
        return u.Length > 0 && (string.Equals(u, ServerUrl, StringComparison.OrdinalIgnoreCase) || string.Equals(u, PublicUrl, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Clean(string? value)
    {
        var v = value?.Trim().TrimEnd('/');
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
