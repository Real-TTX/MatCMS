using System.Reflection;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatPlatform;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Platform;

/// <summary>
/// MatCMS.Cloud as an app of the platform contract (MatDesign docs/PLATFORM.md, contract 2) on MatPlatform (the copy in <c>MatPlatform/</c>):
/// how a shell such as MatOS shows it (a few tiles, links into the back office), what it provides (<see cref="CmsCloud"/>: a MatCMS site gets a
/// key and connects itself, <see cref="BindingRegistration"/>) and what it consumes (the sign-in through MatIdentity and the mail through
/// MatSMTP, both set by their variables; the cloud works without them as before). The cloud's own settings stay on its own pages: <c>/api</c>
/// has no form.
/// </summary>
public sealed class CloudPlatform(AppDbContext db, ReleaseWatcher releases) : IMatPlatformState, IMatPlatformHealth
{
    /// <summary>The id of the cloud in the platform (and its default client id at MatIdentity). The same as its catalog id in MatOS.</summary>
    public const string AppId = "matcmscloud";

    /// <summary>
    /// The capability the cloud provides: <b>cms.cloud</b> (version 1), a key capability. A MatCMS site that consumes it gets
    /// <c>MAT_CMS_CLOUD_URL</c>, <c>MAT_CMS_CLOUD_INTERNAL_URL</c> and <c>MAT_CMS_CLOUD_KEY</c> and enrols itself with the key
    /// (<c>POST /api/provides/cms.cloud/register</c>) into the default profile, as it would with that profile's join code.
    /// </summary>
    public const string CmsCloud = "cms.cloud";

    /// <summary>The accent of MatCMS (MatDesign AppRegistry: the cloud shares MatCMS's admin shell).</summary>
    public const string Accent = "#2563eb";

    public static string Version
    {
        get
        {
            var info = typeof(CloudPlatform).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }

    public static void Describe(MatPlatformBuilder o)
    {
        var T = o.T;
        o.App(AppId, "MatCMS Cloud", T("Steuerzentrale für MatCMS-Websites: Versionen, Benachrichtigungen, Profile und Updates",
            "Control plane for MatCMS websites: versions, notifications, profiles and updates"), Version, "/img/favicon.svg", Accent, "/admin");

        o.Provides(CmsCloud, label: T("Verbindet MatCMS-Websites", "Connects MatCMS websites"));
        o.Consumes(MatCapabilities.IdentityOidc, optional: true);
        o.Consumes(MatCapabilities.MailSmtp, optional: true, parameters: new Dictionary<string, string> { ["sender"] = "cloud" });

        // MatIdentity decides who may use the cloud and, in its mode "Admin", with which role (an operator sees only the sites given to it).
        o.Role(IdentityAccounts.RoleAdmin, T("Administrator", "Administrator"));
        o.Role(IdentityAccounts.RoleOperator, T("Operator", "Operator"));

        o.Launcher("cloud", "MatCMS Cloud", "/img/favicon.svg", "/admin", badge: "updates");
        o.QuickAction("cloud", T("Websites", "Websites"), "globe", url: "/admin/instances");
        o.QuickAction("cloud", T("Profile", "Profiles"), "layers", url: "/admin/profiles");
        o.Widget(new MatWidget("fleet", T("Websites", "Websites"), MatWidgetKinds.Tiles, "medium")
        {
            Items = ["instances", "online", "updates", "pending"],
            Icon = "globe",
            Url = "/admin/instances",
            Audience = MatAudience.Admin,
        });

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("instances", "online", "updates", "pending", "latest"),
            MatSection.Actions("openAdmin", "openInstances", "openSettings"));

        o.Tile("instances", T("Websites", "Websites"), icon: "globe");
        o.Tile("online", T("Online", "Online"), icon: "check");
        o.Tile(new MatStateItem("updates", T("Update verfügbar", "Update available"), "number") { Icon = "refresh", WarnAbove = 0 });
        o.Tile(new MatStateItem("pending", T("Warten auf Freigabe", "Waiting for approval"), "number") { Icon = "clock", WarnAbove = 0 });
        o.Tile("latest", T("Neueste MatCMS-Version", "Latest MatCMS version"), "text", icon: "tag");

        o.LinkAction("openAdmin", T("Cloud öffnen", "Open the cloud"), "external", "/admin");
        o.LinkAction("openInstances", T("Websites", "Websites"), "globe", "/admin/instances");
        o.LinkAction("openSettings", T("Einstellungen", "Settings"), "settings", "/admin/settings");
    }

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var all = await db.Instances.AsNoTracking().ToListAsync(ct);
        var approved = all.Where(i => i.Status == InstanceStatus.Approved).ToList();
        var latest = releases.LatestVersion;
        return new Dictionary<string, MatStateValue>
        {
            ["instances"] = new(approved.Count),
            ["online"] = new(approved.Count(InstanceService.IsOnline)),
            ["updates"] = new(approved.Count(i => releases.IsUpdateAvailableFor(i.Version))),
            ["pending"] = new(all.Count(i => i.Status == InstanceStatus.Pending)),
            ["latest"] = new(latest ?? "–"),
        };
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
        => [new MatHealthCheck("database", await db.Database.CanConnectAsync(ct) ? "ok" : "down")];
}
