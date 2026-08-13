namespace MatCMS.Cloud.Services;

/// <summary>
/// The settings an instance understands, offered as a choice when a key/value row is added.
///
/// <para>Typing a key by hand is how it worked before, and it is the wrong shape of question: the
/// operator is picking from a fixed, known set, and a typo produced a row that looked perfectly fine
/// here and did nothing at all on the site — nothing rejects an unknown key, it simply sits in the
/// instance's settings table forever.</para>
///
/// <para><b>This is a suggestion list, not a contract.</b> The wire contract lives in
/// <c>MatCMS.Shared</c>; these strings are a copy of the CMS's own <c>SettingKeys</c>, and the copy is
/// deliberate. An entry that goes stale costs a wrong suggestion and nothing else — the field still
/// accepts any key, and the instance still applies whatever it is sent. Promoting them to the shared
/// contract would claim a guarantee neither side actually needs.</para>
///
/// <para>What is NOT here matters as much as what is: every key owned by a settings GROUP
/// (<c>smtp.*</c>, <c>translate.*</c>, <c>backup.*</c>, <c>mail.transport</c>) is left out, because
/// the rollout skips those rows unless their group is switched on
/// (<see cref="ProfileService.BuildConfigAsync"/>). Offering one here would let somebody add a free
/// row that can never arrive anywhere, with nothing on screen explaining why.</para>
/// </summary>
public static class InstanceSettingCatalog
{
    /// <param name="Key">The key as the instance stores it.</param>
    /// <param name="Label">What it is, in the operator's words.</param>
    /// <param name="Hint">Only where the key alone is genuinely unclear — otherwise empty.</param>
    public sealed record Entry(string Key, string Label, string Hint = "");

    public sealed record Group(string Name, IReadOnlyList<Entry> Entries);

    /// <summary>Grouped the way the CMS's own settings page groups them, so a key is looked for where
    /// the operator last saw it rather than in one alphabetical wall.</summary>
    public static readonly IReadOnlyList<Group> Groups =
    [
        new Group("Website", [
            new Entry("SiteName", "Name der Website"),
            new Entry("LogoUrl", "Logo (URL)"),
            new Entry("FaviconUrl", "Favicon (URL)"),
            new Entry("FooterText", "Fußzeilen-Text"),
            new Entry("ContactRecipient", "Empfänger für Formular-Mails"),
            new Entry("site.behindHttpsProxy", "Hinter HTTPS-Proxy",
                "1 = an. Nötig, wenn ein Proxy die Verschlüsselung beendet: sonst baut die Website alle absoluten Adressen mit http und ein https-Browser weist sie ab."),
            new Entry("site.canonicalUrl", "Kanonische URL",
                "Pro Website verschieden — als Profilwert nur sinnvoll, wenn wirklich alle Instanzen dieselbe Adresse haben."),
        ]),
        new Group("Kopfleiste", [
            new Entry("TopBarLink1Text", "Link 1: Text"),
            new Entry("TopBarLink1Url", "Link 1: Ziel"),
            new Entry("TopBarLink2Text", "Link 2: Text"),
            new Entry("TopBarLink2Url", "Link 2: Ziel"),
        ]),
        new Group("Seiten & Fehler", [
            new Entry("error.notFoundPage", "Seite für 404"),
            new Entry("error.errorPage", "Seite für Fehler"),
            new Entry("sitemap.enabled", "Sitemap ausliefern", "1 = an, 0 = aus"),
        ]),
        new Group("Wartungsmodus", [
            new Entry("maintenance.enabled", "Wartungsmodus", "1 = an, 0 = aus"),
            new Entry("maintenance.title", "Wartungsmodus: Überschrift"),
            new Entry("maintenance.message", "Wartungsmodus: Text"),
        ]),
        new Group("Code & Tracking", [
            new Entry("code.head", "Code vor </head>"),
            new Entry("code.bodyStart", "Code nach <body>"),
            new Entry("code.bodyEnd", "Code vor </body>"),
            new Entry("analytics.ga4", "GA4 Mess-ID", "G-XXXXXXX — erzeugt das Snippet selbst"),
        ]),
        new Group("Sprache", [
            new Entry("i18n.default", "Standardsprache",
                "Welche Sprachen eine Website anbietet, wird bewusst nicht verteilt — nur die Voreinstellung."),
        ]),
    ];

    public static IEnumerable<Entry> All => Groups.SelectMany(g => g.Entries);

    public static Entry? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
}
