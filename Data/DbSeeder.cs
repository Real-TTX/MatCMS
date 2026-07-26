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
        if (!await db.Templates.AnyAsync(t => t.Name == ModernTemplateName))
        {
            db.Templates.Add(BuildModernTemplate());
        }

        // A ready-made example component so the component designer has something to look at.
        if (!await db.Components.AnyAsync(c => c.Type == ExampleComponentType))
        {
            db.Components.Add(BuildExampleComponent());
        }

        // A ready-made example plugin (a small todo manager) to showcase the plugin system.
        if (!await db.Plugins.AnyAsync(p => p.Name == TodoPluginName))
        {
            db.Plugins.Add(BuildTodoPlugin());
        }

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

    private const string TodoPluginName = "Todo-Verwaltung (Beispiel)";

    /// <summary>A demo plugin: a todo manager with an admin page, a content block and logging.</summary>
    private static Plugin BuildTodoPlugin() => new()
    {
        Name = TodoPluginName,
        Description = "Beispiel-Plugin: Todo-Verwaltung + Block + Log.",
        Enabled = true,
        Code = """
            using System.Text;
            using System.Text.Json.Nodes;

            Log("Todo-Plugin geladen.");
            AddAdminMenu("Todos", "/admin/plugin/todos", "✅");

            JsonArray Load(PluginRequest req)
            {
                var db = req.Service<AppDbContext>();
                var s = db.SiteSettings.FirstOrDefault(x => x.Key == "plugin.todos");
                if (s == null || string.IsNullOrWhiteSpace(s.Value)) return new JsonArray();
                try { return JsonNode.Parse(s.Value) as JsonArray ?? new JsonArray(); } catch { return new JsonArray(); }
            }
            void Save(PluginRequest req, JsonArray todos)
            {
                var db = req.Service<AppDbContext>();
                var s = db.SiteSettings.FirstOrDefault(x => x.Key == "plugin.todos");
                if (s == null) db.SiteSettings.Add(new SiteSetting { Key = "plugin.todos", Value = todos.ToJsonString() });
                else s.Value = todos.ToJsonString();
                db.SaveChanges();
            }
            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

            AddAdminPage("todos", req =>
            {
                var todos = Load(req);
                if (req.IsPost)
                {
                    var action = req.F("action");
                    var id = int.TryParse(req.F("id"), out var pid) ? pid : 0;
                    if (action == "add")
                    {
                        var text = req.F("text").Trim();
                        if (text.Length > 0)
                        {
                            int max = 0;
                            foreach (var t in todos) { var v = t?["id"]?.GetValue<int>() ?? 0; if (v > max) max = v; }
                            todos.Add(new JsonObject { ["id"] = max + 1, ["text"] = text, ["done"] = false });
                        }
                    }
                    else if (action == "toggle")
                    {
                        foreach (var t in todos)
                            if ((t?["id"]?.GetValue<int>() ?? -1) == id)
                                t!["done"] = !(t["done"]?.GetValue<bool>() ?? false);
                    }
                    else if (action == "delete")
                    {
                        JsonNode? rem = null;
                        foreach (var t in todos) if ((t?["id"]?.GetValue<int>() ?? -1) == id) rem = t;
                        if (rem != null) todos.Remove(rem);
                    }
                    Save(req, todos);
                    Log($"Todo: {action} (id {id})");
                    return "";
                }

                var sb = new StringBuilder();
                sb.Append("<div class='page-head'><h1>✅ Todos</h1></div>");
                sb.Append("<div class='card'><form method='post' class='form-row' style='align-items:end;'><input type='hidden' name='action' value='add'/>");
                sb.Append("<div class='form-field' style='flex:1;'><label>Neues Todo</label><input name='text' required/></div>");
                sb.Append("<div><button class='btn' type='submit'>Hinzufügen</button></div></form>");
                sb.Append("<div class='block-list' style='margin-top:16px;'>");
                int count = 0;
                foreach (var t in todos)
                {
                    count++;
                    var id = t?["id"]?.GetValue<int>() ?? 0;
                    var text = Enc(t?["text"]?.GetValue<string>());
                    var done = t?["done"]?.GetValue<bool>() ?? false;
                    var style = done ? "text-decoration:line-through;color:#999;" : "";
                    sb.Append("<div class='block-item'>");
                    sb.Append($"<form method='post' class='inline-form'><input type='hidden' name='action' value='toggle'/><input type='hidden' name='id' value='{id}'/><button class='btn btn-sm btn-ghost' type='submit'>{(done ? "☑" : "☐")}</button></form>");
                    sb.Append($"<div class='b-info' style='flex:1;'><div class='b-type' style='{style}'>{text}</div></div>");
                    sb.Append($"<form method='post' class='inline-form'><input type='hidden' name='action' value='delete'/><input type='hidden' name='id' value='{id}'/><button class='btn btn-sm btn-danger' type='submit'>✕</button></form>");
                    sb.Append("</div>");
                }
                if (count == 0) sb.Append("<p class='muted'>Noch keine Todos.</p>");
                sb.Append("</div></div>");
                return sb.ToString();
            });

            AddBlock("todo-list", "Todo-Liste", "Zeigt offene Todos (Todo-Plugin).", req =>
            {
                var todos = Load(req);
                var sb = new StringBuilder();
                sb.Append("<section class='section'><div class='container'><h2>Offene Todos</h2><ul>");
                int open = 0;
                foreach (var t in todos)
                {
                    if (t?["done"]?.GetValue<bool>() ?? false) continue;
                    open++;
                    sb.Append("<li>" + Enc(t?["text"]?.GetValue<string>()) + "</li>");
                }
                if (open == 0) sb.Append("<li>Keine offenen Todos 🎉</li>");
                sb.Append("</ul></div></section>");
                return sb.ToString();
            });
            """
    };

    private const string ModernTemplateName = "MatCMS Modern";

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
        CustomCss = """
            .hero { background: linear-gradient(135deg, var(--accent), var(--accent-2)); }
            .hero__inner h1, .hero__inner p { color: #fff; }
            .service-grid { gap: 20px; background: transparent; border: none; }
            .service-card { border: 1px solid var(--line); border-radius: 16px; box-shadow: 0 10px 30px rgba(2,6,23,.06); transition: transform .18s ease, box-shadow .18s ease; }
            .service-card:hover { transform: translateY(-3px); box-shadow: 0 16px 40px rgba(2,6,23,.10); background: #fff; }
            .columns-grid { gap: 28px; }
            .btn { box-shadow: 0 8px 22px color-mix(in srgb, var(--accent) 30%, transparent); }
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
