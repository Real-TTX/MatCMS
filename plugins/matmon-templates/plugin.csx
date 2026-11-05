// Matmon template store for matmon.eu. The store itself lives on Matmon Cloud (cloud.matmon.eu): there the operator reviews every
// submission, the review mails go out and every Matmon instance adds the published sets from Templates → Store. This site is its
// public face: /templates lists what the cloud publishes, members submit their Matmon template files (Matmon → Templates → Export)
// and see their submissions' state - all through the cloud's store API with the key in this plugin's settings.
// cloud.csx talks to the cloud, public.csx draws the site's pages, admin.csx the settings, text.csx the wording, css.csx the look.
#load "text.csx"
#load "css.csx"
#load "cloud.csx"
#load "public.csx"
#load "admin.csx"

Plugin.Strings("en", MtText.En);
Plugin.Strings("de", MtText.De);
MtPublic.Configure(Plugin);
MtAdmin.Configure(Plugin);

// An account is deleted here: the cloud drops what it sent that is not published and takes its name off what is.
Plugin.On(HostEvents.MemberErase, async (ctx, payload, ct) =>
{
    var id = payload?["id"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
    if (id > 0 && MtCloud.HasKey(ctx))
    {
        try { await MtCloud.EraseAuthorAsync(ctx, MtCloud.AuthorRef(ctx, id), ct); }
        catch (MtCloudException ex) { ctx.Log("Template store: erasing member " + id + " on the cloud failed: " + ex.Message); }
    }
    return null;
});
