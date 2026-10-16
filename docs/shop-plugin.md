# Shop-Plugin — Entwurf

Stand: 15.10.2026 — **gebaut** (Entwurf unten, Stand der Umsetzung gleich darunter). Ergänzt `docs/plugin-system-v2.md` (§5 „Shop“, Phase 4). Der Shop ist der
zweite Prüfstein der Plugin-API nach dem Blog: alles, was er braucht und die API nicht kann, wird **in die API** gebaut,
nicht im Shop vorbei.

## Stand der Umsetzung (15.10.2026)

**Entschieden** (Empfehlungen des Entwurfs, Matthias: „Bitte mach den Shop erstmal fertig“): **E1** eingebautes Plugin, Standard **aus** (`BuiltInPlugin.EnabledOnNewSite`);
**E2** Stripe im Shop hinter dem Zahlungs-Erweiterungspunkt (`shop.payment.methods`/`.start`/`.paid`, Stripe und Vorkasse sind die eingebauten Anbieter, PayPal ist ein
weiterer); **E3** Rechnung als druckbare HTML-Seite, lückenlos nummeriert, einmal geschrieben und gespeichert — und seit dem Nachtrag vom 15.10. zusätzlich als **PDF** (kein
Bibliothekspaket: ein kleiner eigener PDF-Schreiber, `ShopPdf.cs`).

**Gebaut, mit Tests** (`tests/MatCMS.Plugins.Tests/Shop*Tests.cs`, `PluginApiExtensionsTests.cs`, `PluginRouteOptionsTests.cs`; alle 488 Tests grün):
Phase **4a** (die API-Lücken L1–L9 und L11), **4b** Katalog/Warenkorb/Kasse/Vorkasse/Zustandsmaschine/Admin, **4c** Stripe Checkout + Webhook + Erstattung + Abgleich,
**4d** Kundenbereich + Gast-Link + Adressbuch + Datenschutz, **4e** digitale Produkte, **4f** Versand, Steuer (auch Kleinunternehmer), Rechnung, Dashboard, Cloud-Aktion.
**Auswahl und Blöcke (Nachtrag 15.10.):** Schlagwörter (`tags`) und „Hervorgehoben“ am Produkt; der Katalog filtert nach Kategorie, Schlagwort und Suche, sortiert (Empfohlen, Neueste,
Preis auf-/absteigend, Name), zeigt Zähler an Kategorien und Schlagwörtern und nennt die Kategorie als Überschrift; die Produktseite hat Brotkrumen, Schlagwörter und bis zu vier
ähnliche Produkte (gleiche Kategorie, dann gleiches Schlagwort). **Zwei Seiten-Blöcke** (`ShopBlocks.cs`): „Shop-Produkte“ (Quelle: alle / Kategorie / Schlagwort / Hervorgehoben /
handverlesen; Reihenfolge; Anzahl; Karten oder Liste; Spalten; Kurzbeschreibung und Preis an/aus; Link zum ganzen Shop) und „Einzelnes Produkt“ (groß, mit Knopf). Kategorie, Schlagwort
und Produkt wählt man im Block-Editor aus Listen, die der Shop selbst liefert (`OptionsSource`) — kein Slug-Tippen. Ein Block, der vor diesen Wahlmöglichkeiten gespeichert wurde
(nur `category`), zeigt weiter dasselbe. Dazu zwei Korrekturen an der Plugin-API (siehe `docs/plugin-system-v2.md`): die Texte eines Plugin-Blocks (Name, Feldbezeichnungen,
Optionen) standen im Editor als rohe Schlüssel, und der Block-Editor kannte weder Mehrfachauswahl noch bedingte Felder noch Listen aus dem Plugin.
**Gutscheine, PDF, Gutschriften, Steuer nach Lieferland, Statt-Preis, PayPal (Nachtrag 15.10.):**
- **Gutscheine** (`ShopCoupons.cs`, Shop → Gutscheine, Agent `coupon.create`/MCP `create_coupon`): Prozent, Festbetrag oder „Versand frei“; Mindest-Warenwert, Zeitraum, Höchstzahl, einmal je
  E-Mail-Adresse, auf eine Kategorie beschränkbar. Der Kunde tippt den Code im Warenkorb (er hängt an der Warenkorb-Zeile, nie am Formular). Der Rabatt gilt für die WAREN, nie für den Versand,
  und wird nach Wert auf die Zeilen verteilt (`Money.Allocate`, größter Rest) — die Steuer jeder Zeile ist die Steuer dessen, was bezahlt wird, und die Rechnung geht je Satz auf den Cent auf.
  Beim Aufgeben der Bestellung wird der Gutschein **noch einmal** geprüft (in der Transaktion, die auch die Einlösung zählt: zwei Käufer gegen `MaxUses=1` → genau einer gewinnt); ein Gutschein, der
  zwischendurch ungültig wurde, bricht die Bestellung ab, statt still den vollen Preis zu nehmen. Eine stornierte, unbezahlte Bestellung gibt die Einlösung zurück. Bei Stripe geht der Rabatt als
  einmaliger Stripe-Gutschein (`discounts[0][coupon]`, Stripe nimmt keine negativen Positionen), bei PayPal als `breakdown.discount` — beide Summen sind exakt die der Bestellung.
- **Rechnung als PDF, Gutschriften:** Rechnung und Gutschrift entstehen aus EINEM Wert (`InvoiceDoc`), aus dem HTML-Seite und PDF gezeichnet werden — sie können nicht voneinander abweichen — und
  werden beim Bezahlen bzw. bei der Erstattung einmal geschrieben und gespeichert. Jede Erstattung nach einer Rechnung schreibt eine **Gutschrift** mit eigener lückenloser Nummer
  (`GS-00001`, Präfix einstellbar), die die Steuer je Satz im Verhältnis zurückgibt. Kunde (Bestellseite) und Betreiber (Bestellung im Admin) bekommen beide als Seite und als PDF
  (`/shop/invoice/{Nr}?t=…&doc=GS-00001&pdf=1`; dieselbe Zugriffsregel wie die Bestellung). Das PDF ist reines PDF 1.4, Helvetica, WinAnsi (deutsche und westeuropäische Buchstaben, €),
  mehrseitig mit Kopf und Fußzeile je Seite.
- **Steuer nach Lieferland** (`ShopTax.cs`, Einstellung „Umsatzsteuer nach“): aus = Satz des Produkts überall (wie bisher); „Lieferland (EU-OSS)“ = Lieferung in ein anderes EU-Land mit dem Satz
  DIESES Landes (Download: Land der Rechnungsadresse), außerhalb der EU keine Umsatzsteuer (Ausfuhr, Nettopreis). Ein Produkt, das zu Hause unter dem Normalsatz besteuert wird, bekommt den
  ermäßigten Satz des Landes. Wahlweise bleibt der angezeigte Preis (die enthaltene Steuer ändert sich) oder der Nettopreis. Die eingebauten Sätze (Stand 2026, 27 Länder) sind ein Richtwert,
  jeder lässt sich in den Einstellungen überschreiben. Die Rechnung trägt den passenden Hinweis. **Nicht** behandelt: Geschäftskunden mit USt-IdNr. (Reverse Charge), die 10.000-€-Lieferschwelle.
- **Statt-Preis** am Produkt: durchgestrichen neben dem Preis mit „−x %“ (abgerundet), nur wenn höher und ohne Varianten; berechnet wird immer der Preis; Block-Quelle „Angebote“.
- **PayPal** (Orders v2, `PayPalPayments.cs`): wie Stripe — bezahlt wird von dem, was PayPal über die EIGENE PayPal-Bestellung der Bestellung sagt (nie von einer Angabe in der Adresse), nur bei
  genau Betrag, Währung und Bestellnummer; Webhook `/shop/webhook/paypal`, den PayPal selbst prüft (`verify-webhook-signature`; Zertifikatsadresse muss `*.paypal.com` sein); Erstattung über die
  Capture, jede Erstattung zählt GENAU EINMAL (ihre Id ist das Tor, egal ob Shop oder Benachrichtigung zuerst meldet); stündlicher/15-Minuten-Abgleich wie bei Stripe.
Nicht gebaut: **L10** (eigene Template-Teile) — Aussehen kommt aus `shop-*`-Klassen mit CSS-Variablen und dem Einstellungsfeld „Eigenes CSS“; ein echter Durchlauf mit **Stripe im Testmodus** und mit
**PayPal in der Sandbox** (braucht Schlüssel — Anleitung in §8; beides ist nur gegen Ersatzserver getestet).

**Abweichungen vom Entwurf:** `NextNumberAsync` ist eine Erweiterungsmethode auf einer Tabelle (Name + Wert), nicht `IPluginData.NextNumberAsync`; die Folge liegt in der
Shop-Tabelle `sequences` und läuft deshalb im Backup mit. Neu dazugekommen: `PublicRouteOptions.PostsPerMinute` (der Shop braucht mehr als ein Kontaktformular),
`PluginResult.RedirectExternal` (nur https), `PluginResult.WithStatus`, `UploadedFile`/`Ui.File` (Admin-Uploads), Backup der privaten Dateien auf demselben Schalter wie die
Plugin-Uploads. Download-Links sind `/shop/download/{Bestellung}/{Datei}?t={Zugangsschlüssel der Bestellung}` statt eigener Token — der Schlüssel ist ohnehin das Geheimnis der
Bestellung, die Zeile in `downloads` führt Ablauf, Limit und Zähler.

## 1. Vorgaben (Matthias, 15.10.2026)

- Für **alle Kunden** (jede Instanz kann den Shop einschalten), nicht nur eigene Seiten.
- **Digitale und physische** Produkte.
- **Stripe zuerst**, PayPal danach.
- Kunden haben einen **Anmeldebereich** (gibt es im CMS schon, die Plugin-API muss ihn abdecken) und der Shop hat **Bestellungen**
  mit Verlauf, Status, Rechnung usw.

## 2. Was die Plugin-API heute für den Shop kann — und was nicht

Geprüft gegen `MatCMS.Plugin.Abstractions` und den Host (Stand Commit f6834a0).

**Kann schon:** eigene Tabellen (`Data`, Geld als `Int` in Cent, Transaktion, Backup/Restore/Cleanup), Routen mit Platzhaltern in
jeder Sprache, Admin-Bereich mit Tabs und Detailseiten (`Ui.*`), Einstellungen mit verschlüsselten Geheimnissen (`Secret`),
Jobs (`EveryInterval`, `Daily`), Ereignisse und Erweiterungspunkte per Name + JSON (`Emit`/`CollectAsync`), Sitemap,
Medien-Referenzen, Cloud/MCP-Operationen (`Operation`), Dashboard-Kachel und „Braucht Aufmerksamkeit“.

**Lücken — der Shop ist ohne sie nicht baubar:**

| # | Lücke | Befund im Code | Folge für den Shop |
| --- | --- | --- | --- |
| **L1** | **Besucher-Identität.** `ctx.User` (`PluginUser`) kennt nur den Admin. Ein angemeldetes Mitglied (`SiteMember`, Scheme `Member`, `MemberService.CurrentAsync`) ist für ein Plugin unsichtbar. | `IPluginContext.User` = `IsAdmin` + `Name`. | Kein „Meine Bestellungen“, kein Checkout als Mitglied. |
| **L2** | **Mitglieder-Konto selbst.** `SiteMember` hat **keine E-Mail**, es gibt **keine Selbstregistrierung** (nur der Admin legt Konten an), **kein Passwort-Zurücksetzen**, keine Profilfelder. | `SiteMember`: Username, DisplayName, PasswordHash, RolesCsv, IsActive. | Kunden können sich nicht selbst anlegen; ohne E-Mail keine Bestellbestätigung an ein Konto. |
| **L3** | **Mitglieder-Schutz für Plugin-Routen.** `PageAccess.Members`/`RequiredRole` gibt es nur für CMS-Seiten. | `PublicRouteOptions` hat keine Zugriffsoption. | `/konto/…` müsste jede Route selbst prüfen — fehleranfällig. |
| **L4** | **Request ist zu schmal.** `RequestData` hat Method, Path, Query, Form, Route. **Keine Header, kein Rohkörper, keine Cookies, keine Client-IP.** `PluginResult` kann **weder Cookie noch Header setzen**. | `RequestData.cs`, `PluginResult.cs`. | **Stripe-Webhook unmöglich** (Signatur = Header + Rohkörper). Kein Warenkorb-Cookie für Gäste. |
| **L5** | **Kein CSRF-Schutz auf öffentlichen Routen.** Bewusst aus (`[IgnoreAntiforgeryToken]` in `PluginPage`), `ctx.Antiforgery` ist dort leer. | `PluginPage.cshtml.cs`. | Zustandsändernde Aktionen eines angemeldeten Kunden (Adresse ändern, Bestellung stornieren) wären fälschbar. |
| **L6** | **Webhooks laufen in den POST-Limiter pro IP** (`PluginPostLimiter`) und werden als Formular gelesen. | `PluginPageModel.RunAsync`. | Stripe schickt Bursts von wenigen IPs; zu strenges Limit verliert Zahlungen. |
| **L7** | **Keine Mail-API.** `EmailService` + `MailTemplates` (Schlüssel, im Admin editierbar, Seeder legt `MatCMS.Shared.MailTemplates.All` an) gibt es, ein Plugin kommt nicht heran und kann keine eigenen Vorlagen anmelden. | Nichts in `Abstractions`. | Keine Bestell-, Versand-, Download-Mail. |
| **L8** | **Atomare Zahlen fehlen.** Bestand verringern und **lückenlose** Rechnungs-/Bestellnummern brauchen „Wert um n erhöhen, wenn Bedingung“ und eine fortlaufende Nummer in der Transaktion. `UpdateWhereAsync` setzt nur feste Werte. | `IDataTable`. | Überverkauf bei gleichzeitigen Bestellungen; Nummernlücken (steuerrechtlich ein Problem). |
| **L9** | **Private Dateien.** Digitale Produkte dürfen nicht unter `/uploads` (öffentlich) liegen. `PluginResult.File` nimmt `byte[]` — große Dateien gehen so nicht. | `PluginResult.File(byte[])`. | Download-Links wären erratbar/teilbar oder der Speicher läuft voll. |
| **L10** | **Eigene Template-Teile.** *(gebaut — siehe 3.10)* Früher: „Parts a plugin defines itself are not supported yet“. | `IPluginBuilder.TemplatePart`, `ctx.RenderPart`. | Produkt-, Karten- und Katalogseite sind im Template-Editor überschreibbar. |
| **L11** | **Host-Ereignisse für Konten fehlen.** Es gibt nur `FormSubmitted` und `PageSaved`. | `HostEvents`. | Kein DSGVO-Export/-Löschen beim Löschen eines Mitglieds; kein „Konto angelegt“-Haken. |
| **L12** | **Nur Skripte in der Cloud.** Native-Bundles zeigt der Store schreibgeschützt, `plugins.allowNative` ist absichtlich nicht ausrollbar. | Phase 5 teilweise. | Als Native-DLL müsste jeder Kunde den Shop von Hand freischalten **und** die DLL bekommen. Siehe Entscheidung E1. |

Was **nicht** fehlt: ein HTTP-Client für Stripe (ein Plugin darf `HttpClient` selbst nutzen; kein Stripe-SDK, die REST-Schnittstelle
reicht und hält die Abhängigkeiten klein), Jobs, Geheimnisse, Transaktionen. **Schicht 2 (EF) ist für Version 1 nicht nötig** — mit
L8 reicht Schicht 1 (Bestellpositionen als eigene Tabelle, Berichte als Abfragen). Phase 4 „Schicht 2“ bleibt deshalb offen und
blockiert den Shop nicht.

## 3. API-Erweiterungen (Phase 4a) — vor dem Shop, mit Tests und Konformitätstest

Jede Erweiterung bekommt einen Test im Konformitätstest (Script **und** Native liefern dasselbe) und einen Eintrag im
Katalog des Script-Editors (der folgt der Assembly, es braucht nichts Handgeschriebenes).

1. **`ctx.Member`** (L1) — `PluginMember?`: `Id`, `Username`, `DisplayName`, `Email`, `Roles`. Null für einen anonymen Besucher.
   Der Admin zählt **nie** als Mitglied (zwei Scheiben, wie bisher).
2. **`ctx.Members`** (L2) — `IPluginMembers`: `FindAsync(id|email)`, `CreateAsync(email, password, displayName)` (nur wenn die
   Selbstregistrierung erlaubt ist oder der Aufrufer ein Admin-Handler ist), `SignInAsync/SignOutAsync`, `SetPasswordAsync`,
   `CreateResetTokenAsync/ResetPasswordAsync`. **Kern-Änderung** (additiv, Migration): `SiteMember.Email` (nullable, eindeutig
   wenn gesetzt), `EmailConfirmedAt`, Einstellung `members.selfRegistration` (**Standard aus** — bestehende Seiten ändern sich
   nicht), Bestätigungs- und Reset-Mails über die Mail-API (7). Der **Anmeldebereich bleibt der des CMS** (`/anmelden`,
   Cookie `Member`); der Shop baut kein zweites Login. Kundendaten, die das Konto nicht kennt (Adressen), liegen in einer
   Tabelle des Shops mit `member_id`.
3. **`PublicRouteOptions.RequireMember` / `RequiredRole`** (L3) — der Host leitet sonst auf `/anmelden?returnUrl=…`, wie bei
   einer Mitglieder-Seite, und liefert nie den Handler aus.
4. **`RequestData`: `Headers`, `Body` (Bytes, Obergrenze), `Cookies`, `ClientIp`; `PluginResult`: `WithCookie(...)`,
   `WithHeader(...)`** (L4). Cookies des Plugins tragen den Schlüssel des Plugins als Präfix, sind `HttpOnly`, `Secure`,
   `SameSite=Lax`.
5. **`PublicRouteOptions.Antiforgery`** (L5) — schaltet Token-Prüfung für diese Route ein und füllt `ctx.Antiforgery`. Der
   Shop schaltet sie für alle Aktionen eines angemeldeten Kunden ein.
6. **`PublicRouteOptions.Webhook`** (L6) — kein Formular-Parsen, Rohkörper statt dessen, eigenes großzügiges Limit statt
   `PluginPostLimiter`, Antwort ohne Layout. Die Signatur prüft das Plugin (es kennt das Geheimnis).
7. **`ctx.Mail`** (L7) — `SendAsync(templateKey, to, tokens)` über `EmailService`, plus `Plugin.MailTemplate(key, subject, html)`
   zum Anmelden eigener Vorlagen (landen unter Admin → Mailvorlagen, dort editierbar, werden wie die übrigen geseedet und
   nie überschrieben). Versand schlägt nie die Bestellung fehl — Fehler gehen ins Plugin-Log und in eine Wiederholungs-Warteschlange.
8. **Atomare Daten** (L8) — `IDataTable.IncrementAsync(id, column, delta, whereColumnAtLeast?)` (Zeile nur ändern, wenn die
   Bedingung gilt; Rückgabe = geändert ja/nein) und `IPluginData.NextNumberAsync(sequence)` (in der Transaktion, ohne Lücke,
   solange die Transaktion durchläuft).
9. **`ctx.Files`** (L9) — privater Speicher `appdata/plugin-files/{key}/` (nicht öffentlich ausgeliefert, **im Backup**,
   vom Aufräumen ausgenommen); `PluginResult.FileStream(path, contentType, fileName)` streamt mit Range-Unterstützung.
   Downloads laufen über eine Route des Shops mit signiertem, befristetem Token (siehe 5.4).
10. **Eigene Template-Teile** (L10, **gebaut**) — `Plugin.TemplatePart(new TemplatePartSpec(name, label, defaultHtml) { Placeholders, Help })`:
    der Teil erscheint im Template-Editor als eigene Datei (`{plugin}-{name}.html`, Schlüssel `{plugin}.{name}` in `Template.PartsJson`),
    das aktive Template kann ihn überschreiben, leer/fehlend = Standard des Plugins. Gezeichnet wird mit `ctx.RenderPart(name, werte)`
    (`PartRenderer`: `{{name}}`, `{{#if name}}…{{/if}}`, `{{#unless name}}…{{/unless}}`; Werte werden kodiert, außer der Platzhalter ist
    als HTML deklariert; ein Wert wird nie erneut gelesen). Ein Plugin rendert nur **seine eigenen** Teile. Die Shop-Teile:
    `shop-card.html`, `shop-product.html`, `shop-catalog.html` (`ShopParts`). Warenkorb, Kasse und Kundenseiten bleiben im Plugin
    (Formulare, die funktionieren müssen) — ihr Aussehen ist das CSS des Shops und die Einstellung „eigenes CSS“. Die Cloud-Editoren
    für Templates behalten gespeicherte Plugin-Schlüssel (mit Punkt) beim Speichern, zeigen sie aber nicht.
11. **Host-Ereignisse** (L11) — `host.member.registered`, `host.member.deleted`, und der Erweiterungspunkt
    `host.member.export` (jedes Plugin liefert per `CollectAsync` seine Daten zu einem Mitglied für den DSGVO-Auszug) samt
    `host.member.erase`.

Reihenfolge innerhalb 4a: 4 → 6 → 8 → 1/2/3 → 7 → 5 → 9 → 11 → 10 (zuerst, was Stripe braucht, zuletzt die
Template-Teile; ein Shop mit Standard-Aussehen läuft schon ohne 10).

## 4. Entscheidungen, die noch offen sind

- **E1 — Bauart: built-in (aus) oder Native-DLL?** Der Entwurf von v2 sagt „Native, Standard aus“. Für „alle Kunden“ hat das
  Nachteile: `plugins.allowNative` ist nicht ausrollbar, die DLL käme über den Store (Native in der Cloud ist nur zur Hälfte da),
  und jede Instanz bräuchte erst die Freigabe für fremden Code. **Empfehlung: wie der Blog als eingebautes Plugin
  (`Kind=builtin`, Namespace `MatCMS.Plugin.Shop`, im Image, Standard AUS)** — der Kunde schaltet es im Admin ein, die Cloud
  verteilt es mit dem Image-Update, kein Fremdcode-Gate. Der Code bleibt trotzdem sauber getrennt und gegen dieselbe
  API geschrieben, ein späteres Herauslösen als Native-Bundle ist möglich. Preis: Image wächst um ein paar MB, Shop-Updates
  laufen im CMS-Release-Rhythmus.
- **E2 — Stripe im Shop-Assembly oder als eigenes Plugin?** **Empfehlung: im Shop**, hinter einer internen Schnittstelle
  `IPaymentProvider` (`StartAsync`, `HandleWebhookAsync`, `RefundAsync`); PayPal wird später die zweite Implementierung. Der
  Erweiterungspunkt `shop.payment` (Name + JSON) wird trotzdem gebaut und dokumentiert, damit Dritte Anbieter als Plugin
  ergänzen können. Ein Stripe-Plugin getrennt hieße zwei Pakete, zwei Updates, zwei Supportfälle pro Kunde.
- **E3 — Rechnungen als PDF?** Das CMS hat keine PDF-Bibliothek. Version 1: Rechnung als HTML-Seite (druckbar, als Datei
  speicherbar) plus lückenlose Nummer. **Entschieden und gebaut (15.10.):** das PDF kommt aus einem eigenen kleinen Schreiber (`ShopPdf.cs`, PDF 1.4, die 14 Standardschriften) — keine neue
  Abhängigkeit im Image, keine Schriftdateien; die Beschränkung (nur westeuropäische Zeichen, keine Bilder/Logo) ist der Preis dafür.

## 5. Der Shop

### 5.1 Daten (Schicht 1, Tabellen `p_shop_…`)

Geld immer **Cent als `Int`**, Währung je Shop (Standard EUR). Preise im Shop sind **Brutto** (B2C), der Steuersatz hängt am
Produkt; **jede Bestellposition speichert eine Momentaufnahme** (Name, Einzelpreis, Satz, Steuerbetrag) — spätere Preis- oder
Satzänderungen berühren alte Bestellungen nie.

| Tabelle | Inhalt |
| --- | --- |
| `products` | Slug (je Sprache über `translations` Json), Titel, Beschreibung (Rich-Text), Bilder, Art (`physical`/`digital`), Preis, Steuersatz, SKU, Bestand (`null` = unbegrenzt), Gewicht, Status, Kategorie |
| `variants` | Optionen (Größe/Farbe) mit eigenem Preisaufschlag, SKU, Bestand — v1: einfache Varianten, keine Kombinationsmatrix |
| `product_files` | Digitale Dateien eines Produkts (Verweis auf `ctx.Files`), Name, Größe, Download-Limit |
| `categories` | Name, Slug, Reihenfolge |
| `carts` | Token (Cookie), Mitglied (optional), Positionen als Json, Ablauf — ein Job räumt abgelaufene |
| `orders` | Nummer (lückenlos), Status, Mitglied (optional), E-Mail, Rechnungs-/Lieferadresse (Json-Momentaufnahme), Summen (netto, Steuer, Versand, brutto), Währung, Zahlart, Zahlungsreferenz, AGB-/Widerrufs-Version + Zeitpunkt der Zustimmung, Gast-Token |
| `order_items` | Bestellung, Produkt/Variante (Verweis **und** Momentaufnahme), Menge, Einzelpreis, Satz, Steuer |
| `order_events` | Verlauf: Statuswechsel, Zahlung, Erstattung, Versand, Mails — wer und wann |
| `payments` | Anbieter, Referenz, Betrag, Status, **Stripe-Ereignis-ID eindeutig** (Idempotenz), Rohdaten-Auszug |
| `addresses` | Adressbuch eines Mitglieds (`member_id`) |
| `shipping_zones`, `shipping_rates` | Länder → Tarife (Pauschale, nach Gewicht, ab Bestellwert frei) |
| `invoices` | Nummer (lückenlos), Bestellung, Datum, HTML-Momentaufnahme |
| `downloads` | Ausgegebene Download-Token: Bestellposition, Ablauf, Zähler |

### 5.2 Bestellstatus

`awaiting_payment → paid → fulfilled → completed`; Nebenwege `cancelled` (vor Zahlung, Bestand wird freigegeben),
`refunded`, `partially_refunded`, `payment_failed`/`expired`. **Ein digitales Produkt springt bei `paid` selbst auf
`fulfilled`** (Download-Mail geht raus), ein physisches wartet auf „Versendet“ (Sendungsnummer, Mail). Ein Statuswechsel
ist **ein Ort im Code** (`OrderStateMachine`), der erlaubte Übergänge kennt, den Bestandseffekt auslöst, einen
`order_events`-Eintrag schreibt und das Plugin-Ereignis (`shop.order.paid` usw.) und die Mail anstößt — nie ein direktes
`UpdateAsync` auf `status`.

### 5.3 Stripe (Checkout, gehostet)

Gewählt: **Stripe Checkout (gehostete Seite)**, nicht Elements — Kartendaten berühren den Server nie (PCI-Aufwand SAQ A), und
Stripe bietet dort Karte, SEPA, Klarna, Apple/Google Pay und je nach Konto PayPal an, ohne Zusatzcode.

1. Kasse → Server **rechnet den Preis selbst** (nie aus dem Formular), legt die Bestellung als `awaiting_payment` an und
   **reserviert den Bestand** (atomar, L8, Ablauf 30 Minuten).
2. `POST /v1/checkout/sessions` (REST, formular-codiert) mit `Idempotency-Key = Bestellnummer`, Positionen als `price_data`
   (keine Stripe-Produkte nötig), `client_reference_id`, `metadata[order]`, `expires_at`; Weiterleitung auf die Stripe-URL.
3. **Bezahlt wird allein durch den Webhook** (`checkout.session.completed` mit `payment_status=paid`, bei
   verzögerten Zahlarten `async_payment_succeeded`). Die Danke-Seite vertraut dem Browser **nicht**: sie liest den Zustand aus
   der Datenbank und fragt höchstens die Session bei Stripe ab (Abgleich, falls der Webhook noch fehlt).
4. **Webhook** (`PublicRouteOptions.Webhook`): Signatur prüfen (`Stripe-Signature: t=…,v1=…`, HMAC-SHA256 über `t.Rohkörper`
   mit dem Endpoint-Geheimnis, Toleranz 5 Minuten, Vergleich in konstanter Zeit); danach `payments`-Zeile mit eindeutiger
   Ereignis-ID — ein wiederholtes Ereignis ist ein No-op. Verarbeitet: `completed`, `expired` (Bestand frei),
   `async_payment_failed`, `charge.refunded`/`refund.updated`.
5. **Erstattung** aus der Bestell-Detailseite (`POST /v1/refunds`, ganz oder teilweise), Status wird erst vom Webhook
   bestätigt.
6. Ein Job gleicht stündlich offene `awaiting_payment`-Bestellungen mit Stripe ab (verlorener Webhook) und gibt abgelaufene frei.
7. **Test- und Live-Schlüssel** getrennt in den Einstellungen (Secret), ein Schalter „Testmodus“ mit sichtbarem Hinweis im Admin.
   Lokal prüft `stripe listen --forward-to …` den Webhook.

### 5.4 Digitale Produkte

Dateien liegen in `ctx.Files` (privat). Nach `paid` entsteht pro Position ein `downloads`-Token (signiert, Ablauf z. B. 30 Tage,
Zähler, Limit). Die Mail enthält `/shop/download/{token}`; die Route streamt, zählt, prüft Ablauf und Limit. Eingeloggte Kunden
sehen die Downloads zusätzlich unter `/konto/bestellungen/{nr}`. Ein erstattetes Produkt sperrt seine Token.

### 5.5 Kundenbereich und Gäste

- **Gast-Kasse** ist möglich (Mail + Adresse), die Bestellung ist über einen Link mit Gast-Token in der Mail einsehbar.
  Wer sich beim Bestellen registriert (L2, Selbstregistrierung an), bekommt die Bestellung dem Konto zugeordnet; eine Bestellung
  mit gleicher E-Mail kann nachträglich über die bestätigte Adresse übernommen werden.
- **`/konto`** (`RequireMember`): Bestellungen mit Status und Verlauf, Rechnung, Downloads, Adressbuch, Passwort ändern, Konto
  löschen (löst `host.member.erase` aus; Bestellungen bleiben **anonymisiert** erhalten, weil Rechnungen aufbewahrt werden müssen).
- Anmeldung, Registrierung, Passwort-Reset: die Seiten des CMS (`/anmelden`, Block `memberlogin`); das Template-`LoginHtml`
  gilt weiter.

### 5.6 Admin (Bereich „Shop“, Unterpunkte)

**Menü der Website:** Warenkorb, Konto/Anmelden, Suchfeld und „Shop“ mit Kategorien sind **Menü-Elemente** (Admin → Menü, Auswahl „Plugin-Element“); beim ersten Laden setzt der Host sie einmalig in „Obere Leiste“ bzw. „Hauptmenü“, danach sind es normale Menüpunkte (verschieben, umbenennen, löschen — Löschen bleibt). Die Einstellungen `menu.shop`/`menu.icons` gibt es nicht mehr. Das Suchfeld ist der Template-Teil `shop-search.html` (Klasse `mat-search`). Link-Ziele: Shop-Seiten, Kategorien (mit Pfad) und Produkte stehen in jeder Link-Auswahl.

**Kategorien** sind ein Baum (Elternkategorie, Bild, Beschreibung): „in der Kategorie“ heißt sie selbst und alles darunter (Filter, Zähler, Gutschein-Bereich); Löschen schiebt Unterkategorien, Produkte und Gutscheine eine Ebene hoch. Listen im Admin haben die gerahmte Werkzeugleiste über der Tabelle (Suche, Filter) und die Anlegen-Taste darunter.

**Stand nach dem Umbau:** der Shop ist **eine** Zeile in der Hauptleiste; ein Klick öffnet das **Dashboard** und ersetzt die Seitenleiste durch das Shop-Menü mit „Zurück“
(Übersicht, Bestellungen, Produkte, Kategorien, Kunden, Konditionen, Einstellungen — auf jeder Shop-Seite; `IAdminSection.Link`, `AdminNav.SubmenuAsync`). Das Dashboard (`ShopAdmin.DashboardAsync`)
zeigt zu versenden / wartet auf Zahlung / Umsatz 30 Tage / aktive Produkte, letzte Bestellungen, Hinweise und Bestseller; die Bestellung ist zweispaltig (Positionen + Verlauf | Details, Kunde, Aktionen). *Konditionen*
(Zahlung, Steuern, Rechtliches, Nummern, Gutscheine, Versand) und *Einstellungen* sind je ein Bildschirm mit Untermenü links
(`Ui.Split`/`Ui.NavItem`); Einstellungen werden pro Gruppe gespeichert. Die Produktliste zeigt Vorschaubilder, der Editor ist
zweispaltig (`Ui.Columns`). Menü der Seite: Shop/Konto/Warenkorb-Einträge kommen über `IPluginBuilder.Menu` in **jedes** Theme
(Fallback: eine Leiste über der Seite, wenn das Layout das Menü selbst zeichnet); Blöcke: Produkte (Raster/Karussell, „Bestseller“),
Kategorien, Suche, Warenkorb, Konto.

**Bestellungen** (Liste mit Status-/Datumsfilter, Suche nach Nummer/E-Mail/Name, Detail mit Positionen, Adressen, Zahlung, Verlauf;
Aktionen: als versendet markieren mit Sendungsnummer, erstatten, stornieren, Mails erneut senden, Notiz) ·
**Produkte** (Liste, Editor aus den Bausteinen der UI-Bibliothek: Rich-Text, Bildliste, Dateien) · **Kunden** ·
**Versand & Steuern** · **Einstellungen** (Stripe, Shop-Name, Absender, AGB-/Widerrufs-/Datenschutz-Seite, Währung, Kleinunternehmer,
Bestellnummern-Format). Dashboard: Kachel „Bestellungen heute/offen“, Aufmerksamkeit bei „bezahlt, nicht versendet seit 3 Tagen“ und
niedrigem Bestand.

### 5.7 Recht (Datenmodell trägt es, die Texte liefert der Betreiber)

Preise brutto mit Hinweis, Versandkosten vor der Bestellung, Button „zahlungspflichtig bestellen“, Checkbox AGB/Widerruf **mit
gespeicherter Version und Zeitpunkt**, bei digitalen Inhalten die Zustimmung zum vorzeitigen Erlöschen des Widerrufsrechts,
Kleinunternehmer-Hinweis (ohne ausgewiesene Steuer), lückenlose Rechnungsnummer, DSGVO-Auszug/-Löschung über die Hooks aus 3.11.
Die Umsatzsteuer für Lieferungen ins EU-Ausland (OSS: Steuersatz des Kundenlandes) ist als Einstellung gebaut (siehe „Stand der Umsetzung“); Versand ins Ausland gibt es nur, wo
der Betreiber Zonen anlegt. Nicht behandelt sind Reverse Charge für Geschäftskunden und die Lieferschwelle. Das ist **keine Rechtsberatung**; die Pflichttexte und Abläufe sollte vor dem Einsatz bei Kunden jemand Fachkundiges prüfen.

### 5.8 Sicherheit

Preise und Summen nur serverseitig · Webhook-Signatur und Idempotenz · Antiforgery auf Kundenaktionen · Bestellansicht nur für den
Besitzer oder per Gast-Token (kein Durchzählen der Nummern) · Download-Token signiert, befristet, gezählt · Rate-Limit auf
Anmeldung, Registrierung, Reset und Kasse · Admin- und Mitglieder-Anmeldung bleiben getrennt · Geheimnisse nur im `Secret`-Feld,
nie im Log · Stripe-Antworten werden nicht ungeprüft in HTML gesetzt (alles über den Renderer, der kodiert).

## 6. Tests

- **Einheit:** Geld und Rundung (Brutto → Steuer je Position, Summe), Bestellzustandsmaschine (jeder erlaubte und jeder
  verbotene Übergang), Webhook-Signatur (gültig, manipuliert, zu alt), Idempotenz (dasselbe Ereignis zweimal), Bestands-Wettlauf
  (zwei gleichzeitige Käufe des letzten Stücks), lückenlose Nummer unter Parallelität.
- **Konformität:** jede API-Erweiterung aus 3 als Script **und** Native (dasselbe Ergebnis).
- **Ende zu Ende:** `AppFactory` mit einem **gefälschten Stripe** (`HttpMessageHandler` + selbst signierte Webhooks):
  Produkt anlegen → Warenkorb → Kasse → Webhook → Bestellung bezahlt → Mail erfasst → digitaler Download → Erstattung; als Gast und
  als Mitglied; Mitglied A sieht nie die Bestellung von B.
- **Echt:** ein Durchlauf im Stripe-**Testmodus** mit den Testschlüsseln und `stripe listen` — nur auf deine Freigabe, in einem Container
  mit eigenem Volume und eigenem Port (nie gegen eine Produktiv-DB).

## 7. Phasen

| Phase | Inhalt |
| --- | --- |
| **4a** | API-Erweiterungen aus 3 (Reihenfolge dort), Kern-Migration `SiteMember.Email`, Selbstregistrierung (aus), Mitglieder-Mails, Tests |
| **4b** | Shop-Kern: Katalog, Warenkorb, Kasse, Bestellungen und Verlauf im Admin, **Vorkasse** als erste Zahlart (beweist Kasse und Zustandsmaschine ohne Fremdsystem) |
| **4c** | Stripe Checkout, Webhook, Erstattung, Abgleich-Job, Test mit gefälschtem Stripe, dann Testmodus echt |
| **4d** | Kundenbereich `/konto`, Gast-Link, Adressbuch, DSGVO-Hooks |
| **4e** | Digitale Produkte: private Dateien, Download-Token, Mail |
| **4f** | Versand-Zonen/Tarife, Steuern, Rechnung (HTML), Dashboard/Aufmerksamkeit, Template-Teile (L10), Cloud-Operationen (Produkte per MCP) |
| **5** | PayPal als zweiter Anbieter hinter `IPaymentProvider`; ggf. Native-Bundle-Weg in der Cloud, falls E1 anders fällt |

Nach jeder Phase: Tests grün, CLAUDE.md und dieses Dokument nachgezogen, ein Container-Praxistest.

## 8. Einrichten und prüfen (für den Betreiber)

1. **Einschalten:** Admin → Plugins → „Shop“ (ist auf einer neuen Seite aus). Danach gibt es im Admin den Bereich **Shop** (Bestellungen, Produkte, Kategorien, Kunden,
   Versand, Einstellungen) und im Shop `/shop` und `/konto`. Ausschalten nimmt Seiten, Routen und Menü weg und lässt alle Daten stehen; Löschen geht nicht.
2. **Einstellungen** (Shop → Einstellungen): Verkäufer (Firma, Anschrift, USt-IdNr. — Pflicht für eine ordnungsgemäße Rechnung), Währung, Kleinunternehmer,
   Steuersatz für Versand, Rechtstexte (Adressen der AGB, Widerrufsbelehrung, Datenschutzerklärung und die **Version**, die mit jeder Bestellung gespeichert wird),
   Nummernkreise, Kasse (Bestellen ohne Konto, Konto anbieten), Benachrichtigungsadresse, Downloads, Vorkasse (Bankverbindung, Zahlungsfrist).
3. **Mail:** der Shop verschickt über den SMTP-Zugang der Seite (oder das Relay der Cloud). Ohne Mailversand läuft alles weiter; jeder fehlgeschlagene Versand steht im
   Verlauf der Bestellung, und „E-Mails → Erneut senden“ versucht es noch einmal. Die Texte der sechs Shop-Mails stehen unter **Mailvorlagen** und sind dort änderbar.
4. **Stripe** (Online-Zahlung):
   - Stripe-Dashboard → Entwickler → API-Schlüssel: den **geheimen Schlüssel** (`sk_test_…` zum Üben, `sk_live_…` echt) in Shop → Einstellungen eintragen.
   - Stripe-Dashboard → Entwickler → Webhooks: Endpunkt `https://IHRE-DOMAIN/shop/webhook/stripe` mit den Ereignissen `checkout.session.completed`,
     `checkout.session.expired`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`, `charge.refunded`; das **Signaturgeheimnis**
     (`whsec_…`) in die Einstellungen eintragen. Ohne Webhook erfährt der Shop nichts von Zahlungen (er fragt Stripe nur ab, wenn ein Kunde zurückkommt und stündlich für
     offene Bestellungen) — das Dashboard „Braucht Aufmerksamkeit“ warnt davor.
   - **Testen ohne öffentliche Adresse:** Stripe CLI: `stripe listen --forward-to http://localhost:9101/shop/webhook/stripe` gibt ein `whsec_…` aus; Kartennummer
     `4242 4242 4242 4242`, beliebiges Datum und CVC. Stripe zeigt die Testmodus-Zahlungen getrennt von den echten; der Shop zeigt „(Testmodus)“ im Verlauf der Bestellung.
   - Die Zahlung läuft auf Stripes Seite (Checkout): Kartendaten berühren diesen Server nie; welche Zahlarten erscheinen (Karte, SEPA, Klarna, …), bestimmt das Dashboard.
   **PayPal** (zusätzlich oder statt Stripe):
   - developer.paypal.com → Apps & Credentials: eine App anlegen (zum Üben im Reiter **Sandbox**, für den Betrieb **Live**); **Client-ID** und **Secret** in Shop → Einstellungen → PayPal eintragen.
     Der Schalter „Testumgebung (Sandbox)“ muss zu den Zugangsdaten passen.
   - In derselben App → Webhooks: Adresse `https://IHRE-DOMAIN/shop/webhook/paypal` mit den Ereignissen *Checkout order approved* und *Payment capture completed / pending / denied / refunded /
     reversed*; die **Webhook-ID** in die Einstellungen eintragen. Ohne Webhook zählt nur, wer zum Shop zurückkehrt (alle 15 Minuten fragt der Shop offene Bestellungen selbst ab) — „Braucht
     Aufmerksamkeit“ erinnert daran.
   - Test: ein Sandbox-Käuferkonto (developer.paypal.com → Sandbox → Accounts) bezahlt eine Bestellung; danach „Erstattung“ in der Bestellung.
5. **Versand:** Shop → Versand legt die Versandarten an (Länder, Preis brutto, kostenlos ab, Höchstgewicht). Ein erster Satz „Standardversand Deutschland 4,90 €, frei ab 50 €“
   wird beim ersten Einschalten angelegt. Digitale Produkte brauchen keinen Versand.
6. **Produkte:** Shop → Produkte. Preis ist **brutto**; Steuersatz je Produkt; Varianten als Zeilen `Name | Aufpreis | Bestand | Art.-Nr.`; Bestand zählen oder
   unbegrenzt; bei **digitalen** Produkten nach dem ersten Speichern die Dateien hochladen (privat, nie öffentlich erreichbar). Der Block „Shop-Produkte“ zeigt Produkte auf
   jeder Seite.
7. **Produkte ordnen:** Kategorien (Shop → Kategorien, eine je Produkt, mit Zähler) gliedern grob; **Schlagwörter** (am Produkt, frei, mit Komma — z. B. „Bio, Geschenk“) quer
   dazu, so oft man will. Besucher filtern im Shop danach; im Seiten-Editor gibt es die Blöcke **Shop-Produkte** (aus Kategorie, Schlagwort, „Hervorgehoben“ oder handverlesen,
   als Karten oder Liste) und **Einzelnes Produkt**. „Hervorgehoben“ am Produkt entscheidet, was ein Block „Hervorgehobene Produkte“ zeigt.
8. **Gutscheine:** Shop → Gutscheine → Neuer Gutschein: Art (Prozent / Festbetrag / Versand frei), Wert, Mindest-Warenwert, Zeitraum, Höchstzahl, „nur einmal je E-Mail-Adresse“, ggf. eine
   Kategorie. Code leer lassen = es wird einer erzeugt. Der Kunde löst ihn im **Warenkorb** ein; der Rabatt steht in Bestellung, Rechnung und Mail-Summe. Ein Gutschein, der abläuft oder aufgebraucht
   ist, wird dem Kunden mit Begründung gesagt und nie still berechnet. Ein Agent legt Gutscheine mit dem MCP-Werkzeug `create_coupon` an.
9. **Rechnungen und Gutschriften:** Beim Bezahlen entsteht die Rechnung (Seite und PDF), bei jeder Erstattung eine Gutschrift. Beide erreicht der Kunde auf seiner Bestellseite, Sie in der Bestellung
   im Admin (Knöpfe „Rechnung“, „PDF“, „Gutschrift …“). Nummernkreise und Präfixe: Einstellungen → Nummern. Kopf und Fußzeile des PDFs sind Verkäufer-Daten und „Fußzeile der Rechnung“.
10. **Steuer nach Lieferland:** nur einschalten, wenn Sie wirklich OSS anwenden (Einstellungen → Währung und Steuer → „Umsatzsteuer nach“); prüfen Sie die Sätze (Feld „Eigene Steuersätze je Land“
    überschreibt je Zeile, z. B. `FR 20 5,5`). Dazu brauchen Sie Versandarten, die die Zielländer abdecken (Shop → Versand). Geschäftskunden mit USt-IdNr. behandelt der Shop nicht.
11. **Bestellungen:** Vorkasse → nach Zahlungseingang „Als bezahlt markieren“ (Rechnung und Downloads entstehen, der Kunde wird benachrichtigt); Stripe-Bestellungen werden
   vom Webhook bezahlt. Physische Bestellungen: „Als versendet markieren“ (mit Sendungsnummer), danach „Abgeschlossen“. Erstattung aus der Bestellung (Stripe zahlt zurück,
   bei Vorkasse wird die eigene Überweisung erfasst).

## 9. Grenzen und Bekanntes

- **Recht:** der Shop speichert, was ein Händler nachweisen muss (AGB-Version und Zeitpunkt der Zustimmung, Verzicht auf Widerruf bei digitalen Inhalten, lückenlose
  Rechnungsnummern, Rechnung als unveränderlicher Stand) — die **Texte und Abläufe** (AGB, Widerrufsbelehrung, Preisangaben, Button „zahlungspflichtig bestellen“ ist
  gesetzt) sind Sache des Betreibers und sollten vor dem Einsatz bei einem Kunden von jemandem geprüft werden, der das darf. Dies ist keine Rechtsberatung.
- **Umsatzsteuer:** standardmäßig ein Satz je Produkt überall; mit der Einstellung „Lieferland (EU-OSS)“ der Satz des Ziellandes und außerhalb der EU keine Umsatzsteuer. Die eingebauten Sätze sind ein
  Richtwert (Stand 2026), die ermäßigten je Land nur die häufigste Stufe — ein Betreiber mit abweichenden Waren trägt die Sätze selbst ein. Nicht behandelt: Reverse Charge (Geschäftskunden mit USt-IdNr.),
  die 10.000-€-Lieferschwelle, Sondergebiete (Büsingen, Helgoland …), Preisauszeichnung nach der Omnibus-Richtlinie (30-Tage-Tiefstpreis beim Statt-Preis). Kleinunternehmer-Modus schaltet die Steuer
  überall ab (auch in der Rechnung, mit dem Hinweis nach § 19 UStG).
- **Rechnung und Gutschrift:** als Seite und als PDF (nur Text, kein Logo; westeuropäische Zeichen), einmal geschrieben und gespeichert. Eine E-Rechnung im Sinne von ZUGFeRD/XRechnung ist das nicht.
  Eine Gutschrift entsteht bei jeder Erstattung zu einer Rechnung; wer eine Erstattung ohne Rechnung bucht (nicht bezahlte Bestellungen sind nicht erstattbar), bekommt keine.
- **PayPal live ist hier nicht durchlaufen worden** (es gab keine Zugangsdaten); getestet ist gegen einen PayPal-Ersatz, der Orders, Capture, Erstattung und die Webhook-Prüfung nachbildet.
  Ein Durchlauf in der Sandbox (Abschnitt 8, Punkt 4) ist vor dem ersten echten Verkauf sinnvoll. Ein Zahlungsstatus, den PayPal nur über sein Konto-Dashboard ändert (z. B. eine Rückbuchung), kommt
  als Hinweis im Verlauf an, nicht als Statusänderung.
- **Stripe live ist hier nicht durchlaufen worden** (es gab keine Schlüssel). Getestet ist gegen einen Stripe-Ersatz, der Stripes Antworten, Signaturen und
  Wiederholungen nachbildet; ein Durchlauf im Testmodus (Abschnitt 8, Punkt 4) ist vor dem ersten echten Verkauf sinnvoll.
- **Daten:** SQLite. Mehrere Bestellungen gleichzeitig sind getestet (Wettlauf um das letzte Stück, gleichzeitige Webhooks und Klicks, parallele Downloads); ein Shop mit sehr vielen
  gleichzeitigen Zugriffen wäre auf einer anderen Datenbank besser aufgehoben.
- **Kundenkonto:** Registrierung im Shop nur beim Bestellen (oder auf `/registrieren`, wenn der Betreiber sie einschaltet); Bestellungen eines Gastes erscheinen im Konto erst,
  wenn die E-Mail-Adresse bestätigt ist. Beim Löschen des Kontos bleiben Bestellungen und Rechnungen (ohne Verbindung zum Konto) bestehen — Aufbewahrungspflicht.
- **Offen** (der vollständige Vergleich mit Shopify steht in `docs/shop-vs-shopify.md`): echte Testläufe mit Stripe und PayPal; E-Rechnung (ZUGFeRD); Reverse Charge; mehrere Kategorien je Produkt;
  Warenkorb-Knopf direkt auf der Produktkarte.

## Addresses

| Page | Address |
|---|---|
| Catalogue | `/shop` (filters `?tag=`, `?q=`, `?sort=`, `?page=`) |
| A category (and everything below it) | `/shop/categories/{slug}` — the same filters apply; the old `/shop?cat={slug}` redirects here (301) |
| A product | `/shop/{slug}` |
| Cart, checkout, order, invoice | `/shop/cart`, `/shop/checkout`, `/shop/order/{number}`, `/shop/invoice/{number}` |

All links are built by `ShopUrls`. Whether the shop shows up in the site's menu is not a shop setting: "Shop", cart, account and search are menu elements, ordinary menu items an editor adds or removes in Admin → Menü.

## Pages, cart, invoice branding

- `/shop/categories` lists the top-level categories as tiles; with `?tag=Neuware` it lists every category (at any level) that carries that keyword. Keywords on categories are typed in the category's edit page (comma-separated, like a product's). The template part `categories` (`shop-categories.html`) has the placeholders `title`, `total`, `tag`, `crumbs_html`, `tags_html`, `tiles_html` and `list_html` (the whole tree as a nested list).
- `/shop/tags` lists all keywords of the products with their counts (part `tags`).
- Menu elements: "Kategorien (mehrstufig)" (the category tree as a multi-level dropdown; the heading opens `/shop/categories`) and "Schlagwörter". The link picker also offers "Shop – Kategorien nach Schlagwort" and "Shop – Schlagwörter".
- The cart page: lines with a picture, quantity field (changes are posted when it changes) and "Entfernen"; a summary on the right (one big button to the checkout, the coupon box); an empty cart offers a way on (button, top categories, a few products).
- The invoice and credit note (HTML and PDF) carry a logo and an accent colour: Settings → Seller → "Logo auf Rechnungen" and "Akzentfarbe der Rechnung". The PDF embeds PNG/JPEG/WebP/GIF logos from the media library (as JPEG, transparency on white); an SVG logo shows on the HTML page only. An invoice that was already written keeps what it had — the setting applies to documents written afterwards.

## Attributes, catalogue switches

- **Attributes** (Admin → Shop → Eigenschaften): colour, size, material, weight … Kinds: choice (a list of options, a product may have several), text, number (with a unit; filtered by a range), yes/no. An attribute may apply to all products or to one category and everything below it, and may be offered as a filter. The product form shows a field for every attribute that applies to its category (a category-limited attribute appears once the product is saved in that category).
- On the catalogue the filters sit above the products (`filters_html` in the catalogue part): `?a.farbe=Rot,Blau` (any of these values), `?a.gewicht.min=1&a.gewicht.max=5`; across attributes all must match. The counts are over the products the page would show without the attribute filters. The product page lists the attributes the product has (`attributes_html`).
- **Settings → Katalog und Suche:** show or hide the search box, sorting, the category chips, the keyword row and the attribute filters on `/shop`; what a search looks at (name and article number / + summary and keywords / + description). **Kategorien-Seite:** the keyword links, the product count, the text and the subcategories of a card.
- The orders list in the back office has the status as a drop-down in the toolbar and a traffic light per row; the order page has its next steps as buttons in the head.

## Catalogue controls, tracking link, customer area (theme-able)

- **Everything public is theme-able:** a page is a template part (`shop-{name}.html`, the active template may replace it) with placeholders, and the CSS uses the shop's own variables with the SITE's variables behind them (`var(--shop-accent, var(--accent, #1a1a1a))`, `--shop-line` → `--line`, `--shop-bg` → `--bg`, headings `--font-head`) — a site's accent, lines and heading font reach the shop without a line of shop CSS. A new page gets a part and keeps the old placeholders filled so parts a template already holds do not break.
- **The catalogue page** is the usual shop layout: the search field on top (`search_html`, a rounded box with the magnifier), a side column (`categories_html` = the category list with the branch being looked at opened, `filters_html` = the price range and the attribute filters as collapsible groups with boxes and counts, `tags_html` = keywords) and the main column (a toolbar with `count_html` and `sort_html`, the chips of what is switched on `active_html`, each with its own × and one "Filter zurücksetzen", then `grid_html`, `pager_html`). On a phone the side column folds behind a "Filter" button (a checkbox + label, no script). `layout_class` is `no-side` when every control is switched off. The price range is `?price.min=5&price.max=20` (euros, comma or point; a reversed pair is turned around; the product's own gross price). Code: `ShopCatalogUi.cs` (`CatalogState` carries what the page was asked for and builds every link and hidden field, so a filter survives the other controls).
- **Tracking link:** an order carries a carrier (`orders.carrier`: a key of `ShopTracking.All` — DHL, DHL Express, DPD, Hermes, GLS, UPS, FedEx, Österreichische Post, Die Post — or `custom`) next to its tracking number. The link is built ONLY from a fixed https template with the number escaped into it (`ShopTracking.Url`); a number that is itself an https address is used as it is; the operator's own template (setting `track.url`, must be https and contain `{n}`) serves `custom`. The ship dialog has a carrier drop-down (preselected from the setting `track.carrier`), the order page in the back office a tracking card (change carrier and number without shipping again). The customer sees carrier, number and a "Sendung verfolgen" button on the order page and in the account's order cards; the shipping mail's `{{tracking}}` is the number with the link in the line below (existing mail templates get the link without being edited), `{{tracking_url}}` and `{{carrier}}` are there for templates that want them apart.
- **The customer area (`/konto…`)** is the template part `account` (`shop-account.html`; placeholders `title`, `section`, `name`, `email`, `initials`, `nav_html`, `signout_html`, `content_html`): who is signed in (round picture with initials), the navigation with icons, the way out, and the page. Overview = three tiles (orders, in progress, last order), the latest orders as cards, the default address and the account; "Meine Bestellungen" = order cards (number, date, state with a progress bar while the order is on its way, the product pictures, the total, buttons: details, track the parcel, invoice); addresses = cards with the default marked; profile = cards, deleting the account set apart. Forms, field names and actions are unchanged.

## Variants editor, tax drop-downs, the customer page in the back office

- **Variants are rows, not a text** (`ShopVariants.cs`, `Ui.RowEditor`): the product form's "Varianten" card has the variants as an editable table (name, surcharge, stock — empty = not counted —, article number; add and delete rows in place) and, below it, up to three **options** (name + values as chips). "Kombinationen erzeugen" saves like any save and adds a variant ("M / Rot") for every combination that is not there yet (existing rows keep price and stock; at most 60 variants). A row is identified by its **id**, so renaming a variant keeps its stock and what old orders point at; a row that is taken out is switched off, never deleted; a new row brings back a switched-off variant of the same name. The options are remembered on the product (`products.var_options`, JSON) only for the editor — what is sold are the variants.
- **`Ui.RowEditor`** (plugin UI toolkit; `UiRowEditor`, `UiRowColumn`, `UiRowValue`): rows the user adds to, edits and deletes in place. Posts `{name}.count` and `{name}.{i}.id` / `{name}.{i}.{column key}`, numbered in screen order when the form is sent (`ui-fields.js` renumbers on submit, so no gaps); cells are text, number or switch; an empty label draws no label. Any plugin can use it.
- **Tax rates are drop-downs:** on the product (the home country's standard and reduced rate and "steuerfrei" first, then every other rate the tables know, and the product's own when none lists it — saving never changes a tax silently) and in the settings "Standard-Steuersatz" and "Steuersatz auf Versandkosten" (`ShopTaxChoices.cs`).
- **A customer has a page** in the back office (`customers/{key}`, `ShopAdminCustomers.cs`; the key is the e-mail address in a route-safe form): contact, the addresses the orders used (billing, shipping, earlier ones) and those saved in the account, totals, and the orders; reached from the customer list, the order list and the order page.

## Shop pages in the block editor (CMS 1.13)

- **The idea:** a plugin can declare **pages** (`IPluginBuilder.PageRole`) that the site's editors lay out in the block editor like any other page — sections, columns, headings, texts, images and the plugin's own blocks — with **variables** for the data of the request: `{{product.title}}` in a heading, `{{#if product.on_sale}}…{{/if}}` in a text, a block shown only while a variable has a value ("Nur anzeigen, wenn …" in the block's settings; `!name` = only when it has none). The plugin's route stays the owner of the address, the data and the forms; it only asks the host to draw the page's blocks with its values (`ctx.RenderPageAsync`) and falls back to its own markup (the template part) when there is no such page or it is switched off.
- **The shop's roles** (`ShopPageRoles.cs`): `catalog` (/shop, a category, the search), `categories`, `tags`, `product`, `cart`, `checkout`, `order`, `account` (the frame of /konto with its pages). They exist as pages "Shop – …" in Admin → Seiten (a badge "Seite des Plugins", no address, not deletable, language and address locked; "Auf Standard zurücksetzen" in the page settings), one per content language, created when the shop loads. Each starts with a layout that draws what the page showed before (`ShopPageLayouts.cs`).
- **Variables** (listed under "Variablen" in the editor; click to put one into the field being typed in): the values the page's handler computes — `product.title`, `product.price`, `cart.pieces`, `order.number`, `account.name` … — plus `site.name` and `member.name/email/signed_in` on every plugin page. Variables that hold HTML the shop built (`product.image_html`, `cart.lines_html`, `checkout.form_html` …) are placed with the block **"Shop-Baustein"** (`shop_slot`; the editor tree names it by what it places). The shop's forms stay whole: the buy form, the cart's lines, the checkout form are one piece each — layout and words around them are free.
- **Encoding follows the field** (`BlockScope`): a plain field (text, link, image) gets a value as it is (the block draws it encoded), a field drawn as HTML (rich text, the HTML block) gets it encoded; only a variable the plugin declared as HTML is put in raw. An unknown variable is empty; a value is never read again (braces in a name do nothing).
- **Preview:** the editor opens a real example of the route (a product, the catalogue, the newest order) with `?editor=1`, so the page is drawn by the shop's own route in the site's layout; the live preview of the unsaved draft is drawn with the example's variables. Pages that need a customer or a full cart (checkout, customer area) get sample pieces drawn with the shop's classes.
- **Precedence:** a published block page wins over the template part of the same name. A site that had its own version of such a part when the pages were created keeps it: the page is created **switched off** and the part keeps drawing the page until the page is switched on (the part files in the template editor say so). The parts remain the fallback, and their placeholders are unchanged.
- **Core pieces:** `Page.OwnerPlugin`/`PluginRole` (+ migration), `Content/BlockScope.cs`, `Services/Plugins/PluginPageService.cs` (create, find, draw, preview, reset), `Pages/Shared/_PreviewChrome.cshtml` (the editor's preview script, shared by ordinary and plugin pages), plugin blocks and the HTML block may sit inside sections and columns, `wwwroot/js/plugin-page-vars.js`. Backups carry the owner; plugin pages are not in the sitemap, the link pickers or the cloud's page list, and their own address answers 404 (except to an administrator in the editor's preview).

## Catalogue depth, data tools and storefront comforts (CMS 1.13, packages A and B)

What `docs/shop-vs-shopify.md` listed as missing, built and tested; the rules that are easy to break:

- **More than one category, and rule collections** (`ShopModel.cs`, `ShopCollections.cs`, `ShopStoreExtras.cs`): a product has ONE main category (`category`) plus further ones (`cats_extra`, comma list); a category may carry a **rule**
  (`rule`: `tag:Neuware; price<20; new:30; sale; instock; featured`, at most five clauses, ALL must hold, an empty or invalid rule picks nothing — `ShopCollectionRule.Clean/Matches`). **Membership is computed in one place,
  `ShopMembership`** (main, further, rule, and "everything below": a category counts what its descendants hold, each product once). `FindProductsAsync` filters by category in memory through it and `FacetsAsync` counts through
  it — never write a second "which category is this in" query. Deleting a category cleans extra-category lists and moves children up as before.
- **Moved addresses and search-engine fields:** changing a product's slug writes the old one to `slug_redirects` (`ShopStore.RememberSlugAsync`); the product route answers an unknown slug with a **301** to the product that owns it
  (active products only — a hidden product's old address stays a 404). `seo_title`/`seo_description` replace title and summary in `<title>`, the description meta and the Open-Graph tags (`PageView.Meta`).
- **Translations of product data** (`translations` table: kind `product|category`, ref id, locale, title, summary, description, seo fields; unique per (kind, ref, locale)): the main language is the base, a translation only
  OVERLAYS what it has (blank = fall back). `ShopStore.Public(ctx)` returns a store that overlays for the request's locale — the public pages, blocks, menu elements and checkout use it; **an order stores the title the customer saw**
  (`PlaceAsync` builds its store with the order's locale). Admin: one card per site language on the product and the category (`ShopAdminTranslations.cs`). Attribute names are not translated yet.
- **Duplicate a product** (`DuplicateProductAsync`): a new inactive product "… (Kopie)" with its own slug, variants, attribute values and translations; the article number is emptied (a copy must not share it).
- **Variants in the storefront** (`ShopVariantPicker.cs`): when the product's `var_options` match the variants' names ("M / Rot" = values joined by " / ", the way "Kombinationen erzeugen" names them) the page shows **one picker per
  option** (chips, arrow keys, `role=radiogroup`); the hidden `variant` field, the price (`[data-shop-price]` inside `price_html`) and the main picture (the variant's `image`) follow the choice; a sold-out or missing combination
  disables the button and says so. Anything that does not fit keeps the plain list; a `noscript` copy of the list keeps the form working without script. **The picker is derived, not stored** — no new data. A variant's `image`
  also shows in the cart and checkout lines.
- **Shipping estimate in the cart** (`ShopCartExtras.cs`): `GET /shop/cart?ship=AT` lists each way of shipping with its price (the same `ShopTax.Apply` + `ShopOrders.ShippingOptions` the checkout uses), the total from the cheapest and
  "noch X bis gratis". Countries offered = those an active rate covers; an unknown value falls back to the home country. Nothing is stored.
- **Direct-pay buttons** under the cart's checkout button (`ExpressHtml`): links to `/shop/checkout?pay=paypal|stripe` (only for methods that are set up); the checkout preselects a method that is offered. **This is not a one-click
  wallet checkout** — that needs the order to be created from the payment webhook and live tests.
- **Search suggestions** (`ShopSuggest.cs`): `GET /shop/suggest?q=` (JSON: ≤6 active products with picture and price, ≤3 categories, and a corrected spelling when nothing matches; `Cache-Control: private, no-store`; `suggest` is a
  reserved product slug). The script attaches to every `form input[name=q]` whose form goes to `/shop` (catalogue bar, page block, the menu element — appended OUTSIDE the template part so a template cannot lose it), draws the list
  from text nodes only into `<body>` (a box with `overflow:hidden` would clip it; CSS in `site.css`) and has keyboard and ARIA. "Meinten Sie …?" on an empty catalogue search uses `ShopSuggest.DidYouMean` (Damerau–Levenshtein over
  titles, keywords and category names; words under four letters are left alone).
- **Abandoned-cart reminder** (`ShopReminders.cs`, `ShopStoreReminders.cs`; settings group "Warenkorb-Erinnerung", **off by default**): the customer asks on the cart page (address + button; they can switch it off, which also
  forgets the address); the maintenance job sends **one** mail per ask (template `shop.cart.reminder` under Mailvorlagen) when the cart was untouched for the set hours, is not older than 14 days and the address has not ordered since.
  The cart row is marked BEFORE sending (a failing mail server must not become a retry storm). The mail's link `/shop/cart?restore=<48 hex>` puts the cart back in the browser it is opened in; the key is the only thing that
  reveals a cart. The address is deleted with the cart, with the account (`host.member.erase` also deletes by e-mail) and when switched off. `ShopReminders.MailSender` is the test seam.
- **CSV and the product import** (`ShopCsv.cs`, `ShopExport.cs`, `ShopAdminData.cs`; Shop → Import & Export): orders (one row per order), order lines, customers, products, variants; period and status; German (`;`, decimal comma,
  BOM, CRLF) or international. **Text that starts with `= + - @` or a tab is written with a quote in front** (a product called `=HYPERLINK(…)` must not run in a spreadsheet). The import finds products by slug (made from the
  title when missing; reserved words get `-1`), updates only the columns the file HAS, clears optional fields on an empty cell, sanitises descriptions, accepts only safe picture addresses, creates missing categories at the top level
  and **never** writes in the dry run ("Nur prüfen" is on by default); limits: 2000 rows, 2 MB; an Excel "CSV" in the local code page is read as Latin-1 when it is not UTF-8. Variants come from a second file (`product;name;delta;stock;sku;image`).
- **Sales report** (`ShopReports.cs`, Shop → Berichte): pure `Compute` over orders and lines; an order counts from the day it was **paid** (local time), refunds are deducted (with their share of the tax), unpaid and cancelled orders
  do not count; courses by day (by month over 62 days), best sellers, split by country, payment method and tax rate.
- **Packing slip** (`ShopSlip.cs`, `orders/{id}?slip=1`): a printable page without prices, digital items left out.
- **"Verbindung prüfen"** (`ShopConnectionCheck.cs`, on Zahlungsarten): read-only calls — Stripe `GET /v1/balance` and `/v1/webhook_endpoints`, PayPal `GET /v1/notifications/webhooks` — report key accepted, test/live matches the
  setting, webhook secret/ID present, webhook address and events as the shop needs them. A restricted key (403) is a remark, a rejected one (401) an error. It places no order; the checklist beside it describes the real test
  purchase, which still needs the operator's test credentials.
- **Cloud/MCP operations** (`ShopOps.cs`, answers `{status, message}`; a READ also carries `data`, which `ContentOpApplier` forwards as the report's `ResultJson`): `order.list` (one order by number or the newest, ≤100),
  `order.update` (paid, ship, complete, cancel, note, tracking — through `ShopOrders`, so a move the order cannot make is refused), `variant.set` (by name, in place; `replace` switches the others off, never deletes),
  `attribute.create` (add-only), `product.attributes`. MCP tools: `list_shop_orders`, `update_shop_order`, `set_product_variants`, `create_shop_attribute`, `set_product_attributes`. Orders hold personal data: the tools need the
  key's restore right, also for reading.
