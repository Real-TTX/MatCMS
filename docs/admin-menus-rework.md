# Admin-Menüs: Bestandsaufnahme und Entwurf (Start: Hosting in der Cloud)

Stand: 11.11.2026, Code-Stand `7c76c94`. Nur Dokumentation – noch nichts umgebaut.

Auftrag (15.10.): „Alle Menüs im Admin Bereich brauchen ein Rework – wir fangen mit Hosting an.“
Feste Regeln für die Cloud-Oberfläche:

- **Ein** Menüpunkt „Hosting“ mit Tabs; alles zu Docker/Hosting lebt als Tab darin.
- Keine „auch per API/MCP“-Fußzeilen, keine erzählenden Einleitungstexte.
- Oberfläche nur aus dem Toolkit (MatUi); die Cloud sieht aus wie der CMS-Admin.

Teil 1 ist die Bestandsaufnahme von Hosting, Teil 2 der Rest der Cloud-Seitenleiste, Teil 3 der Entwurf.

---

## Teil 1 – Hosting heute

### 1.1 Wie man hinkommt

| Weg | Ziel |
|---|---|
| Seitenleiste „Betrieb“ → **Hosting** (nur Admin, `ti-server-2`) | `/admin/hosting` (Übersicht) |
| Link-Tabs `_HostingTabs.cshtml` über jeder Hosting-Seite | Übersicht · Hosts · Instanzen · Domains* · Edge-Proxy** · Einstellungen |
| Instanzliste → „Alle aktualisieren“ | `/admin/hosting/updates` |
| Instanzliste → Hinzufügen-Menü → „Neue Instanz“ (nur bei Modul an) | `/admin/instances/new` – markiert in der Seitenleiste **Hosting**, nicht Instanzen |
| Über-Seite → Knopf „Updates“ | `/admin/hosting#updates` |
| Instanz-Details → Tab **Hosting** (jede lokale Instanz, unabhängig vom Modul) | Container-Aktionen, Adressen, Kundendomain, weitere Domains |
| Alt-Adressen | `/admin/hosting/docker` → „Dieser Host“, Tab Docker; `/admin/settings?tab=hosting` → Hosting/Einstellungen; `?tab=docker` → Dieser Host/Docker |

\* nur bei eingeschaltetem Modul (`hosting.enabled`). \*\* nur bei Modul an **und** (Edge an **oder** mindestens ein Node).

### 1.2 Die Hosting-Tabs (Ebene 1)

| Tab | Seite | Inhalt | Aktionen |
|---|---|---|---|
| **Übersicht** | `Pages/Admin/Hosting/Index` | Kacheln (Seiten laufen x/y, Hosts online, Speicher, Updates, Domains); Tabelle **Hosts** mit CPU/RAM-Balken; Karte **Updates**: Cloud-Version, letzter Selbst-Update-Lauf, Instanz-Release, „Automatisch an/aus“ (Link zu Einstellungen), Liste der Kandidaten + veraltete Node-Agenten | „N Instanzen aktualisieren“ (→ Updates), „Cloud jetzt aktualisieren“, „Auf Updates prüfen“ (`?check=true`) |
| *(kein Tab)* **Updates** | `Hosting/Updates` | Checkbox-Liste der Instanzen hinter dem Release, Fortschritt über `_BulkProgress` | „Ausgewählte aktualisieren“, alle/keine |
| **Hosts** | `Hosting/Nodes/Index` | Tabelle: „Dieser Host“ (virtuell, erste Zeile) + alle Nodes mit Status, Hostname, Agent-, Docker-Version, Seitenzahl, zuletzt gesehen | Suche; „Node hinzufügen“ (nur Modul an) |
| *(kein Tab)* Node anlegen | `Hosting/Nodes/Create` | Karte „3 Schritte“ + Feld Name | Anlegen → Details zeigt Token/Befehl einmal |
| **Instanzen** | `Hosting/Instances` | `_HostSites`: jeder MatCMS-Container auf jedem Host (Zustand, CPU/RAM, Domain, Version), auch „nicht verbunden“ | Suche, Filter (läuft/gestoppt/Update); „Neue Instanz“ |
| **Domains** | `Hosting/Domains` | jede Adresse gruppiert nach Instanz: Art (Kunde/weitere/Host-Adresse), Weg (Edge/Host/nur notiert), Route, veröffentlicht; Karte „wartende Routen“ | Suche, Filter, „Alle prüfen“; Veröffentlichen **nicht** hier, sondern im Instanz-Tab Hosting |
| **Edge-Proxy** | `Hosting/Proxy` | Karte „Wofür ist der Edge-Proxy?“ mit ASCII-Skizze; Karte Edge an/aus, „Proxy von Dieser Host nutzen“, Caddy/Matcad-Zugang, vertrauenswürdige IPs | Speichern, Verbindung testen, „N Kundendomains umstellen“; bei Modul aus Weiterleitung auf Einstellungen |
| **Einstellungen** | `Hosting/Settings` | zwei Karten: „Modul“ (Hosting an/aus) und „Automatisch“ (lokale Auto-Updates) | je ein Speichern |

### 1.3 Die Host-Seite (Ebene 2: Tabs innerhalb des Tabs „Hosts“)

`Hosting/Nodes/Details/{id?}` – ohne id = „Dieser Host“. Über der Seite stehen die Hosting-Link-Tabs (Hosts aktiv), darunter
**eine zweite Tab-Leiste** (`button.tab[data-tab]`).

| Host-Tab | Sichtbar | Inhalt | Aktionen |
|---|---|---|---|
| **Übersicht** | immer | ID, Hostname/Docker-Endpunkt, Docker-Version, Fehler, Seiten, CPU/RAM, zuletzt gesehen; für Nodes Karte **Agent**: Startbefehl, Compose (nur Agent), Compose (komplett) | „Token erneuern“, „Agent aktualisieren“ |
| **Container** (n) | immer | `_HostSites` dieses Hosts | „Neue Instanz“ |
| **Docker** | immer, Name hart „Docker“ | Images (lazy, `?handler=Images`): Größe, Version, in Benutzung, alt | „Aufräumen“ (Prune) |
| **Proxy** | Node oder Modul an; Name hart „Proxy“ | „Adressen dieses Servers“ (DNS-Prüfung); **Erreichbarkeit** (`_ProxyFields`: Proxy-Art, Zugang, Test); **Automatische Adressen** (Basis-Domain, Wildcard-Zertifikat, Node-Adresse für den Edge); bei Node **Zugriffsschutz: nur über den Edge** | Speichern, Testen, „Adressen für N bestehende Instanzen anlegen“, Zugriffsschutz an/anwenden/rotieren/aus |
| **Matcad** | nur Dieser Host, Modul an, Matcad erreichbar; Name hart | Matcad-Einstellungen (ACME-Mail, Propagation), **Domains** (alle Routen von Matcad), **DNS-Anbieter** | Route/Anbieter anlegen, bearbeiten, löschen, testen |
| **Einstellungen** | wie Proxy | Name, Portbereich, (Dieser Host:) Namensmuster, nächster Port; Node: Karte **Agent** | Speichern; Node: „Token erneuern“ (zweites Mal), Sperren/Aktivieren, Löschen |
| **Aufträge** | nur Node | Job-Liste (Art, Status, Meldung, von, wann) | – |

### 1.4 Was auffällt

**Doppelt / an zwei Orten**

1. **Cloud-Selbst-Update** steht in Hosting → Übersicht (Karte Updates) **und** auf „Über MatCMS Cloud“ (`Admin/About`: Instanz-Image, Release, „Auf Updates prüfen“, Link zurück nach Hosting#updates). Zwei „Auf Updates prüfen“-Knöpfe.
2. **Updates**: Die Übersicht listet die Kandidaten, die Seite `Hosting/Updates` listet sie noch einmal zum Ankreuzen; die Einstellung „Automatisch“ liegt auf einem dritten Tab (Einstellungen). Drei Orte für ein Thema.
3. **„Instanzen“** heißt sowohl der Seitenleisten-Punkt (Verwaltungsliste der Instanz-Datensätze) als auch der Hosting-Tab (Container-Liste). Gleicher Name, anderer Inhalt; dazu „Container“ auf der Host-Seite = dieselbe Tabelle (`_HostSites`) gefiltert.
4. **Domains an drei Orten**: Hosting → Domains (Übersicht + Prüfen), Instanz → Tab Hosting (Veröffentlichen, weitere Domains), Dieser Host → Matcad → Karte **„Domains“** (Matcad-Routen, auch die der Cloud, gesperrt). Die Matcad-Karte heißt genauso wie der Hosting-Tab.
5. **Proxy an zwei Ebenen**: Hosting → Edge-Proxy (cloud-weit) und Host → Proxy (pro Maschine). Der Zugriffsschutz „nur über den Edge“ und die Node-Adresse für den Edge liegen aber beim Host, nicht beim Edge.
6. **„Token erneuern“** steht auf der Host-Seite zweimal (Übersicht → Agent-Karte und Einstellungen → Agent-Karte). Zwei Karten mit Überschrift „Agent“.
7. **„Neue Instanz“** gibt es in Hosting → Instanzen, in Host → Container und im Hinzufügen-Menü der Instanzliste; aktiv markiert wird dabei Hosting.

**Nur per Link / URL erreichbar, falsch markiert**

8. `Hosting/Updates` ist **kein Tab**; die Seite markiert „Übersicht“ als aktiv und hat keine `h1`.
9. `Hosting/Nodes/Create` hat **keine** Hosting-Tabs (aus dem Zusammenhang gerissen), andere Unterseiten schon.
10. Die Kachel „Hosts online“ der Übersicht verlinkt bei Modul aus auf `Docker` – eine Seite, die nur noch weiterleitet.
11. Der Tab Edge-Proxy verschwindet ohne Node; die Seite leitet bei Modul aus stillschweigend auf Einstellungen.

**Seltsame Namen, Sprache, Überschriften**

12. Gemischt Deutsch/Englisch: „Hosts“, „Node hinzufügen“, „Dieser Host“, „Edge-Proxy“, „Container“, „Images“, „Docker“, „Proxy“, „Matcad“, „Aufträge“ vs. „Jobs“ im Code. Die Tabs „Docker“, „Proxy“, „Matcad“ sind hart im Markup, nicht übersetzt.
13. **Host vs. Node vs. Server**: Tab „Hosts“, Knopf „Node hinzufügen“, Karte „Adressen dieses **Servers**“ – drei Wörter für dasselbe.
14. Karte „Erreichbarkeit“ (`settings.hostingRouting`) ist der Reverse-Proxy des Hosts – der Name verrät das nicht; der Tab darüber heißt „Proxy“.
15. Karte „Automatisch“ (`updates.auto`) als Überschrift allein – „Automatisch was?“.
16. `h1` uneinheitlich: Hosts, Instanzen, Domains tragen `h1` „Hosting“ (wiederholt den Menüpunkt), Übersicht/Updates/Edge/Einstellungen keine, die Host-Seite den Hostnamen. Alle Seiten setzen `ViewData["Title"] = "Hosting"` → der Brotkrumen sagt nie, welcher Tab offen ist.

**Gegen die Regeln**

17. **Erzähltexte**: Edge-Proxy beginnt mit der Karte „Wofür ist der Edge-Proxy?“ samt ASCII-Skizze; „Zugriffsschutz“ mit `p.muted`-Absatz; „Node hinzufügen“ mit einer 3-Schritte-Karte; viele lange `.help`-Texte (Adressen, Edge, Modul).
18. **Kein Toolkit**: Inline-`style` (`margin-top:14px`, `style="margin:0"`), eigene Reveal-Skripte im Edge-Tab, eigenes Ankreuz-Skript in Updates, `pre` mit Inline-Stil; zwei verschiedene Tab-Mechanismen übereinander (Link-Tabs + Button-Tabs) auf der Host-Seite.

**Leer / fast leer, falsch gruppiert**

19. **Hosting → Einstellungen** hat zwei Schalter. Der Modul-Schalter blendet halb aus (Domains/Edge/„Node hinzufügen“), Hosts, Instanzen, Updates bleiben – der Hinweistext sagt „blendet Nodes aus“, der Tab Hosts bleibt aber.
20. **Ports und Namensmuster** (Bereitstellung neuer Instanzen) stehen im Host-Tab „Einstellungen“, die Proxy-Art im Host-Tab „Proxy“, die automatischen Adressen (auch Bereitstellung) ebenfalls dort – Bereitstellung ist über zwei Tabs verteilt.
21. Der Host-Tab „Matcad“ ist eigentlich der Proxy dieses Hosts in Detailansicht und gehört neben „Proxy“, nicht als eigener Tab daneben; für Nodes fehlt er ganz (verständlich, aber ohne Erklärung im UI).
22. Die Instanz-Liste bietet pro Zeile einen Knopf „eingebettet öffnen“ (`asp-page="Site"`) – die Seite `Instances/Site` gibt es seit `002dd5a` nicht mehr; der Link ist tot.

---

## Teil 2 – Der Rest der Cloud-Seitenleiste

Heute (`Pages/Shared/_AdminLayout.cshtml`):

| Gruppe | Punkt | Seite | Tabs / Inhalt | Beobachtung |
|---|---|---|---|---|
| Betrieb | Hosting | `Admin/Hosting` | s. o. | einziger Punkt nur für Admins oben – Operatoren sehen die Gruppe ohne Überschrift |
| Betrieb | Instanzen | `Admin/Instances` | Liste/Kacheln; Details mit 9 Tabs (Übersicht, Hosting, Vorschau, Konfiguration, Statistik, Ereignisse, Spooler, Backups, Einstellungen) | „Alle aktualisieren“ und „Aufräumen“ als Knöpfe unter der Liste; Aufräumen (`Admin/Cleanup`) hat keinen eigenen Platz |
| Betrieb | Statistik | `Admin/Stats` | alle Seiten nebeneinander | ok; auch für Operatoren |
| Betrieb | Profile | `Admin/Profiles` | Edit mit 10 Tabs, einer davon hart „Wirksam“ | Profile + Store sind **Konfiguration**, nicht Betrieb |
| Betrieb | Store | `Admin/Store` | Plugins, Templates, Komponenten, Mail-Vorlagen, MatCMS Store | dito |
| Betrieb | **Spooler** | `Admin/Mail` | Mail-Warteschlange aller Instanzen | Fachjargon; auf der Instanz heißt der Tab ebenfalls „Spooler“. Besser „E-Mails“ |
| Betrieb | Backups | `Admin/Backups` | Übersicht, Liste, Archiv | ok |
| Verwaltung | Einstellungen | `Admin/Settings` | Allgemein, SMTP, KI, API, **Protokoll**, Sicherung | Protokoll-Einstellungen hier, Protokoll-Ansicht als eigener Menüpunkt; API-Schlüssel/OAuth (`Admin/ApiKeys`, `Admin/OAuthClients`) eigene Seiten, die aber „Einstellungen“ markieren |
| Verwaltung | Benachrichtigungen | `Admin/Notifications` | Matrix | ok |
| Verwaltung | Benutzer | `Admin/Users` | Liste/Edit | ok |
| Verwaltung | Protokoll | `Admin/Logs` | Cloud-Log + gespiegelte Instanz-Logs | Konfiguration liegt in Einstellungen |
| – | *(nicht im Menü)* | `Admin/Attention`, `Admin/Syncs`, `Admin/Cleanup` | Dashboard-Listen „Alle anzeigen“, Aufräumen | nur über Dashboard bzw. Knopf erreichbar – so gewollt, aber ohne Brotkrumen-Elternteil |
| Fuß | Version → Über | `Admin/About` | Version, Instanz-Image, Update-Prüfung | doppelt zu Hosting-Übersicht (s. 1.4 Nr. 1) |

Weitere Auffälligkeiten: Gruppennamen „Betrieb/Verwaltung“ (Cloud) vs. „Inhalt/Design/System“ (CMS) – die Cloud hat 8 Punkte unter
„Betrieb“, davon nur 3 wirklich Betrieb. „Store“ ist englisch, alles andere deutsch.

---

## Teil 3 – Entwurf

### 3.1 Grundsätze

- **Eine Tab-Ebene unter Hosting**, Bezeichnungen deutsch, ein Wort je Begriff: **Server** (statt Host/Node/Server gemischt;
  „Dieser Server“ statt „Dieser Host“), **Container**, **Domains**, **Proxy**.
- Jede Unterseite trägt die Hosting-Tabs, der aktive Tab ist korrekt markiert, `ViewData["Title"]` = Tab-Name (Brotkrumen:
  Cloud › Hosting › Server › hetzner-1). Kein `h1` „Hosting“ unter einem Menüpunkt „Hosting“.
- Keine Erklärkarten. Was erklärt werden muss, steht als **eine** Zeile `.help` am Feld; die Edge-Skizze kommt in die Doku
  (`docs/hosting-platform.md`).
- Nur Toolkit: `Ui`-/MatUi-Klassen, keine Inline-Stile, Reveal und „alle ankreuzen“ aus `admin-fields.js`/`admin-list.js`
  statt eigener Skripte.

### 3.2 Neue Tabs unter Hosting

| # | Tab | Pfad (neu) | Inhalt | kommt von |
|---|---|---|---|---|
| 1 | **Übersicht** | `/admin/hosting` | Kacheln + Server-Tabelle (CPU/RAM); kurzer Updates-Hinweis „N Updates offen“ als Kachel, die auf Tab 4 führt. **Keine** Update-Karte mehr | Übersicht (ohne Update-Karte) |
| 2 | **Container** | `/admin/hosting/container` | `_HostSites` aller Server, Filter inkl. **Server**-Filter; „Neue Instanz“ | Hosting → Instanzen (umbenannt, damit es nicht mit dem Menüpunkt Instanzen kollidiert) |
| 3 | **Server** | `/admin/hosting/server` (+ `/server/{id}`, `/server/neu`) | Liste „Dieser Server“ + Nodes; „Server hinzufügen“ | Hosts, Nodes/Create (bekommt die Tabs, die 3-Schritte-Karte entfällt – die Schritte zeigt die Detailseite nach dem Anlegen ohnehin) |
| 4 | **Updates** | `/admin/hosting/updates` | **alles zu Updates an einem Ort**: Karte „MatCMS Cloud“ (Version, letzter Lauf, prüfen, jetzt aktualisieren), Karte „Instanzen“ (die Ankreuz-Liste + Fortschritt), Karte „Server-Agenten“ (veraltete Agenten mit „aktualisieren“), Schalter „Instanzen automatisch aktualisieren“ | Übersicht-Karte + `Hosting/Updates` + Einstellung „Automatisch“ |
| 5 | **Domains** | `/admin/hosting/domains` | wie heute (gruppiert, Prüfen) + Knopf „Domain hinzufügen“, der Instanz wählt und dasselbe Formular wie der Instanz-Tab nutzt | Domains |
| 6 | **Proxy** | `/admin/hosting/proxy` | Karte **Edge** (an/aus, Ziel, vertrauenswürdige IPs, umstellen) – sichtbar sobald das Modul an ist, mit Schalter statt eines verschwindenden Tabs; keine Erklärkarte | Edge-Proxy |
| 7 | **Einstellungen** | `/admin/hosting/einstellungen` | Hosting-Modul an/aus **und** Bereitstellung von „Dieser Server“: Portbereich, Namensmuster | Einstellungen + Teile aus Host → Einstellungen |

Domains und Proxy bleiben modulabhängig; bei Modul aus zeigen Container/Server/Updates weiter das, was ohne Modul funktioniert
(wie heute), und der Hinweistext des Modul-Schalters wird an die Wirklichkeit angepasst.

### 3.3 Die Server-Seite (`/admin/hosting/server/{id}`)

Bleibt eine Seite mit eigenen Tabs, aber **auf 5 reduziert und eindeutig benannt**:

| Tab | Inhalt |
|---|---|
| **Übersicht** | Status, Versionen, Last, zuletzt gesehen; für Nodes der Startbefehl/Compose (einmal) und „Agent aktualisieren“ |
| **Container** | wie heute |
| **Images** | heute „Docker“ – der Tab zeigt Images, also heißt er so |
| **Proxy** | „Adressen dieses Servers“ (DNS), Reverse-Proxy (heute „Erreichbarkeit“), automatische Adressen + Wildcard, Node-Adresse für den Edge, Zugriffsschutz. **Matcad** wird hier eine aufklappbare Karte bzw. ein Unterbereich „Matcad-Routen und DNS-Anbieter“ (nur Dieser Server), die Karte heißt **„Matcad-Routen“** statt „Domains“ |
| **Verwaltung** | Name, Portbereich (Node), Token erneuern (nur **hier**), Sperren, Löschen; Aufträge als Liste darunter oder als Link „Aufträge (n)“ |

Damit entfallen: der doppelte „Token erneuern“, die zweite „Agent“-Karte, der eigene Matcad-Tab, „Aufträge“ als Tab für eine
reine Protokollliste.

### 3.4 Was verschwindet / umzieht

| Heute | Neu |
|---|---|
| Update-Karte auf der Übersicht | Tab **Updates** |
| `Hosting/Updates` ohne Tab | Tab **Updates** |
| Einstellung „Automatisch“ in Hosting → Einstellungen | Tab **Updates** |
| Update-Prüfung auf `Admin/About` | About zeigt nur Version + Kanal + Link „Updates“; Prüfen/Aktualisieren nur in Hosting → Updates |
| Hosting-Tab „Instanzen“ | Tab **Container** |
| Hosts / Node / Host | **Server** durchgehend |
| Host-Tab „Docker“ | Host-Tab **Images** |
| Host-Tab „Matcad“ | Teil des Host-Tabs **Proxy** |
| Host-Tab „Einstellungen“ + „Aufträge“ | Host-Tab **Verwaltung** |
| Portbereich/Namensmuster von Dieser Host im Host-Tab | Hosting → **Einstellungen** (Bereitstellung); Nodes behalten ihren Portbereich in **Verwaltung** |
| Karte „Wofür ist der Edge-Proxy?“, 3-Schritte-Karte, Intro-Absätze | entfallen (Doku) |
| Kachel-Link auf `Docker` | Link auf Server |
| toter Knopf „eingebettet öffnen“ in der Instanzliste | entfernen |

### 3.5 Weiterleitungen alter Adressen

Alle als 302 (wie die bestehenden `Docker`/`?tab=`-Weiterleitungen), damit ein späteres Zurückdrehen nicht am Browser-Cache scheitert.

| Alt | Neu |
|---|---|
| `/admin/hosting/instances` | `/admin/hosting/container` |
| `/admin/hosting/nodes` | `/admin/hosting/server` |
| `/admin/hosting/nodes/create` | `/admin/hosting/server/neu` |
| `/admin/hosting/nodes/details/{id?}?tab=docker\|proxy\|matcad\|settings\|jobs` | `/admin/hosting/server/{id}?tab=images\|proxy\|proxy\|verwaltung\|verwaltung` (ohne id → `dieser`) |
| `/admin/hosting/docker` | `/admin/hosting/server/dieser?tab=images` |
| `/admin/hosting/updates` | bleibt (jetzt Tab) |
| `/admin/hosting/settings` | `/admin/hosting/einstellungen` |
| `/admin/hosting#updates` (About-Link) | `/admin/hosting/updates` |
| `/admin/settings?tab=hosting` / `?tab=docker` | Ziele wie oben anpassen |

Links aus Mails/Benachrichtigungen, `AttentionService` und `docs/` auf die neuen Pfade umstellen; REST/MCP sind nicht betroffen
(eigene Pfade). Seiten-Namespaces dürfen beim Umbau gern bei `Nodes` bleiben – nur die Routen (`@page "…"`) ändern sich.

### 3.6 Ausblick: übrige Cloud-Menüs

Vorschlag für die Seitenleiste (Gruppen nach Frage, nicht nach Technik):

| Gruppe | Punkte |
|---|---|
| *(ohne Überschrift)* | Übersicht (Dashboard, heute nur über das Logo) |
| **Websites** | Instanzen · Statistik · Backups · E-Mails (heute „Spooler“) |
| **Plattform** (Admin) | Hosting · Profile · Store |
| **Verwaltung** (Admin) | Benutzer · Benachrichtigungen · Protokoll · Einstellungen |

- **Protokoll**: Ansicht und Aufbewahrung zusammen – Einstellungen-Tab „Protokoll“ wandert als Tab „Einstellungen“ auf die
  Protokoll-Seite (oder umgekehrt), nicht beides getrennt.
- **Aufräumen** und **„Alle aktualisieren“** gehören nicht unter die Instanzliste: Aufräumen wird Tab der Instanzen-Seite
  (oder Hosting-fremd unter Websites), „Alle aktualisieren“ ist Hosting → Updates.
- **Braucht Aufmerksamkeit / Abgleiche**: bleiben Dashboard-Ziele, bekommen aber einen Brotkrumen „Übersicht ›“.
- Profil-Tab „Wirksam“ übersetzen; „Store“ → „Store“ darf bleiben (Produktname „MatCMS Store“), dann aber konsequent.
- Instanz-Details: 9 Tabs sind viele – „Vorschau“ und „Ereignisse“ könnten in „Übersicht“ aufgehen; eigener Entwurf.

### 3.7 Ausblick: CMS-Admin-Seitenleiste

Heute (`src/MatCMS/Services/AdminNav.cs`): Inhalt (KI-Assistent, Seiten, Menüs, Formulare, Medien) · Design (Templates,
Komponenten, Plugins, Mail-Vorlagen, **Einstellungen**) · System (Statistik, Benutzer, API-Schlüssel, Backup, Protokoll,
Aufräumen) · Plugins.

- **Einstellungen** steht unter „Design“ – gehört nach System. Die Einstellungen haben 11 Tabs (Allgemein, Mail, KI, Fehler,
  Sicherheit, Protokoll, Code, Wartung, Sprachen, Social, Cloud); „Protokoll“ und „Fehler“ wie in der Cloud zur Protokoll-Seite.
- **Plugins** unter „Design“ ist Verwaltung, kein Design; Mail-Vorlagen passen eher zu Inhalt.
- Benennung angleichen: CMS „Backup“ vs. Cloud „Backups“, CMS „Mail-Vorlagen“ vs. Cloud-Store „Mail-Vorlagen“ – gleiche
  Wörter für gleiche Dinge in beiden Apps.
- Gleiche Gruppenlogik wie die Cloud (Inhalt · Darstellung · Erweiterungen · System), damit beide Back Offices sich gleich
  lesen.

### 3.8 Reihenfolge für die Umsetzung

1. Hosting-Tabs neu (Übersicht, Container, Server, Updates, Domains, Proxy, Einstellungen) + Titel/Brotkrumen + Weiterleitungen.
2. Server-Seite auf 5 Tabs, Matcad in Proxy, Dubletten raus.
3. Updates-Tab zusammenführen, About verschlanken.
4. Erklärkarten/Inline-Stile/eigene Skripte raus, Begriffe vereinheitlichen (de.json + en.json).
5. Toter „eingebettet öffnen“-Link.
6. Danach: Cloud-Seitenleiste neu gruppieren, dann CMS.

Jeder Schritt einzeln testbar; Tests, die Pfade aufrufen (`tests/MatCMS.Cloud.Tests`), mit den Weiterleitungen grün halten.
