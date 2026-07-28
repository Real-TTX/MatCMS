namespace MatCMS.Content;

/// <summary>
/// Central catalogue of available content blocks and their editable fields.
/// Add a new block here + a matching partial under Pages/Shared/Blocks to extend the CMS.
/// </summary>
public class BlockRegistry
{
    // Built-in blocks — built once (static), shared across requests.
    private static readonly IReadOnlyList<BlockDefinition> Builtin = Build();

    /// <summary>The reserved built-in block type slugs (a component may not reuse these).</summary>
    public static readonly HashSet<string> BuiltinTypes =
        Builtin.Select(b => b.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The built-in block definitions ("system components") — read-only reference list.</summary>
    public static IReadOnlyList<BlockDefinition> Builtins => Builtin;

    private const string SvgPlugin = @"<path d=""M9 3v5""/><path d=""M15 3v5""/><rect x=""6"" y=""8"" width=""12"" height=""8"" rx=""2""/><path d=""M12 16v5""/>";

    private readonly MatCMS.Data.AppDbContext _db;
    private readonly MatCMS.Services.PluginRegistry _plugins;
    private IReadOnlyList<BlockDefinition>? _all;

    public BlockRegistry(MatCMS.Data.AppDbContext db, MatCMS.Services.PluginRegistry plugins)
    {
        _db = db;
        _plugins = plugins;
    }

    /// <summary>All block definitions: built-in blocks, user-defined components, and plugin blocks.</summary>
    public IReadOnlyList<BlockDefinition> All => _all ??= BuildAll();

    private IReadOnlyList<BlockDefinition> BuildAll()
    {
        var list = new List<BlockDefinition>(Builtin);
        foreach (var c in _db.Components.OrderBy(c => c.Name).ToList())
        {
            list.Add(ComponentDefinition.FromComponent(c));
        }
        foreach (var pb in _plugins.Blocks)
        {
            list.Add(new BlockDefinition
            {
                Type = pb.Type,
                Name = pb.Name,
                Description = pb.Description,
                Svg = SvgPlugin,
                Partial = "(plugin)",
                PluginRender = pb.Render
            });
        }
        return list;
    }

    public BlockDefinition? Get(string type) =>
        All.FirstOrDefault(b => string.Equals(b.Type, type, StringComparison.OrdinalIgnoreCase));

    // Feather-style 24x24 stroke icons (rendered inside <svg fill="none" stroke="currentColor">).
    private const string SvgHero = @"<rect x=""3"" y=""4"" width=""18"" height=""16"" rx=""2""/><path d=""M7 9h10""/><path d=""M7 13h6""/>";
    private const string SvgText = @"<path d=""M4 6h16""/><path d=""M4 12h16""/><path d=""M4 18h11""/>";
    private const string SvgColumns = @"<rect x=""3"" y=""4"" width=""7"" height=""16"" rx=""1""/><rect x=""14"" y=""4"" width=""7"" height=""16"" rx=""1""/>";
    private const string SvgGrid = @"<rect x=""3"" y=""3"" width=""7"" height=""7"" rx=""1""/><rect x=""14"" y=""3"" width=""7"" height=""7"" rx=""1""/><rect x=""3"" y=""14"" width=""7"" height=""7"" rx=""1""/><rect x=""14"" y=""14"" width=""7"" height=""7"" rx=""1""/>";
    private const string SvgCta = @"<rect x=""3"" y=""8"" width=""18"" height=""8"" rx=""4""/><path d=""M11 12h4""/><path d=""M14 10l2 2-2 2""/>";
    private const string SvgMail = @"<rect x=""3"" y=""5"" width=""18"" height=""14"" rx=""2""/><path d=""M4 7l8 6 8-6""/>";
    private const string SvgImage = @"<rect x=""3"" y=""3"" width=""18"" height=""18"" rx=""2""/><circle cx=""8.5"" cy=""9"" r=""1.5""/><path d=""M21 16l-5-5L6 21""/>";
    private const string SvgAccordion = @"<rect x=""3"" y=""4"" width=""18"" height=""5"" rx=""1""/><rect x=""3"" y=""13"" width=""18"" height=""7"" rx=""1""/><path d=""M17 6.5l-1.5 1.5""/>";
    private const string SvgQuote = @"<path d=""M7 7h4v4c0 2-1 3-3 4""/><path d=""M15 7h4v4c0 2-1 3-3 4""/>";
    private const string SvgImageText = @"<rect x=""3"" y=""4"" width=""8"" height=""16"" rx=""1""/><path d=""M14 7h6""/><path d=""M14 12h6""/><path d=""M14 17h4""/>";
    private const string SvgSpacer = @"<path d=""M4 12h16""/><path d=""M8 7l4-4 4 4""/><path d=""M8 17l4 4 4-4""/>";
    private const string SvgLogoStrip = @"<rect x=""3"" y=""9"" width=""5"" height=""6"" rx=""1""/><rect x=""10"" y=""9"" width=""5"" height=""6"" rx=""1""/><rect x=""17"" y=""9"" width=""4"" height=""6"" rx=""1""/>";
    private const string SvgForm = @"<rect x=""4"" y=""3"" width=""16"" height=""18"" rx=""2""/><path d=""M8 8h8""/><path d=""M8 12h8""/><path d=""M8 16h4""/>";

    // Name / Description / field Label / option Label / ItemLabel hold LOCALIZATION KEYS
    // (not display text). They are resolved at render time via the Localizer (@T[...]); the
    // German text lives in Resources/de.json. BlockRegistry is built once at startup, so it
    // must NOT call the localizer here. Placeholders/help stay as literal German for now.
    private static List<BlockDefinition> Build() =>
    [
        new BlockDefinition
        {
            Type = "hero",
            Name = "block.hero.name",
            Description = "block.hero.desc",
            Svg = SvgHero,
            Partial = "Blocks/_Hero",
            Fields =
            [
                new BlockField { Id = "heading", Label = "block.hero.f.heading", Type = FieldType.Textarea, Placeholder = "SICHERE IT.\nKLARE STRUKTUREN.", Help = "Zeilenumbrüche werden übernommen." },
                new BlockField { Id = "subheading", Label = "block.hero.f.subheading", Type = FieldType.Textarea },
                new BlockField { Id = "image", Label = "block.hero.f.image", Type = FieldType.Image },
                new BlockField { Id = "buttonText", Label = "block.hero.f.buttonText", Type = FieldType.Text, Placeholder = "Kontaktieren Sie uns" },
                new BlockField { Id = "buttonUrl", Label = "block.hero.f.buttonUrl", Type = FieldType.Url, Placeholder = "/kontakt" },
                new BlockField { Id = "align", Label = "block.hero.f.align", Type = FieldType.Select, Default = "left",
                    Options = [ new("left", "block.opt.align.left"), new("center", "block.opt.align.center") ] },
                new BlockField { Id = "imageHeight", Label = "block.hero.f.imageHeight", Type = FieldType.Select, Default = "",
                    Options = [ new("", "block.opt.h.auto"), new("sm", "block.opt.h.sm"), new("md", "block.opt.h.md"), new("lg", "block.opt.h.lg"), new("full", "block.opt.h.full") ] },
            ]
        },
        new BlockDefinition
        {
            Type = "richtext",
            Name = "block.richtext.name",
            Description = "block.richtext.desc",
            Svg = SvgText,
            Partial = "Blocks/_RichText",
            Fields =
            [
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField { Id = "body", Label = "block.f.body", Type = FieldType.RichText },
                new BlockField { Id = "align", Label = "block.richtext.f.align", Type = FieldType.Select, Default = "left",
                    Options = [ new("left", "block.opt.align.left"), new("center", "block.opt.align.center") ] },
                new BlockField { Id = "width", Label = "block.f.width", Type = FieldType.Select, Default = "normal",
                    Options = [ new("normal", "block.opt.width.normal"), new("narrow", "block.opt.width.narrow") ] },
            ]
        },
        new BlockDefinition
        {
            Type = "columns",
            Name = "block.columns.name",
            Description = "block.columns.desc",
            Svg = SvgColumns,
            Partial = "Blocks/_Columns",
            AllowedChildren = ["column"],
            Fields =
            [
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField { Id = "intro", Label = "block.f.intro", Type = FieldType.Textarea },
                new BlockField { Id = "columns", Label = "block.columns.f.columns", Type = FieldType.Select, Default = "3",
                    Options = [ new("2", "block.opt.columns.2"), new("3", "block.opt.columns.3") ] },
            ]
        },
        new BlockDefinition
        {
            Type = "column",
            Name = "block.column.name",
            Description = "block.column.desc",
            Svg = SvgText,
            Partial = "Blocks/_Column",
            ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "title", Label = "block.f.title", Type = FieldType.Text },
                new BlockField { Id = "body", Label = "block.f.text", Type = FieldType.RichText },
                new BlockField { Id = "bg", Label = "block.column.f.bg", Type = FieldType.Select, OptionsSource = "themeColors" },
                new BlockField { Id = "fg", Label = "block.column.f.fg", Type = FieldType.Select, OptionsSource = "themeColors" },
            ]
        },
        new BlockDefinition
        {
            Type = "servicegrid",
            Name = "block.servicegrid.name",
            Description = "block.servicegrid.desc",
            Svg = SvgGrid,
            Partial = "Blocks/_ServiceGrid",
            AllowedChildren = ["service"],
            Fields =
            [
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField { Id = "intro", Label = "block.f.intro", Type = FieldType.Textarea },
                new BlockField { Id = "columns", Label = "block.servicegrid.f.columns", Type = FieldType.Select, Default = "4",
                    Options = [ new("2", "2"), new("3", "3"), new("4", "4") ] },
            ]
        },
        new BlockDefinition
        {
            Type = "service",
            Name = "block.service.name",
            Description = "block.service.desc",
            Svg = SvgGrid,
            Partial = "Blocks/_Service",
            ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "title", Label = "block.f.title", Type = FieldType.Text },
                new BlockField { Id = "text", Label = "block.f.description", Type = FieldType.Textarea },
            ]
        },
        new BlockDefinition
        {
            Type = "cta",
            Name = "block.cta.name",
            Description = "block.cta.desc",
            Svg = SvgCta,
            Partial = "Blocks/_Cta",
            Fields =
            [
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField { Id = "text", Label = "block.f.text", Type = FieldType.Textarea },
                new BlockField { Id = "buttonText", Label = "block.f.buttonText", Type = FieldType.Text },
                new BlockField { Id = "buttonUrl", Label = "block.f.buttonUrl", Type = FieldType.Url },
            ]
        },
        new BlockDefinition
        {
            Type = "form",
            Name = "block.form.name",
            Description = "block.form.desc",
            Svg = SvgForm,
            Partial = "Blocks/_Form",
            Fields =
            [
                new BlockField { Id = "form", Label = "block.form.f.form", Type = FieldType.Select, OptionsSource = "forms",
                    Help = "Formulare werden unter „Formulare“ verwaltet." },
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField { Id = "intro", Label = "block.f.intro", Type = FieldType.Textarea },
            ]
        },
        new BlockDefinition
        {
            Type = "image",
            Name = "block.image.name",
            Description = "block.image.desc",
            Svg = SvgImage,
            Partial = "Blocks/_Image",
            Fields =
            [
                new BlockField { Id = "image", Label = "block.f.image", Type = FieldType.Image },
                new BlockField { Id = "alt", Label = "block.f.alt", Type = FieldType.Text },
                new BlockField { Id = "caption", Label = "block.image.f.caption", Type = FieldType.Text },
                new BlockField { Id = "width", Label = "block.f.width", Type = FieldType.Select, Default = "normal",
                    Options = [ new("normal", "block.opt.width.normal"), new("narrow", "block.opt.width.narrow"), new("full", "block.opt.width.full") ] },
            ]
        },
        new BlockDefinition
        {
            Type = "accordion",
            Name = "block.accordion.name",
            Description = "block.accordion.desc",
            Svg = SvgAccordion,
            Partial = "Blocks/_Accordion",
            AllowedChildren = ["faq"],
            Fields =
            [
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField { Id = "intro", Label = "block.f.intro", Type = FieldType.Textarea },
            ]
        },
        new BlockDefinition
        {
            Type = "faq",
            Name = "block.faq.name",
            Description = "block.faq.desc",
            Svg = SvgAccordion,
            Partial = "Blocks/_Faq",
            ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "question", Label = "block.accordion.f.question", Type = FieldType.Text },
                new BlockField { Id = "answer", Label = "block.accordion.f.answer", Type = FieldType.RichText },
            ]
        },
        new BlockDefinition
        {
            Type = "quote",
            Name = "block.quote.name",
            Description = "block.quote.desc",
            Svg = SvgQuote,
            Partial = "Blocks/_Quote",
            Fields =
            [
                new BlockField { Id = "quote", Label = "block.quote.f.quote", Type = FieldType.Textarea },
                new BlockField { Id = "author", Label = "block.quote.f.author", Type = FieldType.Text },
            ]
        },
        new BlockDefinition
        {
            Type = "imagetext",
            Name = "block.imagetext.name",
            Description = "block.imagetext.desc",
            Svg = SvgImageText,
            Partial = "Blocks/_ImageText",
            Fields =
            [
                new BlockField { Id = "image", Label = "block.f.image", Type = FieldType.Image },
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField { Id = "body", Label = "block.f.body", Type = FieldType.RichText },
                new BlockField { Id = "imageSide", Label = "block.imagetext.f.imageSide", Type = FieldType.Select, Default = "left",
                    Options = [ new("left", "block.opt.imageSide.left"), new("right", "block.opt.imageSide.right") ] },
            ]
        },
        new BlockDefinition
        {
            Type = "spacer",
            Name = "block.spacer.name",
            Description = "block.spacer.desc",
            Svg = SvgSpacer,
            Partial = "Blocks/_Spacer",
            Fields =
            [
                new BlockField { Id = "size", Label = "block.spacer.f.size", Type = FieldType.Select, Default = "medium",
                    Options = [ new("small", "block.opt.size.small"), new("medium", "block.opt.size.medium"), new("large", "block.opt.size.large") ] },
            ]
        },
        new BlockDefinition
        {
            Type = "logostrip",
            Name = "block.logostrip.name",
            Description = "block.logostrip.desc",
            Svg = SvgLogoStrip,
            Partial = "Blocks/_LogoStrip",
            Fields =
            [
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField
                {
                    Id = "items", Label = "block.logostrip.f.items", Type = FieldType.List, ItemLabel = "block.logostrip.item",
                    ItemFields =
                    [
                        new BlockField { Id = "image", Label = "block.logostrip.f.image", Type = FieldType.Image },
                        new BlockField { Id = "alt", Label = "block.f.alt", Type = FieldType.Text },
                        new BlockField { Id = "url", Label = "block.logostrip.f.url", Type = FieldType.Url },
                    ]
                },
            ]
        },
        new BlockDefinition
        {
            Type = "leistungen",
            Name = "block.leistungen.name",
            Description = "block.leistungen.desc",
            Svg = SvgGrid,
            Partial = "Blocks/_Leistungen",
            AllowedChildren = ["leistung"],
            Fields =
            [
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField { Id = "intro", Label = "block.f.intro", Type = FieldType.Textarea },
                new BlockField { Id = "columns", Label = "block.columns.f.columns", Type = FieldType.Select, Default = "3",
                    Options = [ new("2", "block.opt.columns.2"), new("3", "block.opt.columns.3"), new("4", "block.opt.columns.4") ] },
            ]
        },
        new BlockDefinition
        {
            Type = "leistung",
            Name = "block.leistung.name",
            Description = "block.leistung.desc",
            Svg = SvgText,
            Partial = "Blocks/_Leistung",
            ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "title", Label = "block.f.title", Type = FieldType.Text },
                new BlockField { Id = "text", Label = "block.f.text", Type = FieldType.Textarea },
                new BlockField { Id = "image", Label = "block.leistung.f.image", Type = FieldType.Image },
            ]
        },
        new BlockDefinition
        {
            Type = "gallery",
            Name = "block.gallery.name",
            Description = "block.gallery.desc",
            Svg = SvgGrid,
            Partial = "Blocks/_Gallery",
            Fields =
            [
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField { Id = "source", Label = "block.gallery.f.source", Type = FieldType.Select, Default = "manual",
                    Options = [ new("manual", "block.gallery.opt.source.manual"), new("media", "block.gallery.opt.source.media") ] },
                new BlockField { Id = "tags", Label = "block.gallery.f.tags", Type = FieldType.Select, OptionsSource = "mediaTags",
                    ShowWhenField = "source", ShowWhenValue = "media",
                    Help = "Einen Tag wählen – es werden alle Medien mit diesem Tag angezeigt (leer = alle)." },
                new BlockField { Id = "showFilter", Label = "block.gallery.f.showFilter", Type = FieldType.Select, Default = "yes",
                    ShowWhenField = "source", ShowWhenValue = "media",
                    Options = [ new("yes", "block.opt.yesno.yes"), new("no", "block.opt.yesno.no") ],
                    Help = "Klickbare Tag-Chips auf der Seite anzeigen, mit denen Besucher die Galerie weiter filtern können." },
                new BlockField { Id = "layout", Label = "block.gallery.f.layout", Type = FieldType.Select, Default = "grid",
                    Options = [ new("grid", "block.opt.layout.grid"), new("masonry", "block.opt.layout.masonry") ] },
                new BlockField { Id = "columns", Label = "block.gallery.f.columns", Type = FieldType.Select, Default = "3",
                    Options = [ new("2", "block.opt.columns.2"), new("3", "block.opt.columns.3"), new("4", "block.opt.columns.4") ] },
                new BlockField
                {
                    Id = "images", Label = "block.gallery.f.images", Type = FieldType.List, ItemLabel = "block.gallery.item",
                    ItemFields =
                    [
                        new BlockField { Id = "image", Label = "block.gallery.f.image", Type = FieldType.Image },
                        new BlockField { Id = "alt", Label = "block.f.alt", Type = FieldType.Text },
                        new BlockField { Id = "caption", Label = "block.gallery.f.caption", Type = FieldType.Text },
                    ]
                },
            ]
        },
        new BlockDefinition
        {
            Type = "cards",
            Name = "block.cards.name",
            Description = "block.cards.desc",
            Svg = SvgColumns,
            Partial = "Blocks/_Cards",
            AllowedChildren = ["card"],
            Fields =
            [
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField { Id = "intro", Label = "block.f.intro", Type = FieldType.Textarea },
                new BlockField { Id = "columns", Label = "block.gallery.f.columns", Type = FieldType.Select, Default = "3",
                    Options = [ new("2", "block.opt.columns.2"), new("3", "block.opt.columns.3"), new("4", "block.opt.columns.4") ] },
                new BlockField { Id = "layout", Label = "block.cards.f.layout", Type = FieldType.Select, Default = "grid",
                    Options = [ new("grid", "block.cards.opt.grid"), new("carousel", "block.cards.opt.carousel") ],
                    Help = "Nebeneinander (Raster) oder als scrollbares Carousel." },
            ]
        },
        new BlockDefinition
        {
            Type = "card",
            Name = "block.card.name",
            Description = "block.card.desc",
            Svg = SvgImageText,
            Partial = "Blocks/_Card",
            ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "image", Label = "block.f.image", Type = FieldType.Image },
                new BlockField { Id = "title", Label = "block.f.title", Type = FieldType.Text },
                new BlockField { Id = "tags", Label = "block.card.f.tags", Type = FieldType.Text,
                    Help = "Merkmale als Chips (kommagetrennt), z. B. „3 Zimmer, Balkon, WLAN“." },
                new BlockField { Id = "text", Label = "block.f.text", Type = FieldType.Textarea },
                new BlockField { Id = "features", Label = "block.card.f.features", Type = FieldType.Textarea,
                    Help = "Detail-Kacheln – eine pro Zeile." },
                new BlockField { Id = "buttonText", Label = "block.f.buttonText", Type = FieldType.Text },
                new BlockField { Id = "buttonUrl", Label = "block.f.buttonUrl", Type = FieldType.Url },
            ]
        },
        new BlockDefinition
        {
            Type = "posts",
            Name = "block.posts.name",
            Description = "block.posts.desc",
            Svg = SvgGrid,
            Partial = "Blocks/_Posts",
            Fields =
            [
                new BlockField { Id = "heading", Label = "block.f.heading", Type = FieldType.Text },
                new BlockField { Id = "tag", Label = "block.posts.f.tag", Type = FieldType.Text,
                    Help = "Nur Beiträge mit diesem Tag anzeigen (leer = alle)." },
                new BlockField { Id = "columns", Label = "block.gallery.f.columns", Type = FieldType.Select, Default = "3",
                    Options = [ new("2", "block.opt.columns.2"), new("3", "block.opt.columns.3") ] },
                new BlockField { Id = "limit", Label = "block.posts.f.limit", Type = FieldType.Text,
                    Help = "Maximale Anzahl (leer = alle)." },
                new BlockField { Id = "showFilter", Label = "block.gallery.f.showFilter", Type = FieldType.Select, Default = "no",
                    Options = [ new("yes", "block.opt.yesno.yes"), new("no", "block.opt.yesno.no") ],
                    Help = "Klickbare Tag-Chips zum Weiterfiltern auf der Seite anzeigen." },
            ]
        },
        new BlockDefinition
        {
            Type = "html",
            Name = "block.html.name",
            Description = "block.html.desc",
            Svg = @"<path d=""M9 8l-4 4 4 4""/><path d=""M15 8l4 4-4 4""/>",
            Partial = "Blocks/_Html",
            Fields =
            [
                new BlockField { Id = "html", Label = "block.html.f.html", Type = FieldType.Textarea, Help = "Eigenes HTML – wird 1:1 ausgegeben. Nur für vertrauenswürdige Inhalte." },
            ]
        },
    ];
}
