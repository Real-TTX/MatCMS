using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// Behind the cloud's edge the Host header is the node's host address; the cookie handler's absolute redirect sent a signed-out
/// matcms.de/admin to matcms.cloud2.matthix.de/login (2026-11-20). The redirect must be relative, whatever Host the request carried.
/// </summary>
public class RelativeAuthRedirectTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public RelativeAuthRedirectTests(AppFactory app) => _app = app;

    [Theory]
    [InlineData("/admin", "/login?ReturnUrl=%2Fadmin")]
    [InlineData("/admin/pages", "/login?ReturnUrl=%2Fadmin%2Fpages")]
    public async Task A_signed_out_admin_request_is_sent_to_the_login_of_the_same_address(string path, string expected)
    {
        var browser = _app.Browser();
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Host = "site.node.example";   // what the edge leaves behind
        var res = await browser.SendAsync(req);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        Assert.False(res.Headers.Location!.IsAbsoluteUri, res.Headers.Location.ToString());
        Assert.Equal(expected, res.Headers.Location.OriginalString);
    }

    [Theory]
    [InlineData("https://node.example/login?ReturnUrl=%2Fadmin", "/login?ReturnUrl=%2Fadmin")]
    [InlineData("/anmelden?ReturnUrl=%2Fx", "/anmelden?ReturnUrl=%2Fx")]
    public void Only_path_and_query_are_kept(string from, string to) =>
        Assert.Equal(to, MatCMS.Services.RelativeAuthRedirects.ToRelative(from));
}
