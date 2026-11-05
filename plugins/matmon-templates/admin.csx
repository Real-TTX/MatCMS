// The back office of the template store on this site: its settings (which cloud, the store API key, the texts of the pages) and one
// page that says where the review happens. The store and its review queue live on Matmon Cloud (Admin > Template store) - there is
// no second queue here, so a template is approved in exactly one place.
using System;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

public static class MtAdmin
{
    public const string Root = "/admin/p/matmon-templates/";

    public static void Configure(IPluginBuilder b)
    {
        b.Settings
            .Text("cloudUrl", "mt.set.cloudUrl", MtCloud.DefaultUrl, "mt.set.cloudUrlHelp")
            .Secret("apiKey", "mt.set.apiKey", "mt.set.apiKeyHelp")
            .TextArea("intro", "mt.set.intro", "Ready-made sensor templates from the Matmon community. Add them in Matmon under Templates → Store, or download a file and import it.", "mt.set.introHelp")
            .TextArea("terms", "mt.set.terms", "I created this template (or may share it) and agree that it is published here free of charge for everybody to use.", "mt.set.termsHelp");

        b.Admin.Section("mt.admin.title", "ti-template", AdminGroup.Content, 60)
            .Page("overview", "mt.a.overview", OverviewAsync);

        b.Admin.Attention((ctx, ct) => Task.FromResult<System.Collections.Generic.IEnumerable<AttentionNote>>(MtCloud.HasKey(ctx)
            ? Array.Empty<AttentionNote>()
            : new[] { new AttentionNote(ctx.T("mt.admin.title"), ctx.T("mt.a.noKey"), Root + "_settings") { Tone = UiTone.Warning } }));
    }

    private static Task<PluginResult> OverviewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cloud = MtCloud.BaseUrl(ctx);
        return Task.FromResult(PluginResult.Page(Ui.Stack(
            MtCloud.HasKey(ctx) ? null : Ui.Alert(ctx.T("mt.a.noKey"), UiTone.Warning),
            Ui.Card(ctx.T("mt.a.whereTitle"),
                Ui.Text(ctx.T("mt.a.whereText", cloud)),
                Ui.Button(ctx.T("mt.a.openCloud"), cloud + "/admin/store", UiButtonKind.Primary),
                Ui.Button(ctx.T("mt.a.viewPublic"), MtPublic.Base, UiButtonKind.Ghost),
                Ui.Button(ctx.T("mt.a.settings"), Root + "_settings", UiButtonKind.Ghost))), ctx.T("mt.admin.title")));
    }
}
