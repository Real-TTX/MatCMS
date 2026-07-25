namespace MatCMS.Services;

public static class SettingKeys
{
    public const string SiteName = "SiteName";
    public const string LogoUrl = "LogoUrl";
    public const string FaviconUrl = "FaviconUrl";
    public const string TopBarLink1Text = "TopBarLink1Text";
    public const string TopBarLink1Url = "TopBarLink1Url";
    public const string TopBarLink2Text = "TopBarLink2Text";
    public const string TopBarLink2Url = "TopBarLink2Url";
    public const string FooterText = "FooterText";
    public const string ContactRecipient = "ContactRecipient";

    // Note: TopBarLink1/2 are intentionally NOT here — the top bar moved to the "toolbar" menu.
    // The constants remain for the one-time migration in DbSeeder.
    public static readonly string[] All =
    [
        SiteName, LogoUrl, FaviconUrl,
        FooterText, ContactRecipient
    ];
}
