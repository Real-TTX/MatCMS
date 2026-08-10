using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Data;

/// <summary>
/// Fills the catalogue with a starter set: blocks, themes and one plugin.
/// <para>Every entry is added only when its identity is missing, and nothing is ever updated — an
/// operator who changed a catalogue entry must not find it reset on the next start. That is also why
/// this is separate from the rest of the seeding: it is CONTENT, and content the operator owns the
/// moment they touch it.</para>
/// </summary>
public static class StoreSeeder
{
    public static async Task SeedAsync(AppDbContext db)
    {
        foreach (var c in Components())
        {
            if (await db.StoreComponents.AnyAsync(x => x.Type == c.Type)) continue;
            db.StoreComponents.Add(c);
        }

        foreach (var t in Templates())
        {
            if (await db.StoreTemplates.AnyAsync(x => x.Name == t.Name)) continue;
            db.StoreTemplates.Add(t);
        }

        foreach (var p in Plugins())
        {
            if (await db.StorePlugins.AnyAsync(x => x.Key == p.Key)) continue;
            db.StorePlugins.Add(p);
        }

        await db.SaveChangesAsync();
    }

    // --- Blocks ---------------------------------------------------------------------------------
    // Field ids are what {{placeholders}} refer to and what already-placed blocks on a live site
    // point at, so they are never renamed once shipped. Markup is deliberately plain and uses the
    // admin's own CSS variables, so a block takes on whatever theme the site runs.

    private static IEnumerable<StoreComponent> Components() =>
    [
        new StoreComponent
        {
            Type = "feature-trio",
            Name = "Drei Vorteile",
            Description = "Drei kurze Punkte nebeneinander — für das, was eine Seite ausmacht.",
            Icon = "layout-grid",
            FieldsJson = """
                [
                  {"id":"title","label":"Überschrift","type":"text"},
                  {"id":"one_title","label":"1 · Titel","type":"text"},
                  {"id":"one_text","label":"1 · Text","type":"textarea"},
                  {"id":"two_title","label":"2 · Titel","type":"text"},
                  {"id":"two_text","label":"2 · Text","type":"textarea"},
                  {"id":"three_title","label":"3 · Titel","type":"text"},
                  {"id":"three_text","label":"3 · Text","type":"textarea"}
                ]
                """,
            TemplateHtml = """
                <section class="section"><div class="container">
                  <h2 style="text-align:center;margin-bottom:28px;">{{title}}</h2>
                  <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:28px;">
                    <div><h3 style="margin:0 0 8px;">{{one_title}}</h3><p style="margin:0;color:var(--muted);">{{one_text}}</p></div>
                    <div><h3 style="margin:0 0 8px;">{{two_title}}</h3><p style="margin:0;color:var(--muted);">{{two_text}}</p></div>
                    <div><h3 style="margin:0 0 8px;">{{three_title}}</h3><p style="margin:0;color:var(--muted);">{{three_text}}</p></div>
                  </div>
                </div></section>
                """,
        },
        new StoreComponent
        {
            Type = "quote-card",
            Name = "Zitat",
            Description = "Eine Stimme von außen: Zitat, Name, Zusatz.",
            Icon = "quote",
            FieldsJson = """
                [
                  {"id":"quote","label":"Zitat","type":"textarea"},
                  {"id":"author","label":"Name","type":"text"},
                  {"id":"role","label":"Zusatz (Ort, Rolle, Datum)","type":"text"}
                ]
                """,
            TemplateHtml = """
                <section class="section"><div class="container">
                  <figure style="max-width:720px;margin:0 auto;text-align:center;">
                    <blockquote style="margin:0;font-size:22px;line-height:1.5;">„{{quote}}“</blockquote>
                    <figcaption style="margin-top:14px;color:var(--muted);">
                      <strong>{{author}}</strong> · {{role}}
                    </figcaption>
                  </figure>
                </div></section>
                """,
        },
        new StoreComponent
        {
            Type = "image-text",
            Name = "Bild mit Text",
            Description = "Bild links, Text rechts — der Arbeitspflicht-Block jeder Seite.",
            Icon = "photo",
            FieldsJson = """
                [
                  {"id":"image","label":"Bild","type":"image"},
                  {"id":"title","label":"Überschrift","type":"text"},
                  {"id":"text","label":"Text","type":"richtext"},
                  {"id":"link_text","label":"Link-Text","type":"text"},
                  {"id":"link_url","label":"Link-Ziel","type":"url"}
                ]
                """,
            TemplateHtml = """
                <section class="section"><div class="container">
                  <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:36px;align-items:center;">
                    <img src="{{image}}" alt="{{title}}" style="width:100%;height:auto;display:block;" />
                    <div>
                      <h2 style="margin:0 0 12px;">{{title}}</h2>
                      <div>{{text}}</div>
                      <p style="margin:18px 0 0;"><a class="btn" href="{{link_url}}">{{link_text}}</a></p>
                    </div>
                  </div>
                </div></section>
                """,
        },
        new StoreComponent
        {
            Type = "faq-item",
            Name = "Frage & Antwort",
            Description = "Eine aufklappbare Frage. Mehrere untereinander ergeben eine FAQ.",
            Icon = "help",
            FieldsJson = """
                [
                  {"id":"question","label":"Frage","type":"text"},
                  {"id":"answer","label":"Antwort","type":"richtext"}
                ]
                """,
            // <details> rather than JavaScript: it opens and closes on its own, works without
            // scripting and is announced correctly by screen readers.
            TemplateHtml = """
                <div class="container" style="max-width:760px;">
                  <details style="border-bottom:1px solid var(--line);padding:14px 0;">
                    <summary style="cursor:pointer;font-weight:600;">{{question}}</summary>
                    <div style="margin-top:10px;color:var(--muted);">{{answer}}</div>
                  </details>
                </div>
                """,
        },
        new StoreComponent
        {
            Type = "info-strip",
            Name = "Info-Streifen",
            Description = "Ein farbig abgesetzter Hinweis über die volle Breite — Öffnungszeiten, Anreise, ein Hinweis zur Saison.",
            Icon = "info-circle",
            FieldsJson = """
                [
                  {"id":"label","label":"Kurzwort links","type":"text"},
                  {"id":"text","label":"Text","type":"text"},
                  {"id":"link_text","label":"Link-Text","type":"text"},
                  {"id":"link_url","label":"Link-Ziel","type":"url"}
                ]
                """,
            TemplateHtml = """
                <div style="background:var(--bg-alt);border-top:1px solid var(--line);border-bottom:1px solid var(--line);">
                  <div class="container" style="display:flex;gap:14px;align-items:center;flex-wrap:wrap;padding-top:14px;padding-bottom:14px;">
                    <strong style="color:var(--accent);">{{label}}</strong>
                    <span style="flex:1;">{{text}}</span>
                    <a href="{{link_url}}">{{link_text}}</a>
                  </div>
                </div>
                """,
        },
        new StoreComponent
        {
            Type = "price-card",
            Name = "Preis-Karte",
            Description = "Preis, Zeitraum und was enthalten ist.",
            Icon = "tag",
            FieldsJson = """
                [
                  {"id":"title","label":"Titel","type":"text"},
                  {"id":"price","label":"Preis","type":"text"},
                  {"id":"unit","label":"Einheit (z. B. pro Nacht)","type":"text"},
                  {"id":"includes","label":"Enthalten","type":"textarea"},
                  {"id":"link_text","label":"Button-Text","type":"text"},
                  {"id":"link_url","label":"Button-Ziel","type":"url"}
                ]
                """,
            TemplateHtml = """
                <div style="border:1px solid var(--line);padding:28px;max-width:360px;">
                  <h3 style="margin:0 0 6px;">{{title}}</h3>
                  <p style="margin:0 0 4px;font-size:34px;font-weight:700;line-height:1;">{{price}}</p>
                  <p style="margin:0 0 16px;color:var(--muted);">{{unit}}</p>
                  <p style="margin:0 0 20px;white-space:pre-line;">{{includes}}</p>
                  <a class="btn" href="{{link_url}}">{{link_text}}</a>
                </div>
                """,
        },
    ];

    // --- Themes ---------------------------------------------------------------------------------
    // Only the values a template carries; no layout. A theme that shipped its own LayoutHtml would
    // override whatever a site had built, which is the one thing a rolled-out design must not do.

    private static IEnumerable<StoreTemplate> Templates() =>
    [
        new StoreTemplate
        {
            Name = "Küste",
            Description = "Ruhiges Blau, warmer Sand, viel Weißraum — für Unterkunft und Reise.",
            AccentColor = "#2f6f8f", SecondaryColor = "#d9c3a0",
            HeadingFont = "Geologica", BodyFont = "Inter", ButtonStyle = "solid",
            HeadingColor = "#14303d", TextColor = "#2b3a41",
            BackgroundColor = "#ffffff", AltBackground = "#f2f6f8",
            ContainerWidth = "1140", ButtonRadius = "3",
            HeaderBackground = "#ffffff", HeaderTextColor = "#14303d", HeaderPadding = "18",
        },
        new StoreTemplate
        {
            Name = "Schiefer",
            Description = "Dunkel, sachlich, hoher Kontrast — für Handwerk, Technik und Portfolios.",
            AccentColor = "#e0a33e", SecondaryColor = "#8a8f98",
            HeadingFont = "Geologica", BodyFont = "Inter", ButtonStyle = "solid",
            HeadingColor = "#0f1115", TextColor = "#2a2d33",
            BackgroundColor = "#ffffff", AltBackground = "#eceef1",
            ContainerWidth = "1180", ButtonRadius = "0",
            HeaderBackground = "#0f1115", HeaderTextColor = "#ffffff", HeaderPadding = "16",
        },
        new StoreTemplate
        {
            Name = "Wiese",
            Description = "Frisches Grün, runde Kanten, freundlich — für Vereine, Praxen und Gastronomie.",
            AccentColor = "#4e8b52", SecondaryColor = "#c8dcae",
            HeadingFont = "Geologica", BodyFont = "Inter", ButtonStyle = "solid",
            HeadingColor = "#1e2b1f", TextColor = "#33403a",
            BackgroundColor = "#ffffff", AltBackground = "#f3f7ef",
            ContainerWidth = "1100", ButtonRadius = "10",
            HeaderBackground = "#ffffff", HeaderTextColor = "#1e2b1f", HeaderPadding = "20",
        },
    ];

    // --- Plugins --------------------------------------------------------------------------------

    private static IEnumerable<StorePlugin> Plugins()
    {
        yield return Plugin(
            key: "wartungsfenster",
            name: "Wartungsfenster",
            version: "1.0",
            description: "Schreibt beim Start eine Zeile ins Plugin-Log und stellt einen Menüpunkt bereit. Bewusst harmlos: ein Katalog, der ausführbaren Code verteilt, sollte mit dem kleinstmöglichen anfangen.",
            code: """
                // Ein absichtlich winziges Beispiel-Plugin. Es zeigt die drei Dinge, die ein Plugin
                // überhaupt tun kann, und tut sonst nichts — importierte Plugins sind auf der Instanz
                // ohnehin deaktiviert, weil Plugin-Code serverseitig läuft.
                Log("Wartungsfenster-Plugin geladen.");
                AddAdminMenu("Wartung", "/admin/plugin/wartung", "🛠️");
                """);
    }

    /// <summary>
    /// Packs one plugin into the exact ZIP the instance's importer expects: a single
    /// <c>plugin.json</c> at the root. Built here rather than copied from a file, so the catalogue
    /// entry cannot go stale against the format.
    /// </summary>
    private static StorePlugin Plugin(string key, string name, string version, string description, string code)
    {
        var manifest = new
        {
            Format = 1,
            Name = name,
            Key = key,
            Version = version,
            Description = description,
            Code = code,
        };

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(MatCMS.Shared.PluginBundle.ManifestEntry);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        return new StorePlugin
        {
            Key = key,
            Name = name,
            Version = version,
            Description = description,
            Bundle = ms.ToArray(),
            UploadedAt = DateTime.UtcNow,
        };
    }
}
