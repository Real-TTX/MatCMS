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

**Not built yet:** a preview of what a sync would change before applying it, and the per-apply
history. `docker-compose.prod.yml` still declares an `external` network `main` that does not exist on
every host.

When extending it, copy the sibling repo's patterns rather than inventing new ones.

### Instance side (lives in `../MatCMS`, changed in lockstep)

- `Services/CloudProtocol.cs` — the mirror of `Services/InstanceProtocol.cs` here. **Both files must
  change together**, and `CloudProtocol.Version` / `InstanceService.CurrentProtocolVersion` must be
  bumped together.
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

### Schema changes hurt — by design

Both apps use `EnsureCreated()` with no EF migrations (MatCMS's rule, inherited deliberately). Adding
a table therefore means `docker compose down -v` on the cloud, which **drops every instance link** —
connected instances then get 401 and must re-enroll. This bit once during development already. While
the schema is still moving fast, expect it; if it becomes painful, that is the moment to argue for
migrations rather than working around it.

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
- **SQLite via EF Core**, one file at `appdata/matcmscloud.db`. **No EF migrations** —
  `db.Database.EnsureCreated()` + a `DbSeeder` at startup, exactly like MatCMS. Consequence: a model
  change needs `docker compose down -v` (say so in the README).
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

These files are **byte-identical copies** and must stay that way — check with `diff` after touching
any of them, and mirror a change into both repos: `wwwroot/css/site.css`, `wwwroot/css/admin.css`,
`wwwroot/js/admin-list.js`, `wwwroot/js/code-editor.js`'s CodeMirror bundle, `Pages/Shared/_IconTrash.cshtml`.
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

- `wwwroot/js/component-editor.js` is MatCMS's `admin-component-editor.js` adapted to the profile
  page: repeatable field rows instead of raw JSON, per-field sample data, a rendered `srcdoc` iframe,
  and a debug panel that names placeholders the template uses but no field defines. **Keep the two
  in sync** — a component authored here must behave exactly like one authored on an instance.
  One rule carried over deliberately: an existing field keeps its `id` when its label is renamed,
  because the id is what `{{placeholder}}` refers to and re-slugging it would break blocks already
  placed on live sites.
- `wwwroot/js/template-preview.js` has no MatCMS counterpart (its designer shows values, not the
  result). It renders a sample page — header, hero, buttons, cards — from the form's current values,
  uses the template's own `LayoutHtml` when it contains `{{content}}`, and marks unresolved `{{token}}`s
  in red so a broken layout is visible here rather than on a customer's homepage.
- The component preview borrows the theme of the template the profile activates
  (`CLOUD_PREVIEW_THEME`), so a block is judged in the design it will actually live in.
- **Instance previews** embed the live site (`Instance.PreviewUrl`). Two sources feed it, in order:
  the URL the instance reports, and — for a local instance — `Instance.LocalPort`, read from the
  container's published port. The second is flagged as guessed in the UI because it only resolves
  when the operator's browser is on the Docker host. On the instance side, `CloudState.ObservedBaseUrl`
  (an in-memory value set by a middleware in MatCMS's `Program.cs`) reports where the site was last
  actually reached, so a site with no canonical URL configured still has an address here.
- Plugins are edited **in place**: the bundle is unpacked, `plugin.json` rewritten, every other entry
  copied byte for byte (`Repack` in `Pages/Admin/Profiles/Edit.cshtml.cs`). Repacking rather than
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
docker compose down -v           # RESET (drops DB, keys, uploads) — needed after a model change
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
  `Services/InstanceProtocol.cs`. Carries app version, protocol version, host name, container id,
  image ref and content counts; the response carries the latest release, `UpdateAvailable`,
  `CloudCanUpdate` and (reserved, always empty) `PendingSync`. An instance counts as **offline**
  after `InstanceService.OfflineAfter` (150 s ≈ 2.5 missed beats). Bump
  `InstanceService.CurrentProtocolVersion` whenever the contract changes — older instances are
  badged *veraltet*. `POST /api/instances/{publicId}/disconnect` marks an instance offline at once
  and suppresses the outage mail.
- **Pull-based sync**: the heartbeat response tells the instance what is pending; the instance then
  fetches and applies it. Nothing is pushed into an instance over an inbound connection.

**Treat `MatCMS` and `MatCMS.Cloud` as one change set.** The link (settings UI, connection worker,
container identity) is in place on both sides; the **sync applier** on the instance still has to be
written when the sync engine lands here.

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

> **Correction to fix (this is currently wrong in the code):** `StoreUser`, `StoreSetting`,
> `ProfileStoreUser` and `ProfileStoreSetting` duplicate what already exists. Replace them with
> links from a profile to the existing `Users` rows and to the global SMTP settings, drop the
> Users/Settings tabs from *Admin → Store*, and delete `Pages/Admin/Store/User.*` and
> `Pages/Admin/Store/Setting.*`. `ProfileService.BuildConfigAsync` then resolves users and settings
> from the global tables plus the profile's own, with the profile's own winning — the same
> precedence that already applies to the store payloads.

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
| **Settings** | key/value against `SettingKeys` (SMTP block + free-form keys) | overwrite (default) or add-missing |
| **Users** | username + **password hash** (hashed once in the cloud, plaintext never stored) | **add-only, always** |
| **Plugins** | the exact `PluginPackager.Export` ZIP; `Plugin.Key` is the identity, a same-key import updates in place and runs the plugin's `Migrate` | overwrite (default) or add-missing |
| **Components** | `Type` is the identity | overwrite (default) or add-missing |
| **Templates** | `Name` is the identity (same as MatCMS's backup/restore); the JSON that MatCMS's template editor exports (`?handler=ExportJson`) imports straight into a profile | overwrite (default) or add-missing |

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

Two guards worth keeping: the instance refuses pushed settings whose key is in `SettingKeys.Cloud`
(otherwise one profile could rewrite an instance's cloud link), and imported plugins stay **disabled**
because plugin code runs server-side.

### Next: three sync modes and a report back (designed, not built)

Today a payload is either overwritten or only added when missing. That misses the case the operator
actually wants most often: **roll this out once to get the site started, then leave it alone.**

Replace the two booleans (`Overwrite*`) with one mode per payload:

| Mode | Meaning | How the instance decides |
| --- | --- | --- |
| **Einmalig** | seed once, never touch again | apply only if this profile/item has not been applied here before |
| **Ergänzen** | create what is missing | apply only when the identity does not exist locally |
| **Synchron halten** | keep in line with the profile | apply every time (today's overwrite) |

*Einmalig* is the only one that needs memory, and it must live **on the instance**: whether something
was already seeded here is a fact about this site, not about the profile. A `cloud.seeded` setting
holding the identities already applied is enough — it survives restarts and costs one row.

**The report is what makes this usable.** The heartbeat already carries `AppliedRevision` and
`SyncError`; extend it to a per-item outcome the instance produces while applying:

```
installed | updated | skipped-exists | skipped-once | failed(reason)
```

The cloud stores that verbatim against the instance and renders it on the detail page. It computes
nothing: the instance is the only party that knows whether a component already existed or a plugin
import failed, so it says so, and the cloud is a record keeper. That is also why this scales — adding
a payload type later needs no cloud-side logic, only a new outcome line.

Two consequences worth keeping in mind when building it: `ConfigRevision` alone stops being enough to
mean "in sync" (an instance can be on the current revision with three items skipped), so the badge
needs to read the report; and *Einmalig* must be reported as `skipped-once` rather than silently
doing nothing, otherwise the operator cannot tell it apart from a broken sync.

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
- **Remote** — notify only, plus the exact command (`docker compose pull && docker compose up -d`).
  A guided/agent-driven remote update is backlog.

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
