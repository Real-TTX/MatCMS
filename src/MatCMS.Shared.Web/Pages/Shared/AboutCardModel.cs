namespace MatCMS.Shared.Web;

/// <summary>
/// The "Über …" card both back offices open from the version at the foot of the sidebar — MatDesign's About
/// card as Matmon has it: the logo, the version as a tag, the delivery channel as a pill, and a foot with the
/// product's website and the copyright. Shared so the CMS's and the cloud's look the same; what differs (logo,
/// name, wording) comes from the caller, since a shared view cannot use either application's <c>Localizer</c>.
/// </summary>
/// <param name="Product">The product's name ("MatCMS", "MatCMS Cloud") — the logo's text for screen readers.</param>
/// <param name="Logo">The light logo; <paramref name="LogoDark"/> is shown instead in a dark back office.</param>
/// <param name="LogoWidth">The logo's width at the card's height of 44 px (its viewBox scaled).</param>
/// <param name="Version">The running version as the build stamped it.</param>
/// <param name="ChannelLabels">The pill's words for release, nightly and a local build, in that order.</param>
/// <param name="Copyright">The foot's right-hand line, e.g. "© 2026 MatCMS · Alle Rechte vorbehalten".</param>
public sealed record AboutCard(
    string Product, string Logo, string LogoDark, int LogoWidth,
    string Version, string[] ChannelLabels,
    string WebsiteUrl, string WebsiteLabel, string Copyright)
{
    /// <summary>"release" (MAJOR.MINOR.BUILD-…, from release.yml), "nightly" (dev.yml) or "local" (anything else:
    /// the Dockerfile's default "local", a dotnet run).</summary>
    public static string Channel(string version) =>
        version.StartsWith("nightly", StringComparison.OrdinalIgnoreCase) ? "nightly"
        : version.Length > 0 && char.IsDigit(version[0]) && version != "1.0.0" ? "release"
        : "local";

    public string ChannelKey => Channel(Version);

    public string ChannelLabel => ChannelKey switch
    {
        "release" => ChannelLabels.ElementAtOrDefault(0) ?? "Release",
        "nightly" => ChannelLabels.ElementAtOrDefault(1) ?? "Nightly",
        _ => ChannelLabels.ElementAtOrDefault(2) ?? "Lokal",
    };

    /// <summary>A release is the good news, a nightly build the accent, a local build neither (MatUi's pill tones).</summary>
    public string PillClass => ChannelKey switch
    {
        "release" => "mat-pill mat-pill--ok",
        "nightly" => "mat-pill mat-pill--accent",
        _ => "mat-pill",
    };
}
