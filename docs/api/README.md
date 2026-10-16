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
- `GET /api/v1/profiles/{id}/settings/recommended` → `{ recommended:[{ key, value, label }], missing:[key] }`; `POST …` ergänzt die fehlenden (bestehende Zeilen bleiben unverändert) → `{ added }`. Neue Profile starten schon mit ihnen.
- Backup-Kontingent/-Aufbewahrung gibt es nur noch im Profil; das **Standardprofil** ist der Rückfall für alle anderen Profile und für Instanzen ohne Profil (danach eingebaut: 2 GB, Aufbewahrung aus).

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
- `DELETE /api/v1/instances/{publicId}?mode=…` – **Instanz entfernen**, dieselben vier Wege und Schutzregeln wie *Instanz → Entfernen*. `mode` ist **Pflicht, ohne Standard**: `unregister` (die Cloud vergisst die Instanz, die Website läuft weiter) · `keep-data` (Container weg, Datenträger bleiben) · `full` (Container **und** Datenträger) · `backup-first` (erst Backup anfordern, nach dessen Eingang `full`; Antwort `202`, die Überwachung beendet es später). **Jeder** Weg – auch `unregister` – löscht die Cloud-Backups der Instanz mit, Einträge **und** ZIP-Dateien (bei `backup-first` bleibt das archivierte). Deshalb braucht jeder Weg **CanManageHosting + CanRestore**. Angefasst wird nur ein Container, den diese Cloud selbst angelegt hat – eine per Join-Code beigetretene Instanz lässt sich nur abmelden (`409` sonst). Nach `unregister` läuft die Website weiter und bekommt bei jedem Heartbeat `401`, bis sie auf der Website selbst von der Cloud getrennt wird. Fehlender oder unbekannter `mode` → `400`, fehlendes Recht → `403`, Ablehnung → `409`, Erfolg → `200 { removed: true }`.
- `DELETE /api/v1/instances/{publicId}/removal` – ein vorgemerktes `backup-first`-Entfernen zurücknehmen (**CanManageHosting**; `409`, wenn keins vorgemerkt ist).
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
- `GET /api/v1/hosting/images?node=<Node-ID>` → `{ images:[{ id, repo, tags, version, size, created, inUse, dangling }], prunable, prunableBytes }` – MatCMS-Images eines Hosts; ohne `node` (oder `local`) der Cloud-Host. `version` aus dem Label `matcms.version`, sonst aus GHCR über den Digest, sonst was ein Container darauf meldet; null = unbekannt. Für einen Node braucht es einen aktuellen Agent (**CanManageHosting + alle Instanzen**).
- `POST /api/v1/hosting/images/prune?node=<Node-ID>` → `{ removed, bytesReclaimed }` – nur alte, ungetaggte, unbenutzte MatCMS-Images (**CanManageHosting + alle Instanzen**).
- `GET /api/v1/hosting/proxy` → `{ provider, managesRoutes, matcadUrl, matcadTokenSet, caddyAdminUrl, caddyServer, upstream, network, upstreamHost }` – der Matcad-Schlüssel wird **nie** zurückgegeben.
- `PUT /api/v1/hosting/proxy` `{ provider?, matcadUrl?, matcadToken?, clearMatcadToken?, caddyAdminUrl?, caddyServer?, upstream?, network?, upstreamHost? }` – Teil-Update, weggelassen = unverändert (**CanManageHosting + alle Instanzen**). Schon veröffentlichte Domains wandern **nicht** mit – nach einem Provider-Wechsel neu veröffentlichen.
- `POST /api/v1/hosting/proxy/test` → `{ ok, provider, message }` – Proxy erreichbar? Netz vorhanden? (**CanManageHosting**)
- `GET /api/v1/instances/{publicId}/domain?check=true` → `{ domain, via, provider, routeId, error, publishedAt, routeExists, hostAddress, hostRouteId, hostError, hostRouteExists }` – Kundendomain (`via` = `edge` | `host`) und automatische Host-Adresse; `check` fragt die Proxys, ob die Routen noch existieren.
- `POST /api/v1/instances/{publicId}/host-address` `?pushCanonical=true` / `DELETE …` – automatische Host-Adresse (`name.<Basis-Domain>` am Proxy des Hosts) anlegen/erneuern bzw. entfernen; eine Kundendomain am Edge wird mit umgestellt (**CanManageHosting**).
- `GET|PUT /api/v1/hosting/auto-domain` `{ enabled, baseDomain }` – automatische Adressen für „Dieser Host“ (Nodes: `PUT /api/v1/nodes/{id}` mit `autoDomainEnabled`, `autoDomainBase`, `address`). `POST /api/v1/hosting/host-addresses?nodeId=` legt fehlende Adressen für bestehende Instanzen an (**CanManageHosting + alle Instanzen**).
- `GET|PUT /api/v1/hosting/wildcard?nodeId=` `{ enabled, dnsProvider, credentials? }` – ein Wildcard-Zertifikat für `*.<Basis-Domain>` des Hosts per DNS-Challenge (Caddy: DNS-Modul + dessen Felder, z. B. hetzner `auth_api_token`; Matcad: Name des Anbieters in Matcad). `credentials` weggelassen = gespeicherte behalten (**CanManageHosting + alle Instanzen**).
- **Matcad von „Dieser Host“ verwalten** (nur wenn dessen Proxy Matcad ist; Matcad eines Nodes antwortet nur auf dem Node) – dasselbe wie Hosting → Hosts → Dieser Host → Matcad (**CanManageHosting + alle Instanzen**, Fehler = `409 { ok:false, error }`):
  - `GET /api/v1/hosting/matcad` → `{ settings:{ baseDomain, acmeEmail }, providerTypes:[{ id, displayName, fields:[{ key, label, secret }] }], providers:[{ id, name, type, values, secretsSet }], routes:[{ id, name, host, wildcard, target, upstream, fallbackUrl, providerId, enabled, allowEmbedding, source, editable, certKind, managedBy, managedInstanceId }] }` – geheime Zugangsdaten werden **nie** zurückgegeben (`secretsSet` nennt nur die gesetzten Felder). `managedBy` = die Instanz (oder „Wildcard“), für die die Cloud die Route angelegt hat – solche Routen sind hier nur lesbar.
  - `PUT /api/v1/hosting/matcad/settings` `{ acmeEmail?, propagationDelay?, propagationTimeout? }` – weggelassen = unverändert, `""` = leeren; die beiden Wartezeiten (Sekunden) gelten für Wildcard-Zertifikate, netcup braucht ca. 600. Matcads Basis-Domain ist hier **nicht** setzbar: Sie folgt automatisch der Domain für Instanz-Adressen (`PUT /api/v1/hosting/auto-domain`).
  - `POST /api/v1/hosting/matcad/providers` `{ id?, name, type, credentials:{ feld: wert } }` – ohne `id` anlegen; leeres geheimes Feld = gespeicherten Wert behalten. `POST …/providers/test` (gleicher Body) → `{ ok, message }`. `DELETE …/providers/{id}` (abgewiesen, solange eine Domain ihn nutzt).
  - `POST /api/v1/hosting/matcad/routes` `{ id?, host, name?, target: "proxy"|"redirect", upstream?, fallbackUrl?, wildcard?, providerId?, enabled? = true, allowEmbedding? }` → `{ ok, id, message }` – Wildcard braucht `providerId`; Felder, die die Cloud nicht kennt (Basic-Auth, eigene ACME-E-Mail), behalten ihren Wert. `DELETE …/routes/{id}`. Routen der Instanzen → `409` (dort über `/api/v1/instances/{id}/domain` bzw. `…/host-address`).
- `GET|PUT /api/v1/hosting/edge` `{ enabled?, useHostProxy?, provider?, caddyAdminUrl?, caddyServer?, matcadUrl?, matcadToken?, clearMatcadToken?, trustedIps? }` (`trustedIps` = Absender-IPs des Edge; jeder Caddy-Host vertraut ihnen, damit die Besucher-IP ankommt) – zentraler Edge-Proxy für Kundendomains; `POST …/edge/test`; `POST …/edge/move` stellt vor dem Umschalten veröffentlichte Kundendomains auf den aktuellen Weg um (**CanManageHosting + alle Instanzen**). Mit Caddy leitet der Edge auf die Host-Adresse (HTTPS, Host-Header umgeschrieben), mit Matcad auf Node-Adresse:Port.
- `PUT /api/v1/instances/{publicId}/domain` `{ domain, pushCanonical? = true }` – veröffentlichen bzw. umziehen (Route + TLS; ohne Proxy nur vermerkt). Der DNS-Eintrag muss schon auf den Proxy zeigen. `pushCanonical` setzt auf der Site `site.canonicalUrl = https://<domain>` + „hinter HTTPS-Proxy“ (beim nächsten Heartbeat). `400` = keine gültige Domain, `409` = Domain vergeben / Instanz nicht auf diesem Docker-Host / Proxy-Fehler (**CanManageHosting**, Schlüssel-Umfang).
- `DELETE /api/v1/instances/{publicId}/domain?pushCanonical=true` – Route entfernen (erst die Route, dann der Eintrag; idempotent); die weiteren Domains gehen mit (**CanManageHosting**, Schlüssel-Umfang).
- **Weitere Domains** (`www.…`, alte Domains) neben der Hauptdomain – sie liegen dort, wo die Hauptdomain liegt, und ziehen bei Änderung, Umzug und Edge-Wechsel mit:
  - `PUT …/domain` nimmt zusätzlich `www: true` – legt `www.<domain>` als Umleitung auf die Domain mit an.
  - `GET /api/v1/instances/{publicId}/domains` → `{ main, extra:[{ domain, mode, via, provider, routeId, error, publishedAt }] }`.
  - `POST /api/v1/instances/{publicId}/domains` `{ domain, mode? }` – `mode` = `redirect` (Standard, Umleitung auf `https://<Hauptdomain>`, Pfad bleibt) oder `alias` (dieselbe Seite). Braucht eine veröffentlichte Hauptdomain; eine schon vorhandene Matcad-Route desselben Namens wird übernommen, nicht verdoppelt. `409` = vergeben / Proxy-Fehler (die Domain bleibt dann mit `error` stehen).
  - `DELETE /api/v1/instances/{publicId}/domains/{domain}` – Route zuerst, dann der Eintrag (**CanManageHosting**, Schlüssel-Umfang).

Beim **Anlegen** einer Instanz mit Domain (Hosting → Neue Instanz) wird die Route sofort angelegt und beim ersten
Heartbeat der neuen Instanz übernommen.

### Nodes & Provisionierung

Ein **Node** ist ein weiterer Docker-Host. Dort läuft ein Agent (das Cloud-Image im Modus `--node-agent`), der sich
**von dort aus** mit der Cloud verbindet und Aufträge abholt — die Cloud greift nie auf einen Host zu. „Dieser Host“
(`local`) ist der Docker-Daemon der Cloud selbst und wird wie bisher über `/api/v1/hosting/proxy` eingestellt.
Container-Aktionen, Logs, Update und Domains (Abschnitte oben) funktionieren für Instanzen auf einem Node genauso;
sie laufen als Auftrag auf dem Node, der Aufruf wartet auf das Ergebnis (`container` → `onNode: true`).

- `GET /api/v1/nodes` → `{ local:{…}, nodes:[{ id, name, revoked, online, lastSeenAt, agentVersion, hostName, dockerVersion, dockerError, instances, portFrom, portTo, proxy:{…} }] }` (**CanManageHosting**)
- `POST /api/v1/nodes` `{ name }` → `{ id, token, command, composeAgent, compose }` – Token, fertiger `docker run`-Befehl und zwei Compose-Dateien (`composeAgent`: nur der Agent, ohne Proxy auf dem Node; `compose`: Agent + Matcad + Caddy, nur `<MATCAD-KEY>` bleibt offen; Cloud-Adresse, Node-ID und Token sind eingesetzt) **nur in dieser Antwort** (**CanManageHosting + alle Instanzen**)
- `GET /api/v1/nodes/{id}` – wie oben plus `containers` (die gemeldeten MatCMS-Container)
- `PUT /api/v1/nodes/{id}/edge-guard` `{ enabled?, rotate? }` → `{ ok, message, notes[], edgeGuard }` – **„Nur über den Edge erreichbar“**: die automatischen Host-Adressen des Nodes beantworten nur noch den Edge-Proxy der Cloud (geheimer Header, den der Edge mitschickt; alle anderen bekommen `403` vom Proxy des Nodes). Kundendomains am Edge laufen weiter, die System-Adressen des Nodes sind von außen zu. `enabled` `true` schließt, `false` öffnet wieder, weggelassen = den aktuellen Stand noch einmal anwenden (nach einem halb unterbrochenen Vorgang); `rotate: true` erneuert den Schlüssel (wenige Sekunden Unterbrechung). Die Reihenfolge hält die Domains oben: beim Einschalten (und Erneuern) lernt je Instanz zuerst deren Edge-Route den Schlüssel, dann verlangt ihn deren Host-Adresse; beim Ausschalten umgekehrt. Der Header wird vor der Website entfernt (die Instanz sieht den Schlüssel nie). `GET /api/v1/nodes/{id}` liefert `edgeGuard` (angefordert) und `edgeGuardIssues` (was die letzte Anwendung nicht geschafft hat, `null` = alles wie gewünscht). Ein Node, der offline ist, lehnt jede Änderung ab. **Wichtig:** Der Schutz gilt für die Adressen am Proxy des Nodes – die veröffentlichten Container-Ports (Port-Bereich des Nodes) bleiben offen, solange keine Firewall sie schließt; Instanzen ohne Kundendomain am Edge sind danach für alle gesperrt. Voraussetzungen (sonst `409` mit Grund): Edge-Proxy an, ein Proxy auf dem Node, der es kann (Caddy oder Matcad ab 0.9 – ein älteres Matcad würde die Regel still ignorieren und wird deshalb abgelehnt), automatische Adressen auf dem Node, aktueller Agent. Der Schlüssel bleibt verschlüsselt in der Cloud und wird nie ausgegeben; `notes` nennt, was nicht geklappt hat, und prüft jede Adresse ohne Schlüssel auf `403` (**CanManageHosting + alle Instanzen**). `GET /api/v1/nodes/{id}` zeigt `edgeGuard`.
- `PUT /api/v1/nodes/{id}` `{ name?, portFrom?, portTo?, provider?, matcadUrl?, matcadToken?, clearMatcadToken?, caddyAdminUrl?, caddyServer?, upstream?, network?, upstreamHost? }` – Teil-Update; der Proxy wird **vom Node aus** angesprochen (**alle Instanzen**)
- `POST /api/v1/nodes/{id}/test-proxy` – Proxy-Test, läuft auf dem Node (**CanManageHosting**)
- `POST /api/v1/nodes/{id}/revoke` | `activate` | `token` (neuer Token + `command` + `composeAgent` + `compose`; der alte gilt sofort nicht mehr) | `update-agent` (der Agent aktualisiert sich selbst über einen Helfer-Container, mit Rollback; `GET …/nodes/{id}` zeigt danach `agentVersion`, `agentOutdated`) – (**alle Instanzen**)
- `DELETE /api/v1/nodes/{id}` – Eintrag löschen; die Websites laufen weiter, die Cloud steuert sie nur nicht mehr (**alle Instanzen**)
- `GET /api/v1/nodes/{id}/jobs?take=50` → `[{ id, kind, state: pending|running|done|failed, message, requestedBy, createdAt, startedAt, finishedAt }]`
- `POST /api/v1/hosting/instances` `{ name, nodeId? ("local"/leer = dieser Host), profileId? (leer = Standardprofil), domain?, imageTag?, pushCanonical? }` → `{ ok, containerName, port, domainFailed, message }` – **neue Website anlegen** (Hosting-Modul an; **CanManageHosting + alle Instanzen**). Die Instanz erscheint in `GET /api/v1/instances`, sobald sie sich mit dem Join-Code ihres Profils angemeldet hat (meist < 1 Min.).

### Umziehen zwischen Hosts

- `POST /api/v1/instances/{publicId}/migrate` `{ target: "local" | <Node-ID>, removeSource?: false }` → `202 { migration:{ id, state, step, from, to, log[] } }` – zieht die Website auf einen anderen Host (**CanManageHosting**, Schlüssel-Umfang; `removeSource: true` braucht zusätzlich **CanRestore**). Der Datenträger wird 1:1 kopiert: Inhalte, Uploads, Benutzer und die Cloud-Verbindung bleiben, die Instanz behält ihre ID. Die Domain zieht mit (DNS danach auf den neuen Host zeigen lassen). **Die Website ist währenddessen offline.** Ohne `removeSource` wird die alte Kopie stillgelegt (gestoppt, umbenannt `…-moved-<Datum>`, startet nie mehr von selbst). Scheitert etwas, läuft die Website wieder an der alten Stelle (`rolled-back`).
- `GET /api/v1/instances/{publicId}/migrations` → `[{ id, state: running|succeeded|failed|rolled-back, step, from, to, removeSource, bytes, requestedBy, startedAt, finishedAt, log[] }]` – zum Verfolgen (neueste zuerst).
- Entfernen (Container + Datenträger) einer Instanz auf einem Node geht genauso wie lokal: *Instanz → Entfernen* oder `DELETE /api/v1/instances/{publicId}?mode=…` (Abschnitt 6).

Antwortet ein Node nicht rechtzeitig, kommt `409` mit „läuft noch (Auftrag #…)“ — das Ergebnis steht dann im
Auftragsverlauf. Ein nicht verbundener Node antwortet sofort mit `409` „nicht verbunden“.

---

## 6a. Cloud-Konfiguration – Einstellungen, Sicherung & Umzug

Alles cloud-weit: **Schlüssel für alle Instanzen mit den Rechten „Profile“ und „Store“**; Benutzer und Server/Hosting zusätzlich „Wiederherstellen“ + „Hosting“.

- `GET /api/v1/cloud/settings` → `{ general:{…}, smtp:{…}, ai:{…}, backup:{…} }` – die Einstellungen der Cloud (Name, HTTPS-Pflicht, 2FA-Pflicht, Auto-Update, Protokoll; SMTP; KI; Backup-Vorgaben). Geheimes nur als `{ set: true|false }`.
- `PUT /api/v1/cloud/settings` `{ values:{ "smtp.host":"…", "smtp.password":"…", … }, clear?:["smtp.password"] }` – nur die Schlüssel aus `GET`; ein geheimer Wert wird nur durch einen nicht-leeren Wert gesetzt (verschlüsselt) und nur über `clear` gelöscht.
- **Mit ChatGPT anmelden** (KI über ein ChatGPT-Konto statt API-Schlüssel, Geräte-Code wie bei Codex und Hermes; **inoffizieller Weg**, OpenAI kann ihn ändern) – dieselben Rechte wie die Einstellungen:
  - `POST /api/v1/cloud/ai/chatgpt/login` → `{ ok, userCode, verificationUrl, expiresAt, intervalSeconds }` – holt bei OpenAI einen Code. **Der Mensch** öffnet `verificationUrl` und gibt den Code dort ein (15 Minuten gültig). Fehler → `502 { error }`.
  - `GET /api/v1/cloud/ai/chatgpt` → `{ connected, email, accountId, plan, accessExpiresAt, connectedAt, lastRefresh, error, loginError, pending:{ userCode, verificationUrl, expiresAt, intervalSeconds }, models:[{ slug, name }] }` – während einer Anmeldung fragt jeder Aufruf OpenAI höchstens einmal pro Intervall, ob der Code bestätigt wurde; also alle paar Sekunden abfragen, bis `connected` `true` ist. `error` = die Anmeldung ist abgelaufen oder widerrufen (neu anmelden). Nie ein Token in der Antwort.
  - `DELETE /api/v1/cloud/ai/chatgpt` – abmelden (Tokens gelöscht).
  - `POST /api/v1/cloud/ai/test` → `{ ok, text, promptTokens, completionTokens }` bzw. `409 { ok:false, error }` – schickt einen kurzen Testsatz an den **gespeicherten** KI-Anbieter (API-Schlüssel oder ChatGPT-Anmeldung); wird keiner Instanz angerechnet. MCP: `test_ai`.
  - Danach `PUT /api/v1/cloud/settings` `{ values:{ "ai.provider":"chatgpt", "ai.model":"<slug aus models>" } }`. Die Tokens liegen verschlüsselt in der Cloud, sind in keiner Einstellungs-API und in keiner Konfigurations-Sicherung enthalten (Refresh-Tokens rotieren – eine Kopie würde die Anmeldung töten). Alle freigegebenen Sites teilen sich dieses eine Konto und sein Kontingent.
- `PUT|DELETE /api/v1/profiles/{id}/backup` `{ enabled?, intervalHours?, retain?, templates?, pages?, menus?, settings?, submissions?, forms?, assets?, plugins?, pluginAssets?, toCloud? }` – die Backup-Gruppe eines Profils (setzen = ausrollen; löschen = nicht mehr ausrollen, auf den Sites wird nichts abgeschaltet). Recht „Profile“.
- **Sicherung der Konfiguration** (nicht der Websites) – ein ZIP mit `cloud-config.json`, abgelegt unter `appdata/config-backups`. Bereiche: `general`, `smtp`, `ai`, `backup`, `store`, `profiles`, `users`, `notifications`, `assignments`, `hosting`.
  - `GET /api/v1/cloud/config-backups` → `{ sections, defaultRestore, backups:[{ id, createdAt, sizeBytes, source, version, sections, hasSecrets, note }] }`
  - `POST /api/v1/cloud/config-backups` `{ sections?, passphrase?, note? }` – ohne `sections` alles. **Geheime Werte** (SMTP-Passwort, KI-Schlüssel, Proxy-Schlüssel, Profil-Geheimnisse, 2FA-Secrets) nur mit `passphrase` (≥ 8 Zeichen): hier entschlüsselt und mit dem Passwort verschlüsselt (PBKDF2-SHA256, AES-GCM) – sie sind sonst an den Schlüsselring DIESER Installation gebunden.
  - `GET …/config-backups/{id}/download` → ZIP · `POST …/config-backups/upload` (rohes ZIP, Name optional in `X-MatCMS-Backup-Name`) · `DELETE …/config-backups/{id}`
  - `POST …/config-backups/{id}/restore` `{ sections?, passphrase?, dryRun? }` → `{ ok, dryRun, report:[…] }` – Standard: alles außer `hosting` (Adressen, Proxy, Ports gehören zum Server der Sicherung). Einstellungen eines Bereichs werden überschrieben, Profile nach Name ersetzt/angelegt (Revision +1 → die Sites ziehen nach), Store nach Key/Name/Typ aktualisiert/angelegt, **Benutzer nur hinzugefügt** (bestehende Konten bleiben, auch Passwort und 2FA), Instanzen nach Public-ID bzw. Name ihrem Profil zugeordnet. Ohne Passwort bleiben geheime Werte, wie sie sind; ein Benutzer mit 2FA, dessen Secret fehlt, kommt ohne 2FA. `dryRun: true` ändert nichts.
  - **Umzug:** in der alten Cloud `POST …/config-backups` (mit Passwort) → `…/download`; in der neuen `…/upload` → `…/restore` mit `dryRun: true`, dann ohne.

## 6b. Benachrichtigungen – wer bekommt was

Die Benachrichtigungs-Matrix: Zeilen sind Empfänger, Spalten Ereignisse.
Zeilen-Schlüssel: `g:admins` (alle Admins), `g:operators` (alle Operatoren — **nur ihre Instanzen**), `u:<Benutzer-ID>`, `e:<Adresse>`.
Ereignisse: `offline`, `update`, `updateFailed`, `migration`, `nodeOffline`, `removal`. `nodeOffline` und `removal` betreffen die ganze Cloud und gehen **nie** an Operatoren.

- `GET /api/v1/notifications` → `{ events, fleetOnlyEvents, rowKinds, users:[{ key, username, displayName, email, role }], rows:[{ key, events[] }] }` (Schlüssel für **alle Instanzen**)
- `PUT /api/v1/notifications` `{ rows:[{ key, events[] }] }` – ersetzt die ganze Matrix; unbekannte Ereignisse/Schlüssel werden verworfen (**alle Instanzen + CanManageProfiles**)

Ein Profil kann Offline-/Update-Meldungen für seine Instanzen abschalten; seine „zusätzlichen Empfänger“ bekommen die Meldungen seiner Instanzen **zusätzlich**.

---

## 6b2. Dashboard-Listen

- `GET /api/v1/attention` → `{ count, items:[{ level, kind, title, text, instanceId, url }] }` – was Aufmerksamkeit braucht, Fehler zuerst (`level` = err | warn | info). Auf die Instanzen des Schlüssels begrenzt; Zeilen zu Nodes und zur Cloud selbst nur mit einem Schlüssel für **alle Instanzen**.
- `GET /api/v1/syncs?take=100` → `{ runs:[{ ranAt, instanceId, instance, revision, outcome, error, installed, updated, failed }] }` – wie die Instanzen ihre Profile übernommen haben, neueste zuerst; `outcome` = error | failed | changes | nochange.

---

## 6c. Besucherstatistik

Jede Website zählt ihren Verkehr selbst (ohne Cookies, ohne gespeicherte IP) und schickt der Cloud Tageszähler mit dem Heartbeat.
Zeitraum `days` = 7, 30 (Standard), 90 oder 365. Jeder Schlüssel, begrenzt auf seine Instanzen.

- `GET /api/v1/stats?days=30` → `{ days, total:{ views, visitors, bots, notFound, serverErrors, previousViews }, instances:[{ id, name, hasData, views, visitors, notFound, serverErrors, previousViews }] }`
- `GET /api/v1/instances/{id}/stats?days=30` → `{ id, name, stats:{ from, to, views, visitors, bots, notFound, serverErrors, previousViews, previousVisitors, days:[{ day, views, visitors }], topPages:[{ path, views }], referrers:[{ host, views }], notFoundPages:[{ path, hits }], devices:[{ device, views }] } }`

`previousViews` ist der gleich lange Zeitraum davor (null = keine Daten). Bots zählen nicht als Aufrufe. Die Cloud bekommt pro Tag die 50 häufigsten Seiten/Herkünfte/404-Pfade; die ganze Liste hat nur die Website selbst.

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
| `create_product` | neues Shop-Produkt anlegen (add-only; Preis brutto in Cent; optional Kategorie — wird angelegt, falls neu —, Schlagwörter, „hervorgehoben“, Statt-Preis; das Shop-Plugin muss auf der Site eingeschaltet sein) |
| `create_coupon` | neuen Gutschein-Code im Shop anlegen (add-only, ein vorhandener Code wird übersprungen; Prozent, Festbetrag oder Versand frei, optional Mindestwert, Ablaufdatum, Höchstzahl, einmal pro Kunde, Kategorie) |
| `list_shop_orders` | Bestellungen einer Instanz lesen (eine nach Nummer oder die neuesten, nach Status/Datum; enthält Kundendaten, daher mit Restore-Recht); Ergebnis über `get_content_op` |
| `update_shop_order` | eine Bestellung weiterbewegen: bezahlt, versendet (mit Paketdienst und Sendungsnummer), abgeschlossen, storniert, Notiz, Sendungsnummer; nicht erlaubte Schritte werden mit Grund abgelehnt |
| `set_product_variants` | Varianten eines Produkts setzen (Name, Aufpreis, Bestand, Art.-Nr., Bild; vorhandene werden an Ort und Stelle geändert, mit `replace` werden die übrigen abgeschaltet, nie gelöscht) und die Optionen für getrennte Wähler |
| `create_shop_attribute` | neue Produkt-Eigenschaft anlegen (Auswahl, Text, Zahl mit Einheit, Ja/Nein; add-only) |
| `set_product_attributes` | einem Produkt Werte für Eigenschaften geben (nur gültige Optionen werden gespeichert, Unbekanntes wird gemeldet) |
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
| `configure_wildcard` | Wildcard-Zertifikat eines Hosts per DNS-Challenge (Hosting-Recht + alle Instanzen) |
| `add_extra_domain` / `remove_extra_domain` | weitere Domain einer Instanz (`www.…`, alte Domain) als Umleitung oder Alias anlegen bzw. entfernen; `publish_domain` hat dafür auch `www=true` (Hosting-Recht) |
| `get_cloud_settings` / `set_cloud_settings` | Einstellungen der Cloud (Allgemein, SMTP, KI, Backup-Vorgaben) lesen/setzen (alle Instanzen + Profile + Store) |
| `test_ai` | kurzer Testsatz über den gespeicherten KI-Anbieter (API-Schlüssel oder ChatGPT-Anmeldung), keiner Instanz angerechnet (alle Instanzen + Profile + Store) |
| `start_chatgpt_login` / `get_chatgpt_login` / `logout_chatgpt` | KI über ein ChatGPT-Konto: Code holen (der Nutzer gibt ihn auf der OpenAI-Seite ein), Status abfragen (fragt bei laufender Anmeldung OpenAI), abmelden — danach `ai.provider = chatgpt` + `ai.model` setzen (alle Instanzen + Profile + Store) |
| `list_cloud_config_backups` / `create_cloud_config_backup` / `restore_cloud_config_backup` / `delete_cloud_config_backup` | Sicherung der Cloud-Konfiguration; `restore` standardmäßig als Vorschau (`dryRun=true`); zwischen zwei Clouds wandert die Datei über REST (download/upload) |
| `set_profile_backup` | Backup-Gruppe eines Profils setzen/entfernen (Profile-Recht) |
| `get_matcad` / `set_matcad_settings` | Matcad von „Dieser Host“: Einstellungen, DNS-Anbieter, alle Domains lesen / ACME-E-Mail + DNS-Wartezeiten setzen (die Basis-Domain folgt `set_auto_domain`) (Hosting-Recht + alle Instanzen) |
| `save_matcad_provider` / `delete_matcad_provider` | DNS-Anbieter in Matcad anlegen/ändern/testen (`test=true`) bzw. löschen (Hosting-Recht + alle Instanzen) |
| `save_matcad_route` / `delete_matcad_route` | eigene Domains in Matcad (Weiterleitung oder Umleitung, auch Wildcard) – nicht die der Instanzen (Hosting-Recht + alle Instanzen) |
| `get_edge_config` / `configure_edge` / `test_edge` / `move_domains_to_edge_setting` | Edge-Proxy für Kundendomains (Hosting-Recht + alle Instanzen) |
| `list_images` / `prune_images` | MatCMS-Images eines Hosts (`nodeId`, ohne = Cloud-Host) / alte unbenutzte entfernen (Hosting-Recht + alle Instanzen) |
| `get_container_status` | Container-Zustand einer Instanz (lokal?, Image, Start, Neustarts, Health) |
| `container_action` | `start` / `stop` / `restart` / `update` des Instanz-Containers — vorher mit dem Nutzer bestätigen |
| `get_container_logs` | letzte Zeilen stdout/stderr des Instanz-Containers |
| `set_node_edge_guard` | Node „nur über den Edge erreichbar“: seine automatischen Adressen antworten nur dem Edge (geheimer Header), alle anderen bekommen 403; `enabled` an/aus/weggelassen = erneut anwenden, `rotate` erneuert den Schlüssel (Hosting-Recht + alle Instanzen) |
| `remove_instance` | **Instanz entfernen** (`mode` Pflicht: `unregister` / `keep-data` / `full` / `backup-first`) — unwiderruflich, vorher Instanz und Modus mit dem Nutzer bestätigen; jeder Weg löscht die Cloud-Backups der Instanz mit und braucht deshalb zusätzlich das Restore-Recht |
| `cancel_instance_removal` | ein vorgemerktes `backup-first`-Entfernen zurücknehmen |
| `get_cloud_update_status` | Cloud-Version, neueste Version, ob Selbst-Update möglich ist, Verlauf des letzten Updates |
| `update_cloud` | **Cloud selbst aktualisieren** (Helfer-Container, Health-Check, Rollback) — vorher bestätigen; Hosting-Recht + alle Instanzen |
| `get_proxy_config` | Reverse-Proxy-Konfiguration (Provider, Adressen, Upstream-Modus; ohne Schlüssel) |
| `configure_proxy` | Proxy einstellen (Teil-Update; Hosting-Recht + alle Instanzen) |
| `test_proxy` | Proxy erreichbar? Netz vorhanden? |
| `get_domain_status` | Domain einer Instanz, Provider, Route noch vorhanden? |
| `publish_domain` | Instanz unter einer Domain veröffentlichen/umziehen (Route + TLS) |
| `unpublish_domain` | Domain entfernen — Site ist darunter danach nicht mehr erreichbar, vorher bestätigen |
| `list_nodes` / `get_node` | Docker-Hosts (dieser Host + Nodes) mit Status, Versionen, Websites; `get_node` mit Containern |
| `create_node` | Node anlegen → Token + `docker run`-Befehl + Compose-Datei einmalig (alle Instanzen) |
| `update_node` | Name, Portbereich, Proxy eines Nodes ändern (alle Instanzen) |
| `set_node_revoked` / `rotate_node_token` / `delete_node` | sperren/freigeben, neuer Token, löschen (alle Instanzen) |
| `test_node_proxy` | Proxy-Test auf dem Node |
| `update_node_agent` | Agent eines Nodes aktualisieren (Helfer-Container, Rollback, Node ~1 Min. getrennt) |
| `list_node_jobs` | Auftragsverlauf eines Nodes (z. B. ein noch laufendes Update verfolgen) |
| `create_instance` | **Neue Website anlegen** — auf diesem Host oder einem Node, optional mit Domain (alle Instanzen) |
| `migrate_instance` | **Website umziehen** auf einen anderen Host (Daten 1:1, Domain zieht mit; offline währenddessen) — vorher bestätigen |
| `get_migrations` | Umzüge einer Website mit Zustand, Schritt und Protokoll |
| `get_notifications` / `set_notifications` | Benachrichtigungs-Matrix lesen / komplett ersetzen (Abschnitt 6b) |
| `get_attention` / `list_sync_runs` | was Aufmerksamkeit braucht / Verlauf der Profil-Übernahmen (Abschnitt 6b2) |
| `get_stats` | Besucherstatistik: ohne `instanceId` alle Sites im Vergleich, mit `instanceId` Verlauf, Top-Seiten, Herkunft, 404, Geräte (Abschnitt 6c) |

**Profil-Tools** (dieselben Operationen wie Abschnitt 4, über denselben Dienst; Lesen mit jedem Schlüssel, Schreiben braucht **CanManageProfiles**, Instanz-Aufrufe sind auf den Schlüssel begrenzt). Jede Änderung zählt die Revision hoch, die Instanzen übernehmen sie beim nächsten Heartbeat:

| Tool | Zweck |
|---|---|
| `list_profiles` / `get_profile` | Profile auflisten / eines vollständig lesen (Geheimnisse maskiert) |
| `create_profile` / `update_profile` / `delete_profile` | anlegen / Richtlinie ändern (nur übergebene Felder) / löschen (Instanzen fallen aufs Standardprofil) |
| `duplicate_profile` / `set_default_profile` / `rotate_join_code` / `republish_profile` | kopieren / Standard setzen / neuer Join-Code / erneut ausrollen |
| `set_profile_ai` | KI-Freigabe, Monatsbudget, Anweisung, Backup vor KI-Änderung |
| `get_recommended_settings` / `add_recommended_settings` | empfohlene Einstellungen und fehlende ergänzen |
| `set_profile_setting` / `delete_profile_setting` | freie Einstellung (Gruppenschlüssel werden abgewiesen) |
| `set_profile_smtp` / `disable_profile_smtp`, `set_profile_translation` / `disable_profile_translation` | Gruppen ein-/ausschalten (gespeicherte Werte bleiben) |
| `upsert_profile_user` / `delete_profile_user` | Benutzer (auf den Sites nur hinzufügen, Passwort wird gehasht) |
| `upsert_profile_component` / `delete_profile_component` | Komponenten |
| `upsert_profile_template` / `delete_profile_template` | Templates |
| `upsert_profile_mail_template` / `delete_profile_mail_template` | Mail-Templates |
| `delete_profile_plugin` | Plugin entfernen (Hochladen bleibt REST) |
| `get_instance_profile` / `assign_instance_profile` / `resync_instance` | Zuordnung einer Instanz lesen / setzen oder entfernen / Neu-Übernahme anstoßen |

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
`accordion`, `features`, `servicegrid`, `timeline`, `pricing`, `bento`, `testimonials`, `references`) enthalten
Kind-Blöcke (`card`, `column`, `faq`, `step`, `service`, `feature`, `plan`, `bentotile`, `testimonial`, `reference` …).

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

### `bento` — Bento-Raster

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Textarea | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `colors` | Select | — | Farben |

### `bentotile` — Kachel

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `icon` | Text | ✅ | Icon (Tabler-Name) |
| `title` | Text | ✅ | Titel |
| `text` | Textarea | ✅ | Text |
| `size` | Select | — | Größe |
| `url` | Url | — | Button-Link |

### `testimonials` — Kundenstimmen

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Textarea | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `colors` | Select | — | Farben |
| `columns` | Select | — | Spaltenanzahl |
| `motion` | Select | — | Bewegung |

### `testimonial` — Kundenstimme

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `name` | Text | ✅ | Name |
| `role` | Text | ✅ | Rolle / Firma |
| `text` | Textarea | ✅ | Bewertungstext |
| `rating` | Select | — | Sterne |
| `image` | Image | — | Foto (optional – sonst Initiale) |

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
