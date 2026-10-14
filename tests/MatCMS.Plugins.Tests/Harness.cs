using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The host's plugin machinery without HTTP: the real registry, the real runner (Roslyn for scripts), the real
/// native loader, an in-memory database and a throw-away content root. Nothing here touches a developer's
/// <c>appdata</c>.
/// </summary>
internal sealed class Harness : IDisposable
{
    public PluginRegistry Registry { get; } = new();
    public NativePluginHost Native { get; } = new(NullLogger<NativePluginHost>.Instance);
    public AppDbContext Db { get; }
    public PluginRunner Runner { get; }
    public PluginDataService Data { get; } = new();
    /// <summary>What a handler can reach: the database of this harness and an ephemeral key ring, like a request scope.</summary>
    public IServiceProvider Services { get; }
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "matcms-plugin-tests-" + Guid.NewGuid().ToString("N"));
    public IWebHostEnvironment Env { get; }

    private readonly SqliteConnection _connection;

    /// <param name="builtIns">Start the plugins that ship with the CMS too. Off by default: most tests are about the plugin machinery and want the registry to hold only what they put there.</param>
    public Harness(bool builtIns = false)
    {
        Directory.CreateDirectory(Root);
        Env = new FakeEnvironment(Root);
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        Db.Database.EnsureCreated();
        var services = new ServiceCollection();
        services.AddSingleton(Db);
        services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        Services = services.BuildServiceProvider();
        Runner = new PluginRunner(Db, Registry, Services, NullLogger<PluginRunner>.Instance, Native, Env, Data) { BuiltIns = builtIns };
    }

    /// <summary>Adds an enabled script plugin.</summary>
    public MatCMS.Models.Plugin AddScript(string key, string code, string version = "1")
    {
        var p = new MatCMS.Models.Plugin { Key = key, Name = key, Version = version, Code = code, Kind = PluginKind.Script, Enabled = true };
        Db.Plugins.Add(p);
        Db.SaveChanges();
        return p;
    }

    /// <summary>The operator's switch for compiled plugins (Plugins → "Native Plugins erlauben").</summary>
    public void AllowNative(bool allow)
    {
        var row = Db.SiteSettings.FirstOrDefault(s => s.Key == SettingKeys.AllowNativePlugins);
        if (row is null) Db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.AllowNativePlugins, Value = allow ? "1" : "0" });
        else row.Value = allow ? "1" : "0";
        Db.SaveChanges();
    }

    /// <summary>Adds an enabled native plugin from the reference fixture, copied to where an instance keeps it.
    /// Native plugins are allowed by default here — most tests are about what they do once they may run.</summary>
    public MatCMS.Models.Plugin AddNative(string key, string version = "1", bool copyFiles = true, bool allowNative = true)
    {
        if (allowNative) AllowNative(true);
        var src = StoragePaths.PluginSrc(Env, key, version);
        string sha = "";
        if (copyFiles)
        {
            Directory.CreateDirectory(src);
            var from = Path.Combine(AppContext.BaseDirectory, "fixtures", "refplugin");
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(src, Path.GetFileName(f)), overwrite: true);
            sha = NativePluginHost.HashOf(Path.Combine(src, "RefPlugin.dll"));
        }
        var p = new MatCMS.Models.Plugin
        {
            Key = key, Name = key, Version = version, Kind = PluginKind.Native, Entry = "RefPlugin.dll", Sha256 = sha,
            Api = typeof(IPlugin).Assembly.GetName().Version!.Major, Enabled = true
        };
        Db.Plugins.Add(p);
        Db.SaveChanges();
        return p;
    }

    public static string FixtureScript() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ref-plugin.csx"));

    /// <summary>A request context for calling a handler directly.</summary>
    public PluginRequestContext Context(string key, RequestData request, string locale = "de")
        => new(new PluginInfo(key, key, "1", "", 2), request, new PluginUser(), locale, null, "", Services, Registry);

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch { /* a loaded DLL may still hold a file on Windows */ }
    }

    private sealed class FakeEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MatCMS.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
