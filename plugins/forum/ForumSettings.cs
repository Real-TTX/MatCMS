namespace MatCMS.Plugin.Forum;

/// <summary>The forum's settings: declared here (the host draws the form under Forum → Einstellungen), read through the small helpers below.</summary>
internal static class ForumSettings
{
    public const string TitleKey = "title", Intro = "intro", Registration = "registration", ModRole = "modRole", ThreadsPerPage = "threadsPerPage",
        PostsPerPage = "postsPerPage", FloodSeconds = "floodSeconds", EditMinutes = "editMinutes", Signatures = "signatures", Images = "images",
        NotifyEmail = "notifyEmail", Subscriptions = "subscriptions", TimeZone = "timeZone", CustomCss = "customCss", Indexing = "indexing";

    public const string DefaultModRole = "forum-moderator";

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
        s.Add(new SettingSpec(TimeZone, "forum.set.timeZone") { Default = "Europe/Berlin", Help = "forum.set.timeZoneHelp", Group = "forum.set.groupLook" });
        s.TextArea(CustomCss, "forum.set.css", "", "forum.set.cssHelp");
    }

    public static string Title(IPluginContext ctx) => ctx.Settings.Get(TitleKey) is { Length: > 0 } t ? t : "Forum";
    public static bool RegistrationOpen(IPluginContext ctx) => ctx.Settings.GetBool(Registration);
    public static string Moderators(IPluginContext ctx) => ctx.Settings.Get(ModRole) is { Length: > 0 } r ? r.Trim() : DefaultModRole;
    public static int ThreadPage(IPluginContext ctx) => (int)Math.Clamp(ctx.Settings.GetInt(ThreadsPerPage, 25), 5, 100);
    public static int PostPage(IPluginContext ctx) => (int)Math.Clamp(ctx.Settings.GetInt(PostsPerPage, 15), 5, 100);
    public static int Flood(IPluginContext ctx) => (int)Math.Clamp(ctx.Settings.GetInt(FloodSeconds, 20), 0, 3600);
    public static int EditWindow(IPluginContext ctx) => (int)Math.Clamp(ctx.Settings.GetInt(EditMinutes, 0), 0, 100000);
}
