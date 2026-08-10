namespace MatCMS.Cloud.Services;

/// <summary>
/// Example content for a mail preview.
/// <para>Kept next to the cloud's editors rather than in the shared declaration: the DECLARATION says
/// which placeholders exist and what they mean — that is a fact about the product. What a nice
/// example looks like is a decision about this admin, and MatCMS makes its own.</para>
/// </summary>
public static class MailSamples
{
    public static string Scalar(string token) => token.ToLowerInvariant() switch
    {
        "form_name" => "Kontakt",
        "fields" => "Name: Erika Musterfrau\nE-Mail: erika@beispiel.de\nNachricht: Beispieltext",
        "site_name" => "Beispiel-Website",
        "date" => DateTime.Now.ToString("dd.MM.yyyy HH:mm"),
        _ => $"[{token}]",
    };

    /// <summary>Two passes per loop: one shows the layout, two show what repetition does to it —
    /// which is the thing a table either survives or does not. The second value deliberately contains
    /// &lt; and &amp;, so the preview shows whether values are escaped.</summary>
    public static Dictionary<string, IReadOnlyList<MatCMS.Shared.MailTemplates.Item>> Lists(
        MatCMS.Shared.MailTemplates.Definition? def) =>
        (def?.Loops ?? []).ToDictionary(
            l => l.Name,
            l => (IReadOnlyList<MatCMS.Shared.MailTemplates.Item>)Enumerable.Range(1, 2).Select(n =>
            {
                var item = new MatCMS.Shared.MailTemplates.Item();
                foreach (var f in l.Fields) item[f.Token] = Loop(f.Token, n);
                return item;
            }).ToList(),
            StringComparer.OrdinalIgnoreCase);

    private static string Loop(string token, int n) => token.ToLowerInvariant() switch
    {
        "label" => n == 1 ? "Name" : "Nachricht",
        "value" => n == 1 ? "Erika Musterfrau" : "Angebot <Apartment 3> für Mai & Juni?",
        _ => $"[{token} {n}]",
    };
}
