namespace MatCMS.Services;

public static class SettingKeys
{
    public const string SiteName = "SiteName";
    public const string LogoUrl = "LogoUrl";
    public const string TopBarLink1Text = "TopBarLink1Text";
    public const string TopBarLink1Url = "TopBarLink1Url";
    public const string TopBarLink2Text = "TopBarLink2Text";
    public const string TopBarLink2Url = "TopBarLink2Url";
    public const string FooterText = "FooterText";
    public const string ContactRecipient = "ContactRecipient";

    public static readonly string[] All =
    [
        SiteName, LogoUrl,
        TopBarLink1Text, TopBarLink1Url,
        TopBarLink2Text, TopBarLink2Url,
        FooterText, ContactRecipient
    ];
}
