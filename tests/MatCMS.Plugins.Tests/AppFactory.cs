using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The whole application, started in memory over a throw-away folder: its own database, its own keys, its own uploads — never a
/// developer's <c>appdata</c>. For the checks that only mean something through HTTP: routing, the admin layout, antiforgery, the
/// pages a visitor sees.
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    /// <summary>The content root of this instance (holds <c>appdata/</c> and <c>Resources/</c>).</summary>
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "matcms-app-" + Guid.NewGuid().ToString("N"));

    /// <summary>Services to replace before the application starts (a fake AI in place of the relay). Set it before the first request.</summary>
    public Action<IServiceCollection>? ExtraServices { get; init; }

    public AppFactory()
    {
        Directory.CreateDirectory(Root);
        // The localiser reads Resources/<culture>.json from the content root.
        var from = Path.Combine(AppContext.BaseDirectory, "Resources");
        Directory.CreateDirectory(Path.Combine(Root, "Resources"));
        foreach (var f in Directory.GetFiles(from, "*.json")) File.Copy(f, Path.Combine(Root, "Resources", Path.GetFileName(f)));
        Directory.CreateDirectory(Path.Combine(Root, "wwwroot"));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Root);
        builder.UseSetting("ConnectionStrings:Default", "Data Source=" + Path.Combine(Root, "appdata", "matcms.db"));
        builder.UseEnvironment("Production");
        if (ExtraServices is { } extra) builder.ConfigureServices(extra);
    }

    /// <summary>A browser: keeps cookies, follows nothing on its own.</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>The antiforgery token of a rendered page.</summary>
    public static string? Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } t ? t : null;

    /// <summary>Signs in as the seeded administrator.</summary>
    public static async Task LoginAsync(HttpClient c)
    {
        var page = await c.GetStringAsync("/login");
        var res = await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "admin", ["Password"] = "admin", ["__RequestVerificationToken"] = Token(page) ?? ""
        }));
        if (res.StatusCode != HttpStatusCode.Redirect) throw new InvalidOperationException("login failed: " + res.StatusCode);
    }

    /// <summary>POSTs a form as a browser would.</summary>
    public static Task<HttpResponseMessage> PostAsync(HttpClient c, string url, Dictionary<string, string> form) =>
        c.PostAsync(url, new FormUrlEncodedContent(form));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch { /* a handle may linger on Windows; the folder is in %TEMP% */ }
    }
}
