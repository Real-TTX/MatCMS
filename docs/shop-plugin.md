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
| **L10** | **Eigene Template-Teile.** „Parts a plugin defines itself are not supported yet“ (`IPluginSite.RenderTemplatePart`). | Doku von `RenderTemplatePart`. | Produkt-, Warenkorb- und Kassenseite können nicht im Look der Kundenseite überschrieben werden; nur das Standard-Aussehen. |
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
10. **Eigene Template-Teile** (L10) — `Plugin.TemplatePart(name, defaultHtml, tokens)`: der Teil erscheint im Template-Editor,
    ein Template kann ihn überschreiben, sonst gilt der Standard des Plugins. Erste Teile: `shop-product`, `shop-list`,
    `shop-cart`, `shop-checkout`.
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

### 5.6 Admin (Bereich „Shop“, Tabs)

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
- **Offen:** eigene Template-Teile für das Aussehen der Shop-Seiten (L10); echte Testläufe mit Stripe und PayPal; E-Rechnung (ZUGFeRD); Reverse Charge; mehrere Kategorien je Produkt;
  Warenkorb-Knopf direkt auf der Produktkarte.
