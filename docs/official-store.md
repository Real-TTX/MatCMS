# MatCMS Store (global) — Entwurf

Stand: 11.11.2026 — **S0 und S1 gebaut und live** (matcms.de/store, 7 MATTHIX-Plugins signiert), **S2 gebaut**, noch nicht live. Zweite Fassung nach Matthias' Klarstellung.

**Gebaut:**
- **S0** — `MatCMS.Shared/StoreSignature.cs` (Text, Prüfung, `StoreKeys.Known` mit dem Store-Schlüssel `0b25964caefce574`), Signier-Werkzeug
  `tools/StoreSign` (`keygen`, `pubkey`, `sign`, `sign-package`; privater Schlüssel in `%APPDATA%\MatCMS\store-signing\key.pem` auf Matthias' Rechner – sichern!),
  `PublicRouteOptions.DuringMaintenance` und `MaxBodyMegabytes` (nur mit `RequireMember` + `Antiforgery`, höchstens 200). Tests `StoreSignatureTests`, `PluginRouteLimitsTests`.
- **S1** — `plugins/matcms-store` (nativ, Key `matcms-store`, 0.1): `/store` (Reiter Skript/Native, Suche, Schlagwörter), `/store/{key}` (Bilderleiste mit
  Lightbox, Text, Fakten, Versionen, Paket-Download), `/store/herausgeber/{slug}`, `/store/registrieren`, `/store/konto` (Herausgeber werden, Profil,
  Einträge), `/store/konto/hochladen`, `/store/konto/eintrag/{key}` (Texte, Bilder, Titelbild); API `/store/api/catalog`, `…/plugins/{key}`,
  `…/{version}/bundle`, `…/images/{id}`; Admin Prüfen (Freigabe nur mit gültiger Signatur), Einträge (Version/Eintrag zurückziehen), Herausgeber
  (prüfen, sperren, Konto zuordnen); Menüpunkt „Store“ im Kopfmenü; Mails eingereicht/freigegeben/abgelehnt. Tests `MatcmsStoreTests` (ganzer Weg,
  Signatur aus `MatCMS.Shared` vom Plugin angenommen, Key-Besitz, Versionen nur aufwärts, reservierte Keys, Wartungsmodus). Lokal mit den sieben
  MATTHIX-Plugins und echten Signaturen durchgespielt.

- **S2** — die Cloud übernimmt aus dem MatCMS Store (`MatCMS.Cloud/Services/MatcmsStoreService.cs`): Admin → Store, Reiter **„MatCMS Store“**
  (Katalog mit Stand je Eintrag: noch nicht übernommen / neuere Version da / aktuell / eigener Eintrag; „Übernehmen“/„Aktualisieren“), REST
  `GET /api/v1/store/matcms`, `POST /api/v1/store/matcms/{key}/take-over` (`CanManageStore`), MCP `list_matcms_store`, `take_over_from_matcms_store`.
  Übernehmen = Paket laden (nur Pfade auf dem Store-Server), **Signatur gegen `StoreKeys.Known` des Images prüfen**, `UpsertPluginAsync` (Profile
  mit dem Eintrag rollen aus), Herausgeber und Bilder (ersetzt) mit. `StorePlugin.Origin = matcms`, `Signature`, `SignatureKeyId` (Migration
  `StorePluginOrigin`), additiv im Katalog für die Websites (`CatalogPlugin.Origin/Signature/SignatureKeyId`). Ein übernommener Eintrag wird in der
  Cloud nicht bearbeitet (Repack würde die Signatur brechen); ein eigenes Paket macht ihn wieder zum eigenen Eintrag. Ein **eigener** Eintrag mit
  demselben Key wird nur auf ausdrücklichen Wunsch ersetzt („Ersetzen“ mit Rückfrage; der Eintrag bleibt derselbe, Profile behalten ihn). Store-Adresse: Cloud-Einstellung `store.matcms.url` (leer = https://matcms.de). Tests
  `MatcmsStoreTakeOverTests`; einmal gegen das echte matcms.de mit dem echten Schlüssel: alle 7 Plugins übernommen.
  **Anders als §8:** kein eigener Knopf im Profil — übernommen wird im Store, ins Profil kommt der Eintrag wie jeder andere über „Aus Global
  hinzufügen“; kein stündliches Abfragen und kein „automatisch übernehmen“ — die Liste fragt matcms.de beim Öffnen (5 Minuten zwischengespeichert)
  und zeigt neuere Versionen dort.

**Abweichungen vom Entwurf (bewusst, für später):** Die öffentlichen Seiten sind noch HTML-Seiten des Plugins (im Look von Store-Einblick 1.2, mit den
Variablen der Website), noch keine Block-Seiten (`PageRole`). Statt einer Tabelle `reviews` steht der Grund einer Ablehnung/eines Rückzugs an der Version.
Texte und Bilder eines Eintrags ändert der Herausgeber direkt (ohne erneute Prüfung; alles wird als Text ausgegeben, nie als HTML). „Registrieren erlauben“
ist im Plugin standardmäßig an – live erst einschalten, wenn die Bedingungen für Herausgeber stehen (§12 Frage 4).

**Vorgabe (Matthias, 10.11.2026):**

> „Sollen wir ein Custom Plugin machen für MatCMS Store was quasi der offizielle Store wird wo sich auch die Cloud / Instanzen bedienen
> können? Was nur auf matcms.de erreichbar ist. matcms.de/store/api/xxxx und matcms.de/store (für den ui der store)“

> „… dass quasi der MatCMS Store (Global) bzw Store Server ein Plugin ist. User können sich auf MatCMS registrieren um Plugins
> hochzuladen usw. Und die Instanzen oder Cloud kann dann auch von Global runterladen. Die Instancen können nur von Global runterladen,
> wenn es im Profil erlaubt ist. Die Cloud kann es natürlich immer, dass man in einem Profil oder den Store von der Cloud über das
> Global Store runterladen kann.“ — „Und es gibt auf MatCMS ein ‚Store‘ Button wo man dann die Inhalte sieht und durchsuchen kann.“

Wie beim Shop und beim Buchungs-Plugin gilt: Was der Store braucht und das Plugin-API nicht kann, kommt **ins API** (§10), nicht am
Plugin vorbei.

## 1. Das Modell in fünf Sätzen

1. **Der MatCMS Store ist ein Store-Server — als natives Plugin `matcms-store` auf matcms.de.** Oberfläche unter `matcms.de/store`
   (im Kopfmenü der Knopf **„Store“**: stöbern, suchen, filtern, Details), API unter `matcms.de/store/api/…`.
2. **Jeder kann sich auf matcms.de registrieren und Plugins hochladen** (später auch Templates und Komponenten). Matthias prüft und gibt
   frei; erst dann ist eine Version im Store.
3. **Eine Cloud darf immer aus dem MatCMS Store holen** — entweder in ihren eigenen Store (Admin → Store) oder direkt in ein Profil.
   Es gibt **keinen automatischen Spiegel**: der Betreiber wählt, die Cloud zeigt neue Versionen an.
4. **Eine Website mit Cloud lädt nur dann selbst aus dem MatCMS Store, wenn ihr Profil es erlaubt** (eigener Schalter am Profil, wie
   „Native Plugins“). Sonst bekommt sie nur, was ihre Cloud ihr gibt.
5. **Alles aus dem MatCMS Store ist signiert** und wird beim Import geprüft — der Store verteilt Code, native Plugins laufen ohne Sandbox.

## 2. Heute

| Was | Wo | Wer liest es |
| --- | --- | --- |
| Store (Plugins, Templates, Komponenten, Mail-Vorlagen) | in **jeder Cloud** (`Store*`-Tabellen, Admin → Store, REST `/api/v1/store`, MCP) | die Profile dieser Cloud; ihre Websites |
| Instanz-Katalog | `GET {cloud}/api/store/{instance}/catalog` + `/plugin/{key}`, `/template/…`, `/component/…`, `/image/{id}` (Instanz-Token) | Store-Dialog der Website (`CloudCatalogService`, `_StoreDialog.cshtml`) — **nur mit verbundener Cloud** |
| Öffentlicher Katalog | `GET {cloud}/api/public/store` (+ Bilder), ohne Schlüssel | Store-Einblick (auf matcms.de: cloud2) |
| Herausgeber, Bilder | am Store-Eintrag der Cloud (`StorePlugin.Publisher`, `StorePluginImage`) | beide Kataloge |
| Pakete | ZIP im Format `MatCMS.Shared/PluginBundle.cs`; Import über `PluginPackager.ImportAsync`, ein Plugin kommt **ausgeschaltet** an; native nur mit `plugins.allowNative` | — |

Ein Plugin von MATTHIX muss heute in jede Cloud einzeln; eine Website ohne Cloud kann nichts aus einem Store installieren; Fremde können
nichts einreichen.

**Name:** In der Cloud heißt der *eigene* Store in den Profilen schon „Global“ (Herkunft „Global“, Knopf „Aus Global hinzufügen“). Damit
das nicht zweimal dasselbe Wort für zwei Dinge ist, nennt dieser Entwurf den neuen **„MatCMS Store“** und den der Cloud **„Cloud-Store“**;
im Profil würde aus „Global“ dann „Cloud-Store“ und daneben „MatCMS Store“ (Frage 1).

## 3. Warum ein Plugin auf matcms.de (entschieden)

Matthias' Vorgabe. Zur Erinnerung, weil Matmon den umgekehrten Weg ging (dort wanderte der Template-Store vom Plugin auf matmon.eu in die
Matmon Cloud): Matmon hat **eine** zentrale Cloud, MatCMS hat **viele** (jeder Betreiber seine) und Websites, die ganz ohne Cloud laufen.
Der Store des Produkts gehört deshalb nicht in eine Betreiber-Cloud, sondern auf die Seite des Produkts. Eine MatCMS-Website hat Konten,
Mails, Dateien, Freigaben und Seitengestaltung schon — der Store-Server ist ein Plugin wie Forum und Verein.

## 4. Wer mit wem

```
             matcms.de — Plugin matcms-store („Store“ im Menü)
   /store (stöbern, suchen) · /store/konto (registrieren, hochladen) · /store/api (Katalog, Pakete, Bilder)
          ▲                                ▲                                   ▲
          │ holt immer                     │ lädt NUR wenn erlaubt             │ lädt (Vorschlag: Schalter,
          │ (in Cloud-Store oder Profil)   │ (Profil-Schalter)                 │  Standard an — Frage 2)
     Cloud (cloud2, …)          Website mit Cloud                   Website ohne Cloud
          │ rollt aus wie heute
          ▼
     Websites der Cloud
```

**Store-Einblick** wird auf matcms.de durch `/store` ersetzt und bleibt für andere Seiten; 1.3 kann den Katalog des MatCMS Store lesen
(gleiches Format, §5.3).

## 5. Das Plugin `matcms-store`

Nativ (wie Forum, Verein, Buchungen), Quelle `plugins/matcms-store`, **nicht im Image** — es läuft nur auf matcms.de. Routen mit festem
erstem Segment `store` (nicht reserviert). Key `matcms-store`, damit es nicht mit dem Cloud-Store verwechselt wird.

### 5.1 Daten (`Data.Table` des Plugins)

| Tabelle | Inhalt |
| --- | --- |
| `publishers` | Herausgeber: Name, Slug, Website, Kurztext, Logo, geprüft ja/nein, Mitgliedskonto(en) |
| `items` | Key, Art (plugin; später template/component), Name, Kurz- und Langtext (Übersetzungen wie im Shop), Herausgeber, Schlagwörter, Lizenz, Homepage, Status (entwurf / in Prüfung / veröffentlicht / zurückgezogen) |
| `versions` | Item, Version, Art (script/native), Plugin-API (native), Mindest-MatCMS, Größe, SHA-256, Signatur + Schlüssel-Id, Änderungen, Status, Downloads (Zähler, anonym), Datum |
| `pictures` | Item, Datei, Bildunterschrift, Reihenfolge (≤ 8, PNG/JPEG/WebP nach den ersten Bytes, wie im Cloud-Store) |
| `reviews` | Prüfverlauf je Version: eingereicht, Rückfrage, freigegeben, abgelehnt (mit Grund) |

Pakete und Bilder liegen in `ctx.Files` (privat, nur über Handler ausgeliefert). **Versionen sind unveränderlich**; eine schlechte wird
*zurückgezogen* (nicht gelöscht — wer sie hat, soll den Grund sehen).

### 5.2 Öffentliche Seiten

- **Knopf „Store“** im Kopfmenü von matcms.de (Menü-Element des Plugins, einmal angelegt, danach wie jeder Menüpunkt verschiebbar).
- `/store` — alles Veröffentlichte: Suche, Reiter nach Art, Filter (Skript/Native, Herausgeber, Schlagwort), Karten mit Titelbild
  (die Darstellung von Store-Einblick 1.2 ist der Ausgangspunkt).
- `/store/{key}` — Detail: Bilderleiste, Text, Versionen mit Änderungen, Herausgeber, Downloads, Installationshinweis.
- `/store/herausgeber/{slug}` — ein Herausgeber und seine Einträge.
- Als **Block-Seiten** (`PageRole`, wie die Shop-Seiten), damit matcms.de sie im Block-Editor gestaltet. Dazu Sitemap, Linkziele und
  Antwort auf `site.search` (Store-Einträge in der Website-Suche und im KI-Bot).

### 5.3 API (`/store/api/…`, öffentlich, nur lesen)

| Weg | Antwort |
| --- | --- |
| `GET /store/api/catalog` | alles Veröffentlichte, **Format 1 wie `/api/public/store`** plus `versions[]` (version, kind, api, minCms, size, sha256, signature, keyId, download), `license`, `homepage` — ETag, `max-age=300` |
| `GET /store/api/plugins/{key}` | ein Eintrag mit allen Versionen (auch zurückgezogenen, mit Grund) |
| `GET /store/api/plugins/{key}/{version}/bundle` | das ZIP, gestreamt (`PluginResult.FromStream`), `immutable`, Header `X-Sha256` |
| `GET /store/api/plugins/{key}/images/{id}` | Bild |

Lesen braucht keinen Schlüssel: Der Katalog ist öffentlich, die Sicherheit kommt aus der Signatur. **Wer laden darf, entscheidet nicht
der Server, sondern die Cloud** (Profil-Schalter, §7) — eine Website ohne Erlaubnis fragt gar nicht erst.

### 5.4 Konten und Hochladen (`/store/konto`)

- **Registrieren** auf matcms.de (Mitgliederkonto mit Rolle `herausgeber`; das Plugin legt das Konto selbst an, unabhängig von
  `members.selfRegistration`), E-Mail bestätigen, **2FA Pflicht** für Herausgeber (`MemberTwoFactorService`, `PublicRouteOptions.RequiredRole`).
  Vorher: Nutzungsbedingungen für Herausgeber annehmen (Frage 4).
- **Hochladen**: Paket + Texte + Bilder → automatische Prüfungen (Format und Größen nach `PluginBundle`, Key-Besitz, Version höher als die
  letzte, native: Plugin-API, Mindest-MatCMS) → Status „in Prüfung“ → Mail an Matthias.
- **Meine Einträge**: Status je Version, Rückfragen beantworten, neue Version hochladen, Texte und Bilder ändern (wieder zur Prüfung),
  Downloads sehen.
- **Ein Key gehört dem Herausgeber, dessen erste Version freigegeben wurde**; Keys der eingebauten Plugins (`blog`, `shop`, `bot`) und alle
  von MATTHIX sind reserviert. Umbenennen geht nicht.

### 5.5 Admin (matcms.de)

Store → Prüfen (Warteschlange: Code lesen bei Skripten, Herkunft und Zweck bei nativen Plugins, Bilder, Texte; freigeben = signieren,
§6; ablehnen mit Grund), Einträge, Herausgeber (prüfen, sperren), Übersicht mit Downloads. MATTHIX lädt seine eigenen Plugins über
denselben Weg hoch — es gibt keinen Sonderweg am Prüfen vorbei.

## 6. Sicherheit — der Store ist Lieferkette

| Gefahr | Maßnahme |
| --- | --- |
| Manipuliertes Paket unterwegs (Cloud, Proxy, Cache) | **Signatur** (ECDSA P-256 über `matcms-store-v1\n{kind}\n{key}\n{version}\n{sha256}`); die öffentlichen Schlüssel stecken **im MatCMS-Image** und im Cloud-Image (`MatCMS.Shared/StoreKeys.cs`: Schlüssel-Id → Schlüssel; ein Wechsel = neue Version) |
| matcms.de-Admin übernommen | privater Schlüssel **nicht auf dem Server** (Empfehlung, Frage 3): beim Freigeben lädt Matthias einen kurzen „zu signieren“-Text herunter, signiert lokal (`sign-store.ps1`) und lädt die Signatur hoch; der Store speichert nur Signaturen. Admin mit 2FA |
| Fremder übernimmt einen Key (`forum`-„Update“) | Key-Besitz (§5.4), reservierte Keys |
| Natives Plugin mit Schadcode | Prüfung vor Freigabe; auf der Website wie heute: kommt ausgeschaltet an, läuft nur mit `plugins.allowNative`; evtl. native Plugins nur von geprüften Herausgebern (Frage 4) |
| Template mit Skript | Templates tragen eigenes JavaScript, das bei Besuchern läuft → gleiche Prüfung und Signatur (sobald Templates kommen) |
| Zip-Bombe, falsche Pfade, SVG | `PluginBundle`-Grenzen beim Hochladen **und** beim Import; Bilder nach Bytes, kein SVG |
| Zurückgezogene Version | Katalog nennt sie mit Grund; Cloud und Websites zeigen es am installierten Eintrag |

Ein Paket, das jemand **von Hand** hochlädt, bleibt ohne Signatur möglich (wie heute) — die Signatur gilt für alles, was *aus dem MatCMS
Store* kommt. Die Cloud bearbeitet geholte Einträge nicht: Ihr Editor schreibt `plugin.json` neu (`Repack`), das bräche die Signatur.

## 7. Websites

- **Mit Cloud:** neuer Profil-Schalter **„Aus dem MatCMS Store laden“** (erlauben / verbieten, Standard: verbieten), für eine einzelne
  Instanz überschreibbar (Details → Konfiguration) — gebaut wie „Native Plugins“: eigenes Feld `InstanceConfig.MatcmsStore` auf dem Draht,
  nie eine freie Einstellung, kein Backup und kein Content-Op setzt es (`SettingKeys.Protected`). Ältere Websites kennen das Feld nicht und
  laden ohnehin nur aus der Cloud.
- **Ohne Cloud:** Schalter in Einstellungen → Plugins (Vorschlag: an; Frage 2). Sobald eine Website mit einer Cloud verbunden ist, gilt das Profil.
- **Store-Dialog:** Cloud-Katalog wie heute; ist der MatCMS Store erlaubt, daneben ein Reiter „MatCMS Store“ (eigener Katalog, Suche).
  Steht ein Key in beiden, gewinnt die Cloud — sie kann eine geprüfte Version bewusst festhalten.
- **Installieren:** Paket holen → SHA-256 und Signatur prüfen → `PluginPackager.ImportAsync` (ausgeschaltet, wie immer). Ohne gültige
  Signatur: Abbruch mit Grund.
- Später: Plugins-Seite zeigt „Update im MatCMS Store“ / „zurückgezogen“.

## 8. Cloud

- **Store-Seite der Cloud:** neuer Reiter „MatCMS Store“ — Katalog durchsuchen, **„In den Cloud-Store übernehmen“** (Paket, Bilder,
  Texte, Herausgeber, Signatur werden kopiert).
- **Profil (Plugins-Reiter):** neben „Aus Cloud-Store hinzufügen“ ein **„Aus dem MatCMS Store hinzufügen“** — übernimmt den Eintrag in
  den Cloud-Store und nimmt ihn im selben Schritt ins Profil (Vorschlag; Frage 5).
- Übernommene Einträge: `StorePlugin.Origin = matcms`, `Signature`, `KeyId`, `OriginVersion` (Migration). **Schreibgeschützt** (Ansicht wie
  native Pakete heute). Die Cloud fragt den Katalog stündlich und zeigt „neue Version x im MatCMS Store“ mit **„Übernehmen“**; ein
  Häkchen „automatisch übernehmen“ je Eintrag ist möglich. Übernommen = neue Version im Cloud-Store → Profile, die ihn nutzen, rollen aus
  (`TouchUsersAsync`), wie heute.
- Wie immer API-first: REST (`/api/v1/store/matcms/…`) und MCP-Werkzeuge für Durchsuchen und Übernehmen, eigenes Recht `CanManageStore`.
- Draht zu den Websites: `CatalogPlugin.Origin`, `Signature`, `KeyId` additiv; die Website prüft die Signatur, wenn eine da ist.

## 9. Profil-Schalter im Detail

| | Cloud-Store | MatCMS Store direkt |
| --- | --- | --- |
| Website mit Cloud, Schalter **aus** (Standard) | ja | **nein** — der Reiter fehlt, der Server wird nie gefragt |
| Website mit Cloud, Schalter **an** | ja | ja |
| Website ohne Cloud | — | Schalter der Website (Frage 2) |
| Cloud selbst | — | **immer** |

## 10. Was Host und Plugin-API dafür brauchen

| Bedarf | Stand |
| --- | --- |
| Öffentliche Routen `/store`, JSON, Header, gestreamte Dateien, private Dateien | **vorhanden** |
| Konten, Rollen, 2FA, Mails, Mailvorlagen, Zähler, Block-Seiten, Menü-Element, Suche, Sitemap | **vorhanden** |
| Pakete hochladen im **Admin** | **vorhanden** (Admin-Seiten nehmen bis 1 GB) |
| Pakete hochladen über `/store/konto` (öffentliche Route) | **fehlt**: öffentliche Routen lesen Formulare mit Kestrels Standardgrenze (30 MB), native Pakete können ~90 MB haben → `PublicRouteOptions.MaxBody` (nur mit `RequireMember` + `Antiforgery`) |
| Store-API im **Wartungsmodus** von matcms.de erreichbar | **fehlt**: nur `/api/v1` ist ausgenommen → `PublicRouteOptions.DuringMaintenance` |
| Signatur prüfen beim Import (Website **und** Cloud) | **fehlt**: `MatCMS.Shared/StoreSignature.cs` + `StoreKeys.cs` |
| Profil-Schalter + Drahtfeld + Website-Schalter | **fehlt** (Cloud + Kern, wie `NativePlugins`) |
| MatCMS-Store-Reiter im Store-Dialog | **fehlt** (Kern): `MatcmsStoreService` neben `CloudCatalogService` |
| MatCMS-Store-Reiter + Übernehmen in der Cloud | **fehlt** (Cloud) |
| Signieren bei Matthias | **fehlt**: `sign-store.ps1` (Schlüsselpaar erzeugen, Text signieren) |

Das Signaturformat steht im Kern (`MatCMS.Shared`) und im Plugin (Anzeige „signiert“) — ein Konformitätstest hält beide gleich, wie beim
Skript/Native-Test von Store-Einblick.

## 11. Stufen

| Stufe | Inhalt | Nutzen |
| --- | --- | --- |
| **S0** | Kern: Signatur (Format, Schlüssel, Prüfung), `DuringMaintenance`, `MaxBody`, `sign-store.ps1`, Schlüsselpaar | Grundlage |
| **S1** | **Store-Server** `matcms-store` auf matcms.de: Knopf „Store“, `/store` mit Suche, Detailseiten, API; Registrieren, Hochladen, Prüfen, Freigeben (signieren), Mails. Befüllt mit den MATTHIX-Plugins aus dem cloud2-Store (über denselben Hochlade-Weg) | der MatCMS Store ist da, Fremde können einreichen |
| **S2** | **Cloud holt**: Reiter „MatCMS Store“ im Cloud-Store, „Aus dem MatCMS Store hinzufügen“ im Profil, Update-Hinweis, REST + MCP | ein Ort zum Veröffentlichen statt jede Cloud einzeln |
| **S3** | **Websites**: Profil-Schalter + Website-Schalter, Reiter „MatCMS Store“ im Store-Dialog, Prüfung beim Import | Websites ohne Cloud (und erlaubte mit Cloud) installieren direkt |
| **S4** | Komfort: Update-Hinweise auf den Websites, Templates und Komponenten im MatCMS Store, Bewertungen, öffentliche Download-Zahlen, evtl. kostenpflichtige Plugins | Store wie man ihn kennt |

## 12. Offene Fragen an Matthias

1. **Namen:** „MatCMS Store“ (matcms.de) und „Cloud-Store“ (der eigene der Cloud) — und im Profil wird aus „Global“ „Cloud-Store“?
2. **Website ohne Cloud:** darf sie aus dem MatCMS Store laden (Vorschlag: ja, Schalter standardmäßig an)?
3. **Signaturschlüssel:** bei dir signieren (Empfehlung; jede Freigabe braucht deinen Rechner) oder auf dem Server (bequemer, schützt nicht
   bei übernommenem Admin)?
4. **Herausgeber:** jeder darf sich registrieren, oder nur auf Einladung? Native Plugins von Fremden ja/nein? Nutzungsbedingungen und
   Haftung für Herausgeber (Rechtliches, vor dem Öffnen klären)?
5. **Profil „Aus dem MatCMS Store hinzufügen“:** immer über eine Kopie im Cloud-Store (Vorschlag: die Cloud hat das Paket selbst, prüft die
   Signatur einmal, matcms.de darf ausfallen) — oder soll das Profil direkt auf den MatCMS Store zeigen?
6. **Was:** zuerst nur Plugins (Vorschlag), Templates und Komponenten später?
7. **Backups von matcms.de:** alle Pakete aller Versionen liegen in `ctx.Files` und damit in Backups mit Plugin-Dateien — Inhaltsänderungen
   an matcms.de künftig im Admin statt über den Backup-Zyklus?
