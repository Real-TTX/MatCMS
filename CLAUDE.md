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

Four things genuinely span both applications, and keeping them in separate repos meant holding them
together by hand:

1. **The wire contract** — now **`src/MatCMS.Shared/CloudProtocol.cs`**, one definition referenced by
   both. It used to be two hand-kept copies with two version constants that had to be bumped
   together; there is nothing left to keep in step. The library is deliberately dependency-free (no
   EF, no ASP.NET): a DTO that needs a package has grown into something that belongs in one of the
   two applications.
2. **The plugin bundle format** — the container shape (manifest entry name, asset folder, the `files/`
   folder for the plugin's further script files and the rule for what path may go in it, size and
   count guards, allowed asset types) is `src/MatCMS.Shared/PluginBundle.cs`. A plugin's further
   script files travel as **one zip entry each under `files/`**, not as a field in the manifest —
   the cloud rewrites `plugin.json` field by field and would flatten a nested object to a string,
   while it copies every other entry byte for byte, so entries survive a cloud round trip untouched.
   The addition is detected by presence and needs no `Format` bump: an older reader skips everything
   outside `assets/` and imports the plugin with its entry file alone, a newer one finds no such
   entries in an old bundle and reads a single-file plugin.
   The manifest additionally carries `Mapping` — a plugin's **folder roles** (folder path → role, see
   `src/MatCMS/Services/PluginMapping.cs`) — as a **string containing JSON, not a nested object**, for
   exactly the reason above. Empty means "never written" and is read as the legacy default (the folder
   `assets` carries the role Assets), so an old bundle and an old backup import unchanged. Note that
   `assets/` remains the fixed folder name **inside the zip and on disk**
   (`appdata/plugin-assets/{Key}/`, served at `/plugin-assets/{Key}/…`); the role only frees the name
   the editor's tree shows for it, which is a label and not a path. The two READERS stay
   separate on purpose: the CMS imports into itself (EF, file system, plugin migration), while the
   cloud only stores and re-packs bundles and edits the manifest **field by field** so properties its
   editor does not surface survive a save. A shared typed manifest class would quietly drop exactly
   those — do not "finish the job" by adding one.
3. **The admin UI** — now **`src/MatCMS.Shared.Web`**, a Razor Class Library holding `site.css`,
   `admin.css`, `admin-list.js`, the CodeMirror and Tabler-Icons bundles, `_IconTrash.cshtml`,
   `_EmptyRow.cshtml` (the "no records" row), `_IconField.cshtml` (the icon picker),
   `_TemplateFiles.cshtml` (the template file editor) and `_ComponentEditor.cshtml` (the whole
   component editor: tabs, fields, field designer and live preview) **with its script,
   `js/component-editor.js`** — what the two applications differ in rides along as `data-`
   attributes on `#field-rows` (field types, the script's own wording, the preview's theme), never
   as a branch on which application is rendering. `_SiteStats.cshtml` renders the visitor statistics
   (tiles, SVG chart, top lists) for the CMS's Admin → Statistik and the cloud's view of a site alike,
   from a `StatsSummary` that `src/MatCMS.Shared/SiteStats.cs` computes once — so the two pages cannot
   disagree about a number (this is why `MatCMS.Shared.Web` references `MatCMS.Shared`).
   `js/file-editor-mobile.js` turns every file tree marked `data-fe` (template editor in CMS and cloud, plugin
   editor) into two steps on a phone — the list, then the open file full screen with back/save in its menu bar,
   wrapped lines and a key bar — as a LAYER over the editors: it only moves what is shown and asks nothing of their
   logic but `window.MatFE.noAutoFocus()` before they focus a just-opened file. Previews write their frame with
   `writeFrame` (detach, set srcdoc, re-insert): a srcdoc change on an attached frame is a history entry, and
   "back" used to step through old previews.
   The shared views take their **wording as strings, not `@T[…]`** — a shared view cannot reference
   either application's `Localizer` type — so each app keeps a thin adapter that looks the keys up
   (`_IconPicker.cshtml`, and the `@{ }` block of the pages using `_ComponentEditor`). The library
   has **no `_ViewImports.cshtml`, so tag helpers are OFF in its views**: `<partial …>` renders
   literally there (it did, once, and the icon field silently stopped being submitted). Use
   `@await Html.PartialAsync(…)`.
   They used to be byte-identical copies kept in step by hand and a `diff`. Both apps reach them at
   `~/_content/MatCMS.Shared.Web/…`; the partial resolves by name as usual. Product-specific assets
   stay put: `cloud.css` in the cloud, the CMS's public-site scripts in the CMS.
4. **The second factor (TOTP)** — the RFC 6238 algorithm is **`src/MatCMS.Shared/TwoFactor.cs`** (secret
   generation, code verification that also returns the matched 30-second step for one-time use, Base32,
   recovery-code hashing), dependency-free like the wire contract, so both apps share ONE implementation
   of security-sensitive crypto rather than two hand-kept copies. The enrolment QR is rendered
   **client-side** from a vendored `qrcode.min.js` in `src/MatCMS.Shared.Web/wwwroot/lib/qrcode/` — the
   cloud ships no image library, so a server-side encoder would have to be added to both. What each app
   keeps to itself: **where the secret lives** (DataProtection-encrypted on its own `User` row; recovery
   codes as SHA-256 hashes), the login challenge (`/login/2fa` + a short-lived encrypted pending cookie),
   the self-service enrolment page (`Admin/Account/TwoFactor`), and the "2FA erforderlich" policy
   (rollable instance setting `security.require2fa` on the CMS; a cloud-wide `CloudSetting` on the cloud,
   enforced by a forced-enrolment gate that also covers the cloud's `/oauth/authorize`). 2FA is per-user
   and **never rides the user rollout** (`ConfigUser` carries no 2FA field; instance users are add-only)
   — and a **restore is add-only for existing accounts**, so neither path can reset a password or strip
   an enrolled admin's second factor. A decrypt failure of the secret (cross-instance restore, lost keys)
   disables only the TOTP branch; the key-independent recovery codes still work, so an admin is never
   hard-locked out. (Known v1 gap: `/api/cloud/link` adoption still authenticates by password alone.)

## Layout

```
MatCMS.slnx                      # dotnet build MatCMS.slnx builds everything (apps, libraries, tests)
.dockerignore                    # applies to BOTH images (context is the repo root)
.github/workflows/
  release.yml  dev.yml           # → matcms        (paths: src/MatCMS/** + src/MatCMS.Shared/**)
  cloud-release.yml  cloud-dev.yml  # → matcms-cloud (paths: src/MatCMS.Cloud/** + …Shared/**)
Directory.Build.props            # promotes MSB9008 (missing ProjectReference) to an error
src/MatCMS.Shared/               # the cloud↔instance contract, referenced by both
src/MatCMS.Shared.Web/           # the shared admin shell (RCL): css, admin-list.js, vendor libs
src/MatCMS.Plugin.Abstractions/  # the API plugins are written against (CMS image only; no ASP.NET, no EF)
tests/MatCMS.Plugins.Tests/      # plugin system tests; tests/Fixtures/RefPlugin = the reference plugin (native)
src/MatCMS/                      # each app keeps its own Dockerfile, compose,
src/MatCMS.Cloud/                #   run-*.ps1, VERSION and appdata/ volume
plugins/                         # plugin SOURCES (plugin.csx + meta.json each) + pack.ps1 → store bundle
```

**MatCMS ships no plugins.** The seeder used to create three (a todo demo, Bewertungen, Google
Bewertungen) and re-wrote the two review plugins' code on every start — a site could never keep its own
fix to one, and an improved version under the same key was silently reverted by the next restart. Their
sources now live in `plugins/` (see its README) and reach a site through the cloud's store or a bundle
import; rows already on existing instances are left untouched. Do not add a plugin back into the
seeder. `plugins/` is not in any workflow's `paths:` — it is not compiled into an image.

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

## The plugin system

Design, status and the traps found while building it: **`docs/plugin-system-v2.md`** — read it before touching
`Services/PluginRuntime.cs`, `Services/Plugins/` or `Services/PluginPackager.cs`.

**One plugin concept, two kinds, one API.** A plugin is a *script* (C#, `.csx`, editable, source in the database) or
*native* (compiled DLLs under `appdata/plugins/{key}/{version}/src/`, not editable). The admin treats them the same
(load/unload, export/import, configuration); both are written against `MatCMS.Plugin.Abstractions`: a script gets the
`IPluginBuilder` as the global `Plugin`, a native plugin implements `IPlugin.Configure`. The older `PluginContext`
members (`AddAdminMenu`, `AddBlock`, …) stay for the plugins already written and register into the same registry.
**Never give the new API a name an old script can see twice**: scripts import `MatCMS.Services`, which already has
`PluginRequest`, so the new request type is `RequestData`.

- **The registry is one slice per plugin** (`PluginRegistry.Begin/Publish/Remove`): a load builds a private slice and
  publishes it in one step, so ONE plugin can be reloaded or unloaded without touching the others. A load that throws
  publishes nothing; a failed reload keeps what ran before — **except** a withdrawn `plugins.allowNative`, which unloads
  ("off" must mean off). Do not go back to "reset everything and re-run all".
- **Plugin routes are not ASP.NET endpoints.** ASP.NET cannot remove an endpoint, so ONE dynamic endpoint
  (`PluginRouteTransformer`, order -100, in front of the CMS's `/{slug?}`) asks the registry. Public routes need a fixed
  first segment and may not claim `PluginRoutePattern.Reserved` (admin, api, login, uploads, …). Admin pages live at
  `/admin/p/{key}/…`.
- **Plugin bundles: scripts keep Format 1 exactly** — older instances and the cloud's store editor read them, and an
  older reader IGNORES `Format`, so a new shape would import there as an empty plugin. Only native bundles are new
  (`Kind: native`, `src/`, `Entry`, `Sha256`; `Code` carries a line that tells an older reader why it cannot run).
  New manifest fields are strings (the cloud rewrites `plugin.json` field by field). The container shape is in
  `MatCMS.Shared/PluginBundle.cs`; sizes are counted while reading, not from the zip's length field.
- **Native plugins are compiled code from outside running with every right of the process — there is no sandbox.**
  `plugins.allowNative` (default OFF, operator-set, live) is the gate; an import, a cloud rollout, a backup or an MCP
  `set_setting` can bring the files, never the permission — it is in `SettingKeys.Protected` (with the cloud link keys),
  which the restore, `CloudSyncService.ApplySettingsAsync` and `ContentOpApplier` all honour. A new outside path that
  writes settings must honour it too. Imported plugins stay disabled, as always. The DLL's SHA-256 is checked at import
  AND at every load. Backups carry native plugins with their files (`plugins-native/{key}/{version}/`), put down BEFORE
  the plugins are started.
- **Unloading is real but depends on the plugin.** `NativePluginHost` releases the `AssemblyLoadContext` and checks with a
  `WeakReference`; a plugin that cannot be freed shows "Neustart nötig" instead of leaking silently. Known holders:
  reflection-based `System.Text.Json` over a plugin's own types (use a source-generated context or `JsonNode`) and EF —
  EF's static provider cache (cleared by `EfInternals`, the one reflection into EF) AND the provider shared with the
  host's own contexts (a plugin context needs its OWN service provider). `PluginSystemTests` unloads a real DLL with an
  EF context; if it fails after an EF update, that is the cause.
- **Debug builds keep every temporary alive until the method ends**, which alone keeps a load context from being
  collected. Code and tests that check unloading hold nothing of the plugin in their own frame (`NoInlining` helpers).
- **Tests: `dotnet test tests/MatCMS.Plugins.Tests`.** They run on in-memory SQLite and temporary folders and never touch
  an `appdata`. The conformance test runs the SAME reference plugin as script and as native and compares registrations and
  answers — a feature only one kind has is a bug in the API, not a difference. Practical checks in the real image: own
  container, own volume, a port that is not 9101/9100, deleted afterwards.
- **`docker build -f src/MatCMS/Dockerfile .` needs every project the app references** (two COPY lines each, and the
  `paths:` filters of `dev.yml`/`release.yml`) — `MatCMS.Plugin.Abstractions` is in both. The cloud does not reference it.
- **"MatCMS ships no plugins" still holds for script plugins and the seeder.** What does ship is the *built-in* plugin
  (`Kind = builtin`, `Services/Plugins/BuiltInPlugins.cs`): compiled into the image, written against the same API, enabled on a
  new site, can be switched off, never deleted, exported or overwritten (a bundle or cloud rollout with its key is refused).
  Today that is **the blog** (`src/MatCMS/Plugin/Blog/`, namespace `MatCMS.Plugin.Blog`): it *adopts* the existing `Posts`
  table (nothing moved), registers `/blog/{slug}`, the `posts` block, its admin section, the dashboard tile, the sitemap
  entries and the cloud operation `post.create`. The core no longer knows posts except for the backup, which reads the table
  through `BlogStore.Table(db)` — so posts are in every backup whether or not the blog is on.
- **Namespaces: `MatCMS.Plugin` is the API** (assembly `MatCMS.Plugin.Abstractions`), **`MatCMS.Plugin.{Name}` is a plugin's own
  code** (`MatCMS.Plugin.Blog`, later `MatCMS.Plugin.Shop`). Consequence: the entity is `MatCMS.Models.Plugin` and a bare
  `Plugin` in host code means the NAMESPACE — write `Models.Plugin` (the compiler says CS0118 where you forgot).
- **What the API offers** (design: `docs/plugin-system-v2.md` §3): `Route`, `Admin.Section/Page/Tile/RecentlyEdited/Attention`,
  `Block`, `Settings` (declared; the host draws the form, validates, encrypts secrets), `Data.Table` (declared; reconciled on load —
  never drops, renames or retypes; backed up, restored and cleaned up by the host), `Lifecycle` (OnInstall/OnUpdate/OnLoad), `On` +
  `ctx.Events` (name + JSON payload, never shared types), `Job`, `Strings` + `ctx.T` (and `strings/{culture}.json` files), `Sitemap`,
  `MediaReferences`, `Operation`. Screens are **component trees** (`Ui.*`, `MatCMS.Plugin` records), drawn by
  `MatCMS.Shared.Web/Ui/UiRenderer.cs` — the one place that knows classes and `data-ui-*` hooks; the rich fields' behaviour is
  `MatCMS.Shared.Web/wwwroot/js/ui-fields.js`. The renderer encodes everything, limits link targets, and adds the antiforgery token.
- **Data layer 1 safety rules** (`Services/Plugins/PluginData*.cs`): table/column names come from the plugin's *declaration*
  (validated, then looked up — never from a request), values are parameters, a plugin's tables are `p_{key}_{name}` and
  `__plugin_tables` records the owner; a restore recomputes the destination and never trusts a table name from the backup; only a
  built-in plugin may `Adopt` an existing table. The cleanup reads the text of ALL plugin tables, loaded or not.
- **A plugin's pages are inside the admin's own chrome:** the sidebar is the `AdminNav` registry (core entries and plugin entries
  ordered together by group + order, optional badge), plugin settings live at `/admin/p/{key}/_settings`.
- **The script editor** (`Pages/Admin/Plugins/Edit.cshtml` + `wwwroot/js/plugin-code-editor.js`) puts CodeMirror (C# mode, vendored with the other modes in
  `Shared.Web/wwwroot/lib/codemirror`) in front of its file textarea and takes over the textarea's `.value`/`focus`, so the page's own script is unchanged. It
  completes the plugin API from `/admin/plugins/edit/{id}?handler=Catalog` (`PluginApiCatalog`: reflection + the API's XML docs — never hand-written) and has
  "Prüfen" (`PluginRunner.Check`: compiles exactly as the loader does, never runs; the include prologue is taken out of the line numbers). New API surface needs
  nothing here: the catalogue follows the assembly. Editor blueprints (`PluginBlueprints`) are loaded for real by `BlueprintTests`.
- **Native bundles in the cloud are read-only:** the store and profile editors show entry, API, checksum and the `src/` files instead of a code editor, and
  `Repack` leaves a native bundle's `Code` (the note for older instances) alone (`MatCMS.Cloud/Services/NativeBundleInfo.cs`). The cloud still never loads one.
- **The plugin machinery tests don't start the built-ins** (`new Harness()`); the blog tests do (`new Harness(builtIns: true)`).
  `BlueprintTests` load every editor blueprint for real. `AppFactory` + `BlogEndToEndTests` start the whole application in memory over a temp folder (own database, own keys) and drive the blog through HTTP: login, antiforgery, the public pages, switching the plugin off and on.

## Conventions

Code, identifiers and comments in **English**; user-facing strings in **German**, via `@T["key"]`
against `Resources/<culture>.json`. Comments explain the *why* and the trap, not the *what*.

**Two language lists, and they are not the same thing.** `Localizer.AdminUiCultures` (de/en) is what
the back-office switcher offers and what `/set-language` accepts — the language of the *admin*.
`Localizer.SupportedCultures` is the *content* universe: the public `/{culture}/…` routes, the
`i18n.languages` setting, every page and menu locale. They used to be one list, so "offer fewer admin
languages" would have taken a live site's `/hr/` and `/sk/` pages off the air. Narrow the first one,
never the second. `Resources/hr.json`/`sk.json` stay on disk (19 of ~1180 keys) so re-offering a
language is a finished translation plus one entry in `AdminUiCultures`; `tools/i18n-audit.js` audits
exactly the languages that are offered.

Razor Pages use the `Index/Create/Edit` triple with `OnGetAsync` / `OnPost<Action>Async` and PRG via
`TempData["Flash"]` / `TempData["FlashError"]`.

The cloud's admin must look and behave exactly like the CMS's — the class-by-class table for that is
in `src/MatCMS.Cloud/CLAUDE.md`. Never invent a parallel CSS class for something the admin already
ships.

**Colours in the back office only through variables** — light/dark/system is a per-user choice (`User.Theme`
in both apps, account page → Darstellung) that the admin layouts write into `<html data-theme>`. The variables
(`--surface`, `--surface-2`, `--sidebar`, `--hover`, `--border-strong`, `--text-2`, `--text-muted`, `--on-ink`,
the `--ok/err/info/warn-*` tints, plus site.css's `--black/--ink/--line/--bg`) are defined at the top of
`admin.css`, with the dark set written twice (`[data-theme=dark]` and `[data-theme=system]` under
`prefers-color-scheme`). A hard-coded `#fff` or grey in admin CSS is a bug that shows up as a white block in
dark mode. The public site never carries `data-theme`; previews of site content (iframes, mail preview, the 2FA
QR code) stay white on purpose. The back office is also **emoji-free**: icons are Tabler classes (`ti ti-…`),
plugin menu icons are Tabler names (`ti-star`).

**No browser dialogs.** `window.confirm/alert/prompt` are never used — `MatDialog.confirm/alert/prompt` from
`MatCMS.Shared.Web/wwwroot/js/mat-dialogs.js` (loaded in the <head> of every admin layout, button wording from
`<html data-dlg-ok/-cancel>`) returns a Promise. A form asks with `data-confirm="…"` (a `.btn-danger` submit or
`data-confirm-danger` makes OK red); never `onsubmit="return confirm(…)"`. Plugin action buttons (`ActionButton(confirm:)`)
render `data-confirm` too.
