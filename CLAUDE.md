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

Both: Razor Pages, SQLite via EF Core with **EF migrations** applied at startup, cookie auth,
Docker-first, no SPA framework and no npm build step. A model change means
`dotnet ef migrations add <Name>` — never `docker compose down -v`, which on the CMS would throw away
the customer's content and on the cloud every instance link. Databases created by the older
`EnsureCreated()` are **baselined** on first start (history table written, the initial migration
recorded without running it) and upgrade normally from there.

## Why they live together

Three things genuinely span both applications, and keeping them in separate repos meant holding them
together by hand:

1. **The wire contract** — now **`src/MatCMS.Shared/CloudProtocol.cs`**, one definition referenced by
   both. It used to be two hand-kept copies with two version constants that had to be bumped
   together; there is nothing left to keep in step. The library is deliberately dependency-free (no
   EF, no ASP.NET): a DTO that needs a package has grown into something that belongs in one of the
   two applications.
2. **The plugin bundle format** — the container shape (manifest entry name, asset folder, size and
   count guards, allowed asset types) is `src/MatCMS.Shared/PluginBundle.cs`. The two READERS stay
   separate on purpose: the CMS imports into itself (EF, file system, plugin migration), while the
   cloud only stores and re-packs bundles and edits the manifest **field by field** so properties its
   editor does not surface survive a save. A shared typed manifest class would quietly drop exactly
   those — do not "finish the job" by adding one.
3. **The admin UI** — now **`src/MatCMS.Shared.Web`**, a Razor Class Library holding `site.css`,
   `admin.css`, `admin-list.js`, the CodeMirror and Tabler-Icons bundles, `_IconTrash.cshtml`,
   `_EmptyRow.cshtml` (the "no records" row) and `_TemplateFiles.cshtml` (the template file editor).
   They used to be byte-identical copies kept in step by hand and a `diff`. Both apps reach them at
   `~/_content/MatCMS.Shared.Web/…`; the partial resolves by name as usual. Product-specific assets
   stay put: `cloud.css` in the cloud, the CMS's public-site scripts in the CMS.

## Layout

```
MatCMS.slnx                      # dotnet build MatCMS.slnx builds all four
.dockerignore                    # applies to BOTH images (context is the repo root)
.github/workflows/
  release.yml  dev.yml           # → matcms        (paths: src/MatCMS/** + src/MatCMS.Shared/**)
  cloud-release.yml  cloud-dev.yml  # → matcms-cloud (paths: src/MatCMS.Cloud/** + …Shared/**)
Directory.Build.props            # promotes MSB9008 (missing ProjectReference) to an error
src/MatCMS.Shared/               # the cloud↔instance contract, referenced by both
src/MatCMS.Shared.Web/           # the shared admin shell (RCL): css, admin-list.js, vendor libs
src/MatCMS/                      # each app keeps its own Dockerfile, compose,
src/MatCMS.Cloud/                #   run-*.ps1, VERSION and appdata/ volume
```

`docker compose up -d --build` inside either app folder still works — but the **build context is the
repo root** (`context: ../..` + `dockerfile: src/<app>/Dockerfile`), because the image needs the two
shared projects next to the app. That is also why the `.dockerignore` lives at the root: a
per-project one no longer applies, and without it every build would upload `.git`, both `appdata`
volumes and all `bin/obj` trees as context.

The **`paths:` filters on the workflows** keep the monorepo cheap: a CMS change never rebuilds the
cloud image, and vice versa. `src/MatCMS.Shared/**` and `src/MatCMS.Shared.Web/**` are listed in
**all four** — both are compiled into both images, so a change there must rebuild both.

**A missing `ProjectReference` is only an MSBuild WARNING (MSB9008).** A Dockerfile that forgot to
copy `src/MatCMS.Shared.Web` therefore built "successfully" and produced an image whose admin had no
CSS at all — the app ran, every page was unstyled. `Directory.Build.props` promotes that warning to
an error; do not remove it. When a project is added, both `COPY` lines (csproj first for the restore
layer, then the folder) go into **both** Dockerfiles.

## Conventions

Code, identifiers and comments in **English**; user-facing strings in **German**, via `@T["key"]`
against `Resources/<culture>.json`. Comments explain the *why* and the trap, not the *what*.
Razor Pages use the `Index/Create/Edit` triple with `OnGetAsync` / `OnPost<Action>Async` and PRG via
`TempData["Flash"]` / `TempData["FlashError"]`.

The cloud's admin must look and behave exactly like the CMS's — the class-by-class table for that is
in `src/MatCMS.Cloud/CLAUDE.md`. Never invent a parallel CSS class for something the admin already
ships.
