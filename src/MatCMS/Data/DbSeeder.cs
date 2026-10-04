using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Content;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Data;

/// <summary>
/// Seeds a fresh, generic MatCMS install. A concrete site (e.g. FeuSys) is applied on top
/// by importing a backup under Admin → Backup.
/// </summary>
public static class DbSeeder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task SeedAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var auth = sp.GetRequiredService<AuthService>();

        // Legacy patcher for databases created before EF migrations existed here. Runs AFTER the
        // baseline in Program.cs, which is what makes that baseline safe: a drifted EnsureCreated()
        // database gets its missing columns back even though the initial migration was recorded
        // without running. FROZEN — new columns belong in a migration, never in here.
        await PatchLegacySchemaAsync(db);
        await BackfillPluginKeysAsync(db);
        await BackfillLanguagesAsync(db);

        if (!await db.Users.AnyAsync())
        {
            db.Users.Add(new User
            {
                Username = "admin",
                PasswordHash = auth.HashPassword("admin"),
                Role = "Admin",
                DisplayName = "Administrator"
            });
        }

        if (!await db.SiteSettings.AnyAsync())
        {
            db.SiteSettings.AddRange(
                S(SettingKeys.SiteName, "MatCMS"),
                S(SettingKeys.LogoUrl, "/img/logo.svg"),
                S(SettingKeys.FaviconUrl, ""),
                S(SettingKeys.TopBarLink1Text, ""),
                S(SettingKeys.TopBarLink1Url, ""),
                S(SettingKeys.TopBarLink2Text, ""),
                S(SettingKeys.TopBarLink2Url, ""),
                S(SettingKeys.FooterText, "© MatCMS"),
                S(SettingKeys.ContactRecipient, "")
            );
        }

        if (!await db.Templates.AnyAsync())
        {
            db.Templates.Add(new Template
            {
                Name = "Standard",
                IsActive = true,
                AccentColor = "#2563eb",
                HeadingFont = "Geologica",
                BodyFont = "Inter",
                ButtonStyle = "solid"
            });
        }

        // A ready-made alternative template. Ensured on every startup (not only on a fresh DB) so
        // it also reappears after importing a backup that only carried the previous theme.
        var modern = await db.Templates.FirstOrDefaultAsync(t => t.Name == ModernTemplateName);
        if (modern is null)
        {
            db.Templates.Add(BuildModernTemplate());
        }
        else if (string.IsNullOrWhiteSpace(modern.LayoutHtml))
        {
            // Upgrade an older "Modern" row (colors/CSS only) to the new slot-based custom layout,
            // so the alternative template actually demonstrates {{menu:slot}} without a volume reset.
            var m = BuildModernTemplate();
            modern.LayoutHtml = m.LayoutHtml;
            modern.MenuMapJson = m.MenuMapJson;
            modern.CustomCss = m.CustomCss;
        }

        // Note: instance-specific themes (FeuSys, Ferienwohnung) are NOT seeded into the base image —
        // they are delivered per instance via a backup import. The base image ships only generic themes.

        // A few bundled starter themes (added to any DB that doesn't have them yet).
        await EnsureThemeAsync(db, BusinessThemeName, BuildBusinessTemplate);
        await EnsureThemeAsync(db, TechThemeName, BuildTechTemplate);
        await EnsureThemeAsync(db, ArtThemeName, BuildArtTemplate);

        // Every declared mail gets its row, so the editor has something to show and a site can
        // change the wording without a release. Existing rows are LEFT ALONE — this runs on every
        // start, and re-seeding would throw away what an operator wrote.
        foreach (var def in MatCMS.Shared.MailTemplates.All)
        {
            if (await db.MailTemplates.AnyAsync(t => t.Key == def.Key)) continue;
            db.MailTemplates.Add(new MailTemplate
            {
                Key = def.Key, Name = def.Name, Description = def.Description,
                Subject = def.Subject, Body = def.Body
            });
        }

        // A ready-made example component so the component designer has something to look at.
        if (!await db.Components.AnyAsync(c => c.Type == ExampleComponentType))
        {
            db.Components.Add(BuildExampleComponent());
        }

        // Plugins are NOT seeded. They used to be (a todo demo, Bewertungen, Google Bewertungen), and
        // the review plugins were even re-written on every start — so a site could never keep its own
        // fixes to one, and every instance carried code it had not asked for. Their sources now live in
        // the repo's plugins/ folder and reach a site through the cloud's store or a bundle import.
        // Rows already on existing instances are left exactly as they are.

        if (!await db.Forms.AnyAsync())
        {
            db.Forms.Add(new Form
            {
                Name = "Kontakt",
                Slug = "kontakt",
                DefinitionJson = BuildContactFormDefinition()
            });
        }

        if (!await db.Pages.AnyAsync())
        {
            foreach (var page in BuildPages())
            {
                // Seeded pages are in the default locale; each is its own translation group.
                page.Locale = Localizer.DefaultCulture;
                page.TranslationGroup = Guid.NewGuid().ToString("N");
                db.Pages.Add(page);
            }
        }

        if (!await db.MenuItems.AnyAsync())
        {
            db.MenuItems.AddRange(
                Mi("header", "Start", "/", 0),
                Mi("header", "Kontakt", "/kontakt", 1),
                Mi("footer", "Start", "/", 0),
                Mi("footer", "Kontakt", "/kontakt", 1)
            );
        }

        // Ensure the built-in menu definitions exist (also on already-seeded databases).
        foreach (var (key, name, order) in new[] { ("header", "Hauptmenü", 0), ("footer", "Footer", 1), ("toolbar", "Obere Leiste", 2) })
        {
            if (!await db.Menus.AnyAsync(m => m.Key == key))
                db.Menus.Add(new Menu { Key = key, Name = name, SortOrder = order, BuiltIn = true });
        }

        // Migrate legacy top-bar links (old Settings fields) into the new "toolbar" menu, once.
        if (!await db.MenuItems.AnyAsync(m => m.Menu == "toolbar"))
        {
            var legacy = await db.SiteSettings.Where(s =>
                s.Key == SettingKeys.TopBarLink1Text || s.Key == SettingKeys.TopBarLink1Url ||
                s.Key == SettingKeys.TopBarLink2Text || s.Key == SettingKeys.TopBarLink2Url).ToListAsync();
            string G(string k) => legacy.FirstOrDefault(s => s.Key == k)?.Value ?? "";
            var order = 0;
            void AddLink(string text, string url)
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                db.MenuItems.Add(new MenuItem
                {
                    Menu = "toolbar",
                    Label = string.IsNullOrWhiteSpace(text) ? url : text,
                    Url = url,
                    Icon = "link",
                    OpenInNewTab = true,
                    SortOrder = order++,
                    Locale = Localizer.DefaultCulture
                });
            }
            AddLink(G(SettingKeys.TopBarLink1Text), G(SettingKeys.TopBarLink1Url));
            AddLink(G(SettingKeys.TopBarLink2Text), G(SettingKeys.TopBarLink2Url));
            // Blank the legacy settings so a later manual delete of toolbar items won't re-migrate.
            foreach (var s in legacy) s.Value = "";
        }

        await db.SaveChangesAsync();

        await MigrateLegacyContactAsync(db);
        await MigrateListBlocksAsync(db);
        await MigrateFeaturesAsync(db);
        await UpgradeTemplatesAsync(db);
    }

    /// <summary>
    /// One-time, idempotent rename of the "leistungen"/"leistung" block types to "features"/"feature"
    /// (the code-hygiene rename to English internals). Runs on every startup, a no-op once done. The
    /// field JSON is identical for old and new, so only the type string changes; a runtime alias in
    /// <see cref="Content.BlockRegistry.Get"/> still resolves any old value that arrives later via a
    /// backup or an older instance's cloud config.
    /// </summary>
    private static async Task MigrateFeaturesAsync(AppDbContext db)
    {
        var legacy = await db.ContentBlocks
            .Where(b => b.BlockType == "leistungen" || b.BlockType == "leistung")
            .ToListAsync();
        if (legacy.Count == 0) return;
        foreach (var b in legacy)
            b.BlockType = b.BlockType == "leistungen" ? "features" : "feature";
        await db.SaveChangesAsync();
    }

    /// <summary>Converts every stored template up to the current template schema version
    /// (<see cref="MatCMS.Content.TemplateSchema.Current"/>). Runs on every startup — old instances
    /// (written at V1) are migrated seamlessly, exactly like the column/data migrations above. Runs
    /// after the main save so freshly-seeded templates are included on a first-ever boot too.</summary>
    private static async Task UpgradeTemplatesAsync(AppDbContext db)
    {
        List<Template> outdated;
        try
        {
            outdated = await db.Templates
                .Where(t => t.SchemaVersion < MatCMS.Content.TemplateSchema.Current)
                .ToListAsync();
        }
        catch { return; } // columns not present yet on a very old DB — retried next boot
        if (outdated.Count == 0) return;

        var n = 0;
        foreach (var t in outdated)
            if (MatCMS.Content.TemplateSchema.Upgrade(t)) n++;
        if (n == 0) return;

        await db.SaveChangesAsync();
        Console.WriteLine($"[MatCMS] {n} Template(s) auf Schema V{MatCMS.Content.TemplateSchema.Current} konvertiert.");
    }

    /// <summary>
    /// Converts list-based blocks (columns/servicegrid/accordion) into nested container blocks:
    /// each "items" entry becomes a child block. Idempotent — skips blocks already migrated.
    /// </summary>
    private static async Task MigrateListBlocksAsync(AppDbContext db)
    {
        (string Container, string Child)[] maps =
        {
            ("columns", "column"),
            ("servicegrid", "service"),
            ("accordion", "faq"),
        };

        var changed = false;
        foreach (var (container, child) in maps)
        {
            var blocks = await db.ContentBlocks.Where(b => b.BlockType == container).ToListAsync();
            foreach (var b in blocks)
            {
                // Already migrated? (has children, or its "items" array was already stripped)
                if (await db.ContentBlocks.AnyAsync(c => c.ParentId == b.Id)) continue;
                try
                {
                    if (JsonNode.Parse(string.IsNullOrWhiteSpace(b.DataJson) ? "{}" : b.DataJson) is not JsonObject node)
                        continue;
                    if (node["items"] is not JsonArray items || items.Count == 0) continue;

                    var order = 0;
                    foreach (var item in items)
                    {
                        if (item is not JsonObject) continue;
                        db.ContentBlocks.Add(new ContentBlock
                        {
                            PageId = b.PageId,
                            ParentId = b.Id,
                            BlockType = child,
                            SortOrder = order++,
                            DataJson = item.ToJsonString()
                        });
                    }
                    node.Remove("items");
                    b.DataJson = node.ToJsonString();
                    changed = true;
                }
                catch { /* leave the block untouched on parse errors */ }
            }
        }

        if (changed) await db.SaveChangesAsync();
    }

    /// <summary>
    /// Idempotently adds columns that were introduced while this project still used
    /// <c>EnsureCreated()</c>, which never ALTERs an existing table. It is <b>frozen history</b>: the
    /// project has moved to EF migrations (see <c>EnsureSchemaCurrentAsync</c> in Program.cs) and a
    /// new column must go into a migration instead.
    /// <para>Do not extend it, and mind that it runs AFTER the migrations: a future migration that
    /// DROPS or renames one of the columns listed here would have it silently re-added on the next
    /// start. Remove the corresponding line here in the same commit.</para>
    /// </summary>
    private static async Task PatchLegacySchemaAsync(AppDbContext db)
    {
        await AddColumnIfMissingAsync(db, "Users", "Email", "TEXT");
        await AddColumnIfMissingAsync(db, "Forms", "SuccessMessage", "TEXT");
        await AddColumnIfMissingAsync(db, "Forms", "SubmitLabel", "TEXT");
        await AddColumnIfMissingAsync(db, "Forms", "NotifyEnabled", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(db, "Forms", "NotifyJson", "TEXT NOT NULL DEFAULT ''");
        await AddColumnIfMissingAsync(db, "Media", "SortOrder", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(db, "MenuItems", "ParentId", "INTEGER"); // hierarchical menus (nullable self-ref)
        await AddColumnIfMissingAsync(db, "Plugins", "Key", "TEXT NOT NULL DEFAULT ''");
        await AddColumnIfMissingAsync(db, "Plugins", "Version", "TEXT NOT NULL DEFAULT ''");
        await AddColumnIfMissingAsync(db, "Plugins", "DataVersion", "TEXT NOT NULL DEFAULT ''");
        await AddColumnIfMissingAsync(db, "Components", "Icon", "TEXT NOT NULL DEFAULT ''");
        // NB: default is '' (not '{}') — ExecuteSqlRaw treats "{}" as a format placeholder and throws.
        // Empty is parsed as an empty config anyway, and saving normalizes it to {}.
        await AddColumnIfMissingAsync(db, "Plugins", "ConfigJson", "TEXT NOT NULL DEFAULT ''");
        await AddColumnIfMissingAsync(db, "Templates", "ParametersJson", "TEXT NOT NULL DEFAULT '[]'");
        await AddColumnIfMissingAsync(db, "Templates", "ParamValuesJson", "TEXT NOT NULL DEFAULT ''");
        // Versioned per-page-type layout parts. Existing templates default to V1 and are converted up
        // to the current schema on startup (UpgradeTemplatesAsync). PartsJson default is '' (not '{}') —
        // ExecuteSqlRaw treats "{}" as a format placeholder and throws; empty is parsed as no parts.
        await AddColumnIfMissingAsync(db, "Templates", "SchemaVersion", "INTEGER NOT NULL DEFAULT 1");
        await AddColumnIfMissingAsync(db, "Templates", "PartsJson", "TEXT NOT NULL DEFAULT ''");

        // New table added after first release: EnsureCreated won't add it to an existing DB, so create
        // it idempotently here (fresh DBs already have it from the model → IF NOT EXISTS is a no-op).
        await CreateTableIfMissingAsync(db,
            """
            CREATE TABLE IF NOT EXISTS "Posts" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Posts" PRIMARY KEY AUTOINCREMENT,
                "Title" TEXT NOT NULL DEFAULT '',
                "Slug" TEXT NOT NULL DEFAULT '',
                "TitleImage" TEXT NULL,
                "Excerpt" TEXT NOT NULL DEFAULT '',
                "ContentHtml" TEXT NOT NULL DEFAULT '',
                "Tags" TEXT NOT NULL DEFAULT '',
                "AttachmentsJson" TEXT NOT NULL DEFAULT '[]',
                "Locale" TEXT NOT NULL DEFAULT 'de',
                "IsPublished" INTEGER NOT NULL DEFAULT 0,
                "PublishedAt" TEXT NOT NULL DEFAULT '',
                "CreatedAt" TEXT NOT NULL DEFAULT '',
                "UpdatedAt" TEXT NOT NULL DEFAULT ''
            )
            """);
        await CreateTableIfMissingAsync(db,
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Posts_Slug_Locale\" ON \"Posts\" (\"Slug\", \"Locale\")");
    }

    /// <summary>Assigns a stable slug Key to any plugin created before the Key column existed.</summary>
    private static async Task BackfillPluginKeysAsync(AppDbContext db)
    {
        List<Plugin> pending;
        try { pending = await db.Plugins.Where(p => p.Key == null || p.Key == "").ToListAsync(); }
        catch { return; }
        if (pending.Count == 0) return;

        var used = (await db.Plugins.Where(p => p.Key != null && p.Key != "")
            .Select(p => p.Key).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pending)
        {
            var baseKey = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(p.Name ?? "");
            if (string.IsNullOrEmpty(baseKey)) baseKey = "plugin-" + p.Id;
            var key = baseKey; var n = 2;
            while (used.Contains(key)) key = baseKey + "-" + n++;
            p.Key = key; used.Add(key);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Runs a hard-coded, idempotent DDL statement (CREATE TABLE/INDEX IF NOT EXISTS).</summary>
    private static async Task CreateTableIfMissingAsync(AppDbContext db, string sql)
    {
        try { await db.Database.ExecuteSqlRawAsync(sql); }
        catch { /* already exists / concurrent create — safe to ignore */ }
    }

    /// <summary>One-time upgrade back-fill for admin-managed languages: if the <c>i18n.languages</c>
    /// setting doesn't exist yet, activate the languages that ALREADY have published content — so an
    /// instance that was multilingual before this feature (e.g. published /en pages) keeps those
    /// languages in the public switcher + sitemap instead of silently dropping to German-only. The row
    /// is written exactly once (even empty), so later admin choices under Settings → Sprachen win.</summary>
    private static async Task BackfillLanguagesAsync(AppDbContext db)
    {
        try
        {
            if (await db.SiteSettings.AnyAsync(s => s.Key == MatCMS.Services.SettingKeys.Languages)) return;
            var present = await db.Pages
                .Where(p => p.IsPublished && p.Locale != MatCMS.Services.Localizer.DefaultCulture)
                .Select(p => p.Locale).Distinct().ToListAsync();
            var nonDefault = MatCMS.Services.Localizer.ParseActive(string.Join(",", present))
                .Where(c => c != MatCMS.Services.Localizer.DefaultCulture);
            db.SiteSettings.Add(new SiteSetting
            {
                Key = MatCMS.Services.SettingKeys.Languages,
                Value = string.Join(",", nonDefault)
            });
            await db.SaveChangesAsync();
        }
        catch { /* first-ever startup before Pages exist — safe to skip; runs again next boot */ }
    }

    private static async Task AddColumnIfMissingAsync(AppDbContext db, string table, string column, string type)
    {
        // table/column/type are hard-coded constants (never user input) — safe to inline.
        var sql = "ALTER TABLE \"" + table + "\" ADD COLUMN \"" + column + "\" " + type;
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists (fresh DB or a previous run) — nothing to do.
        }
    }

    /// <summary>
    /// One-time, idempotent migration of the legacy contact form onto the new Forms system:
    /// converts "contactform" blocks into "form" blocks pointing at a "kontakt" form and moves any
    /// old ContactSubmission rows into FormSubmission. Runs on every startup but is a no-op once done.
    /// </summary>
    private static async Task MigrateLegacyContactAsync(AppDbContext db)
    {
        var hasLegacyBlocks = await db.ContentBlocks.AnyAsync(b => b.BlockType == "contactform");
        var hasLegacySubs = await db.ContactSubmissions.AnyAsync();
        if (!hasLegacyBlocks && !hasLegacySubs) return;

        // Ensure the target "kontakt" form exists.
        var kontakt = await db.Forms.FirstOrDefaultAsync(f => f.Slug == "kontakt");
        if (kontakt is null)
        {
            kontakt = new Form { Name = "Kontakt", Slug = "kontakt", DefinitionJson = BuildContactFormDefinition() };
            db.Forms.Add(kontakt);
            await db.SaveChangesAsync();
        }

        if (hasLegacyBlocks)
        {
            foreach (var b in await db.ContentBlocks.Where(b => b.BlockType == "contactform").ToListAsync())
            {
                var heading = "Kontakt";
                try
                {
                    using var doc = JsonDocument.Parse(b.DataJson);
                    if (doc.RootElement.TryGetProperty("heading", out var h) && h.ValueKind == JsonValueKind.String)
                        heading = h.GetString() ?? heading;
                }
                catch { /* keep default heading */ }
                b.BlockType = "form";
                b.DataJson = Json(new { form = "kontakt", heading, intro = "" });
            }
        }

        if (hasLegacySubs)
        {
            var subs = await db.ContactSubmissions.ToListAsync();
            foreach (var s in subs)
            {
                db.FormSubmissions.Add(new FormSubmission
                {
                    FormId = kontakt.Id,
                    DataJson = Json(new { name = s.Name, email = s.Email, kategorie = s.Category ?? "", nachricht = s.Message }),
                    CreatedAt = s.CreatedAt,
                    IsRead = s.IsRead
                });
            }
            db.ContactSubmissions.RemoveRange(subs);
        }

        await db.SaveChangesAsync();
    }

    private const string ExampleComponentType = "cta-box";

    /// <summary>A ready-made demo component (a call-to-action box) for the component designer.</summary>
    private static Component BuildExampleComponent() => new()
    {
        Type = ExampleComponentType,
        Name = "CTA-Box (Beispiel)",
        Description = "Beispiel-Komponente: Überschrift, Text und ein Button.",
        FieldsJson = """
            [
              {"id":"heading","label":"Überschrift","type":"text"},
              {"id":"text","label":"Text","type":"textarea"},
              {"id":"button","label":"Button-Text","type":"text"},
              {"id":"url","label":"Button-Link","type":"url"}
            ]
            """,
        TemplateHtml = """
            <section class="section"><div class="container">
              <div style="border:1px solid var(--line);border-left:4px solid var(--accent);background:var(--bg-alt);padding:28px 32px;max-width:760px;margin:0 auto;">
                <h3 style="margin:0 0 10px;">{{heading}}</h3>
                <p style="margin:0 0 18px;color:var(--muted);">{{text}}</p>
                <a class="btn" href="{{url}}">{{button}}</a>
              </div>
            </div></section>
            """
    };

    private const string ModernTemplateName = "MatCMS Modern";
    private const string FerienTemplateName = "Ferienwohnung";
    private const string BusinessThemeName = "MatBusiness";
    private const string TechThemeName = "MatTech";
    private const string ArtThemeName = "MatArt";

    /// <summary>Adds a bundled theme to any database that doesn't already have it (idempotent).</summary>
    private static async Task EnsureThemeAsync(AppDbContext db, string name, Func<Template> build)
    {
        if (!await db.Templates.AnyAsync(t => t.Name == name))
            db.Templates.Add(build());
    }

    // Bundled starter themes. Each rides on the default (var-driven) site layout, so a distinct
    // palette + font pairing + radius + header treatment re-skins the whole site — corporate, dark
    // tech, and vibrant artistic looks that are clearly different from one another.
    private static Template BuildBusinessTemplate() => new()
    {
        Name = BusinessThemeName, IsActive = false,
        AccentColor = "#1e3a8a", SecondaryColor = "#0ea5e9",
        HeadingColor = "#0f172a", TextColor = "#334155",
        BackgroundColor = "#ffffff", AltBackground = "#eef2f7",
        HeadingFont = "Montserrat", BodyFont = "Open Sans",
        ButtonStyle = "solid", ButtonRadius = "4", ContainerWidth = "1240",
        HeaderBackground = "#0f172a", HeaderTextColor = "#ffffff", HeaderPadding = "18",
        CustomCss = """
            /* MatBusiness — crisp corporate look */
            .site-header a { color: #e2e8f0; }
            .site-header a:hover { color: #fff; }
            .btn { text-transform: uppercase; letter-spacing: .07em; font-weight: 700; }
            .section h2 { letter-spacing: -.01em; }
            .card, .column { box-shadow: 0 1px 2px rgba(15,23,42,.06); border: 1px solid #e2e8f0; }
            """
    };

    private static Template BuildTechTemplate() => new()
    {
        Name = TechThemeName, IsActive = false,
        AccentColor = "#22d3ee", SecondaryColor = "#a855f7",
        HeadingColor = "#f8fafc", TextColor = "#c7d2fe",
        BackgroundColor = "#0b1020", AltBackground = "#141b34",
        HeadingFont = "Poppins", BodyFont = "Roboto",
        ButtonStyle = "solid", ButtonRadius = "12", ContainerWidth = "1200",
        HeaderBackground = "#0b1020", HeaderTextColor = "#e2e8f0", HeaderPadding = "18",
        CustomCss = """
            /* MatTech — dark neon */
            body { background:
                radial-gradient(1200px 600px at 80% -10%, rgba(168,85,247,.18), transparent 60%),
                radial-gradient(900px 500px at -10% 10%, rgba(34,211,238,.16), transparent 55%),
                var(--bg); }
            .site-header a { color: #cbd5e1; }
            .site-header a:hover { color: var(--accent); }
            h1, h2, h3 { color: var(--black); }
            .btn { box-shadow: 0 0 0 1px rgba(34,211,238,.35), 0 8px 30px rgba(34,211,238,.18); font-weight: 600; }
            .card, .column { background: rgba(255,255,255,.03); border: 1px solid rgba(148,163,184,.18); }
            a { color: var(--accent); }
            """
    };

    private static Template BuildArtTemplate() => new()
    {
        Name = ArtThemeName, IsActive = false,
        AccentColor = "#ff5d8f", SecondaryColor = "#ffb703",
        HeadingColor = "#2b2d42", TextColor = "#4a4e69",
        BackgroundColor = "#fff8f0", AltBackground = "#ffe8d6",
        HeadingFont = "Poppins", BodyFont = "Nunito",
        ButtonStyle = "solid", ButtonRadius = "26", ContainerWidth = "1120",
        HeaderBackground = "", HeaderTextColor = "", HeaderPadding = "20",
        CustomCss = """
            /* MatArt — playful & vibrant */
            h1, h2, h3 { letter-spacing: -.02em; }
            .btn { font-weight: 800; box-shadow: 6px 6px 0 rgba(43,45,66,.14); }
            .card, .column { border-radius: 22px; box-shadow: 8px 8px 0 rgba(255,93,143,.12); }
            .section:nth-child(even) { background: var(--bg-alt); }
            .hero { background: linear-gradient(120deg, rgba(255,93,143,.10), rgba(255,183,3,.12)); }
            """
    };

    /// <summary>A warm, cosy holiday-let theme (sticky header, rounded cards, dark cosy footer).</summary>
    private static Template BuildFerienTemplate() => new()
    {
        Name = FerienTemplateName,
        IsActive = false,
        AccentColor = "#b0703f",
        SecondaryColor = "#7f9b6f",
        HeadingColor = "#33291e",
        TextColor = "#524839",
        BackgroundColor = "#fdfbf6",
        AltBackground = "#f1eadd",
        HeadingFont = "Poppins",
        BodyFont = "Nunito",
        ButtonStyle = "solid",
        ButtonRadius = "14",
        ContainerWidth = "1140",
        LayoutHtml = """
            <header class="fw-header">
              <div class="fw-wrap">
                <a class="fw-logo" href="/">{{logo}}</a>
                <nav class="fw-nav">{{#menu:primary}}<a href="{{url}}"{{target}}>{{label}}</a>{{/menu:primary}}</nav>
                <span class="fw-tools">{{toolbar}}</span>
              </div>
            </header>
            <main class="fw-main">{{content}}</main>
            <footer class="fw-footer">
              <div class="fw-wrap fw-footgrid">
                <div class="fw-footbrand">{{logo}}<p>{{footer_text}}</p></div>
                <nav class="fw-footnav">{{#menu:secondary}}<a href="{{url}}"{{target}}>{{label}}</a>{{/menu:secondary}}</nav>
              </div>
              <div class="fw-copy">© {{year}} {{site_name}}</div>
            </footer>
            """,
        MenuMapJson = """{"primary":"header","secondary":"footer"}""",
        CustomCss = """
            .fw-wrap { max-width: var(--max); margin: 0 auto; padding: 0 24px; }
            .fw-header { position: sticky; top: 0; z-index: 20; background: color-mix(in srgb, var(--bg) 86%, transparent); backdrop-filter: blur(8px); -webkit-backdrop-filter: blur(8px); border-bottom: 1px solid color-mix(in srgb, var(--black) 8%, transparent); }
            .fw-header .fw-wrap { display: flex; align-items: center; gap: 22px; min-height: 76px; }
            .fw-logo img { height: 46px; display: block; }
            .fw-nav { display: inline-flex; gap: 4px; margin-left: auto; flex-wrap: wrap; }
            .fw-nav a { text-decoration: none; color: var(--black); font-family: var(--font-head); font-weight: 600; font-size: 14.5px; padding: 9px 16px; border-radius: 999px; transition: background .15s ease, color .15s ease; }
            .fw-nav a:hover { background: var(--accent); color: #fff; }
            .fw-tools { display: inline-flex; gap: 12px; align-items: center; color: var(--accent); }
            .fw-tools .ti { font-size: 22px; }
            .fw-footer { margin-top: 72px; background: var(--black); color: #efe7da; }
            .fw-footgrid { display: flex; justify-content: space-between; gap: 40px; padding: 54px 24px; flex-wrap: wrap; }
            .fw-footbrand img { height: 42px; filter: brightness(0) invert(1); opacity: .9; }
            .fw-footbrand p { max-width: 320px; opacity: .8; margin: 12px 0 0; font-size: 14px; }
            .fw-footnav { display: flex; flex-direction: column; gap: 10px; }
            .fw-footnav a { color: #efe7da; text-decoration: none; opacity: .85; }
            .fw-footnav a:hover { opacity: 1; text-decoration: underline; }
            .fw-copy { border-top: 1px solid rgba(255,255,255,.14); text-align: center; padding: 18px; font-size: 13px; opacity: .7; }
            @media (max-width: 700px) { .fw-footgrid { flex-direction: column; gap: 24px; } .fw-header .fw-wrap { padding-top: 12px; padding-bottom: 12px; flex-wrap: wrap; } }

            /* Warm, cosy blocks */
            .btn { box-shadow: 0 10px 24px color-mix(in srgb, var(--accent) 26%, transparent); }
            .hero__inner h1 { letter-spacing: -.01em; }
            .service-grid { gap: 20px; background: transparent; border: none; }
            .service-card { background: #fff; border: 1px solid color-mix(in srgb, var(--black) 8%, transparent); border-radius: 18px; box-shadow: 0 12px 30px rgba(51,41,30,.06); transition: transform .18s ease, box-shadow .18s ease; }
            .service-card:hover { transform: translateY(-4px); box-shadow: 0 18px 44px rgba(51,41,30,.12); }
            .columns-grid { gap: 26px; }
            .column { background: #fff; border: 1px solid color-mix(in srgb, var(--black) 8%, transparent); border-radius: 18px; padding: 26px; box-shadow: 0 12px 30px rgba(51,41,30,.05); }
            .imagetext__media img { border-radius: 20px; box-shadow: 0 18px 40px rgba(51,41,30,.12); }
            """,
        CustomJs = ""
    };

    /// <summary>A distinct, modern alternative theme (gradient hero, rounded floating cards).</summary>
    private static Template BuildModernTemplate() => new()
    {
        Name = ModernTemplateName,
        IsActive = false,
        AccentColor = "#7c5cff",
        SecondaryColor = "#22d3ee",
        HeadingColor = "#0f172a",
        TextColor = "#334155",
        BackgroundColor = "#ffffff",
        AltBackground = "#f1f5f9",
        HeadingFont = "Poppins",
        BodyFont = "Inter",
        ButtonStyle = "solid",
        ButtonRadius = "10",
        ContainerWidth = "1200",
        // A genuinely different body structure — centred brand + pill navigation + gradient footer —
        // driven entirely by the CI variables from the managed <head> (accent, fonts). It uses named
        // menu slots so the slot→menu mapping is visible and editable in the template editor.
        LayoutHtml = """
            <div class="v2-topbar">
              <div class="v2-wrap">
                <span class="v2-brandline">{{site_name}}</span>
                <span class="v2-tools">{{toolbar}}</span>
              </div>
            </div>
            <header class="v2-header">
              <a class="v2-logo" href="/">{{logo}}</a>
              <nav class="v2-nav">
                {{#menu:primary}}<a class="v2-navlink" href="{{url}}"{{target}}><span class="v2-ico">{{icon}}</span>{{label}}</a>{{/menu:primary}}
              </nav>
            </header>
            <main class="v2-main">{{content}}</main>
            <footer class="v2-footer">
              <div class="v2-wrap v2-footgrid">
                <div class="v2-footbrand">{{logo}}<p>{{footer_text}}</p></div>
                <nav class="v2-footnav">
                  {{#menu:secondary}}<a href="{{url}}"{{target}}>{{label}}</a>{{/menu:secondary}}
                </nav>
              </div>
              <div class="v2-copy">© {{year}} {{site_name}}</div>
            </footer>
            """,
        MenuMapJson = """{"primary":"header","secondary":"footer"}""",
        CustomCss = """
            /* Block styling (shared with the default look) */
            .hero { background: linear-gradient(135deg, var(--accent), var(--accent-2)); }
            .hero__inner h1, .hero__inner p { color: #fff; }
            .service-grid { gap: 20px; background: transparent; border: none; }
            .service-card { border: 1px solid var(--line); border-radius: 16px; box-shadow: 0 10px 30px rgba(2,6,23,.06); transition: transform .18s ease, box-shadow .18s ease; }
            .service-card:hover { transform: translateY(-3px); box-shadow: 0 16px 40px rgba(2,6,23,.10); background: #fff; }
            .columns-grid { gap: 28px; }
            .btn { box-shadow: 0 8px 22px color-mix(in srgb, var(--accent) 30%, transparent); }

            /* V2 custom layout */
            .v2-wrap { max-width: var(--max); margin: 0 auto; padding: 0 24px; }
            .v2-topbar { background: var(--accent); color: #fff; font-size: 13px; }
            .v2-topbar .v2-wrap { display: flex; justify-content: space-between; align-items: center; height: 38px; }
            .v2-tools { display: inline-flex; gap: 12px; align-items: center; }
            .v2-tools a { color: #fff; display: inline-flex; }
            .v2-tools svg { width: 17px; height: 17px; }
            .v2-header { display: flex; flex-direction: column; align-items: center; gap: 16px; padding: 30px 24px 0; }
            .v2-logo img { height: 48px; display: block; }
            .v2-nav { display: inline-flex; flex-wrap: wrap; gap: 6px; background: var(--bg-alt); padding: 8px; border-radius: 999px; }
            .v2-navlink { display: inline-flex; align-items: center; gap: 7px; padding: 9px 18px; border-radius: 999px; text-decoration: none; color: var(--black); font-weight: 600; font-family: var(--font-head); font-size: 14px; transition: background .15s ease, color .15s ease; }
            .v2-navlink:hover { background: #fff; color: var(--accent); box-shadow: 0 4px 12px rgba(2,6,23,.08); }
            .v2-ico svg { width: 16px; height: 16px; display: block; }
            .v2-ico:empty { display: none; }
            .v2-main { max-width: var(--max); margin: 34px auto 0; padding: 0 24px; }
            .v2-footer { margin-top: 64px; background: linear-gradient(135deg, var(--accent), var(--accent-2)); color: #fff; }
            .v2-footgrid { display: flex; justify-content: space-between; gap: 40px; padding: 48px 24px; flex-wrap: wrap; }
            .v2-footbrand img { height: 40px; filter: brightness(0) invert(1); }
            .v2-footbrand p { max-width: 320px; opacity: .85; font-size: 14px; margin: 12px 0 0; }
            .v2-footnav { display: flex; flex-direction: column; gap: 10px; }
            .v2-footnav a { color: #fff; text-decoration: none; opacity: .9; }
            .v2-footnav a:hover { text-decoration: underline; opacity: 1; }
            .v2-copy { border-top: 1px solid rgba(255,255,255,.2); text-align: center; padding: 18px; font-size: 13px; }
            @media (max-width: 700px) { .v2-footgrid { flex-direction: column; gap: 24px; } }
            """,
        CustomJs = ""
    };

    private static SiteSetting S(string key, string value) => new() { Key = key, Value = value };

    private static MenuItem Mi(string menu, string label, string url, int order) =>
        new() { Menu = menu, Label = label, Url = url, SortOrder = order, Locale = Localizer.DefaultCulture };

    private static string Json(object data) => JsonSerializer.Serialize(data, JsonOpts);

    private static ContentBlock B(string type, int order, object data) =>
        new() { BlockType = type, SortOrder = order, DataJson = Json(data) };

    private static List<Page> BuildPages()
    {
        var pages = new List<Page>();

        // ---------- HOME ----------
        pages.Add(new Page
        {
            Title = "Start",
            Slug = "home",
            NavLabel = "Start",
            IsPublished = true,
            ShowInNav = true,
            NavOrder = 1,
            ShowInFooter = true,
            FooterOrder = 1,
            MetaDescription = "MatCMS – ein leichtgewichtiges, block-basiertes CMS.",
            Blocks =
            [
                B("hero", 0, new
                {
                    heading = "WILLKOMMEN BEI\nMATCMS",
                    subheading = "Ein leichtgewichtiges, block-basiertes CMS. Baue Seiten aus Blöcken, verwalte Menüs und Templates – und sichere alles per Backup.",
                    image = "",
                    buttonText = "Zum Admin",
                    buttonUrl = "/admin",
                    align = "left"
                }),
                B("richtext", 1, new
                {
                    heading = "Block-basiertes Bearbeiten",
                    body = "<p>Jede Seite besteht aus Blöcken, die du im Admin-Bereich hinzufügen, per Drag &amp; Drop sortieren und bearbeiten kannst. Dieser Text ist ein Beispiel-Block – ersetze ihn einfach durch deinen eigenen Inhalt.</p>",
                    align = "center",
                    width = "narrow"
                }),
                B("servicegrid", 2, new
                {
                    heading = "Funktionen",
                    intro = "",
                    columns = "4",
                    items = new object[]
                    {
                        new { title = "Block-Editor", text = "Seiten aus wiederverwendbaren Blöcken zusammenstellen." },
                        new { title = "Templates", text = "Farben, Schriften und Button-Stil per Template umschalten." },
                        new { title = "Menüs", text = "Haupt- und Footer-Menü frei verwalten." },
                        new { title = "Backup & Restore", text = "Alle Inhalte auswählen, exportieren und wiederherstellen." }
                    }
                }),
                B("cta", 3, new
                {
                    heading = "Jetzt loslegen",
                    text = "Melde dich im Admin-Bereich an und baue deine erste Seite.",
                    buttonText = "Zum Admin",
                    buttonUrl = "/admin"
                }),
            ]
        });

        // ---------- KONTAKT ----------
        pages.Add(new Page
        {
            Title = "Kontakt",
            Slug = "kontakt",
            NavLabel = "Kontakt",
            IsPublished = true,
            ShowInNav = true,
            NavOrder = 2,
            ShowInFooter = true,
            FooterOrder = 2,
            Blocks =
            [
                B("hero", 0, new { heading = "KONTAKT", subheading = "", image = "", buttonText = "", buttonUrl = "", align = "center" }),
                B("form", 1, new { form = "kontakt", heading = "Kontaktformular", intro = "" }),
            ]
        });

        return pages;
    }

    // Default "Kontakt" form definition (Name, E-Mail, Kategorie, Nachricht).
    private static string BuildContactFormDefinition()
    {
        var elements = new List<FormElement>
        {
            new() { Id = "name", Type = "text", Label = "Name", Required = true },
            new() { Id = "email", Type = "email", Label = "E-Mail", Required = true },
            new()
            {
                Id = "kategorie", Type = "select", Label = "Kategorie",
                Options =
                [
                    new FormOption { Value = "Allgemeine Anfrage", Label = "Allgemeine Anfrage" },
                    new FormOption { Value = "Service Anfrage", Label = "Service Anfrage" }
                ]
            },
            new() { Id = "nachricht", Type = "text", Label = "Nachricht", Required = true },
        };
        return FormDefinition.Serialize(elements);
    }
}
