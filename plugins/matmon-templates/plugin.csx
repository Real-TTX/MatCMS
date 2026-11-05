// Matmon template store for matmon.eu: members submit Matmon template files (Matmon → Monitoring → Templates → Export), the operator
// reviews them in the back office (a mail for every submission), approved ones are listed under /templates for everybody to download.
// file.csx reads and cleans an uploaded file, store.csx holds the data, public.csx the site's pages, admin.csx the back office,
// mail.csx the three mails, text.csx the wording, css.csx the look.
#load "text.csx"
#load "css.csx"
#load "file.csx"
#load "store.csx"
#load "mail.csx"
#load "public.csx"
#load "admin.csx"

Plugin.Strings("en", MtText.En);
Plugin.Strings("de", MtText.De);
MtStore.Declare(Plugin);
MtMail.Declare(Plugin);
MtPublic.Configure(Plugin);
MtAdmin.Configure(Plugin);

// An account is deleted: unpublished files go, published templates stay but no longer name the person.
Plugin.On(HostEvents.MemberErase, async (ctx, payload, ct) =>
{
    var id = payload?["id"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
    await MtStore.EraseMemberAsync(ctx, id, "", ct);
    return null;
});
