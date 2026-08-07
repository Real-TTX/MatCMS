# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code — update it whenever
architecture, persistence, the cloud↔instance contract or major data flow change.

## What this repo is

A **monorepo** with two ASP.NET Core 10 applications that share a stack and are developed in
lockstep:

- **`src/MatCMS`** — the CMS. Block-based page editor, templates, plugins, forms, i18n.
  Image `ghcr.io/real-ttx/matcms`, host port 9101. See `src/MatCMS/README.md`.
- **`src/MatCMS.Cloud`** — the control plane for connected MatCMS instances: update watching,
  notifications, profile configuration rollout. Image `ghcr.io/real-ttx/matcms-cloud`, host port
  9100. **Its own `CLAUDE.md` lives in `src/MatCMS.Cloud/` and is the detailed one** — read it before
  touching anything cloud-side.

Both: Razor Pages, SQLite via EF Core with `EnsureCreated()` (no migrations — a model change needs
`docker compose down -v`), cookie auth, Docker-first, no SPA framework and no npm build step.

## Why they live together

Three things genuinely span both applications, and keeping them in separate repos meant holding them
together by hand:

1. **The wire contract.** `src/MatCMS/Services/CloudProtocol.cs` and
   `src/MatCMS.Cloud/Services/InstanceProtocol.cs` are the same DTOs, typed twice. They MUST change
   together, and `CloudProtocol.Version` / `InstanceService.CurrentProtocolVersion` must be bumped
   together. In one repo that is one commit.
2. **The plugin bundle format** — `PluginPackager` on the instance, `StoreBundle` in the cloud.
3. **The admin UI.** `site.css`, `admin.css`, `admin-list.js`, the CodeMirror bundle and
   `_IconTrash.cshtml` are **byte-identical copies**. Check with `diff` after touching any of them
   and change both.

**The next structural step is `src/MatCMS.Shared`** (a Razor Class Library) holding exactly those
three, so they stop being copies. Until that exists, the copies are the contract.

## Layout

```
MatCMS.slnx                      # dotnet build MatCMS.slnx builds both
.github/workflows/
  release.yml  dev.yml           # → matcms          (paths: src/MatCMS/**)
  cloud-release.yml  cloud-dev.yml  # → matcms-cloud (paths: src/MatCMS.Cloud/**)
src/MatCMS/                      # each project keeps its own Dockerfile, compose,
src/MatCMS.Cloud/                #   run-*.ps1, VERSION and appdata/ volume
```

Each project is self-contained: `docker compose up -d --build` inside its folder still works exactly
as before the merge, because the compose context is the project folder. The **`paths:` filters on the
workflows** are what keeps the merge free: a CMS change never rebuilds the cloud image, and vice
versa.

## Conventions

Code, identifiers and comments in **English**; user-facing strings in **German**, via `@T["key"]`
against `Resources/<culture>.json`. Comments explain the *why* and the trap, not the *what*.
Razor Pages use the `Index/Create/Edit` triple with `OnGetAsync` / `OnPost<Action>Async` and PRG via
`TempData["Flash"]` / `TempData["FlashError"]`.

The cloud's admin must look and behave exactly like the CMS's — the class-by-class table for that is
in `src/MatCMS.Cloud/CLAUDE.md`. Never invent a parallel CSS class for something the admin already
ships.
