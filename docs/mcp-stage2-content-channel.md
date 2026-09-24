# MCP Stage 2 — Content/Config changes to connected instances

Status: **design only, no code yet.** Stage 1 (the `/mcp` server exposing the read + backup/restore
operator actions) is in progress. This document designs Stage 2: letting an external AI (ChatGPT /
Claude, via the MCP server) make **content and configuration changes** to connected MatCMS instances —
"change this connected website's pages / blocks / settings".

All identifiers below are real types in the repo. Comments explain the *why*.

---

## 1. The core constraint: outbound-only

An instance **always calls the cloud**; the cloud never reaches into a site. This is stated in
`CloudProtocol` (the class summary) and enforced everywhere: the heartbeat
(`POST /api/instances/{publicId}/heartbeat` in `MatCMS.Cloud/Program.cs`), the config pull
(`GET /api/instances/{publicId}/config`), plugin bundles, backups and restores are all **pull-based**.
`HeartbeatResponse` only ever *tells* the instance what is pending (`PendingRestore`, `PendingBackup`,
`ConfigRevision`); the instance decides when to fetch and apply. The one inbound call in the whole
design is cloud-initiated adoption (`/api/cloud/link`), used once.

**Consequence for Stage 2:** an MCP tool cannot synchronously "edit page X on site Y". The cloud can
only **enqueue an intent** and report it as done once the instance has pulled it, applied it through
its own code, and reported back. Every Stage 2 tool is therefore *asynchronous by nature* — it returns
"queued as revision/operation N", and a follow-up read tool (or the sync report) shows the outcome.
This is the same shape as backup/restore today, and it must stay that shape.

---

## 2. Two viable architectures

### Architecture A — reuse the profile-rollout / revision channel

Config-shaped changes already have a complete, trusted pipeline:

- Cloud side: `Profile` payload tables (`ProfileSetting`, `ProfileUser`, `ProfileComponent`,
  `ProfilePlugin`, templates), `ProfileService.TouchAsync` bumps `Profile.Revision`, and
  `ProfileService.BuildConfigAsync` emits an `InstanceConfig`.
- Wire: `InstanceConfig` in `CloudProtocol.cs`, carried by `GET /config`, announced via
  `HeartbeatResponse.ConfigRevision`.
- Instance side: `CloudSyncService.ApplyAsync` applies it with the `keep`/`add`/`once` modes, the
  three safety rules (users add-only, nothing deleted, null section untouched), and produces a
  `SyncReport` (`List<SyncItemReport>`).

An MCP tool like `set_instance_setting` or `add_component` would, under A, just **write a profile
payload row and call `TouchAsync`**. The instance picks it up on its next beat.

**What A can do:** settings, users (add-only), components, templates, plugins, mail templates, the AI
transport/instruction flags — i.e. exactly today's `InstanceConfig` surface.

**What A cannot do:** touch **pages, blocks, posts, menus, forms** — none of these are in
`InstanceConfig`, and they are *per-site content*, not fleet configuration. Bolting content into the
profile channel would be a category error: a profile is shared across many instances ("all my Baltic
rentals get this SMTP"), whereas "rewrite the About page" targets **one** site. `Profile.Revision` is
also fleet-wide; using it for one-off single-site edits would thrash every sibling instance's "in
sync" badge.

**Wire cost:** zero new DTOs for the config part (reuses `InstanceConfig`). But note A only reaches
one instance cleanly if that instance has a **profile of its own** — single-instance profiles would
have to become the norm, which is a product change.

### Architecture B — a new pull-based "pending content operations" queue

Mirror exactly how `PendingBackup` / `PendingRestore` work, but for content intents.

- **Wire (bump `CloudProtocol.Version` 14 → 15):** add to `HeartbeatResponse` a
  `List<PendingContentOp>? ContentOps` (or a single `HasContentOps` flag + a dedicated pull endpoint,
  see below). Add DTOs:

  ```csharp
  // A content change the cloud wants this instance to apply. Pulled, applied by the instance's OWN
  // validated write path, reported back — the cloud never sends executable anything, only intent.
  public sealed class PendingContentOp
  {
      public int OpId { get; set; }              // counter, like PendingBackup.RequestId — survives JSON/HTTP/SQLite
      public string Kind { get; set; } = "";     // "page.create" | "page.updateBlocks" | "block.add" | "setting.set" | ...
      public string TargetJson { get; set; } = "{}";  // which page/slug/locale (validated on the instance)
      public string PayloadJson { get; set; } = "{}"; // the intent: title, slug, blocks[], etc.
      public string Mode { get; set; } = "add";  // "add" (add-only) | "overwrite" — never delete via this channel
  }

  public sealed class ContentOpReport
  {
      public int OpId { get; set; }
      public string Outcome { get; set; } = "";  // "applied" | "skipped-exists" | "invalid" | "failed"
      public string? Detail { get; set; }
  }
  ```

  Instance → cloud reports these on the next beat, alongside the existing `SyncReport` (add
  `List<ContentOpReport>? ContentOpReport` to `HeartbeatRequest`, gated by protocol version like
  `SyncRunAt` is).

- **Pull endpoint:** `GET /api/instances/{publicId}/content-ops` returning the queued ops (kept out of
  the heartbeat body if they can be large — same reasoning that keeps plugin bundles out of
  `InstanceConfig`). Small ops could ride inline; a hybrid is fine.

- **Cloud storage:** a `ContentOp` table hanging off the instance row (id, kind, target, payload,
  mode, created, appliedAt, outcome, detail), pruned like `InstanceSyncRun`.

- **Instance side:** a new `ContentSyncService` (sibling of `CloudSyncService`) that, for each op,
  dispatches to the instance's **existing validated writers** — never a new parser:
  - `page.create` / `site.create` → the same path the AI site generator uses
    (`Pages/Admin/Pages/Index.cshtml.cs` `ValidateSite` + `BlockGenerator.ValidateBlocks`), writing
    `Page` + `ContentBlock` rows add-only (slugs deduped, existing pages never overwritten — that rule
    already exists there).
  - `page.updateBlocks` / `block.add` → `BlockGenerator.ValidateBlocks` (the ONE trusted-boundary
    validator), then write `ContentBlock` with `DataJson`.
  - `setting.set` → the same guard as `CloudSyncService.ApplySettingsAsync` (refuse `SettingKeys.Cloud`
    keys).
  - Whole-site import → `ContentTransferService.ImportAsync` (the exact backup/restore path, which
    already preserves `cloud.*` keys).

**What B can do:** everything A can, **plus** pages/blocks/posts/menus — real content — targeted at
**one** instance without disturbing profiles or siblings.

**Wire cost:** one `CloudProtocol.Version` bump and the DTOs above.

---

## 3. Recommendation: **hybrid, B-leaning**

- **Config-shaped changes** (settings, components, templates, plugins, users, AI flags): route through
  **A** where the target legitimately maps to a profile, because that pipeline is already trusted,
  previewable (`CloudSyncService.PreviewAsync`) and reported. Don't rebuild it.
- **Content changes** (pages, blocks, posts, menus) and **single-instance one-offs**: build **B**, the
  per-instance content-op queue, because content is per-site and the profile/revision channel is the
  wrong grain for it.

Reasoning: A is free and safe for what it already models, but forcing content into it corrupts the
"profile = fleet configuration" concept and abuses `Profile.Revision`. B is the honest model for
"change *this* website", and it reuses the instance's own validated writers, so it introduces **no new
trusted boundary** — the cloud still only ships *intent*, never code or unvalidated content. Start B
with the two highest-value ops (`page.create`, `page.updateBlocks`) and grow the `Kind` set; each new
kind is one dispatch arm on the instance and needs no new wire version (same "add a line to the
report" scalability the sync engine already has).

---

## 4. MCP tools Stage 2 would add

All are key-scoped by the caller's `ApiKey` (`ApiKeyService.CanAccess`), and the destructive ones gated
on `CanRestore` — reusing Stage 1's auth. All are **asynchronous** (return "queued as op N"); a read
tool polls the outcome.

| MCP tool | Maps to | Channel | Gate |
| --- | --- | --- | --- |
| `list_pages(instance)` | needs a new read: `GET /api/v1/instances/{id}/pages` fed by a heartbeat-reported page index, OR a pulled snapshot | read | base |
| `get_page(instance, slug)` | same read surface | read | base |
| `create_page(instance, title, slug, blocks[])` | `PendingContentOp{Kind="page.create"}` → instance `BlockGenerator` + add-only writer | B | CanRestore |
| `update_page_blocks(instance, slug, blocks[])` | `PendingContentOp{Kind="page.updateBlocks"}` | B | CanRestore |
| `set_setting(instance, key, value)` | A (profile row + `TouchAsync`) if profile-mapped, else `PendingContentOp{Kind="setting.set"}` | A/B | CanRestore |
| `add_component(instance, type, …)` | A (`ProfileComponent` + `TouchAsync`) | A | CanRestore |
| `generate_site(instance, briefing)` | `PendingContentOp{Kind="site.create"}` → the AI site generator's validated path | B | CanRestore |
| `get_sync_status(instance)` | reads `Instance.LastSyncReportJson` + content-op outcomes | read | base |

Note the read tools need a **content read surface** that today doesn't exist over the wire (the cloud
knows only `PageCount`, not page titles/slugs). Options: (a) instance reports a lightweight page index
on the heartbeat, or (b) a pull the cloud triggers like a mini-backup. This is an open question (§6).

---

## 5. Trust & safety model

Non-negotiables, all consistent with the existing rules:

1. **The cloud ships intent, never execution.** No HTML/CSS/JS or code is *applied* as received. Every
   op is re-validated on the instance by the **same** validator the local AI uses
   (`BlockGenerator.ValidateBlocks`: known block types only, known TEXT fields only, non-empty
   strings, bounded to 12 blocks / 3000 chars). Unknown types/fields are dropped, exactly as today.
2. **Add-only by default; overwrite is explicit and gated.** Mirror `CloudSyncService`: pages are
   add-only (slug dedupe, never overwrite an existing page) unless `Mode="overwrite"` **and** the key
   has `CanRestore`. Destructive content ops are the "restore" of Stage 2 and get the same gate.
3. **Never deletes via this channel.** Like rule 2 of `CloudSyncService` ("nothing is deleted because
   the profile no longer lists it"), a content op can create/update but not delete pages — deletion
   stays a human action on the instance.
4. **Cloud keys are untouchable.** `setting.set` refuses `SettingKeys.Cloud` (the exact guard in
   `CloudSyncService.ApplySettingsAsync`), and any whole-site import goes through
   `ContentTransferService.ImportAsync`, which already preserves `cloud.*` keys so a change can't
   sever or hijack the cloud link.
5. **Users/2FA never ride this channel.** Users stay add-only via the profile path; 2FA is per-user and
   never rolled out (existing rule). A content op cannot create an admin or strip a second factor.
6. **Plugin code stays disabled on arrival.** If a content op ever installs a plugin (it should not —
   use the profile path), `PluginPackager.ImportAsync`'s rule holds: imported plugins are disabled
   until a human enables them.
7. **Approval + protocol gate.** Ops are only offered to `Approved` instances (like `/config`), and
   hidden/refused for instances older than the protocol version that introduced content ops (mirror
   `InstanceService.IsOutdatedProtocol`), so a staggered rollout can't strand an op on a site that
   ignores the field.
8. **Auditable + reversible.** Every op is logged (reuse `InstanceEventKind`), and because a real
   `create_page` is additive and a backup can be taken first, the operator can always recover. Consider
   auto-requesting a backup before any `overwrite` op.

---

## 6. Phased implementation checklist (per the decisions above — everything single-site via B)

Architecture A is **out of scope for the AI** now (fleet-only, operator-driven). All AI change is B.

**Phase 2a — content READ surface (R1, reuse backups):**
- [ ] Cloud-side parser that reads the backup ZIP (`ContentTransferService`'s export format) into a
      page/block/form/post model **without importing** — read-only.
- [ ] MCP read tools over the freshest available (or freshly requested) backup: `list_pages`,
      `get_page` (blocks), `list_posts`, `get_post`, `list_forms`, `get_form`.
- [ ] Decide/refresh policy: use the newest backup, or auto-`request_backup` when stale, and tell the AI
      "reading as of backup taken at T".

**Phase 2b — the content-op channel (the core write path):**
- [ ] Bump `CloudProtocol.Version` 14 → 15; add `PendingContentOp`, `ContentOpReport` + heartbeat fields
      (`PendingContentOps`, `ContentOpReport`), in `MatCMS.Shared/CloudProtocol.cs`.
- [ ] Cloud `ContentOp` table + enqueue/report + `GET /api/instances/{id}/content-ops` + prune + log
      (`InstanceEventKind`).
- [ ] Instance `ContentSyncService` pulling ops and dispatching to its OWN validated writers:
      `BlockGenerator.ValidateBlocks` (blocks/pages), the add-only page writer, the post/form writers, and
      `ContentTransferService.ImportAsync` for whole-fragment imports. Reports each op's outcome back.

**Phase 2c — full op set (MCP tools, all CanRestore-gated for writes):**
- [ ] `create_page`, `update_page_blocks`, `create_post`, `update_post`, `create_form`, `set_setting`
      (single-site → op, never a profile row), `generate_page`, `generate_site` (reuse the instance AI
      generators' validated path).

**Phase 2d — safety:**
- [ ] New profile flag `Profile.BackupBeforeAiChange` (+ migration); when on, the instance takes a backup
      before applying an op. Add-only default + `CanRestore` gate for overwrite remain. Protocol-gate hides
      the channel from instances older than v15; ops offered only to `Approved` instances.

---

## 7. Decisions (locked 2026-09-24) + the one remaining question

Matthias' decisions:

1. **No per-instance profiles.** Giving each website its own profile would contradict the whole point of
   profiles (grouping many instances under shared config). So **Architecture A stays strictly
   fleet-level** — the operator's profile-rollout tool, not an AI-per-site path. **ALL single-site change
   goes through Architecture B**, including single-site settings (`setting.set` is a `PendingContentOp`,
   never a profile row — a profile row would change every sibling in that profile).
2. **Grain: both, and the full surface.** The AI must be able to edit individual **blocks**, create
   **pages**, and create/edit **forms, posts** (menus, media, …) — "essentially everything a backup
   contains". So Stage 2 is not "coarse-only": it is fine-grained ops **plus** whole-page/site generation,
   over the full content surface, all through B.
3. **Backup-before-AI-change is a profile option.** A new profile flag (e.g. `Profile.BackupBeforeAiChange`)
   — the instance already belongs to a (shared) profile, so the flag rides that, no per-site profile
   needed. When on, the instance takes a backup before applying a content op (esp. any overwrite). This is
   the safety switch; `CanRestore` + add-only default remain underneath.
4. **Async is accepted.** Outbound-only stays; tools return "queued as op N", the outcome shows via the
   report. No inbound push for local instances.

**The one open question — the content READ surface.** To edit "block 3 on the About page" the AI must
first SEE the current content. Two shapes (pick one, or a phased mix):
- **(R1) Reuse backups:** the AI reads current content by pulling a **fresh backup** (`request_backup` →
  download → the cloud parses the ZIP and exposes pages/blocks/forms/posts as read tools). Zero new wire
  contract — the whole read surface is "what's in a backup", which is exactly the scope in decision 2. Cost:
  a read means producing+transferring a backup (heavier, ~a minute of latency), and the cloud must parse the
  backup format.
- **(R2) A dedicated content read channel:** new heartbeat/pull endpoints returning page/block/form/post
  JSON directly (lighter per-read, fresher), but a new slice of wire contract to design and version, partly
  duplicating what the backup already serializes.

Recommendation: **start with R1** (reuses the Stage-1 backup path we just built, matches "everything in a
backup" exactly, no new wire contract for reads), add R2 later only if backup-latency per read proves
annoying. This needs Matthias' nod before Phase 2b.
