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

1. **The wire contract** — now **`src/MatCMS.Shared/CloudProtocol.cs`**, one definition referenced by
   both. It used to be two hand-kept copies with two version constants that had to be bumped
   together; there is nothing left to keep in step. The library is deliberately dependency-free (no
   EF, no ASP.NET): a DTO that needs a package has grown into something that belongs in one of the
   two applications.
2. **The plugin bundle format** — `PluginPackager` on the instance, `StoreBundle` in the cloud. Still
   two implementations; the format is the contract.
3. **The admin UI.** `site.css`, `admin.css`, `admin-list.js`, the CodeMirror bundle and
   `_IconTrash.cshtml` are **byte-identical copies**. Check with `diff` after touching any of them
   and change both. Moving these into a Razor Class Library is the remaining step — the CMS must keep
   working with no cloud in sight, so its assets cannot simply move into a cloud-shaped library.

## Layout

```
MatCMS.slnx                      # dotnet build MatCMS.slnx builds all three
.dockerignore                    # applies to BOTH images (context is the repo root)
.github/workflows/
  release.yml  dev.yml           # → matcms        (paths: src/MatCMS/** + src/MatCMS.Shared/**)
  cloud-release.yml  cloud-dev.yml  # → matcms-cloud (paths: src/MatCMS.Cloud/** + …Shared/**)
src/MatCMS.Shared/               # the cloud↔instance contract, referenced by both
src/MatCMS/                      # each app keeps its own Dockerfile, compose,
src/MatCMS.Cloud/                #   run-*.ps1, VERSION and appdata/ volume
```

`docker compose up -d --build` inside either app folder still works — but the **build context is the
repo root** (`context: ../..` + `dockerfile: src/<app>/Dockerfile`), because the image needs
`src/MatCMS.Shared` next to the project. That is also why the `.dockerignore` lives at the root: a
per-project one no longer applies, and without it every build would upload `.git`, both `appdata`
volumes and all `bin/obj` trees as context.

The **`paths:` filters on the workflows** keep the monorepo cheap: a CMS change never rebuilds the
cloud image, and vice versa. `src/MatCMS.Shared/**` is listed in **all four** — the contract is
compiled into both images, so a change there must rebuild both.

## Conventions

Code, identifiers and comments in **English**; user-facing strings in **German**, via `@T["key"]`
against `Resources/<culture>.json`. Comments explain the *why* and the trap, not the *what*.
Razor Pages use the `Index/Create/Edit` triple with `OnGetAsync` / `OnPost<Action>Async` and PRG via
`TempData["Flash"]` / `TempData["FlashError"]`.

The cloud's admin must look and behave exactly like the CMS's — the class-by-class table for that is
in `src/MatCMS.Cloud/CLAUDE.md`. Never invent a parallel CSS class for something the admin already
ships.
