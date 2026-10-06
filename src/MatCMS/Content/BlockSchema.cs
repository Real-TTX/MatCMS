using MatCMS.Data;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Content;

/// <summary>
/// The field schema of a block as the JS field renderer (<c>MatBlockFields.build</c>) reads it: every
/// localisation key resolved to display text, dynamic option lists (forms, media tags, theme colours)
/// filled in from the database. ONE place for it, shared by the classic page editor and editor v2 —
/// two copies would let the same block offer different fields depending on which editor opened it.
/// </summary>
public static class BlockSchema
{
    /// <summary>Layout options every TOP-LEVEL block gets on top of its own fields (Shopify-style).
    /// Labels are literal German; the localizer passes text without a matching key through.</summary>
    public static readonly BlockField[] GlobalLayoutFields =
    [
        new BlockField { Id = "_width", Label = "Breite", Type = FieldType.Select, Default = "",
            Options = [ new("", "Normal"), new("narrow", "Schmal"), new("full", "Volle Breite") ] },
        new BlockField { Id = "_spaceTop", Label = "Abstand oben", Type = FieldType.Select, Default = "",
            Options = [ new("", "Standard"), new("s", "Klein"), new("m", "Mittel"), new("l", "Groß") ] },
        new BlockField { Id = "_spaceBottom", Label = "Abstand unten", Type = FieldType.Select, Default = "",
            Options = [ new("", "Standard"), new("s", "Klein"), new("m", "Mittel"), new("l", "Groß") ] },
    ];

    /// <summary>Per-block custom CSS (advanced) — for EVERY block, nested elements included (see BlockCss):
    /// scoped under the block's own `.blk-&lt;id&gt;` via native CSS nesting, so rules never leak to other
    /// blocks. Bare declarations style the block itself, `&amp; h2 { … }` what is inside it.</summary>
    public static readonly BlockField CssField =
        new BlockField { Id = "_css", Label = "Custom CSS", Type = FieldType.Textarea,
            Placeholder = "background: #f6f6f6;\n& h2 { color: #289068; }",
            Help = "Nur für diesen Block. Ohne Selektor gilt es für den Block selbst, mit & für sein Inneres, z. B. „& .btn { … }“." };

    /// <summary>The dynamic option lists the given block definitions ask for (by OptionsSource), loaded once.</summary>
    public static async Task<Dictionary<string, List<SelectOption>>> LoadSourcesAsync(AppDbContext db, IEnumerable<BlockDefinition> defs)
    {
        var wanted = defs.SelectMany(d => Flatten(d.Fields)).Select(f => f.OptionsSource)
            .Where(s => s is not null).Select(s => s!).ToHashSet(StringComparer.Ordinal);
        var sources = new Dictionary<string, List<SelectOption>>(StringComparer.Ordinal);
        if (wanted.Contains("forms"))
            sources["forms"] = await db.Forms.AsNoTracking().OrderBy(f => f.Name).Select(f => new SelectOption(f.Slug, f.Name)).ToListAsync();
        if (wanted.Contains("mediaTags"))
        {
            // No "all media" entry: the field using this is a multi-select, where nothing ticked
            // already means all.
            var tags = await db.Media.AsNoTracking().Select(m => m.Tags).ToListAsync();
            sources["mediaTags"] = tags.SelectMany(TagUtil.Split).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Select(t => new SelectOption(t, t)).ToList();
        }
        if (wanted.Contains("themeColors"))
        {
            var t = await db.Templates.AsNoTracking().FirstOrDefaultAsync(x => x.IsActive)
                    ?? await db.Templates.AsNoTracking().FirstOrDefaultAsync();
            var opts = new List<SelectOption> { new("", "Standard") };
            if (t is not null)
            {
                void Add(string? val, string label) { if (!string.IsNullOrWhiteSpace(val)) opts.Add(new SelectOption(val!, label)); }
                Add(t.AccentColor, "Akzent");
                Add(t.SecondaryColor, "Sekundär");
                Add(t.HeadingColor, "Überschrift");
                Add(t.TextColor, "Text");
                Add(t.BackgroundColor, "Hintergrund");
                Add(t.AltBackground, "Alt-Hintergrund");
                Add("#ffffff", "Weiß");
                Add("#111111", "Dunkel");
            }
            sources["themeColors"] = opts;
        }
        return sources;
    }

    /// <summary>The full schema of one block: its own fields, the global layout options when it can stand
    /// at the top level (width/spacing only exist there), and custom CSS for every block.</summary>
    public static List<object> For(BlockDefinition def, Localizer t, IReadOnlyDictionary<string, List<SelectOption>>? sources)
    {
        var list = def.Fields.Select(f => Localize(f, t, sources)).ToList();
        if (!def.ChildOnly) list.AddRange(GlobalLayoutFields.Select(f => Localize(f, t, sources)));
        list.Add(Localize(CssField, t, sources));
        return list;
    }

    /// <summary>A JSON-friendly copy of a field with all localisation keys resolved to text.</summary>
    public static object Localize(BlockField f, Localizer t, IReadOnlyDictionary<string, List<SelectOption>>? sources)
    {
        // A dynamic source replaces the static options; its labels are already display text.
        var options = (f.OptionsSource is not null && sources is not null && sources.TryGetValue(f.OptionsSource, out var dyn))
            ? dyn.Select(o => new { value = o.Value, label = o.Label }).ToList()
            : f.Options.Select(o => new { value = o.Value, label = t[o.Label] }).ToList();
        return new
        {
            id = f.Id,
            label = t[f.Label],
            type = f.Type,
            placeholder = f.Placeholder,
            help = f.Help is null ? null : t[f.Help],
            @default = f.Default,
            options,
            showWhen = f.ShowWhenField is null ? null : new { field = f.ShowWhenField, value = f.ShowWhenValue },
            itemFields = f.ItemFields.Select(x => Localize(x, t, sources)).ToList(),
            itemLabel = t[f.ItemLabel]
        };
    }

    private static IEnumerable<BlockField> Flatten(IEnumerable<BlockField> fields) =>
        fields.SelectMany(f => new[] { f }.Concat(Flatten(f.ItemFields)));
}
