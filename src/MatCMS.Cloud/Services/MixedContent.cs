namespace MatCMS.Cloud.Services;

/// <summary>
/// Whether an embedded address would be refused by the browser as mixed content.
///
/// <para>A page delivered over https may not frame an http one. The browser does not report this
/// anywhere the operator can see — it simply leaves the frame blank, which reads as "the site is
/// down" and sends people looking for a fault that is not there.</para>
///
/// <para>Two different causes end up here, and only one of them is the site's. Usually the address is
/// http because the instance sits behind a TLS-terminating proxy and never learned it — the fix is
/// forwarded headers (see both <c>Program.cs</c>) or a canonical URL on the site. Sometimes the site
/// really is http-only, and then there is nothing to fix and nothing to frame either; a link that
/// opens in its own tab is the honest offer.</para>
/// </summary>
public static class MixedContent
{
    /// <param name="pageIsHttps">Whether the CLOUD's own page is being served over https.</param>
    public static bool IsBlocked(bool pageIsHttps, string? url) =>
        pageIsHttps
        && !string.IsNullOrWhiteSpace(url)
        && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
}
