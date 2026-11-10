namespace MatCMS.Plugin.MatcmsStore;

/// <summary>The store's mails. They land in Admin → Mailvorlagen on the first load; an operator's wording is never overwritten.</summary>
internal static class StoreMail
{
    public const string Submitted = "matcms-store.submitted", Approved = "matcms-store.approved", Rejected = "matcms-store.rejected";

    public static IEnumerable<MailTemplateSpec> Specs { get; } =
    [
        new(Submitted, "Store: neue Einreichung", "An die Prüf-Adresse aus den Store-Einstellungen, wenn ein Herausgeber eine Version hochlädt.",
            "Neue Einreichung: {{name}} {{version}}",
            "{{publisher}} hat eine Version eingereicht:\n\n{{name}} ({{key}}) {{version}} – {{kind}}\n\nZur Prüfung: {{admin_url}}")
        {
            Placeholders = [new("publisher", "Herausgeber"), new("name", "Name"), new("key", "Key"), new("version", "Version"), new("kind", "Art (script/native)"), new("admin_url", "Prüfseite im Admin")]
        },
        new(Approved, "Store: Version freigegeben", "An den Herausgeber, wenn eine Version geprüft und freigegeben ist.",
            "Freigegeben: {{name}} {{version}}",
            "Hallo {{publisher}},\n\n{{name}} {{version}} ist geprüft und jetzt im MatCMS Store:\n{{url}}\n\nDeine Einträge: {{account_url}}")
        {
            Placeholders = [new("publisher", "Herausgeber"), new("name", "Name"), new("key", "Key"), new("version", "Version"), new("url", "Seite im Store"), new("account_url", "Eintrag im Konto")]
        },
        new(Rejected, "Store: Version abgelehnt", "An den Herausgeber, wenn eine Version nicht freigegeben wird.",
            "Nicht freigegeben: {{name}} {{version}}",
            "Hallo {{publisher}},\n\n{{name}} {{version}} wurde nicht freigegeben.\n\nGrund: {{reason}}\n\nDu kannst eine neue Version hochladen: {{account_url}}")
        {
            Placeholders = [new("publisher", "Herausgeber"), new("name", "Name"), new("key", "Key"), new("version", "Version"), new("reason", "Begründung"), new("account_url", "Eintrag im Konto")]
        }
    ];
}
