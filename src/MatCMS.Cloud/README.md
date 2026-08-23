<div align="center">

<img src="wwwroot/img/logo.svg" width="240" alt="MatCMS.Cloud" />

# MatCMS.Cloud

**The control plane for self-hosted MatCMS instances.**

Every website in one view: update monitoring, notifications, remote updates with rollback and profile
sync – for a single instance or a whole fleet.

</div>

![Overview: online/offline, available updates, central release monitoring and the latest events](../../docs/images/cloud-overview.png)

---

**MatCMS.Cloud** is the central management for self-hosted
[MatCMS](https://github.com/Real-TTX/MatCMS) installations. A MatCMS instance connects to the cloud;
from there run **update monitoring**, **notifications** and – where possible – the **execution of
updates**, plus **profiles & sync** for settings, users, plugins and components.

- **Framework:** ASP.NET Core 10 (Razor Pages), C# – the same stack as MatCMS
- **Database:** SQLite (via EF Core) – one file, no extra DB container
- **Auth:** cookie-based, sign-in **only** via `/login` (default: `admin` / `admin`)
- **Ports:** internally `8080` (base-image default), mapped to host `9100`
- **Persistence:** one Docker volume `matcms-cloud-data` → `/app/appdata` (DB, keys)

## Screenshots

### Instances at a glance

![Instance list with status, hosting, version and update hint](../../docs/images/cloud-instances.png)

All connected instances with their **online/offline status**, *local/remote*, reported version and
the last heartbeat. Searchable, filterable and optionally as tiles with a live preview; an instance
on an older image gets an **update** hint.

### Overview & release monitoring

![Dashboard with metrics and the central release check](../../docs/images/cloud-overview.png)

Metrics (instances, online, offline, updates available), the **central** GHCR release check for all
instances at once, and the latest events (offline, new version, sync).

### Profiles & settings

| Profiles | Settings |
|---|---|
| ![Profiles](../../docs/images/cloud-profiles.png) | ![Settings](../../docs/images/cloud-settings.png) |

Profiles bundle configuration (settings/SMTP, users, plugins, components, templates) and roll it out
to the assigned instances; the global settings control notifications and auto-update.

---

## Feature set

- **Instances** – connect via a join code or adoption, a heartbeat every minute, online/offline
  status (dead-man switch after ~150 s), reported version, host, container and content counts, and a
  per-instance history. The list is searchable and filterable (online/offline, awaiting approval,
  update available, configuration drift) and can switch to **tiles with a live preview** of the home
  pages; every instance also has a preview tab with the embedded website.
- **Update monitoring** – the cloud polls the GitHub Container Registry for the latest
  `ghcr.io/real-ttx/matcms` release **once, centrally** (every 30 minutes) and compares it against
  every instance. The instances no longer have to check for themselves.
- **Local vs. remote** – the cloud works out by itself whether an instance runs on the **same Docker
  host**: the instance reports its container id, the cloud looks it up over the mounted Docker
  socket. Match = *local*, otherwise *remote*. Moving to another host automatically demotes the
  instance back to *remote*.
- **Running updates** – for **local** instances at a click: pull the new image, recreate the
  container with an identical configuration (env, volumes, ports, labels, networks), start it – and
  on failure **roll back** to the old container. Optionally automatic (default: off). For **remote**
  instances just the hint plus the command.
- **Notifications** – email (MailKit/SMTP) on *instance offline*, *new version available* and *failed
  update*. Each **once per event**, not once per check.
- **Profiles & sync** – configuration (settings/SMTP, users, plugins, components) is maintained on
  profiles and rolled out to all assigned instances; see below.
- **Users** – cloud operators signing in by email.

Not built yet (see `CLAUDE.md` → backlog): a preview of what a sync would change before it is applied,
and provisioning new instances through MatOS/Matcad.

---

## Quick start with Docker (recommended)

Prerequisite: Docker Desktop.

```bash
docker compose up -d --build
```

The UI then runs at **http://localhost:9100**
Admin sign-in: **http://localhost:9100/login** (user `admin`, password `admin`).

Or the bundled script:

```bash
./run-docker.ps1
```

Useful commands:

```bash
docker compose logs -f       # follow logs
docker compose down          # stop (data stays in the volume)
docker compose up -d --build # rebuild & start
docker compose down -v       # RESET (deletes the volume: DB, keys)
```

### Docker socket: optional, but required for running updates

`docker-compose.yml` mounts `/var/run/docker.sock`. Only with it can the cloud detect **and** update
local instances. The mount is a privilege escalation (socket access ≙ root on the host) – if you only
want notifications, remove the line; then every instance counts as *remote*. The update code only
touches containers whose image (or compose project) was identified as MatCMS.

On Windows/Docker Desktop the endpoint is `npipe://./pipe/docker_engine`
(`MatCmsCloud__Docker__Endpoint`), which `run-dev.ps1` sets automatically.

---

## Connecting an instance

There are two ways, both under **Instances → Add instance**:

**Way 1 – the instance reaches out (join code).** Every profile has a join code. In MatCMS, under
*Settings → Cloud*, enter the cloud URL and the code – the instance fetches its credentials and
configuration itself. This works behind NAT too, because the connection is outbound, and it is the
way to roll out many sites: the code belongs to the **profile**, so the instance automatically lands
in the right group.

**Way 2 – the cloud reaches out (adoption).** Enter the URL of an existing instance plus an
administrator account *of that instance*. The cloud hands the connection over directly; the instance
verifies the credentials against its own user table before accepting them. The credentials are used
for this one operation only and are not stored. For this the instance has to be reachable once –
after that everything is outbound again.

Whether a new instance is active right away or has to be approved first is controlled by the
**Auto-approve** switch on the profile.

## Profiles

A profile bundles rules and configuration for its assigned instances:

- **Rules** – notifications, recipients, automatic updates of local instances. Without a profile the
  global settings apply.
- **Settings** – an SMTP block plus any other MatCMS setting keys.
- **Users** – accounts created on the instances. The password is hashed once in the cloud; it is
  never stored in clear text.
- **Plugins** – plugin packages as MatCMS exports them. Same key = an update. An uploaded package can
  be **edited right here** (name, version, description, C# code); on save it is repacked, bundled
  files stay unchanged.
- **Components** – reusable blocks, identified by their type. The editor is the same as in MatCMS:
  click fields instead of typing JSON, enter test data, get a **live preview** of the rendered block,
  plus a debug panel that shows placeholders without a matching field.
- **Templates** – complete designs with layout HTML, CSS, JS, parameters and layout parts, with a
  **live preview**: a sample page that changes as you type, color pickers for every color value and
  CodeMirror for HTML/CSS/JS. Unresolved `{{placeholders}}` are highlighted red in the preview. The
  fastest way: build the template in MatCMS, export it there under *Templates → open template → "Export
  as JSON"* and paste it into the profile here. Which template becomes **active** on the instances is
  a separate switch – empty means the instance keeps its choice. Template parameters set by the
  customer are not overwritten.

Every change bumps the profile's **revision**. The instances see it in the heartbeat, fetch the new
configuration and report back which revision they applied – that is where the *in sync / drifted /
error* display per instance comes from.

Per payload you can set whether the cloud **overwrites** (the instance is aligned) or only **adds**
(only what is missing is created). Three rules always hold:

1. **Users are only added** – existing accounts are never changed or deleted. Otherwise a cloud
   setting could lock you out of your own site.
2. **Nothing is deleted** just because it is no longer in the profile. Removing a plugin from the
   profile stops future rollouts but does not remove it from running sites.
3. **Imported plugins stay disabled** – plugin code runs server-side, so a human enables it on the
   instance.

In MatCMS the other side lives under *Settings → Cloud*. The API behind it:
`POST /api/instances/{id}/heartbeat` with the header `X-MatCMS-Instance-Token`.

> **For "local" to be detected**, the instance must report its container id – which happens
> automatically from `/proc/self/cgroup` or `/proc/self/mountinfo`. Optionally the environment
> variable `MATCMS_IMAGE` can report its own image (for display only).

---

## CI/CD & versioning

Two GitHub Actions workflows build the Docker image and push it to the
**GitHub Container Registry (GHCR)** under `ghcr.io/<owner>/matcms-cloud` (the `<owner>` is
lowercased). In the monorepo they only fire on changes under `src/MatCMS.Cloud/**` (`paths:` filter),
so they never build a CMS image.

| Branch / context | Workflow                              | Version                                | `:latest`? |
|------------------|---------------------------------------|----------------------------------------|:----------:|
| `main` (release) | `.github/workflows/cloud-release.yml` | `MAJOR.MINOR.<build>-<datetime>`       | yes        |
| `dev` (nightly)  | `.github/workflows/cloud-dev.yml`     | `nightly-<build>-<datetime>`           | no         |
| local            | (Dockerfile default)                  | `local-<datetime>` (set it manually)   | –          |

- `MAJOR.MINOR` comes from the **`VERSION`** file in the project folder `src/MatCMS.Cloud` (default `1.0`).
- `<build>` = `github.run_number`, `<datetime>` = UTC `yyyyMMddHHmmss`.
- The computed version is passed as build arg **`APP_VERSION`** and stored as `InformationalVersion`.

---

## Local development (hot reload)

Prerequisite: .NET SDK 10.

```bash
./run-dev.ps1
```

or manually:

```bash
dotnet restore
dotnet watch run
```

Also runs at **http://localhost:9100**.

---

## Data & persistence

- The SQLite DB (`appdata/matcmscloud.db`) and the data-protection keys live in the **one** volume
  `matcms-cloud-data` (`/app/appdata`).
- The schema is created and kept up to date at startup via **EF Core migrations**
  (`db.Database.Migrate()`) – schema changes ship as a migration in the repo, no volume reset needed
  (just like MatCMS).

---

## Security notes

- **Change the default `admin` / `admin` login after the first start.**
- For public operation put it behind a **reverse proxy with HTTPS**. The container speaks HTTP on
  8080 internally; host port 9100 only comes from the compose mapping.
- **Login protection:** `/login` is rate-limited per client IP (10/min), the instance API at 120/min.
  Behind a reverse proxy enable `ForwardedHeaders` so the real client IP counts.
- **Instance tokens** are stored only as a SHA-256 hash and compared in constant time.
- **The Docker socket is the most critical part.** Only mount it if the cloud should run updates.
