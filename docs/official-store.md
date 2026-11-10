# Offizieller MatCMS-Store — Entwurf

Stand: 10.11.2026 — **Entwurf, nichts gebaut.** Anlass (Matthias, 10.11.2026): „Sollen wir ein Custom Plugin machen für MatCMS Store
was quasi der offizielle Store wird wo sich auch die Cloud / Instanzen bedienen können? Was nur auf matcms.de erreichbar ist.
matcms.de/store/api/xxxx und matcms.de/store (für den ui der store)“. Davor (Backlog): „Später müssen wir daraus ein ‚wirklichen‘ Store
machen. Wo man einreichen kann usw.“

Wie beim Shop und beim Buchungs-Plugin gilt: Was der Store braucht und das Plugin-API nicht kann, kommt **ins API** (§10), nicht am
Plugin vorbei.

## 1. Kurzfassung

1. **Ein natives Plugin `matcms-store` nur auf matcms.de** (Quelle `plugins/matcms-store`, nicht im Image, wie Forum/Verein):
   Oberfläche unter `matcms.de/store`, API unter `matcms.de/store/api/…`. Es hält Plugins (später Templates und Komponenten) mit Versionen,
   Bildern, Herausgeber, Prüfsumme und **Signatur**.
2. **Websites** bekommen eine zweite Quelle im Store-Dialog: den offiziellen Store, **auch ohne Cloud**. Heute hat eine Website ohne Cloud
   gar keinen Store — das passt nicht zu „MatCMS läuft ohne Cloud“.
3. **Clouds spiegeln** den offiziellen Store (unverändert, mit Signatur) und behalten daneben ihre eigenen Einträge. Profile rollen
   gespiegelte Einträge aus wie heute.
4. **Signatur ist Pflicht** für alles, was aus dem Store installiert wird: Der Store verteilt Code, native Plugins laufen ohne Sandbox.
   Empfehlung: Der private Schlüssel liegt **nicht** auf matcms.de, sondern bei Matthias; `pack.ps1`/`pack-native.ps1` signieren gleich beim Bauen.
5. **Einreichen** kommt zuletzt: Herausgeber sind Mitgliederkonten von matcms.de (mit 2FA), jede Version wird von Hand geprüft und signiert.
6. Reihenfolge: **S0** Grundlagen im Host → **S1** Store lesen (aus dem cloud2-Store befüllt) → **S2** Websites ohne Cloud installieren →
   **S3** Cloud spiegelt → **S4** Einreichen. Jede Stufe nützt für sich.

## 2. Heute

| Was | Wo | Wer liest es |
| --- | --- | --- |
| Store (Plugins, Templates, Komponenten, Mail-Vorlagen) | in **jeder Cloud** (`Store*`-Tabellen, Admin → Store, REST `/api/v1/store`, MCP) | die Profile dieser Cloud; ihre Websites |
| Instanz-Katalog | `GET {cloud}/api/store/{instance}/catalog` + `/plugin/{key}`, `/template/…`, `/component/…`, `/image/{id}` (Instanz-Token) | Store-Dialog der Website (`CloudCatalogService`, `_StoreDialog.cshtml`) — **nur mit verbundener Cloud** |
| Öffentlicher Katalog | `GET {cloud}/api/public/store` (+ Bilder) — ohne Schlüssel | Store-Einblick (auf matcms.de: cloud2) |
| Herausgeber, Bilder | am Store-Eintrag der Cloud (`StorePlugin.Publisher`, `StorePluginImage`) | beide Kataloge |
| Pakete | ZIP im Format `MatCMS.Shared/PluginBundle.cs`; Import über `PluginPackager.ImportAsync`, ein Plugin kommt **ausgeschaltet** an; native nur mit `plugins.allowNative` | — |

Folgen: Ein Plugin von MATTHIX muss in jede Cloud einzeln; eine Website ohne Cloud kann nichts aus einem Store installieren; der
„offizielle“ Store ist faktisch der Store von cloud2, also von Matthias' Hosting-Betrieb.

## 3. Wo wohnt der Store — matcms.de oder eine Cloud?

**Gegenbeispiel Matmon:** Dort lag der Template-Store zuerst als Plugin auf matmon.eu (`matmon-templates` 1.0/2.0) und wurde am 05.11.2026
in die Matmon Cloud verlegt („das komplette Submitten und Freigeben in die Cloud … eine API, wo man es auf der Website anzeigt“);
matmon.eu zeigt seitdem nur noch an. Der Plugin-Weg galt dort als Irrweg. Warum es bei MatCMS anders liegt:

| | Matmon | MatCMS |
| --- | --- | --- |
| Clouds | **eine** zentrale Cloud (cloud.matmon.eu) für alle Kunden | **beliebig viele**, jeder Betreiber kann seine eigene haben; cloud2 ist Matthias' Hosting |
| Ohne Cloud? | Geräte hängen immer an der Cloud | Websites sollen **ohne** Cloud vollständig laufen |
| Konten | die Cloud hat Kundenkonten | eine MatCMS-Cloud hat nur Betreiber-Konten; öffentliche Herausgeber-Konten wären neu |
| Wer verbraucht | die Cloud selbst | Clouds **und** Websites ohne Cloud |

Ein Store in „der“ Cloud hieße bei MatCMS: im Hosting-Betrieb cloud2. Andere Clouds und Websites würden dann bei einem fremden Betreiber
einkaufen, und cloud2 bekäme öffentliche Konten, die sie sonst nicht braucht. matcms.de ist dagegen der Ort des **Produkts**, unabhängig
von jedem Betrieb — und eine MatCMS-Website hat Konten, Mails, Dateien, Freigabe und Seitengestaltung schon. **Empfehlung: Plugin auf
matcms.de** (Matthias' Vorschlag). Die Alternative „eigene Store-Cloud unter store.matcms.de“ bleibt möglich, kostet aber eine zweite
Kontenwelt in der Cloud.

## 4. Rollen nach dem Umbau

```
                      matcms.de  (Plugin matcms-store)
                 /store (Oberfläche) · /store/api (Katalog, Pakete, Bilder)
                     ▲                       ▲                     ▲
          spiegelt   │           installiert │                     │ zeigt an
   Cloud (cloud2, …) ┘     Website ohne Cloud ┘     Store-Einblick ┘ (andere Seiten)
          │ eigene Einträge + Spiegel
          ▼
   Websites der Cloud (Store-Dialog: Cloud-Katalog zuerst, dann offizieller Store)
```

- **Store-Einblick** wird auf matcms.de selbst überflüssig (die Seite *ist* der Store) und bleibt für andere Seiten. 1.3 liest wahlweise den
  offiziellen Katalog (`/store/api/catalog`, gleiches Format wie `/api/public/store`, siehe §5.3).
- **Mail-Vorlagen** bleiben Sache der Cloud (sie sind Konfiguration, kein Einkauf).

## 5. Das Plugin `matcms-store`

Nativ (wie Forum, Verein, Buchungen): viel Code, eigene Tests, die es in die ganze Anwendung laden. Key `matcms-store` (nicht `store`:
das Wort meint heute den Cloud-Store), Routen mit festem ersten Segment `store` (nicht reserviert, `PluginRoutePattern.Reserved`).

### 5.1 Daten (`Data.Table` des Plugins)

| Tabelle | Inhalt |
| --- | --- |
| `items` | Key, Art (plugin/template/component), Name, Kurz- und Langtext (Übersetzungen wie im Shop), Herausgeber, Schlagwörter, Status (entwurf/geprüft/veröffentlicht/zurückgezogen), Lizenz, Homepage |
| `versions` | Item, Version, Art (script/native), Plugin-API (native), Mindest-MatCMS, Größe, SHA-256, Signatur + Schlüssel-Id, Änderungen, Status, Downloads (Zähler, anonym), Datum |
| `pictures` | Item, Datei, Bildunterschrift, Reihenfolge (≤ 8, PNG/JPEG/WebP nach den ersten Bytes, wie im Cloud-Store) |
| `publishers` | Name, Slug, Website, Mitgliedskonto(en), geprüft ja/nein |
| `submissions` | Einreichungen und ihr Verlauf (S4) |

Pakete und Bilder liegen in `ctx.Files` (privat, nur über Handler ausgeliefert). **Versionen sind unveränderlich**: eine korrigierte Fassung
ist eine neue Version; eine schlechte wird *zurückgezogen* (nicht gelöscht — Websites, die sie haben, sollen den Grund sehen).

### 5.2 Öffentliche Seiten

`/store` (Liste mit Reitern, Suche, Filtern nach Art), `/store/{key}` (Detail: Bilderleiste, Text, Versionen, Herausgeber,
Installationshinweis), `/store/herausgeber/{slug}`. Als **Block-Seiten** (`PageRole`, wie die Shop-Seiten), damit matcms.de sie im
Block-Editor gestaltet; die Darstellung von Store-Einblick 1.2 ist der Ausgangspunkt. Dazu: Sitemap, Linkziele, Menü-Element, Antwort
auf `site.search` (Store-Einträge in der Website-Suche und im KI-Bot).

### 5.3 API (`/store/api/…`, öffentlich, nur lesen)

| Weg | Antwort |
| --- | --- |
| `GET /store/api/catalog` | alles Veröffentlichte, **Format 1 wie `/api/public/store`** plus Felder: `versions[]` (version, kind, api, minCms, size, sha256, signature, keyId, download), `license`, `homepage` — ETag, `max-age=300` |
| `GET /store/api/plugins/{key}` | ein Eintrag mit allen Versionen (auch zurückgezogenen, mit Grund) |
| `GET /store/api/plugins/{key}/{version}/bundle` | das ZIP, gestreamt (`PluginResult.FromStream`), `immutable`, Header `X-Sha256` |
| `GET /store/api/plugins/{key}/images/{id}` | Bild, wie heute `/api/public/store/plugins/{key}/image/{id}` |
| `GET /store/api/templates/{slug}/{version}`, `…/components/{type}/{version}` | JSON im Format, das die Cloud heute an Instanzen liefert (S1 nur Plugins, siehe Frage 2) |

Kein Schlüssel nötig: Der Katalog ist öffentlich, die Sicherheit kommt aus der Signatur, nicht aus dem Zugang. Zähler „Downloads“ je
Version ohne Adresse (`IncrementAsync`).

### 5.4 Admin

Store → Einträge (anlegen, Texte, Bilder, Herausgeber), Versionen (Paket hochladen → Prüfungen §6 → Signatur hochladen oder gleich mit
signiertem Paket → veröffentlichen/zurückziehen), Herausgeber, Einreichungen (S4), Übersicht mit Downloads.

## 6. Sicherheit — der Store ist Lieferkette

Was schiefgehen kann und was dagegen steht:

| Gefahr | Maßnahme |
| --- | --- |
| Manipuliertes Paket unterwegs (Cloud-Spiegel, Proxy, Cache) | **Signatur** (ECDSA P-256 über `matcms-store-v1\n{kind}\n{key}\n{version}\n{sha256}`), öffentliche Schlüssel **im MatCMS-Image** (`MatCMS.Shared/StoreKeys.cs`, Schlüssel-Id → Schlüssel; Wechsel = neue Version von MatCMS) |
| matcms.de-Admin übernommen | privater Schlüssel **nicht auf dem Server** (Empfehlung): signiert wird bei Matthias (`pack*.ps1 -Sign`, Schlüssel in seinem Benutzerprofil); der Store speichert nur Signaturen. Admin mit 2FA |
| Fremder übernimmt einen Key (`forum`-„Update“) | **Key gehört dem ersten freigegebenen Herausgeber**; Keys der eingebauten Plugins (`blog`, `shop`, `bot`) und alle von MATTHIX reserviert; Umbenennen geht nicht |
| Natives Plugin mit Schadcode | wie heute: kommt ausgeschaltet an, läuft nur mit `plugins.allowNative`; zusätzlich: native Pakete von Fremden erst nach Prüfung, evtl. nur von geprüften Herausgebern (Frage 4) |
| Template mit Skript | Templates tragen `script.js`/eigenes JS, das bei Besuchern läuft → gleiche Prüfung und Signatur wie Plugins |
| Zip-Bombe, falsche Pfade, SVG | `PluginBundle`-Grenzen gelten beim Hochladen im Store **und** beim Import auf der Website; Bilder nach Bytes, kein SVG |
| Zurückgezogene Version weiter verteilt | Katalog nennt sie mit Grund; Websites zeigen „zurückgezogen“ am installierten Plugin (S5) |

Ein Paket, das eine Website **von Hand** hochlädt, bleibt ohne Signatur möglich (wie heute) — die Signatur gilt für alles, was *aus dem
Store* kommt. Eine Cloud bearbeitet gespiegelte Einträge nicht: Ihr Editor schreibt `plugin.json` neu (`Repack`), das bräche die Signatur.

## 7. Websites

- Neue Einstellung `store.official` (Adresse, Standard `https://matcms.de`, leer = aus; von der Cloud ausrollbar — ein Betreiber, der seinen
  Kundenseiten nur die eigene Auswahl zeigen will, schaltet sie aus).
- Store-Dialog: Cloud-Katalog (falls verbunden) **und** offizieller Store, je Key einmal (Cloud zuerst — sie kann eine geprüfte, ältere
  Version bewusst festhalten), Herkunft als Plakette „offiziell“.
- Installieren: Paket holen → SHA-256 und Signatur prüfen → `PluginPackager.ImportAsync` (ausgeschaltet, wie immer). Ohne gültige
  Signatur: Abbruch mit Grund.
- Später (S5): Plugins-Seite zeigt „Update verfügbar“ / „zurückgezogen“ für Plugins aus dem Store.

## 8. Cloud

- `StorePlugin.Origin` („“ eigen | `official`), `Signature`, `KeyId` (Migration). Gespiegelte Einträge sind **schreibgeschützt** (Ansicht
  wie native Pakete heute), Bilder werden mitgespiegelt (Instanz-Bildweg bleibt gleich).
- Abgleich (Hintergrunddienst, stündlich + „Jetzt abgleichen“; REST + MCP nach der API-first-Regel): neue Einträge/Versionen holen,
  prüfen, speichern. Einstellung: **alles spiegeln** oder **auswählen**; „neue Versionen automatisch übernehmen“ ja/nein — eine neue
  Version eines Eintrags, den Profile nutzen, rollt wie heute über `TouchUsersAsync` aus.
- Eigener Eintrag mit gleichem Key gewinnt; der offizielle wird daneben als „auch offiziell, Version x“ angezeigt.
- Draht: `CatalogPlugin.Origin`, `Signature`, `KeyId` additiv (ältere Seiten ignorieren sie, kein Protokoll-Bump nötig); die Website prüft
  die Signatur, wenn eine da ist.

## 9. Einreichen (S4)

- **Herausgeber = Mitgliederkonto** auf matcms.de mit Rolle `herausgeber` und Pflicht-2FA (gibt es: `MemberTwoFactorService`,
  `PublicRouteOptions.RequiredRole`). Selbstregistrierung oder Einladung (Frage 4); Nutzungsbedingungen für Herausgeber vorher nötig.
- `/store/einreichen` (RequireMember, Antiforgery): Paket + Texte + Bilder → automatische Prüfungen (Format, Größe, Key-Besitz, Version
  höher als die letzte, native: Plugin-API) → Status „eingereicht“ → Mail an Matthias.
- Prüfung im Admin: Code lesen (Skript), Herkunft/Zweck (native), Bilder, Texte → Matthias signiert lokal → veröffentlichen; oder ablehnen
  mit Grund (Mail an Herausgeber).
- „Meine Einreichungen“ für Herausgeber: Status, neue Version einreichen, Texte ändern (wieder zur Prüfung).

## 10. Was Host und Plugin-API dafür brauchen

| Bedarf | Stand |
| --- | --- |
| Öffentliche Routen mit festem Segment `/store` | **vorhanden** |
| JSON-Antworten, Header (ETag, Cache-Control), gestreamte Dateien | **vorhanden** (`PluginResult.Json`, `WithHeader`, `FromStream`) |
| Private Dateien | **vorhanden** (`ctx.Files`) — aber: kommen mit dem Schalter „Plugin-Dateien“ in jedes Backup (Frage 7) |
| Konten, Rollen, 2FA, Mails, Mailvorlagen, Zähler, Block-Seiten, Suche, Sitemap | **vorhanden** |
| Pakete hochladen im **Admin** | **vorhanden** (Admin-Seiten nehmen bis 1 GB) |
| Pakete hochladen über eine **öffentliche** Route (Einreichen) | **fehlt**: öffentliche Routen lesen Formulare mit Kestrels Standardgrenze (30 MB), native Pakete können ~90 MB haben → `PublicRouteOptions.MaxBody` (nur zusammen mit `RequireMember` + `Antiforgery`) |
| Store-API im **Wartungsmodus** erreichbar | **fehlt**: nur `/api/v1` ist ausgenommen → `PublicRouteOptions.DuringMaintenance` |
| Signatur prüfen beim Import aus dem Store | **fehlt** (Kern): `MatCMS.Shared/StoreSignature.cs` + `StoreKeys.cs`, Aufruf vor `PluginPackager.ImportAsync`; dieselbe Prüfung in der Cloud beim Spiegeln |
| Zweite Quelle im Store-Dialog | **fehlt** (Kern): `OfficialStoreService` neben `CloudCatalogService`, Einstellung `store.official` |
| Signieren beim Packen | **fehlt**: `pack.ps1`/`pack-native.ps1 -Sign` (Schlüssel bei Matthias) |

Das Signaturformat steht zweimal (Kern in `MatCMS.Shared`, Plugin für die Anzeige „signiert von …“) — ein Konformitätstest hält beide gleich,
wie beim Skript/Native-Test von Store-Einblick.

## 11. Stufen

| Stufe | Inhalt | Nutzen |
| --- | --- | --- |
| **S0** | Host: Signatur (Format, Schlüssel, Prüfung), `DuringMaintenance`, `pack*.ps1 -Sign`, Schlüsselpaar erzeugen | Grundlage, ohne sichtbare Änderung |
| **S1** | Plugin `matcms-store` auf matcms.de: Admin, Seiten `/store`, API; befüllt aus dem cloud2-Store (Pakete neu signiert, Bilder, Herausgeber) | offizielle Store-Seite und API live; Store-Einblick-Abschnitte auf matcms.de werden zu Links auf `/store` |
| **S2** | Websites: offizielle Quelle im Store-Dialog, Prüfung beim Import | **Websites ohne Cloud installieren Plugins** |
| **S3** | Cloud spiegelt (cloud2 zuerst) | ein Ort zum Veröffentlichen statt jede Cloud einzeln |
| **S4** | Einreichen: Herausgeber-Konten, Prüfung, Mails, Bedingungen | Plugins von anderen |
| **S5** | Updates-Hinweis und „zurückgezogen“ auf den Websites, öffentliche Download-Zahlen, Bewertungen, evtl. kostenpflichtige Plugins | Store wie man ihn kennt |

## 12. Offene Fragen an Matthias

1. **Wo:** Plugin auf matcms.de (Empfehlung, §3) — trotz des Matmon-Wegs, der den Website-Store in die Cloud verlegt hat?
2. **Was:** zuerst nur Plugins, oder ab S1 auch Templates und Komponenten?
3. **Schlüssel:** Signieren bei dir (Empfehlung; jede Veröffentlichung braucht deinen Rechner) oder auf dem Server (bequemer, schützt nicht bei
   übernommenem Admin)?
4. **Einreichen:** nur eingeladene Herausgeber oder jeder mit Konto? Native Plugins von Fremden überhaupt? Nutzungsbedingungen/Haftung
   für Herausgeber (Rechtliches, vor S4 zu klären)?
5. **Cloud:** alles spiegeln oder Betreiber wählt aus? Neue Versionen automatisch an die Profile?
6. **Websites:** offizieller Store standardmäßig an? Für Kundenseiten einer Cloud abschaltbar (Vorschlag: ja, als ausrollbare Einstellung)?
7. **Backups von matcms.de:** Alle Pakete aller Versionen liegen in `ctx.Files` und damit in Backups mit Plugin-Dateien. Inhaltsänderungen
   an matcms.de künftig im Admin statt über den Backup-Zyklus, oder Backups ohne Plugin-Dateien für den Zyklus?
8. **Namen:** Plugin-Key `matcms-store`, Adressen `/store` und `/store/api` — so?
