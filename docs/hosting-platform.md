# Hosting platform — MatCMS.Cloud as a hosting control plane

Status: **Increments 1–5 built and tested (2026-09-29); 6 dropped.** This document designs turning MatCMS.Cloud from a control plane
that *watches* instances into one that can also *host* them: an activatable Hosting module, a per-instance
Hosting tab, optional reverse-proxy management, a separable hosting engine with multiple nodes, moving
instances between nodes, and a cloud self-updater. It ends with a cut into deployable increments and the
decisions that are Matthias's to make before any of it is built.

All identifiers below are real types in the repo unless marked *(new)*. Comments explain the *why*.

---

## 0. Guardrails (non-negotiable)

1. **MatCMS works 100% without any cloud.** It is a standalone WordPress replacement first. Nothing in
   this document may move a capability into `src/MatCMS` that *requires* a cloud, a node or a proxy. A
   hosted instance is the same MatCMS image that also runs on its own; hosting only changes *who starts
   the container*.
2. **Hosting is optional inside the cloud.** With the module off, the cloud is exactly today's control
   plane. No Hosting menu, no node concepts in the UI.
3. **The reverse proxy is optional inside hosting.** Hosting must run **without** a proxy (today's model:
   one published host port per instance) **and with** one (a domain per instance via Matcad). The
   structure is built so both work from day one — no code path may assume a proxy exists.
4. **Outbound-only stays.** Instances call the cloud, never the reverse. Remote nodes follow the same rule
   (§3.1). The one sanctioned inbound call remains adoption (`/api/cloud/link`).
5. **API-first: everything is agent-controllable.** Every hosting capability ships with three surfaces
   from day one — admin UI, operator REST (`/api/v1/...`) and an MCP tool — all calling ONE service
   (the `StoreService` pattern). Consequential actions get their own API-key right (like `CanRestore`).
   A capability that exists only in the UI is unfinished.

---

## 1. What exists today

More of the engine is already built than the UI suggests — the first increments are mostly *surfacing*
existing capability, not writing new container code.

| Capability | Where | Notes |
|---|---|---|
| Talk to one Docker daemon | `Services/DockerHostService.cs` | Docker.DotNet over the mounted socket; `IsReachableAsync`. |
| Start / stop / restart a container | `DockerHostService.Start/Stop/RestartContainerAsync` | Already guarded by `LooksLikeMatCms`. No UI tab yet. |
| Update with rollback | `DockerHostService.UpdateContainerAsync` | Pull → park old as `<name>-matcmscloud-old` → recreate → rollback on failure. |
| Provision a new instance | `Services/HostingService.cs` `CreateAsync` + `NextFreePortAsync`/`UsedPortsAsync` | Port-pool based, stamps `DockerHostService.ManagedLabel`. |
| Ownership model | `ManagedLabel = "matcmscloud.managed"`, `Instance.CloudManaged` | Re-read on every heartbeat; "Local" ≠ "ours". |
| Local vs remote classification | `InstanceService.ClassifyAsync` | Container id match on the reachable daemon. |
| Remove (3 destructive ways) | `Services/InstanceRemovalService.cs`, `InspectTeardownAsync`, `RemoveInstanceContainerAsync` | Named volumes removed explicitly. |
| Prune old images | `DockerHostService.PruneMatCmsImagesAsync` | MatCMS images only. |
| Cloud's own version check | `Services/VersionService.cs` `CheckAsync` | **Checks only.** `UpdateCommand` is just a hint string (`docker compose pull && docker compose up -d`). No self-update executes. |
| Backup / restore transport | `BackupStore`, instance `ContentTransferService` | The migration primitive (§3.5). |
| Reverse proxy | sibling repo **Matcad** (`matcad.*` container labels) | Not integrated; the backlog names it. |
| General app/container manager | sibling repo **MatOS** | Overlap to decide (§3.8). |

Today there is exactly **one implicit node**: the Docker daemon whose socket is mounted into the cloud
container. Everything "local" means "on that daemon".

---

## 2. Target picture

```
            ┌──────────────────────── MatCMS.Cloud (control plane + UI) ────────────────────────┐
            │  Hosting module (toggle)   Nodes   Instances→Node   ProxyProvider   Cloud updater │
            └───────▲───────────────────────────▲───────────────────────────▲──────────────────┘
      outbound beat │                 outbound beat │                 outbound beat │
            ┌───────┴────────┐          ┌─────────┴────────┐          ┌────────┴──────────┐
            │ Node "local"   │          │ Node "hetzner-1" │          │ Node "home-lab"   │
            │ (built into    │          │ node-agent (new) │          │ node-agent (new)  │
            │  the cloud)    │          │ + Docker daemon  │          │ + Docker daemon   │
            │ + Matcad (opt.)│          │ + Matcad (opt.)  │          │ (no proxy)        │
            └───┬───────┬────┘          └───┬──────────────┘          └───┬───────────────┘
             MatCMS  MatCMS              MatCMS                         MatCMS
```

- **Cloud** = control plane and UI. Decides *what* should run where.
- **Node** *(new concept)* = one Docker host that can run instances. Executes *how*.
- **Hosting engine** = the code that performs container actions on a node. Today it lives inside the
  cloud (`DockerHostService`); in the target it is the same logic packaged twice (§3.2).
- **ProxyProvider** *(new)* = optional per-node routing layer (§3.4).

---

## 3. Core design decisions

Each has a recommendation; the alternatives are kept so the choice stays visible.

### 3.1 Node ↔ cloud protocol — **recommend: outbound, pull-based (like instances)**

A remote node-agent heartbeats the cloud (`POST /api/nodes/{nodeId}/heartbeat` *(new)*) and **pulls jobs**
("start X", "update Y", "create Z", "migrate-out W"), then reports results — exactly the pending-backup /
content-op shape the repo already trusts (`ContentOp`, `PendingBackup`). The cloud never opens a
connection to a Docker host.

- ✅ Works behind NAT/firewalls, same security posture as instances, one protocol idea in the codebase.
- ✅ Reuses `CloudProtocol` conventions (versioned DTOs, idempotent job ids, report-back).
- ❌ Actions are asynchronous (one beat ≈ seconds; the node can beat faster than instances, e.g. 5–10 s,
  because it is infrastructure, not a site).
- *Alternative:* cloud → node inbound API (node exposes an authenticated endpoint). Synchronous and simpler
  to reason about, but reopens the "cloud reaches in" hole guardrail 4 closes, and needs an open port on
  every host. Rejected unless latency proves unacceptable.

### 3.2 Where the engine runs — **recommend: one engine, two packagings**

Extract the container logic of `DockerHostService` + `HostingService` into an engine library *(new, e.g.
`src/MatCMS.Hosting`)* with no ASP.NET/EF dependency, used by:

1. **The built-in "local" node** inside the cloud — today's behaviour, unchanged, so a single-host setup
   needs nothing new.
2. **A small node-agent image** *(new, `ghcr.io/real-ttx/matcms-node`)* for every additional host. It holds
   the socket, runs the engine, speaks §3.1.

The split mirrors how `MatCMS.Shared` already holds the wire contract once for two apps. The local node
is just a node whose "agent" is in-process.

### 3.3 Security model

- **Node enrolment** like instance join codes: the cloud mints a node token, shown once, stored SHA-256
  (`ApiKeyService`/`InstanceService.HashToken` pattern). A node can be revoked.
- **Scope:** a node only ever receives jobs for instances assigned to it.
- **Existing guards stay mandatory in the engine:** `LooksLikeMatCms`, `ManagedLabel` ownership, explicit
  named-volume removal, never deriving targets from names (see the Removal section in the cloud's
  `CLAUDE.md`). They move *with* the engine so a node enforces them locally, not only the cloud.
- **Destructive jobs** (remove, migrate-out, volume delete) require an operator confirmation in the UI and
  carry the confirmed container id, re-checked on the node — the same rule the current removal flow uses.
- **Least privilege:** the node-agent needs the Docker socket (same `gosu`/socket-gid trick as
  `docker-entrypoint.sh`); nothing else from the host.

### 3.4 Reverse proxy — **optional provider interface**

Introduce `IProxyProvider` *(new)* per node:

| Provider | Routing | Used when |
|---|---|---|
| `NoProxy` (default) | published host port, `Instance.LocalPort` as today | no proxy on the node — **today's mode** |
| `MatcadProvider` | domain + TLS via Matcad's REST API (`/api/v1/routes`, `X-Api-Key`) — *built that way; the `matcad.*` labels originally planned here were dropped, see "Built: Increment 3"* | Matcad runs on the node |
| `CaddyProvider` | domain + TLS by driving a plain Caddy directly (admin API, own `@id` routes only), no Matcad in between | a stock Caddy runs on the node |

**Decided (2026-09-29): all three are first-class and selectable per node.** None is privileged in the
code; `NoProxy` is only the default because it needs nothing installed.

Every place that computes an instance's public address asks the node's provider — never a hardcoded port
or a hardcoded domain. That is what makes "with or without proxy" a configuration, not a fork. Domains and
certificates appear in the UI **only** when the node's provider supports them.

### 3.5 Moving an instance between nodes

A migration job pipeline, all steps reported back:

1. Source node: take a fresh backup (existing `PendingBackup` path) → upload to `BackupStore`.
2. Target node: create the container (engine `CreateAsync`), restore the backup (existing restore path;
   `ContentTransferService.ImportAsync` preserves the `cloud.*` keys, so identity survives).
3. Target provider: publish the route (port or domain).
4. Cloud: switch `Instance.NodeId`, update the public address; the instance's next beat confirms.
5. Source node: stop, then remove the old container **only after** the target reported healthy (the
   "backup survives or nothing is removed" rule from the removal flow).

*Built differently (see "Built: Increment 5"):* the data VOLUME is copied 1:1 instead of backup/restore.
The plan above had a flaw that only building it exposed: a backup restored into a fresh container keeps the
FRESH container's cloud link (`ImportAsync` preserves `cloud.*`), so the site would have arrived as a NEW
instance. The volume carries its own link, so the same instance simply beats from the new host.

### 3.6 Multi-cloud — **needs product clarification before design**

"Multi-cloud connection" can mean different things; each has a different cost:

- (a) **One cloud, many nodes** across providers/hosts — this *is* §2 and needs nothing extra.
- (b) **An operator with several clouds** that should see each other (federation) — a new trust layer.
- (c) **An instance linked to several clouds at once** — conflicts with `cloud.*` being one link per
  instance and with profile rollout ownership. Not recommended.

Recommendation: ship (a) first; decide (b) later only if a real need appears.

### 3.7 Cloud self-updater

A process cannot cleanly replace its own container while running in it. Options:

- (a) **Short-lived helper container** *(recommend first)*: the cloud starts a one-shot helper (same image,
  `--updater` mode, socket mounted) that pulls the new image, recreates the cloud container with the same
  inspected config, health-checks it and **rolls back** on failure — i.e. `UpdateContainerAsync`'s logic
  pointed at the cloud itself, executed from outside it. Then exits.
- (b) **Via the local node-agent** once nodes exist: the cloud is just another container a node updates.
  Cleanest long-term; depends on §3.2.
- (c) Sidecar (Watchtower-style). Adds a permanently running privileged component; rejected.

`VersionService.CheckAsync` already knows when an update exists; the updater adds the *execute* half,
with the same pull → park-old → recreate → rollback discipline as instances.

### 3.8 Relationship to MatOS

MatOS is a general Docker app manager (install engine, port pool, Matcad labels). Two paths:

- **Independent engine** (recommended for now): the hosting engine stays MatCMS-specific and small; MatOS
  keeps its own. Less coupling, faster.
- **Shared engine**: extract a common library both use. Only worth it once both are stable.

Decide after reading `../MatOS/README.md` and `../MatOS/src/MatOS.Web/{Api,Docker,Engine}`.

---

## 4. The Hosting module toggle

- A cloud-wide setting `hosting.enabled` *(new, `CloudSetting`)* under *Einstellungen*. Off by default.
- **Off:** the cloud is today's control plane. No Hosting menu, no node pages, no Hosting tab. Existing
  local-update/removal keep working as they do today (they predate the module and must not regress).
- **On:** a top-level **Hosting** menu (nodes, providers, provisioning) and a **Hosting** tab on each
  instance that runs on a node the cloud controls (`Instance.CloudManaged` / assigned node).
- The Hosting tab holds the *actions* (start/stop/restart/update/logs/resources/migrate), separated from
  the instance's content/sync/backup tabs — the "cleaner separation" asked for.

---

## 5. Increments (each deployable, each standalone-safe)

| # | Increment | New code | Depends on |
|---|---|---|---|
| **1** | Hosting toggle + menu + **instance Hosting tab** surfacing *existing* actions (start/stop/restart/update/container logs) for instances on the local daemon | UI + setting; reuses `DockerHostService` | — |
| **2** | **Cloud self-updater** (helper-container, §3.7a) | updater mode + UI button | — |
| **3** | **`IProxyProvider`** with `NoProxy` (default) + `MatcadProvider` + `CaddyProvider`; domain/TLS per instance — *built* | provider layer, address resolution | 1 |
| **4** | **Node model + node-agent** (outbound protocol, enrolment, local node stays "Dieser Host") — *built; agent = cloud image in `--node-agent` mode, see its spec* | `Node`/`NodeJob` tables, protocol DTOs, agent mode | 1 |
| **5** | **Migration between nodes** incl. proxy update — *built (volume copy, see below)* | migration job pipeline | 3, 4 |
| **6** | Multi-cloud (only if §3.6 (b) is wanted) | federation | product decision |

Increments 1 and 2 are small, independent and immediately useful; 4 is the large structural step and
should get its own detailed spec when it is next.

### Built (2026-09-29): Increments 1 + 2

- **1 — Hosting tab:** `HostingActionsService` (status/start/stop/restart/update/logs), instance Details
  tab `hosting`, Hosting page (`/Admin/Hosting`, nav while the module is on), REST `/api/v1/hosting`,
  `/api/v1/instances/{id}/container[/{action}|/logs]`, MCP `get_hosting_status`, `set_hosting_enabled`,
  `get_container_status`, `container_action`, `get_container_logs`, key right `CanManageHosting`. The
  module switch already existed (`hosting.enabled`); the tab deliberately does NOT depend on it.
- **2 — Cloud self-updater:** helper container + `SelfUpdateRunner`, DB snapshot/restore, health check,
  REST `/api/v1/cloud/update`, MCP `get_cloud_update_status`, `update_cloud`, card on About + Hosting.

**Tested end to end against Docker Desktop** (real cloud container, real instance container on the same
daemon): no-op when current (no restart); two real updates (~5–10 s outage each); rollback of a new image
that touches the DB and crash-loops — detected in 3 s, container rolled back, DB restored, foreign WAL
gone, data intact; the same rollback when the OLD image's record had already vanished; instance
status/logs/restart via REST, MCP and UI; Operator blocked from the fleet pages; key-right matrix.

**Three findings the test forced, all relevant to production:**
1. **containerd image store:** moving a tag (a pull or rebuild) can drop the old image's record while
   its container keeps running — the helper can then not be created from the running image id. It falls
   back to the tag (the new image, which carries the updater too). A stopped container whose image record
   is gone still starts, so the rollback holds.
2. **A crashing new cloud never shows "exited":** it inherits `restart: unless-stopped` and loops in
   `restarting` without an address. The health check therefore treats any restart of a seconds-old
   container as failure (was: sat out the full 120 s).
3. **Docker Desktop's default address pools can be exhausted** — irrelevant to the product, but a test
   network needs an explicit `--subnet`.

**Bootstrapping:** a cloud can only update itself once it RUNS a version that contains the updater. The
first deploy of this version is therefore still the manual `docker compose pull && docker compose up -d`.

### Built (2026-09-29): Increment 3 — reverse-proxy providers, domain per instance

- **Layer:** `Services/Proxy/` — `IProxyProvider` (`TestAsync`, `UpsertAsync`, `DeleteAsync`, `ExistsAsync`)
  with `NoProxyProvider`, `MatcadProvider`, `CaddyProvider`; `ProxyService` owns everything above the
  provider (domain validation + uniqueness, upstream resolution, publish/move/unpublish, status, the
  canonical-URL push, provisioning). Cloud-wide settings for now (`hosting.mode` = `none|matcad|caddy`,
  `hosting.caddy*`, `hosting.proxy*`); in increment 4 each node carries its own copy of exactly
  `ProxySettings`.
- **Upstream modes:** `network` — the cloud attaches the container LIVE to the proxy's Docker network
  (`DockerHostService.ConnectToNetworkAsync`) and routes to `http://<container>:8080`; `hostport` — the proxy
  goes to `http://<upstreamHost>:<published port>`. Container name and port always come from the daemon.
- **Record:** `Instance.ProxyDomain/ProxyProvider/ProxyRouteId/ProxyError/ProxyPublishedAt` (migration
  `AddInstanceProxy`). Publishing pins `Instance.Url = https://<domain>`; with `pushCanonical` a
  `setting.set` content op tells the site `site.canonicalUrl` + `site.behindHttpsProxy`.
- **Surfaces:** each host's page, tab Proxy (Hosting → Hosts; provider, fields per provider, "Speichern und Verbindung testen"),
  the Domain card on the instance's Hosting tab (publish/move/check/unpublish), provisioning with a domain;
  REST `/api/v1/hosting/proxy[/test]`, `/api/v1/instances/{id}/domain`; MCP `get_proxy_config`,
  `configure_proxy`, `test_proxy`, `get_domain_status`, `publish_domain`, `unpublish_domain`.
- **Provisioning:** the route is created right after the container (it only needs the container) and parked
  under `hosting.pendingRoute:<container name>`; `InstanceService.ClassifyAsync` adopts it on the new site's
  first beat. The adopted route keeps its container-name id; a later move reuses it (no duplicate).

Rules that the implementation relies on:
- **Route first, record second.** Unpublish deletes the route and only then clears the record, so a proxy
  that is down never leaves a route behind that the cloud has forgotten. Delete is idempotent (404 = gone).
- **Changing the provider** removes the old provider's route on the next publish; switching to `none`
  removes it too. Already published domains do not move by themselves.
- **Caddy is edited surgically:** only routes with `@id` `matcms-…` via `/id/…`, inserted at the top of the
  server's routes — never `/load`, which would replace everyone else's config. A missing server is created
  listening on `:443` (Caddy only obtains certificates automatically there).
- **Matcad via its API, not labels:** labels are fixed at container creation (a domain change would mean
  recreating the site), Matcad's label discovery is off by default, and a label route cannot be edited or
  removed from outside. Provisioning therefore stamps **no** `matcad.*` labels any more — with discovery on
  they would have produced a second route for the same host. Matcad keeps a per-route ACME e-mail only
  for wildcard routes, so the cloud has no such field; Matcad's own global setting applies.

**Tested end to end against real containers** (test cloud, current CMS instance, stock `caddy:2`, Matcad
built from its current source + `matcad-caddy`): HTTPS 200 through both proxies (`*.localhost`, internal
CA); Caddy starting from an empty config (server created); move = same route patched; a foreign route in
Caddy untouched; route deleted by hand detected (`routeExists: false`) and recreated on republish;
provider switch Caddy → Matcad → none cleans up behind itself; host-port mode; canonical URL + "behind
HTTPS proxy" arriving on the site; provisioning with a domain + adoption on the first beat; UI settings
save + test flash, Details publish/unpublish; REST, MCP and the key-right matrix (no hosting right →
403 on writes, scoped key → 403 on cloud-wide config and 404 outside its scope).

Two findings: a Matcad from before its REST API (image older than 08/2026) answers `/api/v1/*` with a
redirect to its login page — `MatcadProvider` now says so instead of reporting a JSON parser error; and
Matcad's Caddy admin address is hard-wired to `http://caddy:2019` (its `Matcad__Caddy__AdminUrl` env var
is not read), so its Caddy must be reachable under that name.

### Spec: Increment 4 — nodes + node-agent

**Goal:** one cloud, many Docker hosts. Every host other than the cloud's own runs a small **node-agent**
that connects OUT to the cloud, pulls jobs and executes them with the same engine code the cloud uses on
its own daemon. Instances on a node get the full Hosting tab (status, start/stop/restart, update, logs,
domain) and can be provisioned there.

**Packaging decision (deviates from §3.2 on purpose):** the node-agent is **the cloud image itself** in a
second mode — `dotnet MatCMS.Cloud.dll --node-agent`, entered before the web app is built, exactly like
the `--self-update` helper. The engine (`DockerHostService`, the proxy providers) already runs without
ASP.NET/EF there, so this *is* "one engine, two packagings" without a library extraction, a third
Dockerfile, a new CI workflow or a second GHCR package — and an agent can never run an engine version the
cloud does not know. Price: the agent image is the cloud's size. A slim `matcms-node` image can be cut
from the same entry point later without touching the protocol.

**The local node stays virtual.** "Dieser Host" is the cloud's own daemon, `Instance.NodeId = null`,
configured by the existing cloud-wide settings (hosting.*). No row, no migration of existing instances —
a single-host setup sees nothing new. Remote hosts are rows in `Nodes`.

**Model** *(new)*: `Node` (PublicId, Name, TokenHash, Revoked, LastSeenAt, AgentVersion, HostName,
DockerVersion, DockerError, InventoryJson + InventoryAt, own proxy settings = exactly `ProxySettings`,
own port range); `NodeJob` (NodeId, InstanceId?, Kind, PayloadJson, State pending/running/done/failed,
Message, ResultJson, timestamps); `Instance.NodeId`; `InstanceHosting.Node`.

**Protocol** (cloud-internal — both ends are this project, so it lives here, not in `MatCMS.Shared`, and
**instances are not affected**: `CloudProtocol.Version` does not move): `POST /api/nodes/{publicId}/heartbeat`
with `X-MatCMS-Node-Token`. Request: agent version, host name, Docker version/error, the MatCMS containers
on the host (only those `LooksLikeMatCms` accepts — the cloud has no business listing other containers),
and job reports. Response: jobs to run. **Long poll:** with nothing to hand out and nothing reported, the
cloud holds the request up to 25 s and answers the moment a job is enqueued — so a button on the Hosting
tab takes about a second on a node, while the connection stays outbound. The agent runs jobs concurrently
and beats again as soon as one finishes.

**Jobs:** `container.details`, `container.logs`, `container.power` (start/stop/restart),
`container.update`, `instance.create`, `proxy.test`, `proxy.publish`, `proxy.delete`, `proxy.exists`.
A job is handed out ONCE (pending → running); one that is not reported within 15 min fails, one that no
agent picked up within 2 min expires ("Node nicht erreichbar" — short, so a "stop" that was reported as still waiting never fires by surprise when the node returns). The cloud waits for short jobs
synchronously (the UI and API keep their shape) and answers "läuft noch" with the job id past the
timeout. Destructive teardown on nodes is **not** in this increment — only unregistering; the removal
guards will move with increment 5.

**Security:** the node token is shown once and stored SHA-256; revoking answers the heartbeat with 403;
a node only ever receives jobs for itself; the agent executes only the listed kinds, through the engine
whose guards (`LooksLikeMatCms`, `ManagedLabel`) therefore run ON the node. The Matcad key travels in the
`proxy.*` job to the node that must use it (over the same TLS link), never back.

**Classification:** an instance's reported container id is looked up on the cloud's own daemon first,
then in the inventories of nodes seen within the last 5 minutes → `Hosting = Node`, `NodeId`.
A node's beat refreshes container state/port of its instances at once (a stopped site does not beat).

**Surfaces:** Hosting → Hosts (list incl. "Dieser Host", one page per host with Übersicht/Container/Docker/Proxy/Einstellungen/Aufträge, create → token + `docker run` command once,
detail with status/inventory/proxy & ports/jobs, rotate token, revoke, delete), node select when
provisioning; REST `/api/v1/nodes…` and `POST /api/v1/hosting/instances` (provisioning was UI-only until
now); MCP `list_nodes`, `get_node`, `create_node`, `update_node`, `set_node_revoked`, `delete_node`,
`rotate_node_token`, `test_node_proxy`, `list_node_jobs`, `create_instance`.

### Built (2026-09-29): Increment 4 — nodes + node-agent

As specified above. Code: `Models/Node.cs` (`Node`, `NodeJob`; migration `AddNodes`), `Services/Nodes/`
(`NodeProtocol`, `NodeService` = management + heartbeat + `RunAsync`, `NodeSignal` = in-memory wake-ups,
`NodeJobExecutor` = what the agent runs, `NodeAgentRunner` = the agent loop), `Services/Proxy/ProxyEngine.cs`
(the provider + upstream part of `ProxyService`, run in-process for "Dieser Host" and as the `proxy` job on
a node), `DockerHostService` engine section (inventory, daemon info, free port, `CreateInstanceContainerAsync`
— moved out of `HostingService` so both hosts create containers with the same code), `HostingService.ProvisionAsync`
(provisioning as one step for UI/REST/MCP), `Api/NodeApi.cs`, `Mcp/NodeTools.cs`, pages `Admin/Hosting/Nodes/*`,
the shared `_ProxyFields.cshtml` (the Settings page and every node use the same fields).

**Tested end to end** (test cloud WITHOUT a Docker socket, so every action had to go through the node; agent =
the same image with `--node-agent`; stock `caddy:2` on the node side): enrolment via REST with the printed
`docker run` command; agent reports host, versions and inventory; node proxy configured and tested FROM the
node (0.3 s round trip — the long poll answers at once); provisioning on the node with a domain (2 s,
route created on the node's Caddy, adopted on the first beat); instance classified `node`; HTTPS 200 through
the node's proxy; logs 0.2 s, restart 1.1 s, stop/start with the right state, update, domain move — all via
REST, MCP and the UI pages. Failure paths: wrong/missing token 401, revoked node 403 (agent backs off), agent
gone → the first call waits its timeout and says "läuft noch", after the offline threshold calls fail at once
("nicht verbunden"), and the job that was never picked up expired instead of firing when the agent returned.
Key-right matrix (no hosting right 403; scoped hosting key may read and test, but not create/change/delete
nodes or provision). Regression of the local path (cloud with socket, provisioning + domain on "Dieser Host").

Two decisions the test made: a pending job expires after **2 min**, not 10 (a connected agent picks a job
up within a second; an old pending job means the node was gone, and a "stop" must not fire by surprise when
it returns); and the `proxy` job payload — which carries the node's Matcad key — is **encrypted at rest** and
dropped once the job is finished.

**Not in this increment (deliberately):** destructive teardown on nodes (only unregistering — the removal
guards move in increment 5), bulk update / auto-update of node instances (the Hosting tab's update works),
and updating the agent itself (for now: pull the image and recreate the agent container by hand; the agent
reports its version, so a stale one is visible).

*All three are built since (2026-09-29):* teardown with increment 5; bulk and auto-update go through
`HostingActionsService.UpdateAsync` for both hosts; and the **agent self-update** — the `agent.update` job makes
the agent start a one-shot helper from its own image (`--update-container <id>`, same socket/endpoint and
networks), which pulls, recreates and ROLLS BACK unless the new agent keeps running for 20 s. Tested in a dind
node: a new image that cannot start → old agent back; a new image that starts and crash-loops → caught by the
run check, old agent back; the real GHCR `latest` → agent reconnects with the new version.

### Built (2026-09-29): Increment 5 — moving between hosts, removal on nodes

**Flow** (`Services/Nodes/MigrationService.cs`, run by `MigrationWorker` in the background, one move at a time,
every step written to `InstanceMigrations` — migration `AddInstanceMigrations`): stop the source → export the
data volume (`DockerHostService.ExportDataAsync`, Docker's archive API on the STOPPED container, so SQLite is
consistent) → the source host uploads it to the cloud (`PUT /api/nodes/{id}/transfers/{transferId}`) → the target
host downloads it and creates the same container (image, env, name, volume name taken from the source) with the
data unpacked into the volume BEFORE the first start (`CreateInstanceContainerAsync(…, seed)`) → wait until the
INSTANCE beats from the target (not "the container started") → move the proxy route (delete at the old host
first, then publish at the new — with one shared proxy the other order would update and then delete the same
route id) → retire the source (`RetireContainerAsync`: renamed `<name>-moved-<date>`, restart policy "no",
volume kept — the way back) or, if asked, remove container + volume. All hosts go through
`NodeService.RunOnAsync`, so local → node, node → node and node → local are one code path.

**The rule every failure path obeys:** one cloud identity never runs twice. A rollback removes the target
container (and its copied volume) FIRST and only then starts the source again; if the target cannot be removed
the source stays stopped and the move says so. A cloud restart mid-move rolls back — except past verification,
where the site already runs (and may hold new content) on the target: then the move stands and only the tidy-up
is flagged. The transfer endpoints accept exactly the source (upload) and the target (download) of the one
RUNNING move; the file is deleted as soon as the target has it.

**Removal on nodes:** `InstanceRemovalService` inspects and removes through the node (`container.teardownInfo`,
`container.remove` — the node re-checks the managed label itself); the Delete page, including "backup first",
now works for node instances.

**Surfaces:** "Umziehen" card on the Hosting tab (target, "alte Kopie entfernen", step log that follows itself);
REST `POST /api/v1/instances/{id}/migrate`, `GET …/migrations`; MCP `migrate_instance`, `get_migrations`.
Removing the old copy additionally needs the restore right (it deletes data).

**Tested for real with two Docker daemons** (the cloud's own + a `docker:dind` node reached over TCP): Dieser Host
→ node in 28 s (marker page present on the target, same instance id, old copy retired with restart policy "no",
route removed from the local Caddy); node → Dieser Host blocked by the retired copy's volume → clean rollback
(source started again on the node); back with "remove old copy" in 19 s (route recreated, HTTPS 200, node
container AND volume gone); a move started from the UI form; teardown of a node instance through the Delete
page; rights and transfer guards.

**Four findings the test forced:**
1. **Docker.DotNet hangs forever on `tcp://`** (no error, no timeout) — the agent never beat. `tcp://` is now
   translated to the equivalent `http://`, and the agent's daemon calls are time-bounded.
2. **A Docker client could get stuck after an archive extract over http** — every later call waited. The agent now
   uses a fresh client per beat and per job.
3. **Nested Docker reported the wrong container id**: the CMS took the first 64-hex in `mountinfo`, which under
   dind (and some LXC/VM setups) is an OUTER volume id. The CMS now prefers ids from Docker's `…/containers/<id>/`
   paths and cross-checks with the hostname; and the cloud falls back to the reported host name (Docker's short
   id) and then stores the id the DAEMON confirmed, so existing instances are fixed without a CMS rollout.
4. **A long-running caller held a stale node** and declared a connected node "nicht verbunden" mid-move —
   `NodeService.RunAsync` now reads the node's state fresh.

---

### Built (2026-09-30): Automatic addresses + edge proxy

Two independent, optional switches (both off by default, so existing setups do not change):

| Automatische Adressen (per host) | Edge (cloud-wide) | Result |
|---|---|---|
| off | off | as before: port, or a customer domain at the host's proxy |
| on | off | every instance has `name.serverX…`; a customer domain points at its node |
| off | on | the edge forwards to `host-address:port` (reachable ports / private network) |
| on | on | the edge forwards to `name.serverX…` — moves need no DNS change |

The target setup this enables: one server runs the cloud and the edge (80/443, customer domains + wildcard
`*.cloud…`), further servers are nodes (Docker + agent, wildcard `*.serverX…` each). Verified against a real
Caddy: host route, edge route (TLS to the host address, Host header rewritten), removing/recreating the host
address re-points the edge, switching the edge off moves the domain back to the host proxy, teardown deletes both
routes. Wildcard certificates (DNS-01) are not configured by the cloud; per-host certificates via HTTP-01 hit
Let's Encrypt's 50/week-per-domain limit with many sites — use Matcad with a DNS provider or a Caddy with a DNS
plugin on the node for a wildcard.

## 6. Decisions (2026-09-29)

| # | Question | Decision |
|---|---|---|
| 1 | Node protocol (§3.1) | **Outbound, pull-based** (node-agent beats the cloud and pulls jobs). |
| 2 | Engine packaging (§3.2) | One engine library + built-in local node + separate node-agent image *(recommended default, not objected)*. |
| 3 | Proxy (§3.4) | **All three selectable per node:** `NoProxy`, `MatcadProvider`, `CaddyProvider`. |
| 4 | Multi-cloud (§3.6) | **(a) one cloud, many nodes.** No federation. |
| 5 | Self-updater (§3.7) | Helper container first *(recommended default)*. |
| 6 | MatOS (§3.8) | Independent engine for now *(recommended default)*. |
| 7 | Start order | **Increments 1 and 2 now**; 3–5 afterwards, 4 with its own detailed spec. |
