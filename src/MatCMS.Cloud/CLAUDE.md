# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Keep it in sync with the code — update it whenever architecture, persistence, the cloud↔instance
contract or major data flow change.

## What MatCMS.Cloud is

The **control plane for MatCMS instances**. A self-hosted MatCMS connects itself to the cloud; the
cloud then

1. **watches versions** — one central GHCR poll for all instances instead of every instance checking
   for itself, and surfaces "update available" per instance,
2. **notifies** — e-mail on update-available / instance offline / errors,
3. **deploys & keeps settings in sync** — plugins, users, SMTP, components (later templates/blocks)
   pushed from the cloud to one or many instances,
4. **executes updates where it can** — see *Local vs. remote instances* below,
5. **(backlog) provisions new MatCMS instances** together with **MatOS** / **Matcad**.

**Status: working control plane, verified end to end against real containers.** Admin shell
(Übersicht, Instanzen, Profile, Einstellungen, Benutzer); enrollment in **both** directions (join
code and cloud-initiated adoption) with an approval gate; the heartbeat API; the central GHCR release
poll; local/remote classification; cloud-side container updates with rollback; the offline/update
notification watchdog; and the **profile sync engine** rolling out settings, users, plugins,
components and templates. The instance side lives in `../MatCMS` — see *Instance side* below.

**Not built yet:** nothing on the sync path — see the backlog at the end for what is left. `docker-compose.prod.yml` still declares an `external` network `main` that does not exist on
every host.

When extending it, copy the sibling repo's patterns rather than inventing new ones.

### Instance side (lives in `../MatCMS`, changed in lockstep)

- The wire contract is **not** here any more: it lives once in `../MatCMS.Shared/CloudProtocol.cs`,
  referenced by both applications. One `CloudProtocol.Version` to bump;
  `InstanceService.CurrentProtocolVersion` is only an alias for it.
- `Services/CloudService.cs` — settings (token stored DataProtection-encrypted under `cloud.*` keys),
  `SendHeartbeatAsync`, `DisconnectAsync`, plus `CloudState` (singleton, what the admin UI reads) and
  `CloudConnectionService` (60 s worker; re-reads settings each cycle so connect/disconnect is live).
- `Services/ContainerIdentity.cs` — reads the instance's own container id from `/proc/self/cgroup`
  and `/proc/self/mountinfo` (64-hex), falling back to the hostname **only when it looks like a
  12-hex short id** — otherwise a real host name like `web-01` would be reported as a container id.
  Null outside a container, which correctly makes the instance remote.
- `Services/CloudSyncService.cs` — applies the configuration; owns the three safety rules above and
  persists the applied revision in `cloud.appliedRevision` (persisted, not in-memory, so a container
  restart does not re-apply everything).
- `POST /api/cloud/link` in `Program.cs` — the adoption endpoint. Anonymous by necessity, verified
  against the local user table (**Admin role required**), rate-limited at 5/min per IP like `/login`.
- UI: a **Cloud tab** on *Admin → Einstellungen* (`Pages/Admin/Settings/Index.cshtml` +
  `OnPostCloudConnect` (join code) / `CloudManual` / `CloudSync` / `CloudTest` / `CloudDisconnect`).
  Connecting sends a heartbeat immediately so the operator gets a verdict instead of waiting a minute.

### Schema changes: migrations, not a wipe

The cloud uses **EF migrations** (`Data/Migrations/`, `db.Database.Migrate()` at startup) — it was
switched over after `EnsureCreated()` forced `docker compose down -v` four times, each of which
**dropped every instance link** and made connected instances re-enroll. A model change here means
`dotnet ef migrations add <Name>` and nothing else. Check what EF generated before committing: for the
sync-mode change it guessed a column rename that would have cross-wired AND inverted four columns.
MatCMS went the same way (see its `EnsureSchemaCurrentAsync` for the baseline of pre-migrations
databases, and `DbSeeder.PatchLegacySchemaAsync` for the frozen ALTER-TABLE patcher that makes that
baseline safe).

## Sibling repositories (same parent folder `C:\Users\Matthias\Desktop\Development`)

| Repo | Role | Port |
| --- | --- | --- |
| **`MatCMS`** | the managed product; **the stack + style template for this repo** | 9101 |
| **`MatCMS.Cloud`** | this repo — control plane | **9100** |
| **`MatOS`** | browser desktop / Docker app manager (Docker.DotNet on a mounted socket, app install engine) | 4333 |
| **`Matcad`** | Caddy reverse-proxy manager; consumes `matcad.*` container labels | 4433 |
| **`Matmon.Cloud`** | the **closest working precedent** for the cloud↔instance protocol (claim, instance tokens, heartbeat, cloud backups, accounts/orgs) — read it before designing endpoints. Note it uses PostgreSQL; **we do not** (see below). | 8055 |

## Stack — mirror MatCMS exactly

Non-negotiable: same stack, same layout, same versioning as `../MatCMS`. Do not import
Matmon.Cloud's `src/`-layout or PostgreSQL.

- **ASP.NET Core 10 Razor Pages**, C#, `net10.0`, `Nullable`/`ImplicitUsings` enabled,
  `InvariantGlobalization=true` + `PredefinedCulturesOnly=false`.
- **Flat repo root project**: `MatCMS.Cloud.csproj` at the root (no `src/`), `RootNamespace`
  `MatCMS.Cloud`, folders `Program.cs`, `Data/`, `Models/`, `Services/`, `Pages/`, `Resources/`,
  `wwwroot/`.
- **SQLite via EF Core**, one file at `appdata/matcmscloud.db`, plus a `DbSeeder` at startup. This
  is the one place the cloud deliberately diverges from MatCMS: it runs **EF migrations**
  (`db.Database.Migrate()`), because losing every instance link on each model change was untenable.
- **Cookie auth** (`matcmscloud.auth`, 7 days sliding), login only via `/login`, `Admin` policy,
  `AuthorizeFolder("/Admin", "Admin")`, DataProtection keys persisted to `appdata/keys`, per-IP
  rate limit on `/login` (10/min) — copy the `Program.cs` blocks from MatCMS.
- **NuGet floats on `10.0.*`** ("immer aktuell"): `Microsoft.EntityFrameworkCore.Sqlite`,
  `Microsoft.Extensions.Identity.Core`, `MailKit 4.*` (SMTP, implicit SSL *and* STARTTLS),
  `SQLitePCLRaw.bundle_e_sqlite3 3.0.*`. Add `Docker.DotNet` for the local-host update executor.
- **i18n**: `Services/Localizer.cs` + flat `Resources/<culture>.json` maps, used as `@T["key"]` in
  views. Copy the class verbatim; the admin UI is authored in **German** (`de.json` is the fallback
  culture).
- **UI**: `_AdminLayout.cshtml` sidebar shell, Tabler-Icons + Geologica/Inter from Google Fonts,
  hand-written vanilla JS — **no** SPA framework, no Bootstrap, no npm build step.

### The admin must look and work exactly like MatCMS's

These files live **once**, in `../MatCMS.Shared.Web` (a Razor Class Library both apps reference):
`css/site.css`, `css/admin.css`, `js/admin-list.js`, the CodeMirror and Tabler-Icons bundles and
`Pages/Shared/_IconTrash.cshtml`. They are referenced as `~/_content/MatCMS.Shared.Web/…`; the
partial resolves by name. They used to be byte-identical copies kept in step by hand.
Cloud-only rules go in `cloud.css`, and only for things the admin genuinely does not have yet.

**Use the admin's own classes. Do not invent parallel ones** (this was got wrong once and had to be
unpicked: `table.list`, `table.kv`, `.row-actions`, `.badge.is-ok` were all re-inventions of
something that already existed):

| Need | Use |
| --- | --- |
| Record table | `table.data` |
| Row buttons | `<td><div class="actions">…` with `.inline-form` around a POST form, `.btn-icon` + `<partial name="_IconTrash" />` for delete |
| Read-only facts | `.kv` > `.kv-label` + `.kv-value` (a flex row, **not** a table) |
| Field hint | `<div class="help">` (not a muted paragraph) |
| Status pill | `.badge` + `badge-on` / `badge-off` / `badge-err` / `badge-new` |
| Grouped page | `.tabs` > `.tab[data-tab]` + `.tab-panel[data-panel]`, with `?tab=<name>` deep-linking and every handler redirecting back to its own tab |
| Form layout | `.form-grid`, `.form-row`, `.form-field`, `.form-section`, `.checkbox-row` |

**Every list follows one shape**: a `[data-list]` wrapper, a **toolbar above** in the `.page-head`
(`.list-search` for search, `[data-list-filter]` for filtering, `.view-toggle` where a tile view
helps), `table.data[data-list-table]` in the middle, `.list-empty[data-list-empty]` and
`.list-pager[data-list-pager]` after it — and the **create action below** in `.list-actions`.
`admin-list.js` drives all of it markup-only; no per-page JavaScript.

**An empty list still shows its header.** The "no records" message is a ROW inside the table
(`_EmptyRow.cshtml` in `MatCMS.Shared.Web`, italic and muted), never a lone sentence where the table
would be: a list you cannot see the columns of tells you nothing about what belongs in it. The row
uses `colspan="99"` on purpose, so the partial never needs to know how wide the table is and adding
a column cannot break the empty state. `[data-list-empty]` stays separate — that one is the
*search found nothing* message, which is a different thing.

**Creating a record happens on its own page**, reached by the button in `.list-actions` — never an
inline form in the list. The create page asks only for what is needed to exist; everything else is
edited on the record afterwards.

For the tile half of that, use the two shared partials instead of hand-rolling a grid:
`_ViewToggle.cshtml` in the toolbar (the wrapper then needs `data-list-key="<page>-<payload>"` so the
choice is remembered, and `class="list-view-table"` for the default before JS runs) and
`_PayloadTiles.cshtml` right after the table, fed a `List<PayloadTile>` built from **the same
sources in the same order** as the table rows. Tiles are for scanning — clicking one opens the
editor; row actions like *Aus Profil entfernen* stay in the table view. `.payload-tile*` lives in
`cloud.css`: the admin ships `.tpl-card` (template previews) and `.instance-tile` (live homepages),
neither of which is a plain name+meta card, so this one grid is its own rather than a misused copy.

**Per-record settings belong on the record's own page**, not as a row action in the list (which is
why "make default" lives on the profile's General tab).

**Tabs, and sub-pages when a tab still gets long.** Settings and the instance detail page are tabbed.
A payload item with a real editor gets its OWN page, exactly as MatCMS splits Templates/Index from
Templates/Edit: `Profiles/Template/{profileId}/{id?}` (tabs Designer / Layout & Code / Parameter),
`Profiles/Component/{profileId}/{id?}` (field designer + preview), `Profiles/Plugin/{profileId}/{id}`.
The profile's own tabs then hold nothing but a list plus its create action.

Shared page furniture lives in partials so it cannot drift: `_CodeEditorAssets` (CodeMirror bundle,
token chips, colour pickers), `_TabsScript`, `_IconTrash`.
- **Code fields** use CodeMirror (same bundle and version as MatCMS). Never hand-roll another editor:
  add `data-code="html|css|js|json"` to a `<textarea>` and `wwwroot/js/code-editor.js` upgrades it,
  keeping the textarea as the posted value (so plain form posts still work without JS) and validating
  JSON live. Pair it with `.code-toolbar` + `.token-chip` buttons (`data-insert` / `data-target`) when
  a field has placeholders worth discovering.

### Editors: port MatCMS's, don't invent new ones

Payloads are authored **blind** here — without the site they will land on — so every editor needs to
show the result, not just the values.

- The component editor's **view is shared**: `MatCMS.Shared.Web/Pages/Shared/_ComponentEditor.cshtml`
  renders the three tabs (Allgemein / Felder / HTML-Vorlage), the field designer and the live
  preview for the CMS's `Components/Edit`, `Profiles/Component` and `Store/Component` alike. It
  stood three times almost identically; whoever touched one left the others behind. The pages differ
  only in what their fields post as (`ComponentEditorNames`, same idea as `TemplateFiles.FieldName`)
  and in four switches on the model: the type is fixed on an instance and entered here,
  CodeMirror is on only where the bundle is loaded, and the icon goes through each application's OWN
  `_IconPicker` (the CMS resolves legacy names via `MenuIcons`, the cloud must not).
  The `<form>`, the card and the action row stay in the page — the cloud's form carries route values
  and its save button lives outside it. **Everything the partial renders is inside that form**: a
  field outside it is not submitted at all and would arrive empty.
  The preview sits in the admin's own two-column build (`.pf-tree` > `.pf-side` / `.pf-main`, the
  plugin editor's): left the sample fields, right the frame that follows every keystroke.
- The editor's **script is shared too**: `MatCMS.Shared.Web/wwwroot/js/component-editor.js`, loaded
  as `~/_content/MatCMS.Shared.Web/js/component-editor.js`. It was two files
  (`MatCMS/wwwroot/js/admin-component-editor.js` and `MatCMS.Cloud/wwwroot/js/component-editor.js`)
  that had to be kept in sync by hand; there is nothing left to keep in step.
  **What differs travels as `data-` attributes on `#field-rows`**, never as a branch on which
  application is rendering: `data-field-types` (the translated field types — needed even where no
  dropdown is ever seen, because the type decides escaping), `data-labels` (the wording the script
  itself writes: the trash button's title and the debug rows) and `data-preview-theme` (the CMS
  shows its admin's palette, the cloud borrows the theme of the template its profile activates).
  `ComponentEditorJson` builds all three; a page without the shared partial — the component
  thumbnail `Admin/ComponentPreview` — writes the same attributes by hand and therefore runs the
  same renderer.
  **CodeMirror is deliberately NOT a switch**: the script asks the DOM whether an editor hangs on
  the template field and reads through it when there is one. The hook is retried on
  `DOMContentLoaded`/`load` rather than set in a `setTimeout(0)`, because `code-editor.js` builds the
  editor later than that and the hook was silently never attached.
  **A preview is for looking at: every rendered frame is sandboxed.** Both renderers set
  `sandbox=""` on their frame and write `<base target="_blank">` into the document — the frame's
  `srcdoc` inherits the EDITOR PAGE's address as its base, so every `href`, every `action` and even a
  bare `#` pointed into the admin: a click loaded the whole site into the preview, and a
  `target="_top"` carried the editor page away with everything unsaved. The sandbox stops what the
  content does by itself (scripts, `location = …`, `<meta refresh>`, forms, popups, navigating the
  editor window), the `<base>` turns a plain link click into a popup the sandbox then refuses, and a
  `load` watchdog re-renders the preview if the frame ever ends up somewhere else anyway
  (`target="_self"`). It is set in the SCRIPT, not the markup, because the thumbnail pages
  (`Admin/ComponentPreview`, `Admin/TemplatePreview`) write their frames by hand and would forget it.
  Two prices, both accepted: a `<script>` in a component template does not run in the preview, and
  Ctrl-click / middle-click still open a new tab because the BROWSER does that, not the document —
  everything that would stop it (rewriting `href`, `pointer-events: none`) costs exactly the fidelity
  the preview exists for. The same treatment is on the mail-template previews, which render
  server-side; the CMS's page/form builder previews are deliberately the other kind — you interact
  with them, and they intercept navigation in their own script instead.
  One rule carried over deliberately: an existing field keeps its `id` when its label is renamed,
  because the id is what `{{placeholder}}` refers to and re-slugging it would break blocks already
  placed on live sites.
  And the one that must survive every rebuild: **the field list is written into the posted field on
  every keystroke**, not only on submit — otherwise every other way out of the page loses it.
- The template editor's **files tab is shared**: `MatCMS.Shared.Web/Pages/Shared/_TemplateFiles.cshtml`
  renders the pseudo-files (`body.html`, `article.html`, `styles.css`, `script.js`,
  `maintenance.html`) as a **tree** — one root (the template), its files below it, the open file on
  the right with a slim menu bar, node actions on right-click / "…" / the context-menu key — for the
  CMS and the cloud alike. Same build and the same admin classes as the plugin editor
  (`.pf-tree`/`.pf-side`/`.pf-list`/`.pf-file`/`.pf-menu`/`.pf-ctx`); the badge *angepasst / leer*
  hangs on every row, because which file is overridden and which still comes from the theme is the
  one thing a prettier tree must not lose. The pages differ only in what their files post as, which
  is why `FieldName` is on the model. The partial takes its wording as strings, not `@T[…]`: a shared
  view cannot reference either application's `Localizer` type, though the resource keys
  (`tplfiles.*`) are identical in both.
  **It writes into the posted field on every keystroke.** It used to be a list of cards plus a
  CodeMirror *modal* whose "Übernehmen" was the only path from the editor into the form field —
  Abbrechen, Escape, a click on the overlay, a tab switch or a click on *Speichern* while the dialog
  was open all threw the work away without asking. The raw field of the open file stays reachable
  ("Rohform"), and without JavaScript all of them stand there labelled and editable.
- `wwwroot/js/template-preview.js` has no MatCMS counterpart (its designer shows values, not the
  result). It renders a sample page — header, hero, buttons, cards — from the form's current values,
  uses the template's own `LayoutHtml` when it contains `{{content}}`, and marks unresolved `{{token}}`s
  in red so a broken layout is visible here rather than on a customer's homepage. Menus are filled
  with **sample entries** for both forms the CMS renders (`{{menu:slot}}` and the
  `{{#menu:slot}}…{{/menu:slot}}` loop) — deterministic per slot, not random, because a preview that
  reshuffles on every keystroke is unusable.
- **Template thumbnails** in the tile views come from `Pages/Admin/TemplatePreview.cshtml`: a bare,
  layout-less page that feeds a STORED template's values to that same script. Deliberately not a
  second renderer — the tile and the editor could otherwise disagree about what a template looks
  like. The frame renders at desktop width and is scaled down, so the thumbnail shows the desktop
  layout rather than the mobile one a narrow frame would trigger.
- The component preview borrows the theme of the template the profile activates
  (`CLOUD_PREVIEW_THEME`), so a block is judged in the design it will actually live in.
- **Instance previews** embed the live site (`Instance.PreviewUrl`). Two sources feed it, in order:
  the URL the instance reports, and — for a local instance — `Instance.LocalPort`, read from the
  container's published port. The second is flagged as guessed in the UI because it only resolves
  when the operator's browser is on the Docker host. On the instance side, `CloudState.ObservedBaseUrl`
  (an in-memory value set by a middleware in MatCMS's `Program.cs`) reports where the site was last
  actually reached, so a site with no canonical URL configured still has an address here.
- Plugins are edited **in place**: the bundle is unpacked, `plugin.json` rewritten, every other entry
  copied byte for byte (`Repack` in `Pages/Admin/Profiles/Plugin.cshtml.cs`, second copy for the store
  in `Pages/Admin/Store/Plugin.cshtml.cs`). Repacking rather than
  storing code separately keeps ONE format on the wire — the instance still receives the exact ZIP
  its own importer expects. Verified end to end: edit here → roll out → MatCMS imports it with the
  new code and version.

### Code conventions (as found in MatCMS)

- Code, identifiers and comments in **English**; user-facing strings in **German** (via `@T[…]`).
- Comments explain the *why* / the trap, not the *what* — they are frequent and paragraph-shaped
  above non-obvious blocks (see `Program.cs`, `Localizer.cs`). Match that density.
- Razor Pages use the `Index/Create/Edit.cshtml(.cs)` triple per area, constructor-injected
  `AppDbContext`, `OnGetAsync` / `OnPost<Action>Async` handlers, PRG with
  `TempData["Flash"]` / `TempData["FlashError"]`.
- Settings live as key/value rows in a `SiteSettings`-style table with the keys centralised in
  `Services/SettingKeys.cs` as `const string`.

## Build / run / verify

Host port is **9100** → container **8080** (the ASP.NET base-image default; never change the
internal port).

```bash
docker compose up -d --build     # → http://localhost:9100   (admin login /login, admin/admin)
./run-docker.ps1                 # same, with the friendly output
docker compose logs -f
docker compose down              # stop, keep the volume
docker compose down -v           # RESET (drops DB, keys, uploads) — NOT needed for a model change any more
```

Local hot-reload loop (.NET SDK 10 required):

```bash
./run-dev.ps1                    # sets ASPNETCORE_HTTP_PORTS=9100 + dotnet restore + dotnet watch run
```

`docker-compose.yml` sets `build.pull: true` (always repull `sdk:10.0` / `aspnet:10.0`), image
`matcms-cloud:latest`, container `matcms-cloud`, volume `matcms-cloud-data:/app/appdata`,
`restart: unless-stopped`. `docker-compose.prod.yml` pulls
`ghcr.io/real-ttx/matcms-cloud:latest` with `pull_policy: always` on the external `main` network.

There is **no test project** in MatCMS; don't invent one unless asked.

## CI/CD & versioning — identical scheme

`VERSION` at the repo root holds `MAJOR.MINOR` (start at `1.0`). Two workflows push to GHCR
`ghcr.io/real-ttx/matcms-cloud` (owner lowercased), logging in with `github.actor` +
`secrets.GITHUB_TOKEN` and `permissions: packages: write`:

| Branch | Workflow | Tag | `:latest` |
| --- | --- | --- | :-: |
| `main` | `.github/workflows/release.yml` | `MAJOR.MINOR.<run_number>-<utc yyyyMMddHHmmss>` | yes |
| `dev` | `.github/workflows/dev.yml` | `nightly-<run_number>-<utc yyyyMMddHHmmss>` | no |
| local | Dockerfile default | `local` / `local-<datetime>` via `--build-arg APP_VERSION=` | – |

The computed version is passed as build-arg `APP_VERSION` and baked in with
`/p:InformationalVersion=` (free-form string, so non-numeric tags never break the build). A
`Services/VersionService` reads it back via `AssemblyInformationalVersionAttribute`.

## Architecture (target)

### Cloud ↔ instance contract

The instance always connects **outbound** (works behind NAT/firewall); the cloud never needs to
reach in. Per instance the cloud stores an id + a bearer **instance token** (encrypted at rest via
DataProtection), sent as an `X-MatCMS-Instance-Token` header.

- **Enrollment — two directions, both implemented.**
  1. **Join code (instance → cloud).** Every *profile* carries a rotatable, human-typeable
     `JoinCode` (`ProfileService.NewJoinCode`, alphabet without 0/O/1/I because these get read off
     one screen and typed into another machine). The instance posts it to
     `POST /api/instances/register` and receives `{instanceId, token}`. The code hanging off the
     **profile** rather than the cloud is what makes rollout work: an instance lands in the right
     profile with no assignment step. An unknown code is refused outright — knowing the cloud URL
     alone creates nothing. Compared with `FixedTimeEquals` over the tiny profile set.
  2. **Adoption (cloud → instance).** `AdoptionService` takes an existing instance's URL plus one of
     **its own** admin accounts, mints credentials and pushes them to the instance's
     `POST /api/cloud/link`, which verifies the account against its own user table before accepting.
     The credentials are used once and never stored; a failed handover deletes the record again so
     an instance that never accepted does not linger looking merely offline. This is the only
     inbound call in the whole design — everything after it is outbound.
- **Approval gate.** `InstanceStatus` is `Pending` / `Approved` / `Rejected`; `Profile.AutoApprove`
  decides which one enrollment lands in. Pending instances are recorded (so the operator can see what
  is asking to join) but get `ConfigRevision = 0` and a 403 from `/config`. Rejected ones get a 403
  on the heartbeat so they stop asking instead of timing out.
- Tokens are stored as SHA-256 only, verified with `FixedTimeEquals`, and shown exactly once.
- **Heartbeat** (~60 s) — *implemented*: `POST /api/instances/{publicId}/heartbeat`, contract in
  `../MatCMS.Shared/CloudProtocol.cs`. Carries app version, protocol version, host name, container id,
  image ref and content counts; the response carries the latest release, `UpdateAvailable`,
  `CloudCanUpdate`, a `PendingRestore` and a `PendingBackup`. The last two are mirrors of each
  other: the cloud only ever ASKS, and the instance fetches or produces the file itself. An instance
  counts as **offline**
  after `InstanceService.OfflineAfter` (150 s ≈ 2.5 missed beats). Bump
  `InstanceService.CurrentProtocolVersion` whenever the contract changes — older instances are
  badged *veraltet*. `POST /api/instances/{publicId}/disconnect` marks an instance offline at once
  and suppresses the outage mail.
- **Pull-based sync**: the heartbeat response tells the instance what is pending; the instance then
  fetches and applies it. Nothing is pushed into an instance over an inbound connection.

**Treat `MatCMS` and `MatCMS.Cloud` as one change set.** The link (settings UI, connection worker,
container identity) is in place on both sides; the **sync applier** on the instance still has to be
written when the sync engine lands here.

### A settings group has to be switched on

`Profile.SyncSmtp` gates the whole SMTP block: off, the fields are hidden and the keys are left out
of the rolled-out settings — including the global ones — so an instance keeps its own mail
configuration. **The stored values survive an untick**; only the rollout stops, and the save handler
skips writing them precisely because hidden inputs still post (empty), which would otherwise wipe
what the operator only meant to stop sending.

That is the pattern for any settings GROUP added later: a checkbox that reveals its fields, nothing
rolled out until it is ticked. Free key/value settings need none — each row is already an explicit,
individually deletable decision.

There are now four groups, each with its own page and its own switch: **SMTP** (`smtp.*` +
`MailSource`), **translation** (`translate.*`), **mail templates**, and **backup** (`backup.*`,
`Profile.SyncBackup` → `Profiles/Backup.cshtml`). The backup group rolls out the schedule as the ONE
key the instance's own `BackupManager` reads (`backup.schedule`, its JSON format verbatim — two
models of one format would drift, and the site's backup page and the profile have to mean the same
thing), plus `backup.toCloud`. The granular lists inside that format (which pages, which forms) are
written **empty** on purpose: they name items that exist on one site and nowhere else, so
distributing them would leave every other instance backing up nothing, silently.

**The backup QUOTA is not part of that group and is not rolled out.** `Profile.BackupQuotaGb` (a
column, empty = the cloud-wide default in *Einstellungen → Allgemein*) is what the CLOUD grants each
instance in the profile, so one customer can be given more room than another. An instance neither
needs nor should be told the number: it decides which of its uploads get pushed out again, and that
belongs to the side holding the disk. It lives with the profile's policy fields rather than on the
backup group's page for a practical reason too — opening that page switches the rollout ON, so an
operator who only wanted to grant more space would have started backing up their sites.
`BackupStore.QuotaBytesAsync(instanceId)` resolves profile → default; an instance with no profile
falls back to the default, which is a real state (pending, or profile deleted) and must never mean
"no quota". The quota is a **fractional GB `double`** (0.1 = 100 MB) — `BackupStore.ParseGb` accepts a
comma or a dot so a German-entered "0,1" and "0.1" mean the same, and it is stored invariant.

**Retention is separate from — and layered on top of — the quota.** `BackupStore.EnforceRetentionAsync`
(run after every upload and by `InstanceMonitorService`'s ~hourly sweep) first applies a classic
**GFS** over the site's own scheduled (`origin == "auto"`) backups — keep the newest per day for
`KeepDaily` days, per ISO week for `KeepWeekly` weeks, per month for `KeepMonthly` months, capped at
`MaxCount` — then the disk quota drops the oldest surviving AUTO backups until it fits. Two invariants:
**manual/API uploads are never auto-deleted** (only `auto` backups are ever pruned), and the very
newest auto backup and the last backup overall are always kept. The numbers live per profile
(`Profile.BackupKeep*`/`BackupMaxCount`, null = fall back) with cloud-wide defaults in `SettingKeys.BackupKeep*`;
**all zero = retention off, quota only** — which is exactly the old behaviour, so nothing prunes by
surprise until an operator sets a tier.

`ProfileService.IsGroupKey` is what keeps the two kinds apart. A free key/value row carrying a group
key would be skipped by the rollout unless that group happened to be on — it would sit in the list
looking active and never arrive — so `Profiles/Setting` refuses one outright.

**Free settings are picked, not typed.** `Services/InstanceSettingCatalog.cs` lists the keys an
instance understands, grouped as the CMS's own settings page groups them, and the editor offers them
as a dropdown (plus "own key" for anything not listed, and minus what the profile already has, which
would only collide with the unique index). It is a **suggestion list, not a contract**: the strings
are a copy of the CMS's `SettingKeys` and a stale entry costs a wrong suggestion, nothing more.
Typing was the wrong shape of question — a typo produced a row that looked perfect here and did
nothing on the site, because neither side rejects an unknown key.

### Global vs. profile-local — the rule

**A profile consists of global information and may additionally have its own.** There are two
different kinds of "global", and conflating them was a mistake that had to be corrected:

| Global thing | Where it lives | Assignable to a profile | In the instance-facing catalogue |
| --- | --- | --- | --- |
| **Templates, plugins, components** | the **Store** (`Store*` tables, *Admin → Store*) | yes | **yes** — this is what "Weiter durchsuchen…" browses |
| **Users** | the cloud's **existing** `Users` table (*Admin → Benutzer*) | yes | **never** |
| **SMTP / settings** | the cloud's **existing** `CloudSettings` (*Admin → Einstellungen → SMTP*) | yes | **never** |

The store is a **catalogue**: things you shop for and install. Users and SMTP are shared
configuration — you assign them, you do not browse them. That is why they must never appear in the
catalogue API an instance can query, and why they need no store tables of their own.

On the instance, "Weiter durchsuchen…" opens that catalogue as a **store dialog**
(`MatCMS/Pages/Shared/_StoreDialog.cshtml`): a card grid with its own search and an install button
per entry, not a card appended under the list — browsing what you *could* install is a different
activity from managing what you already have, and hanging it below made it read as inventory. One
partial serves plugins, templates and components; they differ only in the route parameter their
`InstallFromCloud` handler expects. Closing it strips `?browse=true` from the URL so a reload does
not reopen it.

There is **no separate "Global" tab** on a profile. The operator thinks in "templates this profile
rolls out", not in where a row is stored, so each payload tab shows **one list** containing both the
profile's own items and the ones it takes from the store, with a *Global* / *eigenes* badge in the
Herkunft column. Below the list, next to *Erstellen* / *Importieren*, sits **Aus Global hinzufügen**,
which opens `_StorePicker.cshtml` (the admin's own `.modal-overlay`) listing only what is not in the
profile yet. Adding is **additive** (`OnPostAddFromStore`), removing is the row action
(`OnPostRemoveFromStore`) — never a full-replace form, which would silently drop selections when a
form is posted without the operator ever having opened that section.

### Profiles and the sync engine

A **profile** is a configuration bundle plus the policy for every instance assigned to it
(`Models/Profile.cs`, `Services/ProfileService.cs`). Payloads live in their own tables —
`ProfileSetting`, `ProfileUser`, `ProfilePlugin`, `ProfileComponent` — each with the identity that
the *instance* uses: setting key, username, plugin `Key`, component `Type`.

**`Profile.Revision` is the entire sync mechanism.** It is bumped on every change
(`ProfileService.TouchAsync`), rides on the heartbeat response as `ConfigRevision`, and an instance
whose `AppliedRevision` differs pulls `/api/instances/{id}/config` and applies it. Every handler
that changes a payload MUST call `TouchAsync` — **a change that does not bump the revision is a
change that silently never arrives.** The instance reports the applied revision (and any error) back
on its next beat, which is what drives the *synchron / abweichend / Fehler* badge.

Plugin bundles are deliberately **not** inlined in the config JSON: `ConfigPlugin` carries key + name
+ version only, and the instance fetches `/api/instances/{id}/plugin/{key}` when its installed
version differs. Otherwise a profile with a dozen plugins turns every revision bump into a
multi-megabyte download.

Payload formats reuse what MatCMS already ships — do not invent new ones:

| Payload | Format / identity | Conflict strategy |
| --- | --- | --- |
| **Settings** | key/value against `SettingKeys` (SMTP block + free-form keys) | `keep` / `add` / `once` |
| **Users** | username + **password hash** (hashed once in the cloud, plaintext never stored) | **add-only, always** — only `add` / `once` |
| **Plugins** | the exact `PluginPackager.Export` ZIP; `Plugin.Key` is the identity, a same-key import updates in place and runs the plugin's `Migrate` | `keep` / `add` / `once` |
| **Components** | `Type` is the identity | `keep` / `add` / `once` |
| **Templates** | `Name` is the identity (same as MatCMS's backup/restore); the JSON that MatCMS's template editor exports (`?handler=ExportJson`) imports straight into a profile | `keep` / `add` / `once` |

Two things templates do **not** do, on purpose. `Profile.ActivateTemplateName` names the one template
that becomes the live design — empty means "roll them out, the site decides", because switching a
running customer site's design must be a decision, not a side effect of a config sync. And
`ParamValuesJson` (what a site's own admin tuned on the published template parameters) is only taken
over when the template is **new** on that instance; overwriting it would throw away per-site
customisation on every revision bump.

Three rules hold on the instance side (`MatCMS/Services/CloudSyncService.cs`) and are the difference
between a sync you can trust and one that eats a customer's site:

1. **Users are add-only.** Never updated, never deleted — an operator must not be able to lock
   themselves out of their own site through a cloud setting.
2. **Nothing is deleted** because the profile no longer lists it. Removing a plugin from a profile
   stops future rollouts; it does not rip it out of running sites.
3. **A null section is untouched.** "Profile doesn't sync this" and "profile syncs an empty list"
   must never look the same — hence nullable lists in `InstanceConfig`.

Three guards worth keeping: the instance refuses pushed settings whose key is in `SettingKeys.Cloud`
(otherwise one profile could rewrite an instance's cloud link); a **restore** preserves those same
keys rather than taking them from the backup (`ContentTransferService.ImportAsync` — a backup carries
the connection as it was when taken, so restoring one used to rewind the token and drop the site off
the cloud seconds after a restore the cloud itself had triggered, and a foreign ZIP restored by hand
would have handed this container another instance's identity); and imported plugins stay **disabled**
because plugin code runs server-side.

### Sync modes and the report back

`Profile` carries one `SyncMode` per payload (`SettingsMode`, `UsersMode`, `PluginsMode`,
`ComponentsMode`, `TemplatesMode`) instead of the old `Overwrite*` booleans:

| Mode | Wire value | Meaning | What the instance does |
| --- | --- | --- | --- |
| **Synchron halten** | `keep` | keep in line with the profile | applies every revision, overwriting |
| **Nur ergänzen** | `add` | create what is missing | applies every revision, add-only |
| **Einmalig übernehmen** | `once` | seed once, never touch again | first apply is a FULL rollout, every later one does nothing |

Modes travel as **strings**, not as the enum's numbers: an instance that predates a mode falls back to
`add` (the cautious end) instead of misreading a number and overwriting a live site. `SyncMode.Keep`
is deliberately `0` so profiles migrated from `Overwrite* = true` behave exactly as before. Users
never offer `keep` — add-only is unconditional, so the UI must not promise something the instance
refuses to do.

*Einmalig* is the only mode that needs memory, and it lives **on the instance**: whether something was
already seeded here is a fact about this site, not about the profile. `cloud.seeded` holds
`<profileId>|settings,users,…`. The profile id is part of the value on purpose — moving a site to
another profile must let that profile seed once as well. The mark is only written **after** the whole
apply succeeded, so a payload that threw is allowed to seed again on the next attempt instead of
freezing forever. `CloudSyncService.ResetAsync` clears it along with the applied revision.

The first *einmalig* apply overwrites: seeding a site with half a configuration because something
happened to exist there already would be useless.

**The report is what makes this usable.** Every beat carries `SyncReport` — a `SyncItemReport` per
item, produced by the instance while applying:

```
kind: setting|user|component|template|plugin
outcome: installed | updated | skipped-exists | skipped-once | failed
```

The cloud stores it verbatim in `Instance.LastSyncReportJson` and renders it on the instance detail
page (Konfiguration tab); the instance shows the same table on its own Einstellungen → Cloud tab. The
cloud **computes nothing**: only the instance knows whether a component already existed or a plugin
import failed. That is also why this scales — adding a payload type later needs no cloud-side logic,
only another line in the report. A skipped *einmalig* payload reports every item it would have
contained as `skipped-once`, so the operator can tell it apart from a broken sync. Failures are
reported **before** the exception is thrown, which is what names the plugin that broke a rollout.

### Applying part of a preview

The preview table has a checkbox per item that would actually change (skipped items get none —
ticking one would promise an action that does nothing). *Ausgewählte übernehmen* posts those
`kind:id` keys, `CloudService.ApplySelectionAsync` narrows the fetched `InstanceConfig` down to them
and hands it to the SAME applier, so every item is still decided by the one piece of code that
decides it everywhere else. A payload nobody picked from becomes null — "don't touch this".

Two rules make this coherent rather than confusing:

- **The applied revision does not move.** Only a subset arrived, so the instance stays *abweichend*
  and the next heartbeat brings the remainder. Claiming the revision would strand the rest forever.
- **Nothing is marked as seeded.** A `once` payload applied in part must not be frozen, or the items
  left out would never arrive.

The run still writes its report and run stamp, so a partial apply appears in the history like any
other — logged as "Selected items of revision N applied".

### Apply history

`InstanceSyncRun` keeps one row per completed apply (time as the INSTANCE reported it, revision,
error, the full report JSON, plus denormalised counts so a listing does not parse 50 blobs). It is
appended **only when `HeartbeatRequest.SyncRunAt` differs from `Instance.LastSyncRunAt`** — the same
report rides on every beat until the next apply, so appending on "the report changed" would both
duplicate runs and miss a re-apply whose outcome happened to be identical. Pruned to
`InstanceSyncRun.KeepPerInstance` (50) on write, because that is the only moment the table grows.
An instance older than protocol 5 sends no timestamp and contributes no history rather than a wrong
one.

### Preview before applying

The instance offers **Vorschau** next to *Konfiguration jetzt anwenden* (Einstellungen → Cloud). It
fetches the config and runs `CloudSyncService.PreviewAsync`, which is **the same code as the real
apply** with every write suppressed (`_dryRun`) — a second "what would change" implementation would
drift from the one that actually changes things, and a preview that lies is worse than none. Plugin
bundles are not downloaded for it: the decision only needs the two version strings. Two details that
only matter in a dry run: the change tracker is cleared afterwards so nothing modified leaks into the
next save on that scoped `DbContext`, and a template named for activation counts as present when
**this same run** would install it, otherwise the preview would report a spurious failure.

`ConfigRevision` alone no longer means "in sync", so the badge reads the report too:
`InstanceService.Summarise` counts the outcomes, and a `failed` item shows as *Fehler* even when the
revision matches and nothing threw — a template named for activation that never arrived fails on its
own without aborting the apply. Skipped items are not an error (that is what `add`/`once` are for)
but are shown next to *synchron*, so it never reads as "everything from the profile is here".

### Behind a reverse proxy

`UseForwardedHeaders` is enabled in **both** apps, and it has to be configured to do anything in
Docker: ASP.NET only trusts loopback by default, and a proxy in another container is not loopback.
Name it with `MatCms:Proxy:Known` / `MatCmsCloud:Proxy:Known` (a comma-separated list of addresses),
or set `…:Proxy:TrustAll` for a container reachable **only** through its proxy. Trust-all anywhere
else lets a client set the headers itself and claim any address it likes.

Without it three things go quietly wrong, and the third is the one that gets reported as a bug: the
login rate limit counts every visitor as one client; `Request.Scheme` is `http`, so the base URL an
instance reports about itself (`CloudState.ObservedBaseUrl`) comes out as `http://…`; and the cloud
then stores, links to and **frames** that address — which an https admin refuses as mixed content and
leaves as a blank rectangle with no explanation anywhere.

`Services/MixedContent.cs` is the second half of that: every place embedding a live instance asks it
first and says so in words instead of rendering a frame the browser will refuse. It stays useful even
with the headers configured, because a site may genuinely be http-only.

**Logging into an instance INSIDE the cloud's iframe** needs one more thing on the instance side.
Framing already works (MatCMS suppresses `X-Frame-Options` and sets `frame-ancestors 'self' <cloud>`),
but the login sits in a CROSS-ORIGIN frame where a `SameSite=Lax` cookie is never set — the form
submits into the void and the frame stays blank (the console's "Blocked autofocusing … cross-origin
subframe" is only a red herring). The instance opts in with **`MatCms:EmbedAuth=true`**, which switches
its auth AND antiforgery cookies to `SameSite=None; Secure`. It REQUIRES the instance to be served over
HTTPS (a `None` cookie without `Secure` is rejected), so it is off by default — turning it on for a
plain-http site breaks login instead of fixing it. Note the modern-browser caveat: with third-party
cookies fully disabled, even `SameSite=None` can be blocked; CHIPS/`Partitioned` is the follow-up if
that bites.

### Update checks & notifications

- `GhcrClient` lists all tags of a public GHCR package. It follows the `Link: …; rel="next"`
  pagination header — GHCR returns tags in **creation order**, so reading only the first page yields
  a stale "latest" and the check silently reports "up to date". `ReleaseVersion` parses/compares the
  `MAJOR.MINOR.BUILD-<utc>` scheme; non-numeric tags (nightly/local) never count as an update.
- `ReleaseWatcher` is a **singleton cache** refreshed by `ReleaseWatcherService` every 30 min. One
  poll serves every instance, every page render and the notifier — instances stop checking for
  themselves. `VersionService` is the separate check for the cloud's *own* image.
- `InstanceMonitorService` (60 s) is the watchdog: offline dead-man switch, update notice, and the
  opt-in auto-update. Both mails are idempotent — `Instance.OfflineNotified` fires once per outage,
  `Instance.UpdateNotifiedVersion` once per release. Mail goes out through `EmailService` (MailKit,
  same as MatCMS: implicit SSL on 465, STARTTLS otherwise).

### Local vs. remote instances

An instance is **local** when it runs on the same Docker daemon the cloud can reach; otherwise it is
**remote**. This is detected, never asked (`InstanceService.ClassifyAsync`, re-run on **every**
heartbeat so a moved site degrades to remote instead of leaving the cloud pointing at a container
that is now something else):

1. The heartbeat reports the instance's own container id (`/proc/self/cgroup` / hostname) and image
   reference.
2. `DockerHostService.FindContainerAsync` enumerates containers over the mounted engine socket
   (**Docker.DotNet**; `MatCmsCloud__Docker__Endpoint`, `unix:///var/run/docker.sock`, or
   `npipe://./pipe/docker_engine` on Windows) and matches the id **by prefix** — an instance often
   only knows the short 12-character form.
3. Match → **local**; no match, no socket, or an unusable endpoint → **remote**. Docker access is
   entirely optional and every method degrades to notify-only.

What each mode can do:

- **Local** — `DockerHostService.UpdateContainerAsync`: pull the configured image, and if the digest
  actually changed, stop the container, park it under `<name>-matcmscloud-old`, recreate it from its
  own inspected config (env, volumes, ports, labels, network aliases) and start it. Any failure
  **rolls back**: remove the half-built container, rename the old one back, start it. Compose labels
  travel with the config, so `docker compose` still recognises the container afterwards.
  Two guards, both load-bearing: the socket mount is **opt-in** at the compose level, and
  `LooksLikeMatCms` refuses any container whose image (or compose project) doesn't name MatCMS.
  **The socket needs no `group_add` / `DOCKER_GID`.** The image runs the app as non-root `app`,
  which cannot read the root-owned `660` socket — the old symptom was `SocketException(13)
  "Permission denied"` and every instance silently staying *remote*. `docker-entrypoint.sh` now
  starts PID 1 as root, reads the mounted socket's own gid and `exec gosu app:<gid>` — the app
  drops to `app` with the socket's group as its primary group, so it is unprivileged **and** can
  read the socket. Mounting the socket is therefore all that is needed (as with Portainer & co.),
  and `docker-compose*.yml` carry no group/user override. No socket → plain `gosu app`, unchanged.
- **Remote** — notify only, plus the exact command (`docker compose pull && docker compose up -d`).
  A guided/agent-driven remote update is backlog.

### Removing an instance — who owns the container

`Instances/Delete` is the only way out. It offers **three destructive ways** (remove the container and
keep the volumes / remove both / take a backup first and then remove both) and, for everything else,
plain **unregistering**: the cloud forgets the instance and the site keeps running.

The removing itself is **not** in the page: it is `Services/InstanceRemovalService.cs`, because the
third way is finished minutes later by `InstanceMonitorService` and two implementations of "delete a
customer's site" would be two chances to get the guards subtly different.

**`Hosting == Local` does NOT mean "ours".** Local only says the container sits on the daemon we can
reach, which is equally true of a site somebody started by hand next to us. The answer is
`DockerHostService.ManagedLabel` (`matcmscloud.managed`), stamped by `HostingService` on everything
the cloud creates itself and re-read from the daemon on **every heartbeat** into
`Instance.CloudManaged` — so a site that moved falls back to "not ours" instead of keeping a licence
to delete something it no longer is. An instance that only joined with a code may be unregistered;
its container is never touched, and the two destructive options are not rendered for it at all.

Two traps, both load-bearing:

- **Nothing derives the target from a name.** The volume name is derivable on paper
  (`HostingService.StackName(...) + "-data"`), but the display name is editable on the instance page,
  so a re-derived name can point at something else entirely. `InspectTeardownAsync` reads the mounts
  off the container that is actually about to be removed. The confirmation form carries the container
  id only so the POST can check it still describes what the operator was shown — the id it acts on
  comes from the record and the daemon again. A confirmation screen that hands its own answer back as
  the instruction is how the wrong container gets removed.
- **`ContainerRemoveParameters.RemoveVolumes` does not remove NAMED volumes.** It is
  `docker rm --volumes`, which only clears anonymous ones, and an instance's data volume is named.
  Relying on it reports "completely removed" while the customer's database is still on disk. Named
  volumes are removed one by one, explicitly, *after* the container (a volume still in use cannot be
  removed), and whatever survives is reported by name rather than swallowed.

Cloud-side backups hang off the instance row and **cascade with it on every way**, including the one
that keeps the volumes — so the page says how many will go. The files themselves stay behind as
orphans for `BackupStore.FindOrphansAsync`.

### The third way: back up first, then remove

Confirmed now, carried out later. `ScheduleWithBackupAsync` only asks the instance for a backup and
records `Instance.PendingRemovalMode`; `InstanceMonitorService` finishes the job on a later tick.

**The gate is the FILE, and nothing else.** `InstanceService.ArrivedBackupAsync` asks for a
`CloudBackup` row carrying the id of *this* request whose bytes are on our disk. Three near-misses
that all look like an answer and are not:

- *"We asked."* Obviously not enough, and the reason this way did not exist before.
- *"The instance reported success."* The upload is a separate request that can still fail afterwards.
  `BackupReport` is recorded for the operator to read and is never what the removal decides on.
- *"A backup arrived after we asked."* A site that was offline for a week uploads last week's file the
  moment it returns. Hence `PendingBackup.RequestId`, echoed on the upload in
  `CloudProtocol.BackupRequestHeader` and stored as `CloudBackup.RequestId`. A counter, not a
  timestamp: it survives JSON, an HTTP header and two SQLite round trips as itself.

**There is no deadline, and there must never be one.** Nothing turns the wait into a removal after
some period — that would be the exact accident the way exists to prevent. It ends when the backup
arrives or when an operator takes it back (`CancelPendingAsync`, always available). What the clock
does instead is *tell somebody*: after `InstanceRemovalService.WaitNoticeAfter` (6 h) the operator
gets one mail saying the removal has not happened and why. A reported failure ends the request
outright rather than being re-asked every beat — a backup takes minutes and holds up the site's
heartbeat while it runs.

**The backup survives the removal, or the removal does not happen.** This is the trap that would have
made the whole way pointless: the backup it just took hangs off the instance row and would cascade
away with it. So the file is lifted into `ArchivedBackup` — a table with **no foreign key**, which
therefore cannot be cascaded — and the bytes move to `appdata/backups-archive/<id>/`, a *sibling* of
the live folder so neither the quota pruner nor the orphan finder can reach them. Nothing prunes the
archive. If archiving fails, nothing is removed. Found afterwards under **Backups → Archiv**, with the
former instance's name, its public id and the reason. Only that one backup is kept; the instance's
others go the way they go on every other route, and the page says so.

**The request is silenced by the file too.** `HeartbeatAsync` asks `ArrivedBackupAsync` before it
offers `PendingBackup` again. Without that the request stands in every response and the instance
builds a fresh backup every minute — fifteen of them in fifteen minutes, which is how it was found.
Deliberately the same question that releases the removal, so there are never two opinions about
whether a backup exists; it also makes it self-healing, because a file that disappears is asked for
again.

**The way is hidden for an instance older than protocol 11** (`InstanceService.IsOutdatedProtocol`),
and refused server-side as well. Such an instance does not know the field and ignores it, so the wait
would stand for ever on a site that is running perfectly well — the one state where "nothing happens,
visibly" is still the wrong answer, because nothing CAN happen. This matters during a staggered
rollout, when the cloud is necessarily updated before its instances.

A backup can also be requested on its own, from the instance's Backup tab, with no removal attached.
That is the ordinary use — the removal way is a *user* of it, not the reason for it.

**An operator can also UPLOAD a backup ZIP** from their own machine on that same tab
(`Details.OnPostUploadBackupAsync` → `BackupStore.StoreAsync` with origin `upload`), optionally
marking it for restore in the same step. It is stored and restored by the exact same path as an
instance-pushed one — there is deliberately no second format and no second restore route — so the
instance downloads and applies it on its next beat, and `ContentTransferService.ImportAsync`
preserving the `cloud.*` keys is what makes uploading even a foreign site's backup safe (it cannot
hand this container another identity). The detail page carries `[RequestSizeLimit]` /
`[RequestFormLimits]` at `BackupStore.MaxUploadBytes` because a backup with media dwarfs the
framework's 128 MB multipart default and Kestrel's 30 MB body cap; the real ceiling is still the
streaming guard in `StoreAsync`.

### Operator API (`/api/v1`) & API keys

A **key-authenticated** surface for driving the backup cycle from outside — pull a site's backup,
upload an edited one, restore it live — without a cookie session. It is the operator counterpart to
the instance API: same "anonymous at the transport level, authenticated by a secret" shape, its own
rate-limit policy (`operatorApi`), and it goes through the **same `BackupStore` / `InstanceService`**
the admin UI uses. There is no second restore path and no second backup format.

- **`ApiKey`** (`Models/ApiKey.cs`, `Services/ApiKeyService.cs`): stored as **SHA-256 only**, shown
  once, with a clear `Prefix` for the list — exactly like an instance token (`ApiKeyService.Hash`
  mirrors `InstanceService.HashToken`). Two rights that were deliberate product decisions:
  **`CanRestore`** gates the one destructive call (pull/upload is the base right; overwriting a live
  site must be granted on purpose), and **`AllInstances` + `ApiKeyInstance` scope** limits a key to
  named instances. A scoped key with no rows reaches nothing, on purpose — a mis-created key is inert,
  not accidentally global. Managed under **Admin → API-Schlüssel** (`Pages/Admin/ApiKeys/`); revoking
  keeps the row (a key that could restore a site is worth an audit trail), deleting cascades its scope.
- **Auth**: `Authorization: Bearer <key>`. `ApiCallerAsync` resolves the key or returns 401;
  `ApiInstanceAsync` resolves the target instance and returns the SAME 404 for "does not exist" and
  "outside this key's scope", so a scoped key cannot enumerate instances by 404-vs-403.
- **Endpoints** (all `/api/v1`, `Program.cs`): `GET instances`; `POST instances/{publicId}/backups/request`
  (fresh backup, returns the `requestId` to correlate the arriving file); `GET …/backups` (list, with
  `requestId` + restore state for polling); `GET …/backups/{id}/download`; `POST …/backups` (upload the
  raw body, origin `api`, Kestrel cap lifted like the instance upload); `POST …/backups/{id}/restore`
  (gated on `CanRestore`). Restore is still only MARKED — the instance downloads and applies it on its
  next beat and reports back, which the list then shows.

## Backlog

- **Provisioning new MatCMS instances** via **MatOS** + **Matcad**: MatOS already installs apps as
  containers from Compose templates with a port pool and stamps Matcad's labels
  (`matcad.enable=true`, `matcad.host=<slug>-<instance>.<BaseDomain>`, `matcad.port=<internal>`) on
  a shared Docker network, so every install gets its own subdomain and multiple installs of the same
  app don't collide. The cloud should drive that path — create the instance record, have MatOS
  create the container, then seed it from a profile (backup ZIP restore + plugin bundles) and claim
  it automatically. Read `../MatOS/README.md` and `../MatOS/src/MatOS.Web/{Api,Docker,Engine}` before
  designing this; MatOS's App Store / template engine is itself only at milestone M2.
- Guided update execution for **remote** instances (instance-side self-update triggered over the
  heartbeat channel).
- Cloud-hosted backup storage per instance (Matmon.Cloud's `/api/instances/{id}/backups` +
  account-scoped restore is the blueprint).
