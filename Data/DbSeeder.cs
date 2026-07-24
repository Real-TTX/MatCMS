using System.Text.Encodings.Web;
using System.Text.Json;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Data;

public static class DbSeeder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const string HeroImage =
        "https://images.unsplash.com/photo-1522071820081-009f0129c71c?auto=format&fit=crop&w=1600&q=80";

    // Original feusys.de section images.
    private const string TransparentImage =
        "https://feusys.de/wp-content/uploads/2026/07/john-FlPc9_VocJ4-unsplash-1024x683.jpg";
    private const string NetzwerkpartnerImage =
        "https://feusys.de/wp-content/uploads/2026/07/krakenimages-Y5bvRlcCx8k-unsplash-1-683x1024.jpg";
    private const string LoesungspartnerImage =
        "https://feusys.de/wp-content/uploads/2026/07/rohan-makhecha-jw3GOzxiSkw-unsplash-683x1024.jpg";

    public static async Task SeedAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var auth = sp.GetRequiredService<AuthService>();

        if (!await db.Users.AnyAsync())
        {
            db.Users.Add(new User
            {
                Username = "admin",
                PasswordHash = auth.HashPassword("admin"),
                Role = "Admin",
                DisplayName = "Administrator"
            });
        }

        if (!await db.SiteSettings.AnyAsync())
        {
            db.SiteSettings.AddRange(
                S(SettingKeys.SiteName, "FEUSYS"),
                S(SettingKeys.LogoUrl, "/img/logo.svg"),
                S(SettingKeys.TopBarLink1Text, "Microsoft-Lizenz Portal"),
                S(SettingKeys.TopBarLink1Url, "https://subhub.feusys.de/"),
                S(SettingKeys.TopBarLink2Text, "Fernwartungstool herunterladen"),
                S(SettingKeys.TopBarLink2Url,
                    "https://my.anydesk.com/v2/api/v2/custom-clients/downloads/public/6BPJF7D3B0GZ/AnyDeskClient.exe"),
                S(SettingKeys.FooterText, "© FEUSYS – Weil moderne IT mehr als nur Technik ist."),
                S(SettingKeys.ContactRecipient, "info@feusys.de")
            );
        }

        if (!await db.Pages.AnyAsync())
        {
            foreach (var page in BuildPages())
                db.Pages.Add(page);
        }

        if (!await db.MenuItems.AnyAsync())
        {
            db.MenuItems.AddRange(
                Mi("header", "Start", "/", 0),
                Mi("header", "Über uns", "/ueber-uns", 1),
                Mi("header", "Kontakt", "/kontakt", 2),
                Mi("footer", "Start", "/", 0),
                Mi("footer", "Kontakt", "/kontakt", 1),
                Mi("footer", "Partner", "/partner", 2),
                Mi("footer", "Produkte", "/produkte", 3),
                Mi("footer", "Impressum", "/impressum", 4),
                Mi("footer", "Datenschutzerklärung", "/datenschutzerklaerung", 5),
                Mi("footer", "Allgemeine Geschäftsbedingungen", "/allgemeine-geschaeftsbedingungen", 6)
            );
        }

        if (!await db.Templates.AnyAsync())
        {
            db.Templates.Add(new Template
            {
                Name = "FeuSys",
                IsActive = true,
                AccentColor = "#de7e11",
                HeadingFont = "Geologica",
                BodyFont = "Inter",
                ButtonStyle = "solid"
            });
        }

        await db.SaveChangesAsync();
    }

    private static SiteSetting S(string key, string value) => new() { Key = key, Value = value };

    private static MenuItem Mi(string menu, string label, string url, int order) =>
        new() { Menu = menu, Label = label, Url = url, SortOrder = order };

    private static string Json(object data) => JsonSerializer.Serialize(data, JsonOpts);

    private static ContentBlock B(string type, int order, object data) =>
        new() { BlockType = type, SortOrder = order, DataJson = Json(data) };

    private static List<Page> BuildPages()
    {
        var pages = new List<Page>();

        // ---------- HOME ----------
        pages.Add(new Page
        {
            Title = "Start",
            Slug = "home",
            NavLabel = "Start",
            IsPublished = true,
            ShowInNav = true,
            NavOrder = 1,
            ShowInFooter = true,
            FooterOrder = 1,
            MetaDescription = "FeuSys – Weil moderne IT mehr als nur Technik ist.",
            Blocks =
            [
                B("hero", 0, new
                {
                    heading = "SICHERE IT.\nKLARE STRUKTUREN.\nPERSÖNLICHER SERVICE.",
                    subheading = "Ob akute Herausforderung oder strategische Weichenstellung: Wir beraten Sie professionell und leisten bei Herausforderungen schnell und unkompliziert Unterstützung.",
                    image = HeroImage,
                    buttonText = "Kontaktieren Sie uns",
                    buttonUrl = "/kontakt",
                    align = "left"
                }),
                B("imagetext", 1, new
                {
                    image = TransparentImage,
                    heading = "TRANSPARENTE DIENSTLEISTUNG FÜR ZUVERLÄSSIGEN IT-SERVICE",
                    body = "<p>Bei FEUSYS gibt es keine anonyme Hotline und kein Ticket-Ping-Pong. Ihr Ansprechpartner kennt Ihre Infrastruktur und Ihre Anforderungen genau. Wenn Sie zusätzliche Sicherheit wollen, bieten wir einfache Dienstleistungsverträge mit klar geregeltem SLA – ganz ohne Vertragszwang. So bestimmen Sie selbst, wie viel Verbindlichkeit Sie brauchen.</p>",
                    imageSide = "left"
                }),
                B("cta", 2, new
                {
                    heading = "",
                    text = "Entdecken Sie, wie FEUSYS Ihre IT mit verlässlichem Support und maßgeschneiderten Konzepten optimiert. Kontaktieren Sie uns für eine individuelle Beratung.",
                    buttonText = "Kontaktieren Sie uns",
                    buttonUrl = "/kontakt"
                }),
                B("richtext", 3, new
                {
                    heading = "PARTNERSCHAFTEN, DIE IHRE IT STÄRKEN",
                    body = "<p>Der beste Service entsteht selten allein. Unsere Netzwerkpartner und Lösungspartner sind fester Bestandteil dessen, was FEUSYS ausmacht.</p>",
                    align = "center",
                    width = "narrow"
                }),
                B("imagetext", 4, new
                {
                    image = NetzwerkpartnerImage,
                    heading = "NETZWERKPARTNER",
                    body = "<p>Mit unseren Netzwerkpartnern verbindet uns eine enge, vertrauensvolle Zusammenarbeit auf Augenhöhe. Gemeinsam ergänzen wir uns in Know-how und Ressourcen, um unseren Kunden noch mehr bieten zu können – auf diese Partnerschaften sind wir bei FEUSYS besonders stolz.</p>",
                    imageSide = "right"
                }),
                B("imagetext", 5, new
                {
                    image = LoesungspartnerImage,
                    heading = "LÖSUNGSPARTNER",
                    body = "<p>Unsere Lösungspartner ermöglichen es uns, Ihnen stets ausgereifte, zuverlässige und zukunftssichere Produkte anzubieten. Diese Beziehungen zu führenden Herstellern sehen wir bei FEUSYS als echten Wettbewerbsvorteil und als etwas, das wir mit Freude und Überzeugung pflegen.</p>",
                    imageSide = "left"
                }),
                B("servicegrid", 6, new
                {
                    heading = "FEUSYS: IT-SYSTEMBETREUUNG, DIE GENAU PASST",
                    intro = "Unser Serviceportfolio begleitet Sie durch Ihre gesamte IT-Landschaft: von On-Premise-Systemen bis hin zu modernen Cloud-Lösungen, immer gut betreut.",
                    columns = "4",
                    items = new object[]
                    {
                        new { title = "Serverinstallation & -Management", text = "Wir installieren, betreiben und pflegen Ihre Server – zuverlässig und performant." },
                        new { title = "Cloud-Dienste", text = "Wir planen und betreuen Ihre Cloud-Infrastruktur – skalierbar und zukunftssicher." },
                        new { title = "Microsoft 365", text = "Wir richten Ihre Office-365-Umgebung inklusive E-Mail-Systeme ein und betreuen sie auf Wunsch dauerhaft." },
                        new { title = "Netzwerksicherheit", text = "Wir schützen Ihre Infrastruktur mit modernen Firewall- und Sicherheitslösungen." },
                        new { title = "Identitätslösungen", text = "Wir verwalten Zugriffe und Berechtigungen sicher über Active Directory und EntraID." },
                        new { title = "Backup-Lösungen", text = "Wir sichern Ihre Daten zuverlässig ab – für den Ernstfall bestens vorbereitet." },
                        new { title = "Support", text = "Wir stehen Ihnen bei IT-Fragen und Problemen schnell und persönlich zur Seite." },
                        new { title = "Hard- und Software", text = "Wir beraten und beliefern Sie mit passender Hard- und Software aus einer Hand." }
                    }
                }),
                B("columns", 7, new
                {
                    heading = "SPEZIALIST FÜR IHRE INFRASTRUKTUR – SICHERHEIT RUND UM IHRE IDENTITÄT",
                    intro = "",
                    columns = "3",
                    items = new object[]
                    {
                        new { title = "ACTIVE DIRECTORY", body = "<p>In einer vernetzten Welt ist das Active Directory weit mehr als nur ein Verzeichnisdienst – es ist die zentrale Steuerungseinheit für Benutzer, Geräte und Zugriffsrechte. Wenn hier etwas nicht rund läuft, spürt man das sofort: Logins funktionieren nicht, Berechtigungen sind falsch gesetzt, und die Sicherheit leidet.</p><p>Genau deshalb kümmern wir von FeuSys uns mit echtem Expertenwissen und viel Erfahrung um Ihre AD-Umgebung. Für Sie heißt das: weniger Sorgen, mehr Stabilität und ein System, das einfach funktioniert.</p>" },
                        new { title = "ENTRAID", body = "<p>In der heutigen Cloud-basierten Welt ist EntraID der Schlüssel zur sicheren und effizienten Verwaltung von Identitäten und Zugriffsrechten. Ob Microsoft 365, externe Anwendungen oder hybride Umgebungen – EntraID sorgt dafür, dass Ihre Mitarbeitenden genau dort hinkommen, wo sie sollen – und nur dort.</p><p>Wir kümmern uns um Einrichtung, Wartung und kontinuierliche Optimierung Ihrer Entra-ID-Umgebung. So bleibt Ihre Infrastruktur sicher, flexibel und zukunftsfähig.</p>" },
                        new { title = "HYBRID", body = "<p>Viele Unternehmen setzen heute auf hybride IT-Strukturen: eine Kombination aus lokaler Infrastruktur und Cloud-Diensten wie Microsoft 365. Damit diese Welten reibungslos zusammenarbeiten, braucht es eine zuverlässige Verbindung.</p><p>Wir verbinden Ihre lokale AD-Struktur nahtlos mit der Cloud und kümmern uns um Synchronisation, Authentifizierung und Konfiguration – damit Ihre Mitarbeitenden überall sicher arbeiten können.</p>" }
                    }
                }),
            ]
        });

        // ---------- ÜBER UNS ----------
        pages.Add(new Page
        {
            Title = "Über Uns",
            Slug = "ueber-uns",
            NavLabel = "Über uns",
            ShowInNav = true,
            NavOrder = 2,
            Blocks =
            [
                B("hero", 0, new { heading = "ÜBER UNS", subheading = "", image = "", buttonText = "", buttonUrl = "", align = "center" }),
                B("richtext", 1, new
                {
                    heading = "",
                    body = "<p>Wir glauben daran, dass Technologie nur dann wirklich wertvoll ist, wenn sie von Menschen getragen wird, die mit Leidenschaft und Überzeugung dahinterstehen. Deshalb setzen wir auf Techniker, die nicht nur Experten sind, sondern ihre Lösungen leben und lieben.</p><p>Bei uns geht es nicht um anonyme Prozesse oder unpersönliche Hotlines. Jeder Kontakt ist geprägt von echter Nähe, direkter Kommunikation und einem partnerschaftlichen Miteinander. Für uns ist es selbstverständlich, dass Sie sich auf uns verlassen können – ohne Wenn und Aber.</p><p>Wir begleiten Sie mit Herzblut, Kompetenz und dem festen Anspruch, Ihre IT so zu betreuen, als wäre es unsere eigene.</p>",
                    align = "center",
                    width = "narrow"
                }),
                B("cta", 2, new { heading = "Wir nehmen Ihre IT persönlich.", text = "", buttonText = "", buttonUrl = "" }),
            ]
        });

        // ---------- KONTAKT ----------
        pages.Add(new Page
        {
            Title = "Kontakt",
            Slug = "kontakt",
            NavLabel = "Kontakt",
            ShowInNav = true,
            NavOrder = 3,
            ShowInFooter = true,
            FooterOrder = 2,
            Blocks =
            [
                B("hero", 0, new { heading = "KONTAKT", subheading = "Zuverlässige IT-Systembetreuung für Ihren Erfolg. Kontaktieren Sie uns direkt für persönliche Beratung und schnelle Hilfe.", image = "", buttonText = "", buttonUrl = "", align = "center" }),
                B("contactform", 1, new { heading = "Kontaktformular", intro = "", categories = "Allgemeine Anfrage, Service Anfrage" }),
            ]
        });

        // ---------- PARTNER ----------
        pages.Add(new Page
        {
            Title = "Partner",
            Slug = "partner",
            ShowInFooter = true,
            FooterOrder = 3,
            Blocks =
            [
                B("hero", 0, new { heading = "PARTNER", subheading = "", image = "", buttonText = "", buttonUrl = "", align = "center" }),
                B("richtext", 1, new
                {
                    heading = "",
                    body = "<p>Als zertifizierter DATEV-Partner bietet unser Partner die Help4You GmbH nicht nur fundiertes Fachwissen rund um DATEV-konforme IT-Lösungen, sondern auch maßgeschneiderte ASP- und Hosting-Dienste in diesem Bereich. Diese Lösungen ermöglichen einen sicheren, ortsunabhängigen Zugriff auf Ihre Systeme und Anwendungen – revisionssicher, leistungsfähig und jederzeit verfügbar. Damit schaffen wir die technische Grundlage für eine moderne, digitale Zusammenarbeit zwischen Kanzleien und Mandanten.</p>",
                    align = "center",
                    width = "narrow"
                }),
                B("logostrip", 2, new
                {
                    heading = "UNSERE PARTNER",
                    items = new object[]
                    {
                        new { image = "https://feusys.de/wp-content/uploads/2025/09/Logo_help_4_you-1024x234.png", alt = "Help4You GmbH – DATEV-Partner", url = "" }
                    }
                }),
            ]
        });

        // ---------- PRODUKTE ----------
        pages.Add(new Page
        {
            Title = "Produkte",
            Slug = "produkte",
            ShowInFooter = true,
            FooterOrder = 4,
            Blocks =
            [
                B("hero", 0, new { heading = "PRODUKTE", subheading = "", image = "", buttonText = "", buttonUrl = "", align = "center" }),
                B("richtext", 1, new
                {
                    heading = "",
                    body = "<p>heylogin revolutioniert das Passwortmanagement mit einer innovativen, passwortfreien Authentifizierung, die auf biometrischen Daten und verschlüsselten Tokens basiert. Dadurch wird das Risiko von Phishing und Passwortdiebstahl deutlich reduziert. Die Lösung kombiniert deutsche Ingenieurskunst mit strengem Datenschutz und moderner Technologie. Dank Zero-Knowledge-Architektur, ISO-Zertifizierung und DSGVO-Konformität bietet heylogin eine sichere, schnelle und unkomplizierte Passwortmanagement-Lösung „Made in Germany“.</p>",
                    align = "center",
                    width = "narrow"
                }),
            ]
        });

        // ---------- IMPRESSUM ----------
        pages.Add(LegalPage("Impressum", "impressum", null, 5, ImpressumHtml));

        // ---------- DATENSCHUTZERKLÄRUNG ----------
        pages.Add(LegalPage("Datenschutzerklärung", "datenschutzerklaerung", null, 6, DatenschutzHtml));

        // ---------- AGB ----------
        pages.Add(LegalPage("Allgemeine Geschäftsbedingungen", "allgemeine-geschaeftsbedingungen", null, 7, AgbHtml));

        return pages;
    }

    private static Page LegalPage(string title, string slug, string? navLabel, int footerOrder, string bodyHtml) =>
        new()
        {
            Title = title,
            Slug = slug,
            NavLabel = navLabel,
            ShowInFooter = true,
            FooterOrder = footerOrder,
            Blocks =
            [
                B("hero", 0, new { heading = title.ToUpperInvariant(), subheading = "", image = "", buttonText = "", buttonUrl = "", align = "center" }),
                B("richtext", 1, new { heading = "", body = bodyHtml, align = "left", width = "narrow" }),
            ]
        };

    // ---------------------------------------------------------------------
    // Legal texts (verbatim from feusys.de)
    // ---------------------------------------------------------------------

    private const string ImpressumHtml = @"
<h3>FeuSys GmbH</h3>
<p>Lise-Meitner-Str. 10<br>74321 Bietigheim-Bissingen</p>
<p>Handelsregister: HRB 801071<br>Registergericht: Amtsgericht Stuttgart</p>
<p><strong>Vertreten durch:</strong><br>Christian Feuerstein</p>
<h3>Kontakt</h3>
<p>Telefon: +49 7142 9499110<br>E-Mail: info@feusys.de</p>
<h3>Umsatzsteuer-ID</h3>
<p>Umsatzsteuer-Identifikationsnummer gemäß § 27 a Umsatzsteuergesetz:<br>DE456257947</p>
<h3>Verbraucherstreitbeilegung / Universalschlichtungsstelle</h3>
<p>Wir sind nicht bereit oder verpflichtet, an Streitbeilegungsverfahren vor einer Verbraucherschlichtungsstelle teilzunehmen.</p>";

    private const string DatenschutzHtml = @"
<h3>1. Datenschutz auf einen Blick</h3>
<p><strong>Allgemeine Hinweise</strong><br>Die folgenden Hinweise geben einen einfachen Überblick darüber, was mit Ihren personenbezogenen Daten passiert, wenn Sie diese Website besuchen. Personenbezogene Daten sind alle Daten, mit denen Sie persönlich identifiziert werden können. Ausführliche Informationen zum Thema Datenschutz entnehmen Sie unserer unter diesem Text aufgeführten Datenschutzerklärung.</p>
<p><strong>Wer ist verantwortlich für die Datenerfassung auf dieser Website?</strong><br>Die Datenverarbeitung auf dieser Website erfolgt durch den Websitebetreiber. Dessen Kontaktdaten können Sie dem Abschnitt „Hinweis zur Verantwortlichen Stelle“ in dieser Datenschutzerklärung entnehmen.</p>
<p><strong>Wie erfassen wir Ihre Daten?</strong><br>Ihre Daten werden zum einen dadurch erhoben, dass Sie uns diese mitteilen. Hierbei kann es sich z. B. um Daten handeln, die Sie in ein Kontaktformular eingeben. Andere Daten werden automatisch oder nach Ihrer Einwilligung beim Besuch der Website durch unsere IT-Systeme erfasst. Das sind vor allem technische Daten (z. B. Internetbrowser, Betriebssystem oder Uhrzeit des Seitenaufrufs).</p>
<p><strong>Wofür nutzen wir Ihre Daten?</strong><br>Ein Teil der Daten wird erhoben, um eine fehlerfreie Bereitstellung der Website zu gewährleisten. Andere Daten können zur Analyse Ihres Nutzerverhaltens verwendet werden.</p>
<p><strong>Welche Rechte haben Sie bezüglich Ihrer Daten?</strong><br>Sie haben jederzeit das Recht, unentgeltlich Auskunft über Herkunft, Empfänger und Zweck Ihrer gespeicherten personenbezogenen Daten zu erhalten. Sie haben außerdem ein Recht, die Berichtigung oder Löschung dieser Daten zu verlangen. Wenn Sie eine Einwilligung zur Datenverarbeitung erteilt haben, können Sie diese Einwilligung jederzeit für die Zukunft widerrufen. Des Weiteren steht Ihnen ein Beschwerderecht bei der zuständigen Aufsichtsbehörde zu.</p>

<h3>2. Hosting</h3>
<p>Wir hosten die Inhalte unserer Website bei folgendem Anbieter:</p>
<p><strong>Strato</strong><br>Anbieter ist die Strato AG, Otto-Ostrowski-Straße 7, 10249 Berlin (nachfolgend „Strato“). Wenn Sie unsere Website besuchen, erfasst Strato verschiedene Logfiles inklusive Ihrer IP-Adressen. Weitere Informationen entnehmen Sie der Datenschutzerklärung von Strato: https://www.strato.de/datenschutz/.</p>
<p>Die Verwendung von Strato erfolgt auf Grundlage von Art. 6 Abs. 1 lit. f DSGVO. Wir haben ein berechtigtes Interesse an einer möglichst zuverlässigen Darstellung unserer Website. Wir haben einen Vertrag über Auftragsverarbeitung (AVV) zur Nutzung des oben genannten Dienstes geschlossen.</p>

<h3>3. Allgemeine Hinweise und Pflichtinformationen</h3>
<p><strong>Datenschutz</strong><br>Die Betreiber dieser Seiten nehmen den Schutz Ihrer persönlichen Daten sehr ernst. Wir behandeln Ihre personenbezogenen Daten vertraulich und entsprechend den gesetzlichen Datenschutzvorschriften sowie dieser Datenschutzerklärung. Wir weisen darauf hin, dass die Datenübertragung im Internet (z. B. bei der Kommunikation per E-Mail) Sicherheitslücken aufweisen kann. Ein lückenloser Schutz der Daten vor dem Zugriff durch Dritte ist nicht möglich.</p>
<p><strong>Hinweis zur verantwortlichen Stelle</strong><br>Die verantwortliche Stelle für die Datenverarbeitung auf dieser Website ist:</p>
<p>FeuSys GmbH<br>Lise-Meitner-Str. 10<br>74321 Bietigheim-Bissingen<br>Telefon: 015234658557<br>E-Mail: info@feusys.de</p>
<p><strong>Speicherdauer</strong><br>Soweit innerhalb dieser Datenschutzerklärung keine speziellere Speicherdauer genannt wurde, verbleiben Ihre personenbezogenen Daten bei uns, bis der Zweck für die Datenverarbeitung entfällt. Wenn Sie ein berechtigtes Löschersuchen geltend machen oder eine Einwilligung zur Datenverarbeitung widerrufen, werden Ihre Daten gelöscht, sofern wir keine anderen rechtlich zulässigen Gründe für die Speicherung Ihrer personenbezogenen Daten haben.</p>
<p><strong>Empfänger von personenbezogenen Daten</strong><br>Im Rahmen unserer Geschäftstätigkeit arbeiten wir mit verschiedenen externen Stellen zusammen. Wir geben personenbezogene Daten nur dann an externe Stellen weiter, wenn dies im Rahmen einer Vertragserfüllung erforderlich ist, wenn wir gesetzlich hierzu verpflichtet sind, wenn wir ein berechtigtes Interesse nach Art. 6 Abs. 1 lit. f DSGVO an der Weitergabe haben oder wenn eine sonstige Rechtsgrundlage die Datenweitergabe erlaubt.</p>
<p><strong>Widerruf Ihrer Einwilligung zur Datenverarbeitung</strong><br>Viele Datenverarbeitungsvorgänge sind nur mit Ihrer ausdrücklichen Einwilligung möglich. Sie können eine bereits erteilte Einwilligung jederzeit widerrufen. Die Rechtmäßigkeit der bis zum Widerruf erfolgten Datenverarbeitung bleibt vom Widerruf unberührt.</p>
<p><strong>Beschwerderecht bei der zuständigen Aufsichtsbehörde</strong><br>Im Falle von Verstößen gegen die DSGVO steht den Betroffenen ein Beschwerderecht bei einer Aufsichtsbehörde zu, insbesondere in dem Mitgliedstaat ihres gewöhnlichen Aufenthalts, ihres Arbeitsplatzes oder des Orts des mutmaßlichen Verstoßes.</p>
<p><strong>Recht auf Datenübertragbarkeit</strong><br>Sie haben das Recht, Daten, die wir auf Grundlage Ihrer Einwilligung oder in Erfüllung eines Vertrags automatisiert verarbeiten, an sich oder an einen Dritten in einem gängigen, maschinenlesbaren Format aushändigen zu lassen.</p>
<p><strong>Auskunft, Berichtigung und Löschung</strong><br>Sie haben im Rahmen der geltenden gesetzlichen Bestimmungen jederzeit das Recht auf unentgeltliche Auskunft über Ihre gespeicherten personenbezogenen Daten, deren Herkunft und Empfänger und den Zweck der Datenverarbeitung und ggf. ein Recht auf Berichtigung oder Löschung dieser Daten.</p>
<p><strong>SSL- bzw. TLS-Verschlüsselung</strong><br>Diese Seite nutzt aus Sicherheitsgründen und zum Schutz der Übertragung vertraulicher Inhalte eine SSL- bzw. TLS-Verschlüsselung. Eine verschlüsselte Verbindung erkennen Sie daran, dass die Adresszeile des Browsers von „http://“ auf „https://“ wechselt.</p>

<h3>4. Datenerfassung auf dieser Website</h3>
<p><strong>Kontaktformular</strong><br>Wenn Sie uns per Kontaktformular Anfragen zukommen lassen, werden Ihre Angaben aus dem Anfrageformular inklusive der von Ihnen dort angegebenen Kontaktdaten zwecks Bearbeitung der Anfrage und für den Fall von Anschlussfragen bei uns gespeichert. Diese Daten geben wir nicht ohne Ihre Einwilligung weiter. Die Verarbeitung dieser Daten erfolgt auf Grundlage von Art. 6 Abs. 1 lit. b DSGVO, sofern Ihre Anfrage mit der Erfüllung eines Vertrags zusammenhängt oder zur Durchführung vorvertraglicher Maßnahmen erforderlich ist. In allen übrigen Fällen beruht die Verarbeitung auf unserem berechtigten Interesse an der effektiven Bearbeitung der an uns gerichteten Anfragen (Art. 6 Abs. 1 lit. f DSGVO).</p>
<p><strong>Anfrage per E-Mail, Telefon oder Telefax</strong><br>Wenn Sie uns per E-Mail, Telefon oder Telefax kontaktieren, wird Ihre Anfrage inklusive aller daraus hervorgehenden personenbezogenen Daten (Name, Anfrage) zum Zwecke der Bearbeitung Ihres Anliegens bei uns gespeichert und verarbeitet. Diese Daten geben wir nicht ohne Ihre Einwilligung weiter.</p>";

    private const string AgbHtml = @"
<h3>1. Zustandekommen und Inhalt des Vertrages</h3>
<p><strong>1.1</strong> Allen Vertragsabschlüssen liegen die nachfolgenden Bedingungen zugrunde. Diese Allgemeinen Geschäftsbedingungen der FeuSys GmbH gelten für alle – auch zukünftige – Verträge, Lieferungen und sonstigen Leistungen aus der gesamten Geschäftsbeziehung mit dem Vertragspartner. Für Hosting- und Betreuungsverträge gelten zusätzliche Bedingungen.</p>
<p><strong>1.2</strong> Unsere Angebote sind freibleibend. An den erteilten Auftrag ist der Auftraggeber vier (4) Wochen gebunden. Der Vertrag kommt erst mit unserer schriftlichen Auftragsbestätigung oder durch Lieferung bzw. Leistung zustande. Wir sind berechtigt, zur Vertragserfüllung Dritte heranzuziehen. Zusicherungen, Nebenabreden und Änderungen des Vertrages bedürfen zu ihrer Wirksamkeit der Schriftform.</p>
<p><strong>1.3</strong> Der Vertragspartner ist nicht berechtigt, Rechtspositionen, die in den geschlossenen Verträgen begründet sind, ohne vorherige schriftliche Zustimmung der FeuSys GmbH ganz oder teilweise an Dritte abzutreten oder zu übertragen.</p>

<h3>2. Preise und Zahlungsbedingungen</h3>
<p><strong>2.1</strong> Unsere Preise verstehen sich in Euro zuzüglich der jeweils geltenden gesetzlichen Mehrwertsteuer.</p>
<p><strong>2.2</strong> Bei Zahlungsverzug des Vertragspartners berechnen wir Verzugszinsen in Höhe von 5 % über dem Basiszinssatz gemäß § 247 BGB. Die Geltendmachung eines höheren Schadens bleibt vorbehalten.</p>
<p><strong>2.3</strong> Rechnungen sind sofort nach Erhalt per Überweisung ohne Abzug zahlbar. Zahlungsziel und etwaige Skontoabzüge bedürfen einer ausdrücklichen schriftlichen Vereinbarung.</p>
<p><strong>2.4</strong> Die Aufrechnung mit Gegenansprüchen sowie die Zurückbehaltung von Zahlungen aus anderen Vertragsverhältnissen sind ausgeschlossen, sofern die Gegenansprüche nicht rechtskräftig festgestellt oder von uns schriftlich anerkannt wurden.</p>

<h3>3. Eigentumsvorbehalt</h3>
<p><strong>3.1</strong> Sämtliche von uns gelieferten Waren bleiben bis zur vollständigen Bezahlung aller Ansprüche aus der Geschäftsbeziehung Eigentum der FeuSys GmbH. Der Vertragspartner ist verpflichtet, die Vorbehaltsware pfleglich zu behandeln und auf Verlangen jederzeit Auskunft über ihren Verbleib zu erteilen.</p>

<h3>4. Lieferzeit</h3>
<p><strong>4.1</strong> Die Angabe einer Lieferzeit ist unverbindlich, sofern keine schriftliche Bestätigung vorliegt.</p>
<p><strong>4.2</strong> Eine verbindliche Lieferzeit ist nur vereinbart, wenn sie von uns ausdrücklich schriftlich bestätigt wird. Sie beginnt mit Vertragsabschluss. Können wir aufgrund von nicht zu vertretenden Hindernissen, insbesondere höherer Gewalt, Streik oder Betriebsstörungen, vereinbarte Liefer- und Leistungsfristen nicht einhalten, so verlängern sich diese angemessen.</p>
<p><strong>4.3</strong> Wir haften im Falle der Überschreitung von Lieferfristen nur bei Vorsatz oder grober Fahrlässigkeit.</p>

<h3>5. Gewährleistung und Haftung</h3>
<p><strong>5.1</strong> Wir haften – gleich aus welchem Rechtsgrund – nur dann, wenn die Ansprüche des Kunden auf die schuldhafte Verletzung einer wesentlichen Vertragspflicht (Kardinalspflicht), auf grobe Fahrlässigkeit oder Vorsatz, auf das arglistige Verschweigen eines Mangels oder auf die Verletzung der Haftungsvorschriften des Produkthaftungsgesetzes zurückzuführen sind.</p>
<p><strong>5.2</strong> Die Gewährleistung erstreckt sich nur auf neu hergestellte Sachen und nur auf Mängel, die die Lieferung infolge eines vor dem Gefahrenübergang liegenden Umstandes unbrauchbar machen oder in ihrer Brauchbarkeit erheblich beeinträchtigen. Die Gewährleistungsfrist beträgt 12 Monate ab Lieferung bzw. Leistung, sofern gesetzlich keine längere Frist zwingend vorgeschrieben ist.</p>
<p><strong>5.3</strong> Wir haften nicht für Schäden, die auf unsachgemäßer Verwendung, fehlerhafter Bedienung oder Behandlung, natürlichem Verschleiß, unterlassener Wartung, ungeeigneten Betriebsmitteln oder sonstigen chemischen, elektrischen oder physikalischen Einflüssen beruhen. Wir haften ferner nicht für die Lauffähigkeit von Programmen auf Hardware, die nicht von uns geliefert wurde.</p>
<p><strong>5.4</strong> Erkennbare Mängel sind unverzüglich nach Empfang der Lieferung/Leistung, spätestens jedoch innerhalb von 10 Werktagen, schriftlich zu rügen. Versteckte Mängel sind unverzüglich nach Entdeckung, spätestens ebenfalls innerhalb von 10 Werktagen nach Entdeckung, schriftlich anzuzeigen.</p>
<p><strong>5.5</strong> Eine Haftung für Produkte und Leistungen Dritter, die im Auftrag und/oder auf Rechnung vertrieben werden, wird ausgeschlossen. Es gelten die jeweiligen Geschäftsbedingungen der Hersteller bzw. Drittanbieter.</p>
<p><strong>5.6</strong> Wir haften nicht für Schäden, die der Vertragspartner durch ihm zumutbare Maßnahmen – insbesondere durch regelmäßige Datensicherung auf separaten Datenträgern – hätte verhindern können.</p>
<p><strong>5.10</strong> Im Rahmen der Mängelhaftung sind wir zunächst berechtigt, nach unserer Wahl den Mangel zu beseitigen (Nachbesserung) oder eine mangelfreie Sache zu liefern (Ersatzlieferung).</p>
<p><strong>5.14</strong> Ansprüche im Rahmen der Mängelhaftung verjähren in 12 Monaten, beginnend mit der Ablieferung oder Abnahme. Hiervon ausgenommen sind die Fälle der Ziffer 5.1 dieser AGB.</p>

<h3>6. Erfüllungsort und Gerichtsstand</h3>
<p><strong>6.1</strong> Erfüllungsort und Gerichtsstand ist der Sitz der FeuSys GmbH.</p>
<p><strong>6.2</strong> Es gilt ausschließlich das Recht der Bundesrepublik Deutschland. Die Anwendung des UN-Kaufrechts (CISG) wird ausgeschlossen.</p>

<h3>7. Zusätzliche Bedingungen für Installationen, Reparaturen und Dienstleistungen</h3>
<p><strong>7.1</strong> Die im Angebot/Auftrag aufgeführten Leistungsangaben sind Erfahrungswerte und setzen eine reibungslose Durchführung der Arbeiten voraus. Zusätzliche Leistungen an Fremdprodukten, die zur ordnungsgemäßen Auftragsausführung erforderlich sind, werden gesondert in Rechnung gestellt.</p>
<p><strong>7.2</strong> Alle nicht im Auftrag aufgeführten Leistungen, die von uns erbracht werden sollen, bedürfen der ausdrücklichen Zustimmung beider Vertragspartner.</p>
<p><strong>7.5</strong> Im Auftrag vereinbarte Termine und Fristen sind verbindlich. Verschiebungen und Stornierungen durch den Auftraggeber sind kostenpflichtig, sofern nicht bis spätestens 5 Werktage vor dem Leistungstermin ein Ersatzauftraggeber gefunden wird, der den Auftrag in vollem Umfang übernimmt.</p>

<h3>8. Datenschutz</h3>
<p><strong>8.1</strong> Die FeuSys GmbH verarbeitet personenbezogene Daten des Vertragspartners ausschließlich zur Erfüllung des Vertragsverhältnisses sowie zur Wahrung berechtigter Interessen, soweit dies gesetzlich zulässig ist.</p>
<p><strong>8.2</strong> Die Verarbeitung personenbezogener Daten erfolgt in Übereinstimmung mit den Bestimmungen der Datenschutz-Grundverordnung (DSGVO) sowie des Bundesdatenschutzgesetzes (BDSG).</p>

<h3>9. Salvatorische Klausel und Schlussbestimmungen</h3>
<p><strong>9.1</strong> Sollten einzelne Bestimmungen dieser AGB ganz oder teilweise unwirksam oder undurchführbar sein oder werden, so wird dadurch die Wirksamkeit der übrigen Bestimmungen nicht berührt.</p>
<p><strong>9.2</strong> Änderungen und Ergänzungen dieser AGB bedürfen der Schriftform. Dies gilt auch für die Aufhebung des Schriftformerfordernisses selbst.</p>
<p><strong>9.3</strong> Im Falle von Widersprüchen zwischen diesen AGB und individuellen Vereinbarungen haben die individuellen Vereinbarungen Vorrang, soweit sie schriftlich festgehalten sind.</p>
<p>FeuSys GmbH · Stand: März 2026</p>";
}
