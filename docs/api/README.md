# MatCMS Cloud – API-Handbuch (für Automatisierung / KI)

Dieses Dokument beschreibt, wie ein externes Programm **oder eine KI** verbundene MatCMS-Websites
über die **MatCMS-Cloud** verwaltet: Instanzen, Backups (Inhalte ziehen/zurückspielen), **Profile**
(ausgerollte Konfiguration) und **Inhalte** (Seiten/Beiträge/Blöcke) über den MCP-Server.

Alles läuft über **eine** Cloud (`https://<deine-cloud>`, z. B. `https://matcmscloud.matthix.de`).
Die Cloud greift nie in eine Site hinein – eine Site meldet sich per Heartbeat und **holt** sich
Aufträge ab. Schreibaktionen (Restore, Content-Ops) werden daher **vorgemerkt** und greifen beim
nächsten Heartbeat der Instanz (~1 Minute).

---

## 1. Authentifizierung

Alle Aufrufe nutzen einen **Operator-API-Schlüssel** (`mck_…`) im Header:

```
Authorization: Bearer mck_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
```

Schlüssel werden im Cloud-Admin unter **API-Schlüssel** angelegt (SHA-256 gespeichert, einmalig im
Klartext sichtbar). Rechte pro Schlüssel:

| Recht | Bedeutung |
|---|---|
| *(Basis)* | Instanzen listen, Backups ziehen/hochladen/listen, Profile **lesen**, Content-Ops lesen. |
| **CanRestore** | Darf ein Backup **live** zurückspielen (`…/restore`) – überschreibt die Produktivseite. |
| **CanManageProfiles** | Darf Profile und ihre Inhalte **schreiben** und Instanzen einem Profil zuordnen. |
| **CanManageStore** | Darf den cloud-weiten **Store** (Templates/Plugins/Komponenten/Mail-Templates) **schreiben**. Eine Store-Änderung erreicht **jedes Profil**, das den Eintrag ausgewählt hat – deshalb ein eigenes Recht. Lesen braucht nur einen gültigen Schlüssel. Ignoriert den Instanz-Umfang (der Store ist cloud-weit). |
| **CanManageHosting** | Darf **Container** von Instanzen starten/stoppen/neu starten/aktualisieren und ihre **Logs** lesen (im Instanz-Umfang). Mit einem Schlüssel für **alle Instanzen** zusätzlich: Hosting-Modul schalten und die **Cloud selbst aktualisieren**. |
| **Instanz-Umfang** | „Alle Instanzen" oder auf ausgewählte begrenzt. Eine unbekannte/außerhalb liegende Instanz gibt immer **404** (nicht 403), damit ein begrenzter Schlüssel keine Instanzen aufzählen kann. |

Fehlerformat immer JSON: `{ "error": "…" }` mit passendem HTTP-Status (401/403/404/400/409).
Rate-Limit-Policy `operatorApi` gilt für alle Endpunkte.

---

## 2. Instanzen

- `GET /api/v1/instances` → `{ canRestore, instances:[{ id, name, status, hosting, version, url, lastHeartbeatUtc }] }`
  `id` ist die **publicId** (im Rest der API als `{publicId}` verwendet).
- `GET /api/v1/instances/{publicId}/profile` → aktuelle Profil-Zuordnung + Sync-Stand
  `{ profileId, profileName, appliedRevision, profileRevision, inSync, lastSyncError }`

---

## 3. Backups – Inhalte ziehen & live zurückspielen

Ein Backup ist eine **ZIP** mit `content.json` (Seiten, Templates, Menüs, Einstellungen, Medien,
Formulare, Beiträge, Komponenten, Benutzer, Plugins) + Ordner `assets/` (die Mediendateien).
`content.json` ist UTF-8 **mit BOM**, hübsch eingerückt. Für zielgenaue Änderungen den exakten String
erst im `content.json` verifizieren; alles außerhalb der Änderung byte-für-byte lassen.

- `POST /api/v1/instances/{publicId}/backups/request` → `{ requestId }` – fordert ein **frisches**
  Backup an; die Instanz lädt es beim nächsten Heartbeat hoch.
- `GET /api/v1/instances/{publicId}/backups` → Liste (neueste zuerst) mit
  `{ id, fileName, sizeBytes, createdAt, uploadedAt, origin, sha256, requestId, restorePending, restoreDoneAt, restoreError }`.
  Auf `uploadedAt` + passende `requestId` pollen.
- `GET /api/v1/instances/{publicId}/backups/{id}/download` → ZIP-Bytes.
- `POST /api/v1/instances/{publicId}/backups` → lädt eine (bearbeitete) ZIP als **rohen Body** hoch.
  Dateiname im Header `X-MatCMS-Backup-Name` (muss auf `.zip` enden). → `{ ok, id, fileName, sha256 }`.
- `POST /api/v1/instances/{publicId}/backups/{id}/restore` → spielt das Backup **live** zurück
  (Recht **CanRestore**). Nur **vorgemerkt**; die Instanz zieht & wendet es beim nächsten Heartbeat an.

**Zyklus:** request → warten (~60 s) → download → `content.json`/`assets/` bearbeiten → upload → restore.
Immer eine datierte lokale Kopie des Vor- und Nach-Zustands behalten (Rollback).

---

## 4. Profile – ausgerollte Konfiguration

Ein **Profil** bündelt Konfiguration (Einstellungen, SMTP, Übersetzung, Backup-Plan, Benutzer,
Plugins, Komponenten, Templates, Mail-Templates) und wird an alle zugeordneten Instanzen ausgerollt.
Jede Schreibänderung erhöht die **Revision** des Profils; die Instanz zieht die neue Konfiguration
beim nächsten Heartbeat. **Nichts wird gelöscht**, weil ein Profil es nicht mehr listet (add-only für
Benutzer; Entfernen stoppt nur künftige Rollouts). Sync-Modi je Payload: `keep` (synchron halten,
überschreibt), `add` (nur ergänzen), `once` (einmalig übernehmen). Benutzer sind immer add-only.

Schreiben braucht **CanManageProfiles**. Lesen braucht nur einen gültigen Schlüssel.

### Profile allgemein
- `GET /api/v1/profiles` → `{ canManageProfiles, profiles:[{ id, name, description, isDefault, revision, joinCode, instanceCount }] }`
- `GET /api/v1/profiles/{id}` → vollständiges Profil inкл. aller Payload-Listen (Secrets maskiert als `***`) und zugeordneter Instanzen.
- `POST /api/v1/profiles` `{ name, description? }` → `{ id, name, joinCode }`
- `PATCH /api/v1/profiles/{id}` – Felder optional, nur gesetzte werden geändert:
  `name, description, autoApprove, autoUpdateLocal, notifyOffline, notifyUpdate, notifyRecipients,
  activateTemplateName, syncSettings, syncUsers, syncPlugins, syncComponents, syncTemplates,
  syncMailTemplates, removeDefaultAdmin, settingsMode, usersMode, pluginsMode, componentsMode,
  templatesMode, mailTemplatesMode` (Modi: `keep|add|once`; Users nie `keep`).
- `PUT /api/v1/profiles/{id}/ai` `{ syncAi?, aiMonthlyTokenBudget?, aiInstruction?, backupBeforeAiChange? }`
- `DELETE /api/v1/profiles/{id}` (zugeordnete Instanzen fallen auf globale Policy zurück)
- `POST /api/v1/profiles/{id}/duplicate` → `{ id, name, joinCode }`
- `POST /api/v1/profiles/{id}/make-default`
- `POST /api/v1/profiles/{id}/rotate-join-code` → `{ joinCode }`
- `POST /api/v1/profiles/{id}/touch` – Revision erhöhen (erzwingt Neu-Sync aller Instanzen)

### Freie Einstellungen (Key/Value)
- `POST /api/v1/profiles/{id}/settings` `{ key, value }` – **Gruppen-Keys** (smtp.*, translate.*, backup.*, ai.*, mail.transport) werden abgelehnt; dafür die Gruppen-Endpunkte nutzen.
- `DELETE /api/v1/profiles/{id}/settings/{key}`

### Gruppen SMTP / Übersetzung
- `PUT /api/v1/profiles/{id}/smtp` `{ mailSource: "global"|"own"|"cloud", host?, port?, user?, password?, fromEmail?, fromName?, ssl? }` – Passwort wird verschlüsselt gespeichert; leer lassen behält das gespeicherte.
- `DELETE /api/v1/profiles/{id}/smtp` – Rollout aus (gespeicherte Werte bleiben).
- `PUT /api/v1/profiles/{id}/translation` `{ provider?, apiKey?, url? }` – `apiKey` verschlüsselt.
- `DELETE /api/v1/profiles/{id}/translation`

### Benutzer (add-only, Passwort wird serverseitig gehasht)
- `POST /api/v1/profiles/{id}/users` `{ username, email?, displayName?, password? | passwordHash?, role? }`
  Neuer Benutzer braucht `password` **oder** `passwordHash`. Klartext wird nie gespeichert.
- `DELETE /api/v1/profiles/{id}/users/{username}`

### Komponenten
- `POST /api/v1/profiles/{id}/components` `{ type, name, description?, icon?, fieldsJson?, templateHtml? }` (`fieldsJson` muss gültiges JSON sein; `type` = Identität)
- `DELETE /api/v1/profiles/{id}/components/{type}`

### Templates (Designer-Felder; Identität = `name`)
- `POST /api/v1/profiles/{id}/templates` `{ name, accentColor?, secondaryColor?, headingFont?, bodyFont?, buttonStyle?, headingColor?, textColor?, backgroundColor?, customCss?, customJs?, layoutHtml?, menuMapJson?, parametersJson?, paramValuesJson? }`
- `DELETE /api/v1/profiles/{id}/templates/{name}`

### Mail-Templates
- `POST /api/v1/profiles/{id}/mail-templates` `{ key, name?, description?, subject, body?, enabled?, isHtml? }`
- `DELETE /api/v1/profiles/{id}/mail-templates/{key}`

### Plugins (rohes ZIP-Bundle)
- `GET /api/v1/profiles/{id}/plugins/{pluginKey}/download`
- `POST /api/v1/profiles/{id}/plugins` – Body = das **rohe Plugin-ZIP** (wie `PluginPackager.Export`); Key/Name/Version werden aus `plugin.json` gelesen, Upsert nach Key.
- `DELETE /api/v1/profiles/{id}/plugins/{pluginKey}`

### Instanz ↔ Profil + Sync
- `PUT /api/v1/instances/{publicId}/profile` `{ profileId }` – zuordnen/verschieben (setzt `appliedRevision=0`, erzwingt Neu-Pull). Braucht **CanManageProfiles** **und** Instanz-Umfang.
- `DELETE /api/v1/instances/{publicId}/profile` – Zuordnung entfernen.
- `POST /api/v1/instances/{publicId}/sync` – Neu-Sync vormerken (Instanz zieht `/config` beim nächsten Heartbeat). Es gibt **keinen** cloud-seitigen Sofort-Apply; das Ergebnis erscheint im Sync-Report der Instanz.

---

## 5. Store – der cloud-weite Katalog

Der **Store** ist der globale Katalog aus **Templates, Plugins, Komponenten und Mail-Templates**, aus
dem Profile auswählen und den Instanzen durchstöbern. Eine Änderung an einem Store-Eintrag erreicht
**jedes Profil, das ihn ausgewählt hat** (die Cloud erhöht deren Revision automatisch), und rollt beim
nächsten Heartbeat auf deren Instanzen aus. Das Entfernen aus dem Store stoppt nur künftige Rollouts;
auf den Instanzen bleibt der Eintrag bestehen.

Schreiben braucht **CanManageStore**. Lesen braucht nur einen gültigen Schlüssel. Identität je Typ:
Template = `name`, Komponente = `type`, Mail-Template/Plugin = `key`. Ein `POST` ist ein **Upsert**
(gleiche Identität aktualisiert, sonst neu); die Antwort enthält `created: true|false`.

- `GET /api/v1/store` → `{ canManageStore, plugins[], templates[], components[], mailTemplates[] }` (je mit `usedBy`-Zähler).
- **Templates:** `GET /api/v1/store/templates` · `GET …/templates/{name}` · `POST …/templates` `{ name, description?, accentColor?, secondaryColor?, headingFont?, bodyFont?, buttonStyle?, headingColor?, textColor?, backgroundColor?, altBackground?, containerWidth?, buttonRadius?, headerBackground?, headerTextColor?, headerPadding?, customCss?, customJs?, layoutHtml?, menuMapJson?, parametersJson?, paramValuesJson?, partsJson? }` (nur gesetzte Felder ändern sich) · `DELETE …/templates/{name}`
- **Komponenten:** `GET …/components` · `GET …/components/{type}` · `POST …/components` `{ type, name, description?, icon?, fieldsJson?, templateHtml? }` (`fieldsJson` muss gültiges JSON sein) · `DELETE …/components/{type}`
- **Mail-Templates:** `GET …/mail-templates` · `GET …/mail-templates/{key}` · `POST …/mail-templates` `{ key, name?, description?, subject, body?, enabled?, isHtml? }` · `DELETE …/mail-templates/{key}`
- **Plugins (rohes ZIP-Bundle):** `GET …/plugins` · `GET …/plugins/{key}/download` · `POST …/plugins` (Body = das **rohe Plugin-ZIP**; Key/Name/Version werden aus `plugin.json` gelesen, Upsert nach Key, 64 MB) · `DELETE …/plugins/{key}`

---

## 6. Hosting – Container & Cloud-Update

Alles, was der **Hosting-Tab** einer Instanz und die Seite **Hosting** können, geht auch per API. Container-
Aktionen wirken sofort (die Cloud spricht mit ihrem eigenen Docker-Daemon) und nur auf Instanzen, die auf
**diesem** Docker-Host laufen (`local: true`); eine Instanz woanders antwortet mit `409`.

- `GET /api/v1/hosting` → `{ enabled, dockerConfigured, dockerReachable, canManageHosting }`
- `PUT /api/v1/hosting` `{ enabled }` – Hosting-Modul schalten (**CanManageHosting + alle Instanzen**). Steuert Menüpunkt + Provisionierung; Container-Aktionen gehen immer.
- `GET /api/v1/instances/{publicId}/container` → `{ hosting, local, cloudManaged, containerState, localPort, container:{ name, image, state, startedAt, restartCount, publishedPort, health } }`
- `POST /api/v1/instances/{publicId}/container/{action}` mit `action` = `start` | `stop` | `restart` | `update` (**CanManageHosting**). `update` zieht das neueste Image und erstellt den Container neu (mit Rollback) – blockierend.
- `GET /api/v1/instances/{publicId}/container/logs?tail=200` → `{ logs }` – stdout/stderr mit Zeitstempeln, 1–5000 Zeilen (**CanManageHosting**).
- `GET /api/v1/cloud/update?check=true` → `{ current, latest, updateAvailable, canSelfUpdate, blocker, lastRun:{ state, message, log[] } }`
- `POST /api/v1/cloud/update` → `202` – **aktualisiert die Cloud selbst** (**CanManageHosting + alle Instanzen**). Ein Helfer-Container tauscht den Cloud-Container, prüft ihn per Health-Check und rollt bei Fehlern **Container und Datenbank** zurück. Die Cloud ist ~1–2 Min. weg; danach `GET …/cloud/update` → `lastRun.state` = `succeeded` | `current` | `rolled-back` | `failed`.

### Reverse-Proxy & Domains

Der Proxy ist **optional**: `provider` = `none` (kein Proxy – Instanzen über ihren Host-Port, eine Domain wird nur
vermerkt), `matcad` (Route über Matcads REST-API) oder `caddy` (Route direkt über Caddys Admin-API, nur eigene
`@id`-Routen `matcms-…`, fremde Routen bleiben unberührt). `upstream` sagt, wie der Proxy die Instanz erreicht:
`network` (die Cloud hängt den Container an das Docker-Netz `network` des Proxys, Ziel `http://<container>:8080`)
oder `hostport` (Ziel `http://<upstreamHost>:<Host-Port>`).

- `GET /api/v1/hosting/overview` → `{ hosts:[{ node, thisHost, online, cpus, memTotal, sitesRunning, sitesTotal, cpuPercent, memBytes, … }], instances:[{ instanceId, name, container, node, state, status, cpuPercent, memBytes, memLimit, port, domain, version, updateAvailable }] }` – das Hosting-Dashboard; Node-Werte = letzte Messung des Agents (ca. minütlich), `instanceId` null = Container ohne Cloud-Verbindung (**CanManageHosting + alle Instanzen**).
- `GET /api/v1/hosting/updates` → `{ latest, instances:[{ instanceId, name, node, version }] }` – Instanzen, die die Cloud selbst aktualisieren kann und die hinter dem neuesten Release liegen (im Instanz-Umfang des Schlüssels).
- `POST /api/v1/hosting/updates` `{ instanceIds? }` → `{ runId, count }` – aktualisiert die genannten (weggelassen = alle) nacheinander mit Rollback; Ids ohne Update werden übergangen; 409, solange die Cloud sich selbst aktualisiert (**CanManageHosting**).
- `GET /api/v1/hosting/updates/{runId}` → `{ done, total, completed, items:[{ name, from, to, status, message }] }` – Fortschritt.
- `GET /api/v1/hosting/images` → `{ images:[{ id, repo, tags, version, size, created, inUse, dangling }], prunable, prunableBytes }` (`version` aus dem Label `matcms.version`, null bei älteren Images) – MatCMS-Images auf dem Cloud-Host (**CanManageHosting + alle Instanzen**).
- `POST /api/v1/hosting/images/prune` → `{ removed, bytesReclaimed }` – nur alte, ungetaggte, unbenutzte MatCMS-Images (**CanManageHosting + alle Instanzen**).
- `GET /api/v1/hosting/proxy` → `{ provider, managesRoutes, matcadUrl, matcadTokenSet, caddyAdminUrl, caddyServer, upstream, network, upstreamHost }` – der Matcad-Schlüssel wird **nie** zurückgegeben.
- `PUT /api/v1/hosting/proxy` `{ provider?, matcadUrl?, matcadToken?, clearMatcadToken?, caddyAdminUrl?, caddyServer?, upstream?, network?, upstreamHost? }` – Teil-Update, weggelassen = unverändert (**CanManageHosting + alle Instanzen**). Schon veröffentlichte Domains wandern **nicht** mit – nach einem Provider-Wechsel neu veröffentlichen.
- `POST /api/v1/hosting/proxy/test` → `{ ok, provider, message }` – Proxy erreichbar? Netz vorhanden? (**CanManageHosting**)
- `GET /api/v1/instances/{publicId}/domain?check=true` → `{ domain, via, provider, routeId, error, publishedAt, routeExists, hostAddress, hostRouteId, hostError, hostRouteExists }` – Kundendomain (`via` = `edge` | `host`) und automatische Host-Adresse; `check` fragt die Proxys, ob die Routen noch existieren.
- `POST /api/v1/instances/{publicId}/host-address` `?pushCanonical=true` / `DELETE …` – automatische Host-Adresse (`name.<Basis-Domain>` am Proxy des Hosts) anlegen/erneuern bzw. entfernen; eine Kundendomain am Edge wird mit umgestellt (**CanManageHosting**).
- `GET|PUT /api/v1/hosting/auto-domain` `{ enabled, baseDomain }` – automatische Adressen für „Dieser Host“ (Nodes: `PUT /api/v1/nodes/{id}` mit `autoDomainEnabled`, `autoDomainBase`, `address`). `POST /api/v1/hosting/host-addresses?nodeId=` legt fehlende Adressen für bestehende Instanzen an (**CanManageHosting + alle Instanzen**).
- `GET|PUT /api/v1/hosting/edge` `{ enabled?, useHostProxy?, provider?, caddyAdminUrl?, caddyServer?, matcadUrl?, matcadToken?, clearMatcadToken? }` – zentraler Edge-Proxy für Kundendomains; `POST …/edge/test`; `POST …/edge/move` stellt vor dem Umschalten veröffentlichte Kundendomains auf den aktuellen Weg um (**CanManageHosting + alle Instanzen**). Mit Caddy leitet der Edge auf die Host-Adresse (HTTPS, Host-Header umgeschrieben), mit Matcad auf Node-Adresse:Port.
- `PUT /api/v1/instances/{publicId}/domain` `{ domain, pushCanonical? = true }` – veröffentlichen bzw. umziehen (Route + TLS; ohne Proxy nur vermerkt). Der DNS-Eintrag muss schon auf den Proxy zeigen. `pushCanonical` setzt auf der Site `site.canonicalUrl = https://<domain>` + „hinter HTTPS-Proxy“ (beim nächsten Heartbeat). `400` = keine gültige Domain, `409` = Domain vergeben / Instanz nicht auf diesem Docker-Host / Proxy-Fehler (**CanManageHosting**, Schlüssel-Umfang).
- `DELETE /api/v1/instances/{publicId}/domain?pushCanonical=true` – Route entfernen (erst die Route, dann der Eintrag; idempotent) (**CanManageHosting**, Schlüssel-Umfang).

Beim **Anlegen** einer Instanz mit Domain (Hosting → Neue Instanz) wird die Route sofort angelegt und beim ersten
Heartbeat der neuen Instanz übernommen.

### Nodes & Provisionierung

Ein **Node** ist ein weiterer Docker-Host. Dort läuft ein Agent (das Cloud-Image im Modus `--node-agent`), der sich
**von dort aus** mit der Cloud verbindet und Aufträge abholt — die Cloud greift nie auf einen Host zu. „Dieser Host“
(`local`) ist der Docker-Daemon der Cloud selbst und wird wie bisher über `/api/v1/hosting/proxy` eingestellt.
Container-Aktionen, Logs, Update und Domains (Abschnitte oben) funktionieren für Instanzen auf einem Node genauso;
sie laufen als Auftrag auf dem Node, der Aufruf wartet auf das Ergebnis (`container` → `onNode: true`).

- `GET /api/v1/nodes` → `{ local:{…}, nodes:[{ id, name, revoked, online, lastSeenAt, agentVersion, hostName, dockerVersion, dockerError, instances, portFrom, portTo, proxy:{…} }] }` (**CanManageHosting**)
- `POST /api/v1/nodes` `{ name }` → `{ id, token, command }` – Token und fertiger `docker run`-Befehl **nur in dieser Antwort** (**CanManageHosting + alle Instanzen**)
- `GET /api/v1/nodes/{id}` – wie oben plus `containers` (die gemeldeten MatCMS-Container)
- `PUT /api/v1/nodes/{id}` `{ name?, portFrom?, portTo?, provider?, matcadUrl?, matcadToken?, clearMatcadToken?, caddyAdminUrl?, caddyServer?, upstream?, network?, upstreamHost? }` – Teil-Update; der Proxy wird **vom Node aus** angesprochen (**alle Instanzen**)
- `POST /api/v1/nodes/{id}/test-proxy` – Proxy-Test, läuft auf dem Node (**CanManageHosting**)
- `POST /api/v1/nodes/{id}/revoke` | `activate` | `token` (neuer Token + Befehl; der alte gilt sofort nicht mehr) | `update-agent` (der Agent aktualisiert sich selbst über einen Helfer-Container, mit Rollback; `GET …/nodes/{id}` zeigt danach `agentVersion`, `agentOutdated`) – (**alle Instanzen**)
- `DELETE /api/v1/nodes/{id}` – Eintrag löschen; die Websites laufen weiter, die Cloud steuert sie nur nicht mehr (**alle Instanzen**)
- `GET /api/v1/nodes/{id}/jobs?take=50` → `[{ id, kind, state: pending|running|done|failed, message, requestedBy, createdAt, startedAt, finishedAt }]`
- `POST /api/v1/hosting/instances` `{ name, nodeId? ("local"/leer = dieser Host), profileId? (leer = Standardprofil), domain?, imageTag?, pushCanonical? }` → `{ ok, containerName, port, domainFailed, message }` – **neue Website anlegen** (Hosting-Modul an; **CanManageHosting + alle Instanzen**). Die Instanz erscheint in `GET /api/v1/instances`, sobald sie sich mit dem Join-Code ihres Profils angemeldet hat (meist < 1 Min.).

### Umziehen zwischen Hosts

- `POST /api/v1/instances/{publicId}/migrate` `{ target: "local" | <Node-ID>, removeSource?: false }` → `202 { migration:{ id, state, step, from, to, log[] } }` – zieht die Website auf einen anderen Host (**CanManageHosting**, Schlüssel-Umfang; `removeSource: true` braucht zusätzlich **CanRestore**). Der Datenträger wird 1:1 kopiert: Inhalte, Uploads, Benutzer und die Cloud-Verbindung bleiben, die Instanz behält ihre ID. Die Domain zieht mit (DNS danach auf den neuen Host zeigen lassen). **Die Website ist währenddessen offline.** Ohne `removeSource` wird die alte Kopie stillgelegt (gestoppt, umbenannt `…-moved-<Datum>`, startet nie mehr von selbst). Scheitert etwas, läuft die Website wieder an der alten Stelle (`rolled-back`).
- `GET /api/v1/instances/{publicId}/migrations` → `[{ id, state: running|succeeded|failed|rolled-back, step, from, to, removeSource, bytes, requestedBy, startedAt, finishedAt, log[] }]` – zum Verfolgen (neueste zuerst).
- Entfernen (Container + Datenträger) einer Instanz auf einem Node geht jetzt genauso wie lokal über *Instanz → Entfernen*.

Antwortet ein Node nicht rechtzeitig, kommt `409` mit „läuft noch (Auftrag #…)“ — das Ergebnis steht dann im
Auftragsverlauf. Ein nicht verbundener Node antwortet sofort mit `409` „nicht verbunden“.

---

## 6b. Benachrichtigungen – wer bekommt was

Die Benachrichtigungs-Matrix: Zeilen sind Empfänger, Spalten Ereignisse.
Zeilen-Schlüssel: `g:admins` (alle Admins), `g:operators` (alle Operatoren — **nur ihre Instanzen**), `u:<Benutzer-ID>`, `e:<Adresse>`.
Ereignisse: `offline`, `update`, `updateFailed`, `migration`, `nodeOffline`, `removal`. `nodeOffline` und `removal` betreffen die ganze Cloud und gehen **nie** an Operatoren.

- `GET /api/v1/notifications` → `{ events, fleetOnlyEvents, rowKinds, users:[{ key, username, displayName, email, role }], rows:[{ key, events[] }] }` (Schlüssel für **alle Instanzen**)
- `PUT /api/v1/notifications` `{ rows:[{ key, events[] }] }` – ersetzt die ganze Matrix; unbekannte Ereignisse/Schlüssel werden verworfen (**alle Instanzen + CanManageProfiles**)

Ein Profil kann Offline-/Update-Meldungen für seine Instanzen abschalten; seine „zusätzlichen Empfänger“ bekommen die Meldungen seiner Instanzen **zusätzlich**.

---

## 7. MCP-Server (`/mcp`) – Inhalte per KI verwalten

Für KI-Clients (ChatGPT, Claude, Cursor) bietet die Cloud einen **Model-Context-Protocol**-Server
unter `/mcp` (streamable HTTP). Auth = **derselbe** `mck_…`-Schlüssel als `Authorization: Bearer …`.
Die Tools laufen über dieselben Dienste wie die REST-API und sind auf den Schlüssel-Umfang begrenzt.

Inhalts-Tools schreiben **nicht** direkt in die Site, sondern reihen eine **Content-Op** ein, die die
Instanz beim nächsten Heartbeat über ihre **eigenen** Validierer anwendet (add-only). Sie sind daher
**asynchron**: das Tool liefert eine `opId`, dann `get_content_op(opId)` pollen.

| Tool | Zweck |
|---|---|
| `list_instances` | verbundene Sites auflisten |
| `get_instance` | Detail einer Site |
| `list_backups` / `request_backup` / `restore_backup` | Backup-Zyklus (restore braucht CanRestore) |
| `list_pages` | Seiten einer Site (async → `get_content_op`) |
| `get_page` | eine Seite mit Blöcken lesen (async) |
| `create_page` | neue Seite anlegen (add-only) |
| `update_page_blocks` | die Blöcke einer Seite ersetzen (CanRestore) |
| `create_post` | neuen Beitrag anlegen (add-only) |
| `create_form` | neues Formular anlegen (add-only; Felder als JSON-Array) — CanRestore |
| `set_setting` | eine Site-Einstellung setzen (`cloud.*`-Schlüssel werden abgewiesen) — CanRestore |
| `analyze_cleanup` | unbenutzte Medien/Komponenten + Plugins der Site ermitteln (async → `get_content_op`, `result` = Liste) — read |
| `apply_cleanup` | gewählte unbenutzte Medien/Komponenten/Plugins löschen (Site re-prüft „noch unbenutzt") — **destruktiv**, CanRestore |
| `get_content_op` | Status/Ergebnis einer Op abfragen: `pending` / `applied` / `skipped-exists` / `failed` |

**Store-Tools** (verwalten den cloud-weiten Katalog direkt, ohne Instanz-Umlauf; **synchron**; Schreiben braucht **CanManageStore**):

| Tool | Zweck |
|---|---|
| `list_store` | den Store auflisten (Templates/Komponenten/Mail-Templates/Plugins, mit `usedBy`) |
| `upsert_store_template` / `delete_store_template` | ein Theme (Template) anlegen/ändern bzw. entfernen |
| `upsert_store_component` / `delete_store_component` | eine Komponente anlegen/ändern bzw. entfernen |
| `upsert_store_mail_template` / `delete_store_mail_template` | ein Mail-Template anlegen/ändern bzw. entfernen |
| `delete_store_plugin` | ein Plugin aus dem Store entfernen (Hochladen bleibt REST: `POST /api/v1/store/plugins`) |

**Hosting-Tools** (wirken sofort; Rechte wie REST-Abschnitt 6):

| Tool | Zweck |
|---|---|
| `get_hosting_status` | Hosting-Modul an/aus, Docker erreichbar |
| `set_hosting_enabled` | Hosting-Modul schalten (Hosting-Recht + alle Instanzen) |
| `get_hosting_overview` | Hosts mit Größe/Last + alle Container mit CPU/RAM/Host/Update (Hosting-Recht + alle Instanzen) |
| `list_update_candidates` / `start_instance_updates` / `get_update_run` | Instanzen mit Update / (Teil-)Update starten (Hosting-Recht) / Fortschritt |
| `publish_host_address` / `remove_host_address` | automatische Host-Adresse einer Instanz (Hosting-Recht) |
| `set_auto_domain` / `create_missing_host_addresses` | automatische Adressen für „Dieser Host“ / fehlende nachziehen (Hosting-Recht + alle Instanzen) |
| `get_edge_config` / `configure_edge` / `test_edge` / `move_domains_to_edge_setting` | Edge-Proxy für Kundendomains (Hosting-Recht + alle Instanzen) |
| `list_images` / `prune_images` | MatCMS-Images auf dem Cloud-Host / alte unbenutzte entfernen (Hosting-Recht + alle Instanzen) |
| `get_container_status` | Container-Zustand einer Instanz (lokal?, Image, Start, Neustarts, Health) |
| `container_action` | `start` / `stop` / `restart` / `update` des Instanz-Containers — vorher mit dem Nutzer bestätigen |
| `get_container_logs` | letzte Zeilen stdout/stderr des Instanz-Containers |
| `get_cloud_update_status` | Cloud-Version, neueste Version, ob Selbst-Update möglich ist, Verlauf des letzten Updates |
| `update_cloud` | **Cloud selbst aktualisieren** (Helfer-Container, Health-Check, Rollback) — vorher bestätigen; Hosting-Recht + alle Instanzen |
| `get_proxy_config` | Reverse-Proxy-Konfiguration (Provider, Adressen, Upstream-Modus; ohne Schlüssel) |
| `configure_proxy` | Proxy einstellen (Teil-Update; Hosting-Recht + alle Instanzen) |
| `test_proxy` | Proxy erreichbar? Netz vorhanden? |
| `get_domain_status` | Domain einer Instanz, Provider, Route noch vorhanden? |
| `publish_domain` | Instanz unter einer Domain veröffentlichen/umziehen (Route + TLS) |
| `unpublish_domain` | Domain entfernen — Site ist darunter danach nicht mehr erreichbar, vorher bestätigen |
| `list_nodes` / `get_node` | Docker-Hosts (dieser Host + Nodes) mit Status, Versionen, Websites; `get_node` mit Containern |
| `create_node` | Node anlegen → Token + `docker run`-Befehl einmalig (alle Instanzen) |
| `update_node` | Name, Portbereich, Proxy eines Nodes ändern (alle Instanzen) |
| `set_node_revoked` / `rotate_node_token` / `delete_node` | sperren/freigeben, neuer Token, löschen (alle Instanzen) |
| `test_node_proxy` | Proxy-Test auf dem Node |
| `update_node_agent` | Agent eines Nodes aktualisieren (Helfer-Container, Rollback, Node ~1 Min. getrennt) |
| `list_node_jobs` | Auftragsverlauf eines Nodes (z. B. ein noch laufendes Update verfolgen) |
| `create_instance` | **Neue Website anlegen** — auf diesem Host oder einem Node, optional mit Domain (alle Instanzen) |
| `migrate_instance` | **Website umziehen** auf einen anderen Host (Daten 1:1, Domain zieht mit; offline währenddessen) — vorher bestätigen |
| `get_migrations` | Umzüge einer Website mit Zustand, Schritt und Protokoll |
| `get_notifications` / `set_notifications` | Benachrichtigungs-Matrix lesen / komplett ersetzen (Abschnitt 6b) |

**`create_page`** (Beispiel-Eingabe):
```json
{
  "publicId": "oC4LaoOTRafCBoT8",
  "title": "Über uns",
  "slug": "ueber-uns",
  "blocksJson": "[{\"type\":\"hero\",\"data\":{\"heading\":\"Über uns\",\"subheading\":\"…\"}},{\"type\":\"richtext\",\"data\":{\"heading\":\"Unsere Geschichte\",\"body\":\"<p>…</p>\"}}]",
  "showInNav": true
}
```
Blöcke sind ein JSON-Array `[{ "type": "<blocktyp>", "data": { "<feld-id>": "<wert>" } }]`. Die Instanz
**re-validiert**: nur bekannte Blocktypen und **Textfelder** überleben (Bilder/Auswahlfelder werden
verworfen und über den Editor bzw. Backup/Restore gesetzt). RichText wird server-seitig **bereinigt**
(kein `<script>`/`on*`/`javascript:`). Slug existiert schon → `skipped-exists` (nie überschreiben).

Deep-Edits an Bildern, Layout, Farben etc. laufen über **Backup/Restore** (Abschnitt 3), nicht über
Content-Ops.

---

## 8. Block-Katalog

Blöcke, die eine MatCMS-Site kennt, mit ihren Feld-IDs. Spalte **KI-setzbar** = das Feld überlebt eine
`create_page`/`update_page_blocks`-Content-Op (nur Text-/RichText-Felder). Bild-/Auswahl-/Link-Felder
setzt man über den Editor oder Backup/Restore. Container-Blöcke (`section`, `columns`, `cards`,
`accordion`, `features`, `servicegrid`, `timeline`, `pricing`, `references`) enthalten
Kind-Blöcke (`card`, `column`, `faq`, `step`, `service`, `feature`, `plan`, `reference` …).

> ¹ „KI-setzbar" = über den Content-Op-Kanal setzbar (Textfelder). Der Katalog wird aus
> `src/MatCMS/Content/BlockRegistry.cs` erzeugt (`tools/gen-api-blocks.js`) und kann bei einer
> Instanz um Komponenten-/Plugin-Blöcke erweitert sein.

<!-- BLOCKS:BEGIN (generiert – nicht von Hand pflegen) -->
### `hero` — Hero (Kopfbereich)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Textarea | ✅ | Überschrift |
| `subheading` | Textarea | ✅ | Untertext |
| `image` | Image | — | Bild (Band darunter) |
| `buttonText` | Text | ✅ | Button-Text |
| `buttonUrl` | Url | — | Button-Link |
| `align` | Select | — | Ausrichtung |
| `imageHeight` | Select | — | Bildhöhe |
| `imagePosition` | Select | — | Bildausschnitt (Fokus) |
| `heroStyle` | Select | — | Hero-Stil |
| `heroBg` | Select | — | Hero-Hintergrund |
| `images` | List | — | Weitere Bilder (Slideshow) |
| `image` | Image | — | Bild (Band darunter) |
| `alt` | Text | ✅ | Alternativtext |
| `heroMedia` | Select | — | Slideshow-Effekt |
| `heroInterval` | Select | — | Wechsel alle |
| `heroFit` | Select | — | Bildanzeige |
| `heroWidth` | Select | — | Bildbreite |

### `richtext` — Text

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `body` | RichText | ✅ | Inhalt |
| `align` | Select | — | Ausrichtung |
| `width` | Select | — | Breite |

### `columns` — Spalten

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `columns` | Select | — | Spaltenanzahl |

### `column` — Spalte

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `title` | Text | ✅ | Titel |
| `body` | RichText | ✅ | Text |
| `bg` | Select | — | Hintergrundfarbe |
| `fg` | Select | — | Textfarbe |

### `servicegrid` — Leistungen (Raster)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `columns` | Select | — | Spalten (Desktop) |

### `service` — Service

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `icon` | Text | ✅ | Icon (Tabler-Name) |
| `title` | Text | ✅ | Titel |
| `text` | Textarea | ✅ | Beschreibung |

### `cta` — Call-to-Action

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `text` | Textarea | ✅ | Text |
| `buttonText` | Text | ✅ | Button-Text |
| `buttonUrl` | Url | — | Button-Link |

### `countup` — Zähler

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `date` | Text | ✅ | Datum/Uhrzeit |
| `mode` | Select | — | Richtung |

### `form` — Formular

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `form` | Select | — | Formular |
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |

### `memberlogin` — Mitglieder-Login

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |

### `image` — Bild

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `image` | Image | — | Bild |
| `alt` | Text | ✅ | Alternativtext |
| `caption` | Text | ✅ | Bildunterschrift |
| `width` | Select | — | Breite |

### `accordion` — Akkordeon (FAQ)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |

### `faq` — Frage

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `question` | Text | ✅ | Frage |
| `answer` | RichText | ✅ | Antwort |

### `quote` — Zitat

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `quote` | Textarea | ✅ | Zitat |
| `author` | Text | ✅ | Autor / Quelle |

### `imagetext` — Bild & Text

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `image` | Image | — | Bild |
| `heading` | Text | ✅ | Überschrift |
| `body` | RichText | ✅ | Inhalt |
| `imageSide` | Select | — | Bildseite |

### `spacer` — Abstand

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `size` | Select | — | Größe |

### `logostrip` — Logo-Leiste

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `items` | List | — | Logos |
| `image` | Image | — | Logo |
| `alt` | Text | ✅ | Alternativtext |
| `url` | Url | — | Link (optional) |

### `features` — Leistungen (Container)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `columns` | Select | — | Spaltenanzahl |

### `feature` — Leistung

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `title` | Text | ✅ | Titel |
| `text` | Textarea | ✅ | Text |
| `image` | Image | — | Bild (optional) |

### `herocta` — Hero mit Aktion

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Textarea | ✅ | Überschrift |
| `priceOld` | Text | ✅ | Alter Preis (durchgestrichen) |
| `priceNew` | Text | ✅ | Neuer Preis (hervorgehoben) |
| `sub` | Textarea | ✅ | Untertext |
| `buttonText` | Text | ✅ | Button-Text |
| `buttonUrl` | Url | — | Button-Link |
| `button2Text` | Text | ✅ | Zweiter Button – Text |
| `button2Url` | Url | — | Zweiter Button – Link |
| `note` | Text | ✅ | Hinweiszeile (z. B. „Vertrauen von über 30 Unternehmen“) |
| `arrow` | Select | — | Pfeil auf den Button zeigen |
| `arrowText` | Text | ✅ | Pfeil-Notiz (handschriftlich) |
| `colors` | Select | — | Farben |

### `timeline` — Ablauf (Timeline)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `colors` | Select | — | Farben |

### `step` — Schritt

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `icon` | Text | ✅ | Icon (Emoji) |
| `title` | Text | ✅ | Titel |
| `text` | Textarea | ✅ | Text |

### `pricing` — Preistabelle

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Textarea | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `colors` | Select | — | Farben |
| `extrasHeading` | Text | ✅ | Überschrift Zusatzleistungen |
| `extrasIntro` | Textarea | ✅ | Text Zusatzleistungen |
| `extras` | Textarea | ✅ | Zusatzleistungen |

### `plan` — Paket

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `name` | Text | ✅ | Paketname |
| `desc` | Textarea | ✅ | Text |
| `priceOld` | Text | ✅ | Alter Preis (durchgestrichen) |
| `price` | Text | ✅ | Preis |
| `priceNote` | Text | ✅ | Hinweis unter dem Preis |
| `badge` | Text | ✅ | Aktions-Kennzeichnung |
| `saving` | Text | ✅ | Preisvorteil |
| `features` | Textarea | ✅ | Leistungen |
| `buttonText` | Text | ✅ | Button-Text |
| `buttonUrl` | Url | — | Button-Link |
| `highlight` | Select | — | Hervorheben |
| `highlightLabel` | Text | ✅ | Text der Hervorhebung |

### `comparison` — Vergleich (Pro/Contra)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Textarea | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `colors` | Select | — | Farben |
| `proBadge` | Text | ✅ | Pro: Kennzeichnung |
| `proTitle` | Text | ✅ | Pro: Überschrift |
| `proText` | Textarea | ✅ | Pro: Text |
| `proItems` | List | — | Pro: Punkte |
| `title` | Text | ✅ | Titel |
| `text` | Textarea | ✅ | Text |
| `proTotalLabel` | Text | ✅ | Pro: Summenzeile (Text) |
| `proTotalValue` | Text | ✅ | Pro: Summenzeile (Betrag) |
| `proNote` | Text | ✅ | Pro: Fußnote |
| `conBadge` | Text | ✅ | Contra: Kennzeichnung |
| `conTitle` | Text | ✅ | Contra: Überschrift |
| `conText` | Textarea | ✅ | Contra: Text |
| `conItems` | List | — | Contra: Punkte |
| `title` | Text | ✅ | Titel |
| `text` | Textarea | ✅ | Text |
| `conTotalLabel` | Text | ✅ | Contra: Summenzeile (Text) |
| `conTotalValue` | Text | ✅ | Contra: Summenzeile (Betrag) |
| `conNote` | Text | ✅ | Contra: Fußnote |

### `gallery` — Galerie

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `source` | Select | — | Quelle |
| `tags` | MultiSelect | — | Tag |
| `showFilter` | Select | — | Tag-Filter anzeigen |
| `layout` | Select | — | Layout |
| `carouselFit` | Select | — | Bildgröße im Carousel |
| `autoplay` | Select | — | Automatisch bewegen |
| `columns` | Select | — | Spalten |
| `strip` | Select | — | Bildstreifen in der Großansicht |
| `images` | List | — | Bilder |
| `image` | Image | — | Bild |
| `alt` | Text | ✅ | Alternativtext |
| `caption` | Text | ✅ | Bildunterschrift |

### `slider` — Slider (Großansicht)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `fit` | Select | — | Bildzuschnitt |
| `height` | Select | — | Höhe |
| `autoplay` | Select | — | Automatisch bewegen |
| `speed` | Select | — | Geschwindigkeit |
| `hint` | Select | — | Wisch-Hinweis anzeigen |
| `images` | List | — | Bilder |
| `image` | Image | — | Bild |
| `alt` | Text | ✅ | Alternativtext |
| `caption` | Text | ✅ | Bildunterschrift |

### `cards` — Karten / Vergleich

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `columns` | Select | — | Spalten |
| `layout` | Select | — | Anordnung |
| `autoplay` | Select | — | Automatisch bewegen |

### `card` — Karte

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `icon` | Text | ✅ | Icon (Emoji) |
| `image` | Image | — | Bild |
| `title` | Text | ✅ | Titel |
| `tags` | Text | ✅ | Merkmale (Chips) |
| `text` | Textarea | ✅ | Text |
| `features` | Textarea | ✅ | Detail-Kacheln |
| `buttonText` | Text | ✅ | Button-Text |
| `buttonUrl` | Url | — | Button-Link |

### `section` — Bereich (Container)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `layout` | Select | — | Anordnung |
| `bg` | Select | — | Hintergrund |

### `posts` — Beiträge

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `display` | Select | — | Anzeige |
| `tag` | Text | ✅ | Tag-Filter |
| `columns` | Select | — | Spalten |
| `perPage` | Text | ✅ | Pro Seite |
| `limit` | Text | ✅ | Anzahl |
| `showFilter` | Select | — | Tag-Filter anzeigen |

### `references` — Referenzen / Projekte

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `display` | Select | — | Darstellung |
| `columns` | Select | — | Spalten |

### `reference` — Referenz

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `image` | Image | — | Hauptbild (optional) |
| `title` | Text | ✅ | Titel |
| `subtitle` | Textarea | ✅ | Subtext |
| `url` | Url | — | URL |
| `tags` | Text | ✅ | Tags (Stack) |
| `screenshots` | List | — | Screenshots |
| `image` | Image | — | Bild |
| `alt` | Text | ✅ | Alternativtext |
| `caption` | Text | ✅ | Bildunterschrift |

### `html` — HTML

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `html` | Textarea | ✅ | HTML |
<!-- BLOCKS:END -->

---

## 9. Rezepte für eine KI

- **Neue Unterseite mit Text anlegen:** `create_page` mit `hero` + `richtext`/`cards` → `opId` → `get_content_op` bis `applied`.
- **Bestehende Seite umtexten:** `get_page` (async) → Blöcke anpassen → `update_page_blocks` (CanRestore).
- **Bild/Design ändern:** Backup ziehen (Abschnitt 3) → `content.json`/`assets/` bearbeiten → hochladen → restore.
- **Konfiguration auf viele Sites ausrollen:** Profil per Abschnitt 4 bearbeiten (`PATCH`/Payload-Endpunkte); die Instanzen ziehen die neue Revision automatisch. Mit `POST …/instances/{publicId}/sync` einen sofortigen Neu-Pull vormerken.
- **Neue Site an ein Profil hängen:** `PUT /api/v1/instances/{publicId}/profile { profileId }`.
