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

    /// <summary>All known part keys (whitelist for the editor + import sanitising).</summary>
    public static readonly IReadOnlyList<string> KnownParts = new[] { PartPost };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Only "post_*" tokens are substituted, and every value is precomputed by the caller (already
    // encoded where needed), so a single non-recursive pass is both safe and sufficient.
    private static readonly Regex PostTokenRx =
        new(@"\{\{(post_[a-zA-Z_]+)\}\}", RegexOptions.Compiled);

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

    /// <summary>Default layout for a given part key (empty for unknown keys).</summary>
    public static string DefaultFor(string key) => key switch
    {
        PartPost => DefaultPostPart,
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
