// The three mails of the template store. Their wording lives in Admin → Mailvorlagen once the plugin has loaded (the texts here are only
// the starting point). A mail that cannot go out never stops a submission or a decision: it is written to the plugin's log instead.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

public static class MtMail
{
    public const string Submitted = "matmon-templates.submitted";
    public const string Approved = "matmon-templates.approved";
    public const string Rejected = "matmon-templates.rejected";

    public static void Declare(IPluginBuilder b)
    {
        b.MailTemplate(new MailTemplateSpec(Submitted, "Template-Store: neue Einreichung", "Geht an die Adresse aus den Einstellungen des Template-Stores, sobald jemand ein Template oder eine neue Version einreicht.",
            "Neues Template zur Prüfung: {{title}}",
            "Hallo,\n\n{{author}} ({{email}}) hat im Template-Store ein Template eingereicht:\n\n{{title}} {{version}}\n{{summary}}\n\nEnthält {{count}} Template(s): {{templates}}\nBeim Hochladen entfernt: {{removed}}\n\nPrüfen und freigeben:\n{{review_url}}\n")
        {
            Placeholders = new[]
            {
                new MailPlaceholder("title", "Titel"), new MailPlaceholder("version", "Version"), new MailPlaceholder("summary", "Kurzbeschreibung"),
                new MailPlaceholder("author", "Name des Einreichers"), new MailPlaceholder("email", "E-Mail des Einreichers"),
                new MailPlaceholder("count", "Anzahl Templates in der Datei"), new MailPlaceholder("templates", "Namen der Templates"),
                new MailPlaceholder("removed", "Was beim Hochladen entfernt wurde (Passwörter, Tokens …)"), new MailPlaceholder("review_url", "Link zur Prüfung im Admin")
            }
        });
        b.MailTemplate(new MailTemplateSpec(Approved, "Template-Store: freigegeben", "Geht an den Einreicher, wenn seine Einreichung freigegeben wurde.",
            "Your template is live: {{title}}",
            "Hi {{name}},\n\nthank you! Your template \"{{title}}\" {{version}} has been approved and is now available in the Matmon template store:\n\n{{url}}\n\nThe Matmon team\n")
        {
            Placeholders = new[] { new MailPlaceholder("name", "Name des Einreichers"), new MailPlaceholder("title", "Titel"), new MailPlaceholder("version", "Version"), new MailPlaceholder("url", "Adresse des Templates") }
        });
        b.MailTemplate(new MailTemplateSpec(Rejected, "Template-Store: abgelehnt", "Geht an den Einreicher, wenn seine Einreichung abgelehnt wurde, mit der Begründung.",
            "About your template: {{title}}",
            "Hi {{name}},\n\nthank you for submitting \"{{title}}\" {{version}} to the Matmon template store. We could not publish it this time:\n\n{{reason}}\n\nYou can upload a corrected version at any time:\n{{mine_url}}\n\nThe Matmon team\n")
        {
            Placeholders = new[] { new MailPlaceholder("name", "Name des Einreichers"), new MailPlaceholder("title", "Titel"), new MailPlaceholder("version", "Version"), new MailPlaceholder("reason", "Begründung"), new MailPlaceholder("mine_url", "Link zu „Meine Einreichungen“") }
        });
    }

    private static string Origin(IPluginContext ctx) => (ctx.Site.Origin ?? "").TrimEnd('/');

    private static async Task SendAsync(IPluginContext ctx, string template, string to, Dictionary<string, string> values, string? replyTo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(to)) { ctx.Log($"Mail {template} not sent: no address"); return; }
        var r = await ctx.Mail.SendAsync(template, new[] { to.Trim() }, values, null, string.IsNullOrWhiteSpace(replyTo) ? null : replyTo, ct);
        if (!r.Ok) ctx.Log($"Mail {template} to {to} failed: {r.Error}");
    }

    public static Task NotifyOperatorAsync(IPluginContext ctx, DataRow entry, long versionId, IReadOnlyDictionary<string, string> form, PluginMember member, MtParsed file, CancellationToken ct)
    {
        var to = ctx.Settings.Get("notifyEmail");
        return SendAsync(ctx, Submitted, to, new Dictionary<string, string>
        {
            ["title"] = form["title"], ["version"] = form["version"], ["summary"] = form["summary"],
            ["author"] = member.DisplayName, ["email"] = member.Email,
            ["count"] = file.Templates.Count.ToString(), ["templates"] = string.Join(", ", file.Templates.Take(30).Select(t => t.Name)) + (file.Templates.Count > 30 ? ", …" : ""),
            ["removed"] = file.Removed.Count == 0 ? "nichts" : string.Join("; ", file.Removed.Take(20)),
            ["review_url"] = Origin(ctx) + "/admin/p/matmon-templates/versions/" + versionId
        }, member.Email, ct);
    }

    public static Task DecisionAsync(IPluginContext ctx, DataRow v, DataRow entry, bool approved, CancellationToken ct)
    {
        var values = new Dictionary<string, string>
        {
            ["name"] = v.Text("author"), ["title"] = v.Text("title"), ["version"] = v.Text("version"),
            ["url"] = Origin(ctx) + MtPublic.Base + "/" + entry.Text("slug"), ["reason"] = v.Text("reason"), ["mine_url"] = Origin(ctx) + MtPublic.Base + "/mine"
        };
        return SendAsync(ctx, approved ? Approved : Rejected, v.Text("email"), values, null, ct);
    }
}
