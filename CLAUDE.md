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
- **Nothing long-lived in the host may hold a plugin's object strongly** — a delegate is enough to keep the whole load context alive, and `gcroot`
  does not show that last step (a delegate's hold on its collectible method is runtime-internal). Two holders found with the forum: `AdminNav`'s badge
  cache kept the section (with its badge provider) in a singleton — now a `ConditionalWeakTable` keyed by the section; and the route match in
  `HttpContext.Items`, which `PluginPage` now removes as soon as it read it. `PluginHttpUnloadTests`/`ForumUnloadTests` serve pages, open the admin
  and switch the plugin off through Admin → Plugins (a request of its own, as on a site). In a test, unloading in the test's own code right after a
  request, or while holding an undisposed `HttpResponseMessage` of a plugin route, keeps the request alive and looks like a leak.
  A plugin's redirect cannot carry a `#fragment` (`PluginResults.SafeLocal` turns it into "/") — the forum jumps to a post with `?p=` instead.
- **The forum** (`plugins/forum`, native, key `forum`, see its README) is the store's first larger native plugin: boards with rights, threads,
  BBCode (`ForumMarkup`: encode first, only its own tags out), members = the site's accounts, moderation by a member role. `ForumTests` load its
  built DLL into the whole application.
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
  Three exist: **the AI bot** (`src/MatCMS/Plugin/Bot/`, off on a new site — see "The AI bot" below), **the shop** (`src/MatCMS/Plugin/Shop/`, namespace `MatCMS.Plugin.Shop`, **off on a new site** — `BuiltInPlugin.EnabledOnNewSite` — see `docs/shop-plugin.md`
  and "The shop" below) and **the blog** (`src/MatCMS/Plugin/Blog/`, namespace `MatCMS.Plugin.Blog`): it *adopts* the existing `Posts`
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
- **Customer accounts, visitor routes and the rest of what a shop needed** (all in `MatCMS.Plugin.Abstractions`, implemented in `Services/Plugins/PluginAccessParts.cs` and
  `PluginRequestContext.cs`): `ctx.Member` (the signed-in customer of the guest area — read from the DATABASE on every request, so a deleted or switched-off account stops being
  "the member" at once, cookie or not), `ctx.Members` (find / create / sign in / change password / delete — the account itself is `SiteMember`, now with `Email`, confirmation and
  reset tokens as SHA-256 hashes; the pages `/registrieren`, `/anmelden/vergessen`, `/anmelden/zuruecksetzen`, `/anmelden/bestaetigen`; self-registration is the setting
  `members.selfRegistration`, off by default, and does NOT bind a plugin that creates an account for its own purpose), `ctx.Mail` + `Plugin.MailTemplate` (templates land in
  Admin → Mailvorlagen, rows are never overwritten; a plugin sends only keys that start with its own), `ctx.Files` (`appdata/plugin-files/{key}`, validated names, in the backup
  on the plugin-uploads switch, deleted with the plugin), `IDataTable.IncrementAsync` + `NextNumberAsync` (atomic counters, gapless sequences inside a transaction).
  `PublicRouteOptions`: `RequireMember`/`RequiredRole` (sign-in redirect, handler never runs), `Antiforgery` (checked BEFORE the handler, answer `no-store`), `Webhook` (POST only,
  raw body ≤ 1 MB in `RequestData.Body`, own looser limit), `PostsPerMinute` (default 20 per address and plugin). A plugin's cookies are `{key}.`-prefixed on the wire,
  `HttpOnly`, `SameSite=Lax`. `PluginResult.RedirectExternal` accepts only an absolute https address. Host events for accounts: `host.member.registered/confirmed/deleted/export/erase`.
- **Block fields of a plugin**: besides text/select/image/list a field can be `MultiSelect`, can appear only while another field has a value (`ShowWhenField/Value`) and can draw its choices from
  a list the plugin registered with `IPluginBuilder.OptionsSource` (the host asks when the editor opens, via `PluginContributions.OptionsAsync` and `BlockSchema.LoadSourcesAsync`; the
  field's source is stored host-side as `plugin:{key}:{name}`). A block's texts (name, labels, option labels) are looked up in the plugin's own string tables: `Localizer` falls back to
  `PluginRegistry.TextOf` for keys the core does not have — a plugin key therefore contains a dot, and a plugin block that shows raw keys in the editor means this lookup broke.
- **A plugin in the site's navigation and in the back office's sidebar** (all general API, the shop is the first user): `IAdminSection.Link(label, path, icon, heading, badge, also…)` gives a section a MENU OF ITS OWN (the plugin decides by declaring links): the main sidebar keeps ONE line for it (with a chevron),
  and opening it REPLACES the whole sidebar with the section's menu — "Zurück" to the main menu, the section's name, its links with sub-headings — on every page of that plugin (`AdminNav.SubmenuAsync`, `_AdminNavSub.cshtml`, chosen in `_AdminLayout`).
  A section with links opens on its FIRST registered page (the shop: `dashboard`; `PluginRegistry.MatchAdminPage`), the bare section address counts as that link being active. An accordion under the line and a separate block with
  separators were both tried and rejected. No tab bar above such pages; `_settings?group=…` shows one group of the plugin's settings with a sub-menu (`Ui.Split` + `Ui.NavItem`; a group is named on its first setting and holds the ones after it, and a group
  form posts and saves only its own fields). `Ui.Columns` is the two-column editor layout. A `Ui.Table` with search, paging or a `Filter` is a LIST in the admin's own shape: the framed toolbar (search + filter select, rows name their values with `Tags`) sits in a page-head ABOVE the table, `Below` holds the "New …" button under it (`.list-actions`) — never a hand-made button row or a loose search box. Filtering a list by status is `Ui.Tabs`, not a row of buttons.
- **Template parts** (`IPluginBuilder.TemplatePart(TemplatePartSpec)`, `IPluginContext.RenderPart`): a plugin ships HTML files with a built-in default that the ACTIVE template may replace — the template
  editor lists them next to `body.html` (`{key}-{name}.html`, stored in `Template.PartsJson` under the dotted key `{key}.{name}`; blank or absent = the plugin's default). `Services/Plugins/PartRenderer.cs` fills them
  (`{{x}}` encoded unless declared HTML, `{{#if x}}`/`{{#unless x}}`, a value is never re-read), a plugin can only render its OWN parts. The template editor keeps stored keys of parts whose plugin is off (and the cloud's
  two template editors keep every dotted key they do not list). The shop's: `shop-card.html`, `shop-product.html`, `shop-catalog.html` (`ShopParts.cs`) — the cart form (`form_html`) is always the shop's own. `IPluginBuilder.Menu(provider)` adds entries to the PUBLIC menus (`header`, `footer`, `toolbar`, custom keys) — asked per page view with the request's context (member, the plugin's cookies),
  merged by `SiteContext.MenuItems` after the site's own items (negative ids, `MenuItem.Badge` for a counter, links only local or http(s), 400 ms budget, a failing plugin is skipped); `PluginRequestContext.FromBlockAsync` gives blocks the
  member, the cookies and the client address (pages are drawn per request, nothing is cached for everybody). The shop's own blocks: `shop_categories` (chips, list, tiles with picture; top level or the subcategories of one), `shop_search`, `shop_cart`, `shop_account`, the carousel layout and the "Meistverkauft" source of `shop_products`. The account IS the CMS's members area (`SiteMember`, `/anmelden`, roles, members-only pages).
- **Menu elements, link targets, dashboard cards** (plugin API, the shop and the blog are the users): `IPluginBuilder.MenuElement(spec, provider)` offers a piece for the site's menus that is more than a link — the shop's cart with its count, "Anmelden"/"Mein Konto", a search box,
  "Shop" with its categories as a dropdown. It is stored as an ORDINARY menu item with the address `plugin:{plugin}.{element}` (Admin → Menü; the link picker of the menu form offers "Plugin-Elemente"), so an editor moves, renames and deletes it like any other;
  `PluginContributions.ResolveElementsAsync` (called by `SiteContext.MenuItems`) turns it into what THIS visitor gets (label, address, counter, children, `MenuEntry.Html`) — an element that is gone, says nothing, fails or sends a bad address is dropped WITH what hangs under it (also a child listed before its parent),
  never drawn as "plugin:…"; in the back office such items are left out. `MenuElementSpec.DefaultMenu` makes the host add the element ONCE when the plugin is first loaded (`PluginMenuElements.SeedAsync`, for every content language, marker `menu.seeded.{plugin}.{element}` so deleting sticks and a reload never puts it back).
  The look is the template's: a link-like element goes through the template's menu markup (`{{icon}}`, `{{badge}}`), an element with `Html` (the search box) is drawn raw — build it with `ctx.RenderPart` so it is a template part (`shop-search.html`, default look = `.mat-search` in site.css, following the template's accent). `MenuItem.Element`/`Site.ElementKeys` put the shown elements on the page as `<body data-els>`,
  which lets the shop's small in-page bar step aside only while the cart element really is there. `IPluginBuilder.LinkTargets(group, provider)` adds a group to the link picker (`/admin/api/link-targets`, used by every URL field: menus, buttons, links in blocks): the shop offers its pages, categories (with path) and products, the blog its overview (`/blog`, new) and posts; at most 300 per group, only local or http(s) addresses.
  `Admin.DashboardCard(provider)` gives a plugin a card of its own on the dashboard ("Shop · Neueste Bestellungen") — `RecentlyEdited` is only for things an editor edits, orders are not that.
- **Menus are multi-level** (up to `MenuTree.MaxLevels` = 4): the renderer (`.mat-hassub > .mat-sub`, flyouts beside the dropdown, indented on a phone) always handled any depth; the editor now offers every item as a parent (`MenuTree.ParentChoices`: not itself or what hangs below it, and only where its own subtree still fits), and a plugin's `MenuEntry.Children` nest as deep as a menu goes (the shop's "Kategorien" element is the category tree, category > subcategory > …). The link picker (`openLinkPicker` in `media-picker.js`, `.lp-pop`) is a compact drop-down under the address field (`opts.anchor`): a search box on top, the targets grouped by where they come from (a sticky heading per group), one line each (title, address); arrow keys, Enter, Escape; its words follow `<html lang>`. It was a modal with a table once and was rejected — keep it a drop-down.
- **Menu entries have a visibility** (`MenuItem.Visibility`: "" everybody, "in" signed-in members, "out" visitors who are not signed in; migration `MenuItemVisibility`, backup field `Visibility`): `SiteContext.MenuItems` filters per request through `MenuVisibility.Filter` (pure; a hidden entry takes ALL its sub-entries along, a child may be narrower than its parent), the back office shows everything. So "Konto" (for members) with "Bestellungen" (/konto/bestellungen) and "Abmelden" (/anmelden, which shows the sign-out button to a signed-in member) below it and "Anmelden" (for guests) is plain menu content, no plugin element needed.
- **Setting kinds `Image` and `Color`** (`SettingKind`): a media-library picture (stored as local path or https address) and a `#rrggbb` colour (an empty post keeps the default). `IPluginSite.LoadImageAsync(url, maxWidth)` hands a plugin a library picture as a JPEG (transparency on white; never SVG, never from outside `/uploads/`) — what a PDF needs.
- **The dashboard is configurable per user** (`User.DashboardJson`; an admin may save his layout as everybody's default in the setting `dashboard.layout`): a layout is only an ORDER plus a set of HIDDEN ids (`Services/DashboardLayout.cs`, pure functions), never content, so a widget that
  disappears (a plugin off) is skipped and a new one (a plugin card) is added at the end, shown. The page is a list of widgets (`tiles`, `attention`, `website`, `recent`, `submissions`, `card:{plugin}`, `errors`, `quickstart`), each its own partial (`Pages/Admin/_Dash*.cshtml`),
  drawn in one `.dash-stack` column with one gap (wide widgets alone in a row, cards sharing a grid row); "Anpassen" opens the panel (`wwwroot/js/dash-customize.js`: tick, move up/down, posts the rows as they stand). New dashboard content = a new widget in `IndexModel.OnGetAsync`'s default list, not a
  hand-placed block in the view. The cloud's own dashboard is a separate page and not configurable yet.
- **A layout places a plugin element itself** with `{{element:shop.search}}` (a menu element drawn where the token stands, independent of any menu; what is placed is left out of `{{toolbar}}`/the floating strip); the template editor offers every loaded element as a placeholder chip and "Standard einfügen" (the starter body) now carries `{{toolbar}}` in a `.header-icons` div.
- **SQLite under concurrency — two traps found by the shop's race tests.** (1) `UPDATE … RETURNING` through Microsoft.Data.Sqlite: the driver waits for a busy database only on a
  statement's FIRST step, the commit of a RETURNING statement happens on a later one and failed with "database is locked" — counters are `UPDATE` + `SELECT` inside a
  `Serializable` (= `BEGIN IMMEDIATE`) transaction. Plugin transactions (`InTransactionAsync`) are `Serializable` too, and a transaction that reads before it writes should start with a
  write (`ShopStore.LockForWriteAsync`) so it queues instead of being refused. (2) `UseStatusCodePagesWithReExecute` re-runs every EMPTY error response as the status page; for a POST
  that ends as a bare 400 — `PluginPage` therefore always gives an error answer to a POST a short body.
- **The plugin machinery tests don't start the built-ins** (`new Harness()`); the blog tests do (`new Harness(builtIns: true)`).
  `BlueprintTests` load every editor blueprint for real. `AppFactory` + `BlogEndToEndTests` start the whole application in memory over a temp folder (own database, own keys) and drive the blog through HTTP: login, antiforgery, the public pages, switching the plugin off and on.

## The shop

A built-in plugin (off until switched on), written against the plugin API and nothing else — the core does not know it. Read `docs/shop-plugin.md` before touching
`src/MatCMS/Plugin/Shop/`. The rules that are easy to break:

- **Money is whole cents, prices are GROSS, and the server prices everything** (`ShopOrders.PriceAsync`); nothing a form sends as a price or total is read. An order and its lines
  are a **snapshot** (title, price, tax) — later product edits never touch them; the invoice is written ONCE when the order is paid and stored as HTML.
- **Every status change goes through `ShopOrders`** (`OrderStatus.CanMove` is the only list of allowed moves; `ShopStore.SetStatusAsync` is called by nobody else). Each move does
  its whole job in one transaction — stock, invoice number, downloads, history — and sends mails/events after the commit. Paying is idempotent; a payment that arrives for a
  cancelled order revives it (stock is taken again, a warning is written if there was none).
- **Stripe:** Checkout only (`StripeClient` is plain REST, no SDK). The order is paid by the signed WEBHOOK, never by the browser's return; the return page asks Stripe about the
  ORDER'S OWN session (never a session id from the address). Amount and currency of the session must equal the order's. Events are applied idempotently (absolute refunded totals, an
  event id table, a status check inside the lock). `StripeClient.HandlerFactory` is the test seam (`FakeStripe` in the tests).
- **Stock** is taken atomically (`IncrementAsync` with a floor of 0) inside the order transaction; a lost race throws `SoldOutException` and the whole transaction is undone.
  Order and invoice numbers come from gapless sequences taken inside that transaction.
- **Customer data:** orders belong to an account by `member_id`; a guest's earlier orders join the account only when its address is CONFIRMED (`host.member.confirmed`). Deleting an
  account (`host.member.erase`) removes addresses and carts and unlinks the orders — orders and invoices stay (retention duty).
- **Categories are a tree** (`categories.parent_id`, `image`, `description`; `ShopCategories` = pure tree functions, tolerant of orphans and loops): a product sits in ONE category (any node), "in this category" always means the category AND everything below it
  (`FindProductsAsync` filter, `FacetsAsync` counts rolled up, a coupon's category scope), deleting a category moves its children, products and coupons up one level, and `SaveCategoryAsync` writes a loop-making or missing parent as top level.
  **Attributes (EAV)** — `attributes` (slug, name, kind choice|text|number|bool, options, unit, filterable, category scope, sort) and `product_attrs` (product, attribute, value; a choice holds several rows). `ShopAttributes` = the pure rules (clean values — no commas —, read a form, `AppliesTo` a category, read/match/count filters), `ShopStore` partial in the same file; admin: Eigenschaften (list, edit, delete takes the values with it), the product form has a field per applicable attribute (it names the ones it showed in `attrs`, only those are written). Public: a filter box on the catalogue (`filters_html`; `?a.farbe=Rot,Blau` = any of these, `?a.gewicht.min=1&a.gewicht.max=5`; counted over the page WITHOUT the filters), a table on the product page (`attributes_html`). **Catalogue switches** (settings group "Katalog und Suche": search box, sorting, category chips, keyword row, attribute filters; what a search looks at: title | text | all; the categories page: keyword links, counts, text, subcategories) are `ShopConfig` flags the pages honour. **Pages of the catalogue:** `/shop/categories` (the top level as tiles; `?tag=Neuware` = the categories — at any level — that carry that keyword, which a category gets in its own edit page; template part `categories`/`shop-categories.html` with `tiles_html`, `list_html` for the nested list, `tags_html`), `/shop/tags` (all keywords of the products; part `tags`). Menu elements `shop.categories` (the whole tree as a multi-level dropdown) and `shop.tags`; link groups "Shop – Kategorien nach Schlagwort" and "Shop – Schlagwörter". **The cart** is a two-column page (lines with picture, quantity and "Entfernen" | summary with one big checkout button and the coupon box); an EMPTY cart shows a message, the way back, the top categories and a few products (`EmptyCartAsync`). **An order's next steps** (paid, ship, complete, cancel) are buttons in the order page's head — "paid" and "complete" ask once, "shipped" opens a dialog for the tracking number, "cancel" one for notifying the customer — the rarer actions (refund, mails, note) sit in the side column. The orders list filters by status with a drop-down in its toolbar (`UiListFilter.Selected` lets a link such as `?status=paid` land on a filtered list) and every row has a traffic light (red = paid and waiting to be shipped — the row is tinted, amber = waiting for money, green = on its way or done); the customer's order page shows a step bar (ordered, paid, shipped, completed), the items with pictures and a side column. **Invoice and credit note** carry the seller's logo (`seller.logo`, shown top left; embedded in the PDF as a JPEG) and accent colour (`seller.accent`: heading, rules, total). **Addresses live in `ShopUrls`** (never assemble one by hand): a category is `/shop/categories/{slug}`, a product `/shop/{slug}` (`categories` is a reserved product slug), keyword/search/sort/page stay a query; the old `/shop?cat=…` answers with a permanent redirect (`PluginResult.RedirectPermanent`, 301), an unknown category slug is a 404, the sitemap lists the categories that hold products. Admin: the list is a tree (indented rows, picture, "add subcategory"), one category is edited on its own page `categories/{id}`; the slug is fixed after creation (products refer to it). Public: the catalogue page of a category shows breadcrumbs, picture, text and
  its subcategories as tiles (catalog part placeholders `crumbs_html`, `category_image_html`, `category_description`, `subcategories_html`), the block `shop_categories` has "tiles" and "subcategories of …".
- **Public shop pages are theme-able, always:** a page is a template part with placeholders (old placeholders stay filled), the CSS is `var(--shop-x, var(--accent|--line|--bg|--font-head, …))` — the SITE's variables stand behind the shop's own, so the shop follows a site's CI by itself. **Catalogue page** (`ShopCatalogUi.cs`, `CatalogState`): search field on top, a side column (category list with the open branch, price range + attribute filters as collapsible groups, keywords; a checkbox hack folds it behind a "Filter" button on a phone), a toolbar (count, sort), chips for what is switched on (`active_html`, each with its own ×); price range `?price.min=&price.max=` in euros. **Tracking link:** `orders.carrier` + `tracking`; `ShopTracking.Url` builds the address ONLY from a fixed https template with the escaped number (or an https number as it is, or the operator's own https template `track.url` containing `{n}`); ship dialog and order page have a carrier drop-down, the customer gets a "Sendung verfolgen" button (order page, account cards) and the link in the shipping mail's `{{tracking}}`. **Customer area:** template part `account` (frame with initials, navigation, sign-out) around order cards with a progress bar, address cards, profile cards (`ShopAccount.cs`).
- **Variants** are editable rows (`Ui.RowEditor`, a general toolkit component: posts `name.count` + `name.{i}.id/key`, renumbered in screen order on submit) with the options (name + chips) above that make every combination ("Kombinationen erzeugen"); a row is its variant by **id** (rename keeps stock and order history), a removed row is switched off, never deleted (`ShopVariants.cs`). Tax rates are drop-downs (`ShopTaxChoices`). A customer has a back-office page (`ShopAdminCustomers`, key = e-mail in a route-safe form).
- **The shop's pages are block pages (CMS 1.13):** a plugin declares **pages** (`IPluginBuilder.PageRole` → `PageRoleSpec`: role, variables, starting layout, an example to preview with) that the editors lay out in the block editor; they are `Page` rows with `OwnerPlugin`/`PluginRole` (slug `_shop-product`, no address of their own — the plugin's route answers and calls `ctx.RenderPageAsync(role, PageScope)`, which draws the page's blocks with the request's **variables** `{{product.title}}` (`Content/BlockScope.cs`: encoding by field kind, `{{#if}}`, `_showIf`); `null` = no such page / switched off → the route draws its template part as before). `PluginPageService` creates them for every content language on load, `shop_slot` places a piece the shop built (`*_html` variables; the forms stay whole), the editor lists the variables (`plugin-page-vars.js`), the page list locks them, backups carry the owner, sitemap/link pickers/cloud list skip them. A site whose template already had its own part for a role gets that page created switched off. Rules: never serve a plugin page at its own slug, never put a value into a raw-HTML field unencoded, keep money forms whole. See docs/shop-plugin.md.
- **Choosing products:** one category per product plus free **keywords** (`ShopTags`: kept as typed, compared without case, never a comma) and a `featured` flag; every list of products goes through
  `ShopStore.FindProductsAsync(ProductQuery)` (catalogue, the two page blocks `shop_products`/`shop_product` in `ShopBlocks.cs`, related products). A keyword is matched in memory after a
  database `Contains` (a longer word must not match), everything else in the database. The blocks' CSS is `ShopHtml.CardCss` (also inside the page's stylesheet) and the markup sits in
  `section.section > .container` like the other blocks.
- **Coupons, tax by country, documents** (the chain every price goes through, in this order): `ShopOrders.PriceAsync` (gross lines, stock) → `ShopTax.Apply` (the country of DELIVERY for goods, of the BILLING address
  for downloads; with "oss" on: another EU country's rate, outside the EU no VAT and the net price; shipping gets a `ShipTax`) → `ShopCoupons.Resolve/Apply` (discount on the goods only, shared over the lines with
  `Money.Allocate` so every line's VAT is the VAT of what is paid; `free shipping` is a flag) → `ShippingOptions` → `Compute` (total = subtotal − discount + shipping). `PlaceAsync` runs the whole chain AGAIN inside its
  transaction (a coupon is checked, and its use counted, under the same write lock — two buyers against `MaxUses=1` cannot both win; a coupon that fell away since the cart is a refusal with the reason, never a silent full
  price). An order stores the discount, the coupon code, the tax note ("", "oss", "export") and the shipping rate it was charged with. **Invoice and credit note** are drawn from ONE `InvoiceDoc`
  (`ShopInvoice.Build/BuildCredit`) into HTML (`ShopInvoice.Html`) and PDF (`ShopPdf`, a hand-written PDF 1.4 writer — no library; WinAnsi text, Helvetica) and stored once (`invoices` rows, `kind` invoice|credit,
  PDF as base64). `ShopOrders.ApplyRefundAsync` writes a credit note (gapless `credit` sequence) inside the refund's transaction. The invoice page's own PDF link is a marker (`ShopInvoice.PdfMarker`) the route
  replaces — the stored page never contains the order's key.
- **PayPal** (`Payments/PayPalClient.cs`, `PayPalPayments.cs`; `PayPalClient.HandlerFactory` is the test seam, `FakePayPal` the stand-in): the order is paid by what PayPal says about the order's OWN PayPal order (`payment_ref`),
  when amount, currency and `custom_id` match; a notification is verified BY PayPal (`verify-webhook-signature`; the certificate address must be `*.paypal.com`); a refund counts exactly once, gated by its id in
  `webhook_events` (`refund:{id}`) whoever reports it first, and `ApplyRefundAsync(isIncrease: true)` adds to what the transaction reads.
- **Catalogue depth and data tools** (details: `docs/shop-plugin.md` "Catalogue depth, data tools and storefront comforts"). A product has one main category plus further ones and a category may have a **rule**; which category holds
  which product is decided ONLY by `ShopMembership` (never a second query). Product and category texts have **translations** that overlay the main language (`ShopStore.Public(ctx)`; an order keeps the title the customer saw);
  a changed slug is remembered in `slug_redirects` (301). The product page's **variant picker** is derived from `var_options` + the variants' "A / B" names (no new data; anything that does not fit keeps the plain list), the
  cart has a **shipping estimate** (`?ship=CC`, same rules as the checkout), **direct-pay links** (`?pay=…` preselects, NOT a wallet checkout) and an opt-in **reminder** (off by default, one mail per ask, 14-day limit, address
  deleted with the cart). **Search suggestions** are `/shop/suggest` + a script that builds its list from text nodes only. **CSV** is written by `ShopCsv` only (formula guard: a leading `= + - @` gets a quote); the product
  **import** has a dry run and never writes in it. The **sales report** counts from the day of payment minus refunds. Cloud/MCP **operations** for orders, variants and attributes live in `ShopOps.cs` (a READ answers with `data`);
  orders hold personal data, so their tools need the restore right.
- **Downloads** are private files streamed by `ShopDownloads.ServeAsync`: paid order, the order's secret key (or the owner, or the admin), a download row that is not revoked or
  expired, a count that is taken atomically.
- Texts: `ShopStrings.All` (key, German, English in one row); a test checks that every key used in the code has both.

## The AI assistant

Admin → KI-Assistent (`Pages/Admin/Ai/`, `Services/Assistant/`). It replaced the one-shot generators ("KI: Seite generieren",
"KI: Website generieren", "Block verbessern" and their `AiPreview` page) — those could only fill TEXT fields, append or add-only, and showed
nothing of the way there. The narrow helpers that work stay: meta description / teaser (`_AiSeoDialog`) and the theme suggestion
(`Templates/Customize`). Only where AI is on for the site (`ai.transport`, rolled out by the cloud profile); every model call still goes
through the cloud relay — the key never lives on the site.

- **A run is a loop, written down as it happens.** The editor's message starts a run (`AssistantService.SendAsync` → `AssistantRunner`, a
  hosted queue, ≤ 3 conversations at once, each stoppable); `AssistantAgent` calls the model, executes the actions it asks for, hands the
  results back, until the model says `done` (≤ 30 calls, ≤ 8 actions per answer). Every message, every action (tool, arguments, result,
  warnings), every error is an `AiStep` the page polls (`?handler=Steps`) — that IS the "see what the AI does". A restart marks a running
  conversation idle with a line saying so.
- **The protocol is a JSON object per answer** (`{"message","actions":[{"tool","args"}],"done"}`), not a provider's native tool calling:
  it works through the one plain relay request with every model the cloud may use (DeepSeek, OpenAI, a ChatGPT account). `AiRequest.Json`
  asks for JSON mode where the provider has it; `AssistantAgent.Parse` reads leniently (fences, prose around it, `name`/`arguments`
  spellings) and asks again twice before showing a non-protocol answer as text.
- **The tools** (`AssistantTools`, list in `Catalog`): reads (site_overview, read_page, list_media, read_theme) and writes (create_page incl.
  `translationOf` and `addToMenu`, update_page, set_page_blocks, add_blocks, update_block = a PATCH that keeps unnamed fields and clears on
  null, move_block, delete_block, delete_page — never the home page —, set_menu — keeps the plugin elements —, update_theme, update_settings
  for four whitelisted keys). Plugin pages are read-only. They write the same tables the editor writes.
- **The trusted boundary is `AssistantBlocks`**: the catalogue the model is told (EVERY field of every block — pictures, lists, choices, the
  layout fields `_width/_spaceTop/_spaceBottom/_hidden/_css`) and the validation of what comes back, per field kind: a choice must be one of
  its options (dynamic sources resolved), a picture must be in the media library or https, a link is never `javascript:`/`data:`/`//`, rich
  text goes through `SafeHtml`, `_css` loses every `<`. **Blocks with a raw-HTML field (the HTML block) are not offered at all** — an
  attached document could steer the model into writing markup onto a live page. What does not fit is dropped WITH a warning that goes back to
  the model, so it corrects itself. `BlockGenerator` stays for the cloud's MCP content ops.
- **Forms** (`AssistantToolsForms.cs`): `read_form`, `create_form`, `update_form` write the builder's definition through the builder's own clean-up (`FormDefinition.Sanitize`, shared with Admin → Formulare; `ContentOpApplier.NormalizeFormFields` puts a model's words for types right — "message" → textarea). The assistant NEVER sets where submissions go (the notification), a form's slug or deletes a form: a document could otherwise steer it into mailing every visitor's message elsewhere. Undo removes a created form unless it already has submissions.
- **Beyond pages** (`AssistantToolsMore.cs`): `plugin_action` runs the operations plugins DESCRIBE (`IPluginBuilder.Operation(PluginOperationSpec, …)`: name,
  description, example arguments, `Writes`, `PersonalData`, `Internal`) — the blog's `post.list/read/create/update/delete` (`BlogOps`), the shop's
  `product.list/read/create/update`, `category.list/create/update`, coupons, variants, attributes (`ShopAiOps`). The prompt lists them under
  „Plugin-Aktionen“. Never offered: undescribed ops, `PersonalData` (orders — every answer goes to an outside model) and `Internal` ones
  (only the host runs them: `RunOperationAsync(allowInternal: true)`; the cloud's content ops get a refusal). A write answers with `undo`
  ({name, args}, usually an internal `*.restore`/`*.remove`), journalled as kind `op` and run OUTSIDE the undo transaction (the plugin has its
  own connection; SQLite's write lock would make it wait). Components (create/update, the registry is reset so the new type is a block at once),
  templates (files body/styles/login/article/maintenance/plugin parts; `script.js` is never written; create a copy, activate) and script plugins
  (compiled with `PluginRunner.Check` first; a new one is saved OFF, a running one whose code changed is switched OFF until the admin looks).
  Markup for layouts and components is REFUSED, not cleaned, when anything in it runs (`AssistantMarkup`: script/iframe/object/base/meta/link,
  `on*`, `srcdoc`, `javascript:`/`data:` addresses, CSS expressions) — the refusal names it so the model rewrites it.
- **A run always finishes visibly**: an answer that only ANNOUNCES ("done": false, no actions) is told to go on (twice, then "stalled"), a
  question to the user ends it as "waiting", a busy/slow provider is asked again (2×, growing pause), the limit is 50 calls. The LAST step of
  every run is kind `end` with `state` done|waiting|limit|stalled|error|cancelled — the page draws it as a status line, with "Weitermachen"
  (sends "Mach weiter.") where it makes sense. The runner's start-up reset only touches runs older than its start (a run begun in the
  first second used to be marked idle while it went on).
- **Undo is per run** (`AiChange`, `AssistantUndo`): the first time a run touches a page / menu (menu+locale) / template / setting, its state
  is journalled; created things are journalled as null. Undo restores blocks and menu entries WITH THEIR OLD IDS (SQLite never reuses one)
  and only the newest run with changes may be undone, then the one before — a run may build on the previous one. A tool that fails half-way
  detaches everything unsaved (`AssistantTools.Discard`), or the next log line's save would write the half-change.
- **Scope**: opened from editor v2 (sparkles in the top bar = this page, on a block = that block) or the classic editor, with
  `?pageId=&blockId=` — NOT `page`, which Razor Pages keeps as a route name (the query value never arrived). "Nur hier ändern" makes the
  tools refuse writes outside the page/block; reading stays allowed. The editor saves unsaved work before it hands over.
- **The prompt** = the site's layers (below) → the assistant's own basic instruction (`assistant.prompt`, edited in the Grundanweisung tab;
  empty = `AssistantPrompt.Default`; NOT under `ai.`, which is the cloud's group) → the fixed part nobody edits (protocol, tools, catalogue)
  → the situation. "Vollständige Anweisung anzeigen" shows the exact text.
- **Three layers, the same for EVERY use of AI on a site** (`AiService.PreambleText` — the one place; assistant, SEO/theme helpers and every
  plugin's `AskAiAsync` get it): the cloud operator's `ai.instruction` → the site's own **"Über diese Website"** (`site.aiContext`,
  Einstellungen → KI; works without a cloud, travels in backups) → the operator's guide for THIS use from `ai.actionGuides`
  (`assistant`, `seo`, `theme`, `plugin:{key}` — the bot is `plugin:bot`; the old `pagegen`/`sitegen` are dropped on the next profile save).
  After them comes the user's own text: how ONE helper works (assistant's basic instruction, the bot's rules + its setting). Instructions
  are NOT part of an AI profile — a profile is the connection, swapped freely; the brand must not change with the model. A plugin cannot
  leave the layers out (the host puts them in front of its messages); a plugin's instruction is an ordinary setting of its own. The KI tab
  pre-fills "Über diese Website" with a hand-written basic instruction while it was never saved, but stores it only on save (copied
  silently, the assistant would read it twice).
- **Attachments** (`AssistantFiles`, ≤ 6 per message): pictures go into the media library (tag `KI-Anhang`, usable on a page at once) and a
  ≤ 1600 px JPEG copy rides as `AiAttachment` on THIS run's user message (`appdata/ai-files/{conversation}`, deleted with it); PDF (PdfPig)
  and DOCX go into the library and their TEXT into the message; txt/md/csv/json/html are read only. The cloud drops pictures for a model
  that refuses them and says so (`AiResponse.Note`).
- **Cloud side** (`MatCMS.Cloud/Services/AiService.cs`): completion cap 16 000 and 280 s — a reasoning model spends the cap on thinking and
  came back EMPTY with the old 60/220/500-token caps, which is why the old actions "did not work"; an empty answer is now an error that says
  why. Refusals naming `max_tokens`, `temperature`, `response_format` or a picture are retried without it. `CloudProtocol.Version` 18.
- **The site's OWN AI connection** (Einstellungen → KI, `Services/OwnAi.cs`) — MatCMS must work without a cloud: an API key (OpenAI or any
  compatible server, e.g. DeepSeek) or a signed-in ChatGPT account (device code, like Codex). `SiteAiTransport` (the one `IAiTransport`)
  uses it when set up, else the cloud relay; `AiService.Enabled` = own connection configured OR `ai.transport=cloud`. The provider code is
  ONE implementation in `MatCMS.Shared/Ai/` (`ChatCompletionsClient`, `ChatGptAccount` + `IChatGptTokenStore`), used by the cloud too —
  each app only stores the tokens its way. The keys (`SettingKeys.AiOwn`, `assistant.provider/apiBaseUrl/apiModel/apiKey/chatgptModel/
  chatgptTokens`) are NOT under `ai.` (the cloud's group would overwrite them), key and sign-in are DataProtection-encrypted (purpose
  `MatCMS.AiSecrets`), they are in `SettingKeys.Protected` (no rollout, content op or restore writes them) and **never in a backup**
  (`ContentTransferService` leaves them out): a ChatGPT refresh token ROTATES and may be used once — a copy elsewhere kills the sign-in here.
- **AI profiles** (Einstellungen → KI, `Pages/Admin/Settings/AiProfile`): several own connections side by side ("DeepSeek Flash",
  "OpenRouter kostenlos"), ONE active — `assistant.profiles` (JSON list, each key encrypted on its own) + `assistant.activeProfile`
  (empty = the cloud's relay); both in `SettingKeys.AiOwn`, so protected and in no backup. `OwnAi.GetAsync` returns the ACTIVE profile,
  so `SiteAiTransport` did not change. A site from before profiles has no `assistant.profiles` row: `OwnAi.Read` presents its loose
  keys as the profile (active if it was in use) and the first save writes the list and clears them. All ChatGPT profiles share the one
  sign-in — NO: every ChatGPT profile has its OWN sign-in (`assistant.chatgptTokens.{profile}`, `SiteChatGptAccounts.For(id)`, one
  `ChatGptLoginState` per account from the singleton `ChatGptLoginStates`), started on the profile page; the old single sign-in is MOVED to the
  first ChatGPT profile that reads it (never copied — a refresh token may be used once). `SettingKeys.IsAiOwn/IsProtected` cover the prefix.
  "Modelle laden" = `ChatCompletionsClient.ListModelsAsync` (the provider's `GET /models`; free = OpenRouter's prices "0" or
  `:free`, never guessed). The assistant switches the active profile from its compose bar (site-wide, like the KI tab).
- **SVG** is allowed now (upload, assistant attachments, the assistant's own `create_svg` for logos/icons, `update_settings` logo +
  favicon): `Services/SafeSvg.cs` REBUILDS the file from an allow-list (drawing elements and presentation attributes; script,
  `foreignObject`, `image`, animation elements and every `on…` dropped; an unknown wrapper like `<a>` is unwrapped; references only `#id`;
  no DTD), and `/uploads/*.svg` is served with `Content-Security-Policy: default-src 'none'; …; sandbox` as a second wall. An SVG is
  never scaled (`ThumbnailService`) or read as a bitmap (`LoadImageAsync`, the shop's invoice logo stays a raster picture).
- Tests: `AssistantTests` (the whole app in memory, `FakeAi` in place of the relay), `OwnAiTests` (shared provider clients, the KI tab,
  encryption, backup exclusion).

## The AI bot (built-in plugin `bot`)

A chat assistant for a site's VISITORS (not the editors' assistant above): `src/MatCMS/Plugin/Bot/`, `wwwroot/js/ai-bot.js` + `wwwroot/css/ai-bot.css`.
Built in, **off on a new site** (`EnabledOnNewSite: false`). Name, greeting, avatar, colour (template accent by default), bubble on/off + position,
basic instruction, fallback, sources, customer data, limits and history are its settings (`BotConfig`).

- **What it needed became general plugin API** (on `IPluginSite`, one implementation in `PluginSiteFacade`): `AiEnabled` + `AskAiAsync(PluginAiCall)` —
  the site's active AI connection, a plugin never holds a key — and `SearchPagesAsync` (`Services/SiteSearch.cs`: published pages, never plugin-owned
  ones, hidden blocks or unpublished pages; members-only pages only for a visitor who may open them; texts cached a minute per language). Two event
  conventions: **`site.search`** `{q,max,locale}` → `{items:[{title,url,text,score,kind}]}` (the blog answers with posts, the shop with products) and
  **`member.data`** `{member_id}` → `{title,text,url}` (the shop: the account's LINKED orders, as the customer sees them — no payment refs, no keys).
- **The boundary is code, not prompt** (`BotAsk`): the member id comes from `ctx.Member` only, there is no tool or field naming a customer, so "show me
  customer 17's orders" has nothing to work with. Customer data only with the operator's switch `memberData` (off by default — it goes to the AI
  provider). The browser's history is the visitor's own words: only `user`/`assistant` roles, cut, last six turns.
- **Limits before the model is asked**: per visitor and day (questions, tokens) and per site and month (tokens), in table `usage`. A visitor is a hash of
  address + browser + DAY (`BotStore.VisitorKey`), deleted after two days. Conversations (`chats`) only while `keepDays` > 0, cleaned by a job.
- **Consent before the first question** (settings group Einwilligung, off by default): the operator ticks it and picks a text template — provider in the EU / USA under the Data Privacy Framework / third country without an adequacy decision (e.g. China, Art. 49(1)(a)) / own text, with `{provider}`, `{country}`, `{privacy}` filled in (`BotConfig.ConsentFor`). The widget shows the text and one button where the input will be; `/ai-bot/ask` CHECKS it too (`consent` must equal `BotConfig.ConsentVersion`, a hash of the text) — a request that skips the window reaches no model, and a changed text asks again. Not legal advice; the templates say so in the admin.
- The widget renders everything as DOM text; from an answer only `**bold**`, lists and links to `/local` or `https://` addresses become markup. The
  conversation lives in `sessionStorage` (this tab). A site that switches the bot on needs a paragraph in its privacy statement.
- Tests: `BotTests` (whole app, `FakeAi`, shop on: own orders yes, another customer's no, members-only page, limits, history roles, chats).

## Fonts are served by this server (LocalFonts)

No page may send a visitor's browser to Google: `MatCMS.Shared.Web/LocalFonts.cs` maps `/fonts/css2` (+ `/fonts/css`, `/fonts/f/{file}`) in
BOTH apps; the first request fetches Google's stylesheet and font files server-side into `appdata/fonts`, every later one is served
from there. Layouts link `/fonts/css2?…` (same query as Google). User-written template code (CSS `@import`, layout, head code, login
and maintenance pages) goes through `LocalFonts.Rewrite` when rendered. If Google is unreachable on that first fetch the answer is an
empty, uncached stylesheet (system fonts) — never a link to Google. A new place that emits template/user HTML must call `Rewrite` too.
Trap: a minimal-API lambda taking ONLY `HttpContext` and returning a Task binds to `RequestDelegate` and drops its IResult (empty 200).

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

**Standardsprache and Standardroute are two settings.** `i18n.default` (which language is the default; read at
startup, a change needs a restart) and `i18n.rootRoute` ("Standardsprache unter „/“", Settings → Sprachen; empty/"1"
= on, the behaviour every site had): on, the default language has the prefix-less URLs and `/de/…` redirects there;
off, it lives under its own prefix like the others (`/de`, `/de/kontakt`) and `/`, `/kontakt` redirect there. The
`/{culture}/{slug?}` route exists for EVERY supported culture so the switch needs no restart (the settings page sets
`Localizer.SetDefaultAtRoot` live; restore/cloud op take effect at the next start). The redirects are 302 on purpose
— a cached 301 would outlive switching back. Every public page URL goes through `Localizer.PrefixFor` /
`SiteContext.LocalizedUrl` — never write `locale == DefaultCulture ? "" : "/" + locale` by hand again (there were
three copies). Plugin routes accept the default language's prefix only while the Standardroute is off.

Razor Pages use the `Index/Create/Edit` triple with `OnGetAsync` / `OnPost<Action>Async` and PRG via
`TempData["Flash"]` / `TempData["FlashError"]`.

The cloud's admin must look and behave exactly like the CMS's — the class-by-class table for that is
in `src/MatCMS.Cloud/CLAUDE.md`. Never invent a parallel CSS class for something the admin already
ships.

**Both back offices run on MatUi (in progress).** MatDesign's toolkit is COPIED (CODE-03) into
`MatCMS.Shared.Web/wwwroot/lib/matui/matui.css` (version in its first line; update by copying `/ui/matui.css` of a MatDesign
release again). The CMS's `_AdminLayout` is MatUi's shell (`mat-app`, `mat-nav` with group headings, `mat-topbar` with the
breadcrumb, the user and the theme button at the foot, `mat-page`); `<html data-app="matcms" data-density="compact">`.
`css/admin-matui.css` is the bridge, loaded last: the compact density (written like a MatUi token set — a candidate for MatUi
itself), every admin variable (`--surface`, `--line`, `--accent`, the status tints …) READING a `--mat-*` token, and the
admin's own classes (`.btn`, `.card`, `table.data`, `.tabs`, `.form-field`) in MatUi's look until the pages move to `mat-*`
classes. `matui.js` is NOT loaded yet: it handles `data-confirm` too, and with `mat-dialogs.js` every question would be asked
twice — `js/admin-shell.js` does the shell (drawer, user menu, theme → `Account?handler=Theme`) until then. Icons stay Tabler
(plugins name Tabler icons). The cloud's `_AdminLayout` is the same shell (groups Betrieb / Verwaltung); its instance
switcher (`_ContextPicker`) is MatUi's menu switcher under the logo (`mat-nav__switch` > `mat-switcher`), showing a small
live picture of the CURRENT site (one frame, desktop only, after load — `context-picker.js`); the bar has the breadcrumb
(cloud / site / page). The admin bar over the public site (`_AdminBar`, `.mat-adminbar` in site.css) has the same look with
the MatUi values set as its OWN variables — the public site never loads matui.css (it would restyle the customer's template).
**The Cloud | Website | Admin switch sits on the SAME pixels in all three views** (site bar, CMS admin, cloud): it is always
the LAST element of its bar (other actions go to its left), its size is fixed px in site.css (never `--mat-control-h`), the
bar's right padding is the compact content padding in steps (28/24/16 px — never `vw`, which shrinks where the window keeps
a scrollbar gutter), and both keep a thin scrollbar's room always (`.mat-main` and, while the bar is shown,
`html:has(> body.has-adminbar)`: `scrollbar-gutter: stable`). Measured with real scrollbars (Playwright hides them unless
`--hide-scrollbars` is dropped); change one of these and measure again.
A cloud-connected site's admin has the cloud's menu switcher on the SAME pixels under its logo, offering only THIS site and
"Zurück zur Cloud" (to this site's page on the cloud) — never the other sites. A template's own sticky header below the
site's bar uses `top: var(--mat-adminbar-h, 0)`, never the pixels (FEUSYS had copied the old `top: 67px`: a 15px gap once the
bar became 52; site.css's `html body.has-adminbar .site-header` outweighs such a copy).
Live site pictures in tile views (`.instance-tile-shot`, `.payload-tile-shot`) are scaled to the tile's real width by
`admin-shell.js` (`--shot-scale`), never by a fixed factor. The sign-in, 2FA, OAuth and error
pages (`body.login-page.login-matui`) are MatUi's auth card through the same bridge. A state is a tint behind the WHOLE item
plus icon and words (MatUi COLOR-04/08) — never a coloured bar on one side: the bridge turns the old bars of the attention
rows, the dashboard tiles and the assistant's steps into that; do not add a new one. The page editor v2 (own layout) is not moved yet.

**On a phone a record table is a stack of cards** (MatUi TABLE-02): `admin-shell.js` writes each cell's column name as `data-label`
(from the table head) and adds `.has-labels`; `admin-matui.css` (≤ 600 px) draws title, label/value rows and actions. Left as they
are: tables one types into (inputs, selects, row editors) and the ones with a phone layout of their own (attention, cleanup,
notification matrix) — they scroll sideways. A new table needs nothing for this; a new special one is added to that exception list.

**Both back offices are installable apps (PWA)** — `MatCMS.Shared.Web/Pwa.cs` (+ `js/pwa.js`, icons in `wwwroot/pwa/`, rendered
from MatUi's logo tile with the registry glyph: `template` for the CMS, `cloud` for the cloud). `/pwa/manifest.webmanifest`,
`/pwa/sw.js` (scope `/`, `Service-Worker-Allowed: /`), `/pwa/offline`; `Pwa.Head(theme, shortName, icon)` in every admin layout and
sign-in page writes manifest, `theme-color` (= the top bar's colour, per the user's scheme), iOS's home-screen meta and the
`apple-touch-icon`. The CMS's maintenance mode lets `/pwa/` through. **The worker never caches a page**: back-office pages are
signed in, hold antiforgery tokens and personal data — static files are cached (versioned cache-first, the rest
stale-while-revalidate), a failed back-office navigation gets the offline page (it reconnects by itself), and on a SITE the worker
answers only `/admin` and `/login` navigations — the public site is the visitors' and stays untouched. `pwa.js`: a bar when the
connection drops, a form is not sent while offline (its input stays), and back online a page without unsaved input reloads
(no stale lists). On a touch screen fields are 16 px (iOS zooms into smaller ones); zoom itself stays allowed. Tests: `PwaTests`.

**The MatCMS logo is "Blöcke"** — an accent tile (#2563eb, rx 11/48) with a header bar and columns, the block editor as a picture,
next to the word mark "Mat**CMS**" (system font 800, `textLength` pins its width so any font fits the box). Files: CMS
`wwwroot/img/logo.svg` + `logo-dark.svg` + `favicon.svg` (the mark alone), cloud the same with "CLOUD" under the word mark and
the SAME mark as tab icon (it once had a cloud glyph there — it did not match the logo); only its installed-app icon
(`pwa/cloud-*`) keeps the cloud glyph, so the two apps differ on a home screen. App icons `MatCMS.Shared.Web/wwwroot/pwa/`. A site with MatCMS's own logo (`SiteContext.UsesDefaultLogo`)
shows the dark twin in a dark back office and the mark as favicon. A site with a logo of its own may name its dark twin in
`LogoDarkUrl` (Einstellungen → Allgemein, rollable) — `SiteContext.LogoDarkUrl` = that, else MatCMS's dark logo while MatCMS's logo is in
use, else none (the one logo in both schemes). Only the back office's sidebar uses it; the public site has no dark scheme.

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

## The site's picture of itself (SitePreview)

The admin's site switcher and the cloud's instance tiles, site picker and top-bar switcher used to load every site LIVE in an iframe
(the switcher's on every single admin page). Now a site keeps a picture of its home page: `appdata/site-preview.webp`, served
public at **`/_preview.webp`** (no-cache + ETag, so a new picture is seen at once and an unchanged one costs a 304).
- **Taken in the admin's browser, not on the server** (no browser in the image): `Services/SitePreview.cs` decides when one is due
  (none yet, a content fingerprint changed — published pages' last change, active template, header menu, site name/logo — or older
  than 7 days; never twice within 10 min) and `_AdminLayout` then renders `[data-site-shot-due]`; `admin-shell.js` loads `/` into an
  off-screen same-origin frame, loads the vendored `wwwroot/lib/modern-screenshot` (MIT) INTO that frame and draws the frame's own
  document, posts the WebP to `POST /admin/api/site-preview` (Admin + antiforgery header), the server re-encodes it with ImageSharp
  (top cut-out, 800×500). Only on sites connected to a cloud (they are the ones showing it), desktop only.
- Traps found on the way: the library waits for EVERY `<img>` of the page — lazy ones below the fold never load in a hidden frame, so
  without `timeout: 3000` it waits its default 30 s; Git Bash rewrites a CDP navigate path "/admin" into a Windows path (test with
  `MSYS_NO_PATHCONV=1`).
- **Where it is shown:** `img.site-shot[data-fallback]` (CSS in `admin-matui.css`). A site without a picture (or an older version
  without the endpoint) answers 404 and `admin-shell.js` swaps in the live frame it always had (never on a phone for the small ones).
  The instance Details page keeps its live frame on purpose.

## The media library ("Bild bearbeiten")

A click on an item opens an INFO dialog (preview, kind, dimensions, size, focal point, URL, alt, tags, "Verwendet auf" from
`MediaEditor.UsesAsync`); editing is a separate dialog (`Pages/Admin/Media`, `wwwroot/js/media-library.js`, Cropper.js in the browser).
- **"Übernehmen" edits IN PLACE — the URL stays**, so every page that uses the picture shows the edit. The first edit sets the original
  aside (`Media.OriginalUrl`, deleted with the item and by the cleanup); every later edit starts from the original with the stored settings
  (`Media.CropJson`), "Auf Original zurücksetzen" copies it back. "Als neues Bild" makes a copy row (`SourceMediaId`). The server re-encodes
  whatever the browser sends (`MediaEditor`, ImageSharp — metadata stripped, size limits); the browser's file is never stored as it came.
- **Thumbnail URLs carry a version** (`/thumb/{w}/{name}?v=…`, thumbs are served immutable): `ThumbnailService.Invalidate` deletes the scaled
  copies and bumps the version — never just "forget" it (two edits within one tick would keep the old URL). `/uploads` is `no-cache`, since an
  address no longer means one content forever.
- **SVG is code, not pixels:** "SVG bearbeiten" (text + live preview, saved through `SafeSvg`) and a focal point — no crop/resize. GIF gets
  only the focal point (re-encoding would lose the animation). "Datei ersetzen" keeps the address too (raster re-encoded into the item's format).
- **Focal point in blocks** is `style="@(x == null ? null : "object-position:" + x)"` — Razor drops a null attribute. A whole `style="…"`
  written as a string is HTML-ENCODED and silently does nothing (it did, in Hero/Gallery/ImageText/Slider, for a long time).
- **Backups carry** focal point, edit settings, original, width/height and the copy's source (by URL); an `OriginalUrl` from a backup must be
  a plain `/uploads/` path.
