namespace MatCMS.Plugin.Forum;

/// <summary>The forum's settings: declared here (the host draws the form under Forum → Einstellungen), read through the small helpers below.</summary>
internal static class ForumSettings
{
    public const string TitleKey = "title", Intro = "intro", Registration = "registration", ModRole = "modRole", ThreadsPerPage = "threadsPerPage",
        PostsPerPage = "postsPerPage", FloodSeconds = "floodSeconds", EditMinutes = "editMinutes", Signatures = "signatures", Images = "images",
        NotifyEmail = "notifyEmail", Subscriptions = "subscriptions", TimeZone = "timeZone", CustomCss = "customCss", Indexing = "indexing",
        DiscussionBoard = "discussionBoard";

    public const string DefaultModRole = "forum-moderator";

    private static readonly string[] Zones =
    [
        "Europe/Berlin", "Europe/Vienna", "Europe/Zurich", "Europe/Amsterdam", "Europe/Brussels", "Europe/Luxembourg", "Europe/Paris", "Europe/Rome",
        "Europe/Madrid", "Europe/Lisbon", "Europe/London", "Europe/Dublin", "Europe/Copenhagen", "Europe/Stockholm", "Europe/Oslo", "Europe/Helsinki",
        "Europe/Warsaw", "Europe/Prague", "Europe/Budapest", "Europe/Zagreb", "Europe/Ljubljana", "Europe/Bratislava", "Europe/Bucharest", "Europe/Sofia",
        "Europe/Athens", "Europe/Istanbul", "UTC", "America/New_York", "America/Chicago", "America/Denver", "America/Los_Angeles", "Asia/Tokyo", "Australia/Sydney"
    ];

    public static void Declare(ISettingsBuilder s)
    {
        s.Add(new SettingSpec(TitleKey, "forum.set.title") { Default = "Forum", Group = "forum.set.groupGeneral", Required = true });
        s.TextArea(Intro, "forum.set.intro", "", "forum.set.introHelp");
        s.Bool(Indexing, "forum.set.indexing", true, "forum.set.indexingHelp");
        s.Add(new SettingSpec(Registration, "forum.set.registration", SettingKind.Bool) { Default = "false", Help = "forum.set.registrationHelp", Group = "forum.set.groupMembers" });
        s.Text(ModRole, "forum.set.modRole", DefaultModRole, "forum.set.modRoleHelp");
        s.Bool(Signatures, "forum.set.signatures", true);
        s.Bool(Images, "forum.set.images", true, "forum.set.imagesHelp");
        s.Add(new SettingSpec(FloodSeconds, "forum.set.flood", SettingKind.Number) { Default = "20", Min = 0, Max = 3600, Help = "forum.set.floodHelp", Group = "forum.set.groupPosting" });
        s.Number(EditMinutes, "forum.set.edit", 0, 0, 100000, "forum.set.editHelp");
        s.Number(ThreadsPerPage, "forum.set.threadsPerPage", 25, 5, 100);
        s.Number(PostsPerPage, "forum.set.postsPerPage", 15, 5, 100);
        s.Add(new SettingSpec(Subscriptions, "forum.set.subscriptions", SettingKind.Bool) { Default = "true", Help = "forum.set.subscriptionsHelp", Group = "forum.set.groupMail" });
        s.Text(NotifyEmail, "forum.set.notify", "", "forum.set.notifyHelp");
        // a choice instead of free text; the list is wide enough for every site the forum runs on (a value outside it would not be offered)
        s.Add(new SettingSpec(TimeZone, "forum.set.timeZone", SettingKind.Choice)
        {
            Default = "Europe/Berlin", Group = "forum.set.groupLook",
            Options = Zones.Select(z => new FieldOption(z, z.Replace('_', ' '))).ToArray()
        });
        s.TextArea(CustomCss, "forum.set.css", "", "forum.set.cssHelp");
        s.Add(new SettingSpec(DiscussionBoard, "forum.set.discussionBoard") { Default = "", Help = "forum.set.discussionBoardHelp", Group = "forum.set.groupDiscussion" });
    }

    /// <summary>The address (slug) of the board that holds the discussions of the site's articles; empty = the feature is off.</summary>
    public static string DiscussionBoardSlug(IPluginContext ctx) => (ctx.Settings.Get(DiscussionBoard) ?? "").Trim().ToLowerInvariant();

    public static string Title(IPluginContext ctx) => ctx.Settings.Get(TitleKey) is { Length: > 0 } t ? t : "Forum";
    public static bool RegistrationOpen(IPluginContext ctx) => ctx.Settings.GetBool(Registration);
    public static string Moderators(IPluginContext ctx) => ctx.Settings.Get(ModRole) is { Length: > 0 } r ? r.Trim() : DefaultModRole;
    public static int ThreadPage(IPluginContext ctx) => (int)Math.Clamp(ctx.Settings.GetInt(ThreadsPerPage, 25), 5, 100);
    public static int PostPage(IPluginContext ctx) => (int)Math.Clamp(ctx.Settings.GetInt(PostsPerPage, 15), 5, 100);
    public static int Flood(IPluginContext ctx) => (int)Math.Clamp(ctx.Settings.GetInt(FloodSeconds, 20), 0, 3600);
    public static int EditWindow(IPluginContext ctx) => (int)Math.Clamp(ctx.Settings.GetInt(EditMinutes, 0), 0, 100000);
}
