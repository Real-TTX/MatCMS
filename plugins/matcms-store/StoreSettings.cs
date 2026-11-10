namespace MatCMS.Plugin.MatcmsStore;

/// <summary>The store's settings (Admin → MatCMS Store → Einstellungen).</summary>
internal static class StoreSettings
{
    public const string Title = "title", Intro = "intro", Registration = "registration", RequireTwoFactor = "requireTwoFactor", NotifyEmail = "notifyEmail",
        Terms = "terms", ReservedKeys = "reservedKeys", SigningKeys = "signingKeys", MaxPackageMb = "maxPackageMb";

    /// <summary>The store key of MATTHIX (tools/StoreSign keygen, 2026-11-10). The same as in MatCMS.Shared/StoreKeys.</summary>
    public const string DefaultSigningKeys =
        "0b25964caefce574=MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIyUpFf6y4xRjx9CCR/iz8QKx1qB9B1x74chmIvusKfStQtQTG8LBFiEGUY3y+gkpcw4X5PmmaJ1RCwGoAk2vcg==";

    /// <summary>Keys only a VERIFIED publisher may claim: the built-in plugins and everything MATTHIX publishes or has published elsewhere.</summary>
    public const string DefaultReserved =
        "blog\nshop\nbot\nforum\nverein\nmoments\nbooking\nleserstimmen\ncookie-consent\ncoverage-map\nstore-einblick\nstore-einblick-native\nmatmon-live\nmatcms-store";

    /// <summary>The largest package the store takes (MB) — the upload route allows a little more for the form around it.</summary>
    public const int MaxPackageLimit = 95;

    public static void Declare(ISettingsBuilder s)
    {
        s.Add(new SettingSpec(Title, "store.set.title") { Default = "MatCMS Store", Required = true, Group = "store.set.groupGeneral" });
        s.TextArea(Intro, "store.set.intro", "Plugins für MatCMS – geprüft und signiert. Installieren in der eigenen Website unter Plugins → „Weiter durchsuchen …“.");
        s.Add(new SettingSpec(Registration, "store.set.registration", SettingKind.Bool) { Default = "true", Help = "store.set.registrationHelp", Group = "store.set.groupPublishers" });
        s.Bool(RequireTwoFactor, "store.set.twoFactor", true, "store.set.twoFactorHelp");
        s.TextArea(Terms, "store.set.terms", "", "store.set.termsHelp");
        s.TextArea(ReservedKeys, "store.set.reserved", DefaultReserved, "store.set.reservedHelp");
        s.Add(new SettingSpec(NotifyEmail, "store.set.notify") { Default = "", Help = "store.set.notifyHelp", Group = "store.set.groupReview" });
        s.TextArea(SigningKeys, "store.set.keys", DefaultSigningKeys, "store.set.keysHelp");
        s.Number(MaxPackageMb, "store.set.maxPackage", 90, 1, MaxPackageLimit);
    }

    public static string StoreTitle(IPluginContext ctx) => ctx.Settings.Get(Title) is { Length: > 0 } t ? t : "MatCMS Store";

    public static bool IsReserved(IPluginContext ctx, string key) =>
        (ctx.Settings.Get(ReservedKeys) ?? "").Split(['\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

    public static long MaxPackageBytes(IPluginContext ctx) => Math.Clamp(ctx.Settings.GetInt(MaxPackageMb, 90), 1, MaxPackageLimit) * 1024L * 1024L;
}

/// <summary>Every address of the store, in one place.</summary>
internal static class StoreUrls
{
    public const string Home = "/store", Account = "/store/konto", Upload = "/store/konto/hochladen", Register = "/store/registrieren",
        Catalog = "/store/api/catalog", Admin = "/admin/p/matcms-store/";

    /// <summary>Item keys that would collide with the store's own addresses under <c>/store/{key}</c>.</summary>
    public static readonly string[] Taken = ["api", "konto", "registrieren", "herausgeber", "suche"];

    public static string Item(string key) => Home + "/" + Uri.EscapeDataString(key);
    public static string Publisher(string slug) => Home + "/herausgeber/" + Uri.EscapeDataString(slug);
    public static string AccountItem(string key) => Account + "/eintrag/" + Uri.EscapeDataString(key);
    public static string Bundle(string key, string version) => "/store/api/plugins/" + Uri.EscapeDataString(key) + "/" + Uri.EscapeDataString(version) + "/bundle";
    public static string Image(string key, long id) => "/store/api/plugins/" + Uri.EscapeDataString(key) + "/images/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string ItemApi(string key) => "/store/api/plugins/" + Uri.EscapeDataString(key);
}
