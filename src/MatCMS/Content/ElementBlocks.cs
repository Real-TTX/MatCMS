namespace MatCMS.Content;

/// <summary>
/// Editor v2's building blocks (docs/editor-v2.md, stage 2): a page made of SECTIONS that hold
/// ELEMENTS — heading, text, image, buttons, spacing — and COLUMNS that hold elements of their own on
/// the left and the right. Where a classic block is one form with every field of a hero in it, these
/// are small pieces put together in the tree, so a hero is a section with an image, a heading, a text
/// and a row of buttons, each selectable and movable on its own.
/// <para>Plain built-in blocks, stored like every other ContentBlock (type + data + parent): existing
/// pages, backups and the cloud see nothing new. Elements are child-only — they make no sense loose
/// on a page — while a section and columns may stand at the top level.</para>
/// </summary>
public static class ElementBlocks
{
    public const string Section = "el-section";
    public const string Columns = "el-columns";
    public const string Column = "el-column";
    public const string Heading = "el-heading";
    public const string Text = "el-text";
    public const string Image = "el-image";
    public const string Buttons = "el-buttons";
    public const string Button = "el-button";
    public const string Spacer = "el-spacer";

    /// <summary>What a column may hold: every element, but no further columns (one level of columns
    /// is what a page needs; columns in columns is how layouts become unmaintainable).</summary>
    private static readonly List<string> ColumnContent = [Heading, Text, Image, Buttons, Button, Spacer];
    /// <summary>What a section may hold: the elements plus columns.</summary>
    private static readonly List<string> SectionContent = [Heading, Text, Image, Buttons, Button, Spacer, Columns];

    public static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        [Section] = "layout", [Columns] = "layout", [Column] = "layout", [Spacer] = "layout",
        [Heading] = "text", [Text] = "text", [Image] = "media", [Buttons] = "text", [Button] = "text",
    };

    private const string SvgSection = @"<rect x=""3"" y=""4"" width=""18"" height=""16"" rx=""2""/><path d=""M3 9h18""/>";
    private const string SvgColumns = @"<rect x=""3"" y=""4"" width=""7"" height=""16"" rx=""1""/><rect x=""14"" y=""4"" width=""7"" height=""16"" rx=""1""/>";
    private const string SvgColumn = @"<rect x=""8"" y=""4"" width=""8"" height=""16"" rx=""1""/>";
    private const string SvgHeading = @"<path d=""M6 4v16""/><path d=""M18 4v16""/><path d=""M6 12h12""/>";
    private const string SvgText = @"<path d=""M4 6h16""/><path d=""M4 12h16""/><path d=""M4 18h11""/>";
    private const string SvgImage = @"<rect x=""3"" y=""3"" width=""18"" height=""18"" rx=""2""/><circle cx=""8.5"" cy=""9"" r=""1.5""/><path d=""M21 16l-5-5L6 21""/>";
    private const string SvgButtons = @"<rect x=""2"" y=""8"" width=""9"" height=""8"" rx=""3""/><rect x=""13"" y=""8"" width=""9"" height=""8"" rx=""3""/>";
    private const string SvgButton = @"<rect x=""4"" y=""8"" width=""16"" height=""8"" rx=""4""/>";
    private const string SvgSpacer = @"<path d=""M4 12h16""/><path d=""M8 7l4-4 4 4""/><path d=""M8 17l4 4 4-4""/>";

    private static SelectOption O(string value, string label) => new(value, label);
    private static readonly List<SelectOption> Align = [O("left", "block.el.opt.left"), O("center", "block.el.opt.center"), O("right", "block.el.opt.right")];

    public static List<BlockDefinition> Build() =>
    [
        new BlockDefinition
        {
            Type = Section, Name = "block.el.section.name", Description = "block.el.section.desc", Svg = SvgSection,
            Partial = "Blocks/_ElSection", AllowedChildren = SectionContent,
            Fields =
            [
                new BlockField { Id = "label", Label = "block.el.f.label", Type = FieldType.Text, Help = "block.el.f.label.help", AiIgnore = true },
                new BlockField { Id = "bg", Label = "block.el.f.bg", Type = FieldType.Select, Default = "none",
                    Options = [O("none", "block.el.opt.bg.none"), O("alt", "block.el.opt.bg.alt"), O("accent", "block.el.opt.bg.accent"), O("dark", "block.el.opt.bg.dark"), O("image", "block.el.opt.bg.image")] },
                new BlockField { Id = "bgImage", Label = "block.el.f.bgImage", Type = FieldType.Image, ShowWhenField = "bg", ShowWhenValue = "image" },
                new BlockField { Id = "overlay", Label = "block.el.f.overlay", Type = FieldType.Select, Default = "medium", ShowWhenField = "bg", ShowWhenValue = "image",
                    Options = [O("none", "block.el.opt.overlay.none"), O("light", "block.el.opt.overlay.light"), O("medium", "block.el.opt.overlay.medium"), O("strong", "block.el.opt.overlay.strong")] },
                new BlockField { Id = "align", Label = "block.el.f.align", Type = FieldType.Select, Default = "left", Options = Align },
                new BlockField { Id = "width", Label = "block.el.f.contentWidth", Type = FieldType.Select, Default = "normal",
                    Options = [O("narrow", "block.el.opt.width.narrow"), O("normal", "block.el.opt.width.normal"), O("wide", "block.el.opt.width.wide")] },
                new BlockField { Id = "pad", Label = "block.el.f.pad", Type = FieldType.Select, Default = "m",
                    Options = [O("none", "block.el.opt.pad.none"), O("s", "block.el.opt.s"), O("m", "block.el.opt.m"), O("l", "block.el.opt.l"), O("xl", "block.el.opt.xl")] },
                new BlockField { Id = "minHeight", Label = "block.el.f.minHeight", Type = FieldType.Select, Default = "",
                    Options = [O("", "block.el.opt.auto"), O("half", "block.el.opt.height.half"), O("screen", "block.el.opt.height.screen")] },
            ]
        },
        new BlockDefinition
        {
            Type = Columns, Name = "block.el.columns.name", Description = "block.el.columns.desc", Svg = SvgColumns,
            Partial = "Blocks/_ElColumns", AllowedChildren = [Column],
            Fields =
            [
                new BlockField { Id = "layout", Label = "block.el.f.layout", Type = FieldType.Select, Default = "50-50",
                    Options = [O("50-50", "50 / 50"), O("33-67", "33 / 67"), O("67-33", "67 / 33"), O("40-60", "40 / 60"), O("60-40", "60 / 40"), O("33-33-33", "33 / 33 / 33"), O("25-25-25-25", "4 × 25")] },
                new BlockField { Id = "valign", Label = "block.el.f.valign", Type = FieldType.Select, Default = "top",
                    Options = [O("top", "block.el.opt.top"), O("center", "block.el.opt.center"), O("bottom", "block.el.opt.bottom")] },
                new BlockField { Id = "gap", Label = "block.el.f.gap", Type = FieldType.Select, Default = "m",
                    Options = [O("s", "block.el.opt.s"), O("m", "block.el.opt.m"), O("l", "block.el.opt.l")] },
                new BlockField { Id = "mobile", Label = "block.el.f.mobile", Type = FieldType.Select, Default = "stack",
                    Options = [O("stack", "block.el.opt.mobile.stack"), O("reverse", "block.el.opt.mobile.reverse"), O("keep", "block.el.opt.mobile.keep")] },
            ]
        },
        new BlockDefinition
        {
            Type = Column, Name = "block.el.column.name", Description = "block.el.column.desc", Svg = SvgColumn,
            Partial = "Blocks/_ElColumn", AllowedChildren = ColumnContent, ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "bg", Label = "block.el.f.bg", Type = FieldType.Select, Default = "none",
                    Options = [O("none", "block.el.opt.bg.none"), O("alt", "block.el.opt.bg.alt"), O("accent", "block.el.opt.bg.accent"), O("dark", "block.el.opt.bg.dark")] },
                new BlockField { Id = "pad", Label = "block.el.f.innerPad", Type = FieldType.Select, Default = "none",
                    Options = [O("none", "block.el.opt.pad.none"), O("s", "block.el.opt.s"), O("m", "block.el.opt.m"), O("l", "block.el.opt.l")] },
                new BlockField { Id = "align", Label = "block.el.f.align", Type = FieldType.Select, Default = "", Options = [O("", "block.el.opt.inherit"), ..Align] },
            ]
        },
        new BlockDefinition
        {
            Type = Heading, Name = "block.el.heading.name", Description = "block.el.heading.desc", Svg = SvgHeading,
            Partial = "Blocks/_ElHeading", ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "text", Label = "block.el.f.text", Type = FieldType.Textarea, Help = "block.el.f.headingText.help" },
                new BlockField { Id = "kicker", Label = "block.el.f.kicker", Type = FieldType.Text, Help = "block.el.f.kicker.help" },
                new BlockField { Id = "level", Label = "block.el.f.level", Type = FieldType.Select, Default = "h2",
                    Options = [O("h1", "H1"), O("h2", "H2"), O("h3", "H3"), O("h4", "H4")] },
                new BlockField { Id = "size", Label = "block.el.f.size", Type = FieldType.Select, Default = "",
                    Options = [O("", "block.el.opt.auto"), O("s", "block.el.opt.s"), O("m", "block.el.opt.m"), O("l", "block.el.opt.l"), O("xl", "block.el.opt.xl")] },
                new BlockField { Id = "align", Label = "block.el.f.align", Type = FieldType.Select, Default = "", Options = [O("", "block.el.opt.inherit"), ..Align] },
            ]
        },
        new BlockDefinition
        {
            Type = Text, Name = "block.el.text.name", Description = "block.el.text.desc", Svg = SvgText,
            Partial = "Blocks/_ElText", ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "body", Label = "block.el.f.text", Type = FieldType.RichText },
                new BlockField { Id = "size", Label = "block.el.f.size", Type = FieldType.Select, Default = "",
                    Options = [O("s", "block.el.opt.s"), O("", "block.el.opt.normal"), O("l", "block.el.opt.l")] },
                new BlockField { Id = "align", Label = "block.el.f.align", Type = FieldType.Select, Default = "", Options = [O("", "block.el.opt.inherit"), ..Align] },
                new BlockField { Id = "measure", Label = "block.el.f.measure", Type = FieldType.Select, Default = "",
                    Options = [O("", "block.el.opt.measure.full"), O("read", "block.el.opt.measure.read")] },
            ]
        },
        new BlockDefinition
        {
            Type = Image, Name = "block.el.image.name", Description = "block.el.image.desc", Svg = SvgImage,
            Partial = "Blocks/_ElImage", ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "image", Label = "block.f.image", Type = FieldType.Image },
                new BlockField { Id = "alt", Label = "block.f.alt", Type = FieldType.Text },
                new BlockField { Id = "shape", Label = "block.el.f.shape", Type = FieldType.Select, Default = "",
                    Options = [O("", "block.el.opt.shape.original"), O("rounded", "block.el.opt.shape.rounded"), O("circle", "block.el.opt.shape.circle"),
                               O("square", "block.el.opt.shape.square"), O("portrait", "4 : 5"), O("landscape", "16 : 9")] },
                new BlockField { Id = "size", Label = "block.el.f.imageSize", Type = FieldType.Select, Default = "full",
                    Options = [O("full", "block.el.opt.full"), O("l", "block.el.opt.l"), O("m", "block.el.opt.m"), O("s", "block.el.opt.s"), O("xs", "block.el.opt.xs")] },
                new BlockField { Id = "align", Label = "block.el.f.align", Type = FieldType.Select, Default = "", Options = [O("", "block.el.opt.inherit"), ..Align] },
                new BlockField { Id = "caption", Label = "block.image.f.caption", Type = FieldType.Text },
                new BlockField { Id = "link", Label = "block.el.f.link", Type = FieldType.Url },
            ]
        },
        new BlockDefinition
        {
            Type = Buttons, Name = "block.el.buttons.name", Description = "block.el.buttons.desc", Svg = SvgButtons,
            Partial = "Blocks/_ElButtons", AllowedChildren = [Button], ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "align", Label = "block.el.f.align", Type = FieldType.Select, Default = "", Options = [O("", "block.el.opt.inherit"), ..Align] },
                new BlockField { Id = "stack", Label = "block.el.f.arrange", Type = FieldType.Select, Default = "row",
                    Options = [O("row", "block.el.opt.row"), O("column", "block.el.opt.column")] },
            ]
        },
        new BlockDefinition
        {
            Type = Button, Name = "block.el.button.name", Description = "block.el.button.desc", Svg = SvgButton,
            Partial = "Blocks/_ElButton", ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "text", Label = "block.el.f.buttonText", Type = FieldType.Text },
                new BlockField { Id = "url", Label = "block.el.f.link", Type = FieldType.Url },
                new BlockField { Id = "style", Label = "block.el.f.style", Type = FieldType.Select, Default = "primary",
                    Options = [O("primary", "block.el.opt.btn.primary"), O("outline", "block.el.opt.btn.outline"), O("link", "block.el.opt.btn.link")] },
                new BlockField { Id = "newTab", Label = "block.el.f.newTab", Type = FieldType.Select, Default = "no",
                    Options = [O("no", "block.el.opt.no"), O("yes", "block.el.opt.yes")] },
            ]
        },
        new BlockDefinition
        {
            Type = Spacer, Name = "block.el.spacer.name", Description = "block.el.spacer.desc", Svg = SvgSpacer,
            Partial = "Blocks/_ElSpacer", ChildOnly = true,
            Fields =
            [
                new BlockField { Id = "size", Label = "block.el.f.size", Type = FieldType.Select, Default = "m",
                    Options = [O("s", "block.el.opt.s"), O("m", "block.el.opt.m"), O("l", "block.el.opt.l"), O("xl", "block.el.opt.xl")] },
                new BlockField { Id = "line", Label = "block.el.f.line", Type = FieldType.Select, Default = "no",
                    Options = [O("no", "block.el.opt.no"), O("yes", "block.el.opt.yes")] },
            ]
        },
    ];
}
