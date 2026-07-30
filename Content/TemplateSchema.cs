using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatCMS.Models;

namespace MatCMS.Content;

/// <summary>
/// Versioned template FORMAT + per-page-type layout "parts".
///
/// A template is more than a colour theme: it also carries named layout parts (one per page type)
/// stored as HTML with <c>{{token}}</c> placeholders in <see cref="Template.PartsJson"/>. Today one
/// part exists — <see cref="PartPost"/> (the blog detail page, previously a fixed Razor view). More
/// page types can be added by declaring a new key + default here and rendering it from the page.
///
/// <see cref="Template.SchemaVersion"/> tracks the FORMAT a stored template was written in. When the
/// engine ships a newer format (<see cref="Current"/>), <see cref="Upgrade"/> converts older templates
/// step by step. Conversion runs automatically on startup (see the DbSeeder) so old instances (V1)
/// are migrated to the current version without a data reset.
/// </summary>
public static class TemplateSchema
{
    /// <summary>The template FORMAT version this engine writes. Bump when the parts format changes,
    /// and add a matching case to <see cref="Upgrade"/>.</summary>
    public const int Current = 2;

    /// <summary>Part key: the blog detail page ("/blog/{slug}").</summary>
    public const string PartPost = "post";

    /// <summary>Part key: the maintenance / "coming soon" page (served site-wide when maintenance mode
    /// is on). Unlike <see cref="PartPost"/> this is a FULL standalone HTML document (it is served on its
    /// own, not wrapped in the site layout).</summary>
    public const string PartMaintenance = "maintenance";

    /// <summary>All known part keys (whitelist for the editor + import sanitising).</summary>
    public static readonly IReadOnlyList<string> KnownParts = new[] { PartPost, PartMaintenance };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Only "post_*" tokens are substituted, and every value is precomputed by the caller (already
    // encoded where needed), so a single non-recursive pass is both safe and sufficient.
    private static readonly Regex PostTokenRx =
        new(@"\{\{(post_[a-zA-Z_]+)\}\}", RegexOptions.Compiled);

    // Generic {{token}} matcher (letters/digits/underscore) for parts whose token set isn't a single
    // prefix — e.g. the maintenance page mixes theme-colour, text and site tokens.
    private static readonly Regex TokenRx =
        new(@"\{\{([a-zA-Z0-9_]+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Built-in default layout for the blog detail page. Faithful to the original fixed markup, so a
    /// template that doesn't customise the part renders exactly as before. Available tokens:
    /// {{post_image}} {{post_date}} {{post_title}} {{post_excerpt}} {{post_body}} {{post_tags}}
    /// {{post_attachments}}.
    /// </summary>
    public const string DefaultPostPart =
        """
        <article class="section post-single">
          <div class="container narrow">
            {{post_image}}
            <p class="post-meta muted">{{post_date}}</p>
            <h1 class="post-title">{{post_title}}</h1>
            {{post_excerpt}}
            <div class="rich post-body">{{post_body}}</div>
            {{post_attachments}}
          </div>
        </article>
        """;

    /// <summary>
    /// Built-in default for the maintenance page: a full, standalone HTML document styled from the
    /// active template's colours + fonts (all supplied as {{tokens}} by the renderer). The admin can
    /// override it per template (file "maintenance.html"); the title/badge/message tokens come from the
    /// Settings → Wartung fields, so the text is editable without touching the template.
    /// Available tokens: {{lang}} {{site_name}} {{favicon}} {{logo}} {{maint_badge}} {{maint_title}}
    /// {{maint_message}} {{year}} and the theme tokens {{accent}} {{accent_dark}} {{accent_soft}}
    /// {{bg}} {{ink}} {{heading_color}} {{hairline}} {{heading_font}} {{body_font}} {{fonts_href}}.
    /// </summary>
    public const string DefaultMaintenancePart =
        """
        <!doctype html>
        <html lang="{{lang}}">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <meta name="robots" content="noindex" />
          <title>{{maint_title}} – {{site_name}}</title>
          {{favicon}}
          <link rel="preconnect" href="https://fonts.googleapis.com">
          <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
          <link href="{{fonts_href}}" rel="stylesheet">
          <style>
            :root{
              --accent:{{accent}}; --accent-dark:{{accent_dark}}; --accent-soft:{{accent_soft}};
              --bg:{{bg}}; --ink:{{ink}}; --heading:{{heading_color}}; --hairline:{{hairline}};
              --head-font:'{{heading_font}}',system-ui,-apple-system,Segoe UI,Roboto,sans-serif;
              --body-font:'{{body_font}}',system-ui,-apple-system,Segoe UI,Roboto,sans-serif;
            }
            *{box-sizing:border-box}
            html,body{height:100%}
            body{margin:0;font-family:var(--body-font);color:var(--ink);line-height:1.6;
              background:radial-gradient(1100px 560px at 50% -8%, var(--accent-soft), var(--bg) 62%);
              display:flex;align-items:center;justify-content:center;min-height:100vh;padding:24px}
            .m-card{max-width:560px;width:100%;text-align:center;background:var(--bg);
              border:1px solid var(--hairline);border-radius:20px;padding:52px 40px;
              box-shadow:0 30px 80px rgba(0,0,0,.12)}
            .m-logo{max-height:66px;width:auto;margin:0 auto 30px;display:block}
            .m-badge{display:inline-block;font-size:12px;font-weight:700;letter-spacing:.09em;
              text-transform:uppercase;color:var(--accent);background:var(--accent-soft);
              padding:6px 15px;border-radius:999px;margin-bottom:22px}
            h1{font-family:var(--head-font);color:var(--heading);margin:0 0 14px;
              font-size:clamp(1.6rem,4vw,2.3rem);line-height:1.2}
            p{margin:0 auto;max-width:46ch;font-size:1.06rem;opacity:.85}
            .m-rule{height:4px;width:66px;border-radius:999px;margin:30px auto 0;
              background:linear-gradient(90deg,var(--accent),var(--accent-dark))}
            .m-foot{margin-top:34px;font-size:13px;opacity:.55}
          </style>
        </head>
        <body>
          <main class="m-card">
            {{logo}}
            <span class="m-badge">{{maint_badge}}</span>
            <h1>{{maint_title}}</h1>
            <p>{{maint_message}}</p>
            <div class="m-rule"></div>
            <p class="m-foot">© {{year}} {{site_name}}</p>
          </main>
        </body>
        </html>
        """;

    /// <summary>Default layout for a given part key (empty for unknown keys).</summary>
    public static string DefaultFor(string key) => key switch
    {
        PartPost => DefaultPostPart,
        PartMaintenance => DefaultMaintenancePart,
        _ => ""
    };

    /// <summary>Parse the stored parts map ({ "post": "&lt;html&gt;", … }); empty/invalid → empty map.</summary>
    public static Dictionary<string, string> Parse(string? partsJson)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(partsJson)) return d;
        try
        {
            using var doc = JsonDocument.Parse(partsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                        d[p.Name] = p.Value.GetString() ?? "";
        }
        catch { /* malformed → treated as no parts */ }
        return d;
    }

    /// <summary>Serialise a parts map to JSON for storage (keeps only known, non-empty parts).</summary>
    public static string Serialize(IReadOnlyDictionary<string, string> parts)
    {
        var clean = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in KnownParts)
            if (parts.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                clean[key] = v;
        return JsonSerializer.Serialize(clean, WriteOpts);
    }

    /// <summary>The layout HTML actually used for <paramref name="key"/>: the template's stored
    /// override if it set one, otherwise the built-in default.</summary>
    public static string EffectivePart(Template tpl, string key)
    {
        var parts = Parse(tpl.PartsJson);
        return parts.TryGetValue(key, out var html) && !string.IsNullOrWhiteSpace(html)
            ? html
            : DefaultFor(key);
    }

    /// <summary>Replace <c>{{post_*}}</c> tokens with precomputed values (unknown tokens → empty).
    /// Single pass: inserted values are never re-scanned, so a value may safely contain braces.</summary>
    public static string RenderPost(string partHtml, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(partHtml)) return "";
        return PostTokenRx.Replace(partHtml,
            m => tokens.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : "");
    }

    /// <summary>Replace any <c>{{token}}</c> with a precomputed value (unknown tokens → empty). Like
    /// <see cref="RenderPost"/> but not limited to the <c>post_</c> prefix; used for the maintenance page.
    /// Single pass — inserted values are never re-scanned.</summary>
    public static string RenderTokens(string partHtml, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(partHtml)) return "";
        return TokenRx.Replace(partHtml,
            m => tokens.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : "");
    }

    /// <summary>
    /// Convert a template from its stored <see cref="Template.SchemaVersion"/> up to <see cref="Current"/>,
    /// applying each format step in order. Idempotent: a template already at the current version is
    /// unchanged. Returns true if anything changed (so the caller knows to persist + log).
    /// </summary>
    public static bool Upgrade(Template tpl)
    {
        var changed = false;
        // Guard against runaway loops from bad data (version above Current or negative).
        var guard = 0;
        while (tpl.SchemaVersion < Current && guard++ < 64)
        {
            switch (tpl.SchemaVersion)
            {
                // V1 → V2: introduce the per-page-type parts system. Nothing to transform in existing
                // data — just ensure PartsJson is a valid object; the blog detail page falls back to the
                // built-in default until the admin customises it.
                case <= 1:
                    if (string.IsNullOrWhiteSpace(tpl.PartsJson)) tpl.PartsJson = "{}";
                    tpl.SchemaVersion = 2;
                    break;

                // Unknown intermediate version → jump straight to current (never leave it stuck).
                default:
                    tpl.SchemaVersion = Current;
                    break;
            }
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(tpl.PartsJson)) { tpl.PartsJson = "{}"; changed = true; }
        return changed;
    }
}
