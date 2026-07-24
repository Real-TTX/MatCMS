namespace MatCMS.Content;

/// <summary>
/// Central catalogue of available content blocks and their editable fields.
/// Add a new block here + a matching partial under Pages/Shared/Blocks to extend the CMS.
/// </summary>
public class BlockRegistry
{
    public IReadOnlyList<BlockDefinition> All { get; }

    public BlockRegistry()
    {
        All = Build();
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

    private static List<BlockDefinition> Build() =>
    [
        new BlockDefinition
        {
            Type = "hero",
            Name = "Hero (Kopfbereich)",
            Description = "Großer Seitenkopf: Überschrift, optionaler Untertext, Button und Bildband darunter.",
            Svg = SvgHero,
            Partial = "Blocks/_Hero",
            Fields =
            [
                new BlockField { Id = "heading", Label = "Überschrift", Type = FieldType.Textarea, Placeholder = "SICHERE IT.\nKLARE STRUKTUREN.", Help = "Zeilenumbrüche werden übernommen." },
                new BlockField { Id = "subheading", Label = "Untertext", Type = FieldType.Textarea },
                new BlockField { Id = "image", Label = "Bild (Band darunter)", Type = FieldType.Image },
                new BlockField { Id = "buttonText", Label = "Button-Text", Type = FieldType.Text, Placeholder = "Kontaktieren Sie uns" },
                new BlockField { Id = "buttonUrl", Label = "Button-Link", Type = FieldType.Url, Placeholder = "/kontakt" },
                new BlockField { Id = "align", Label = "Ausrichtung", Type = FieldType.Select, Default = "left",
                    Options = [ new("left", "Links"), new("center", "Zentriert") ] },
            ]
        },
        new BlockDefinition
        {
            Type = "richtext",
            Name = "Text",
            Description = "Textabschnitt mit optionaler Überschrift und formatiertem Fließtext (fett, Listen, Links).",
            Svg = SvgText,
            Partial = "Blocks/_RichText",
            Fields =
            [
                new BlockField { Id = "heading", Label = "Überschrift", Type = FieldType.Text },
                new BlockField { Id = "body", Label = "Inhalt", Type = FieldType.RichText },
                new BlockField { Id = "align", Label = "Ausrichtung", Type = FieldType.Select, Default = "left",
                    Options = [ new("left", "Links"), new("center", "Zentriert") ] },
                new BlockField { Id = "width", Label = "Breite", Type = FieldType.Select, Default = "normal",
                    Options = [ new("normal", "Normal"), new("narrow", "Schmal") ] },
            ]
        },
        new BlockDefinition
        {
            Type = "columns",
            Name = "Spalten",
            Description = "Zwei oder drei nebeneinanderliegende Text-Spalten mit Titel – z. B. für Partner oder Leistungsbereiche.",
            Svg = SvgColumns,
            Partial = "Blocks/_Columns",
            Fields =
            [
                new BlockField { Id = "heading", Label = "Überschrift", Type = FieldType.Text },
                new BlockField { Id = "intro", Label = "Einleitung", Type = FieldType.Textarea },
                new BlockField { Id = "columns", Label = "Spaltenanzahl", Type = FieldType.Select, Default = "3",
                    Options = [ new("2", "2 Spalten"), new("3", "3 Spalten") ] },
                new BlockField
                {
                    Id = "items", Label = "Spalten", Type = FieldType.List, ItemLabel = "Spalte",
                    ItemFields =
                    [
                        new BlockField { Id = "title", Label = "Titel", Type = FieldType.Text },
                        new BlockField { Id = "body", Label = "Text", Type = FieldType.RichText },
                    ]
                },
            ]
        },
        new BlockDefinition
        {
            Type = "servicegrid",
            Name = "Leistungen (Raster)",
            Description = "Kachel-Raster für Leistungen oder Features, je Kachel Titel und kurze Beschreibung.",
            Svg = SvgGrid,
            Partial = "Blocks/_ServiceGrid",
            Fields =
            [
                new BlockField { Id = "heading", Label = "Überschrift", Type = FieldType.Text },
                new BlockField { Id = "intro", Label = "Einleitung", Type = FieldType.Textarea },
                new BlockField { Id = "columns", Label = "Spalten (Desktop)", Type = FieldType.Select, Default = "4",
                    Options = [ new("2", "2"), new("3", "3"), new("4", "4") ] },
                new BlockField
                {
                    Id = "items", Label = "Leistungen", Type = FieldType.List, ItemLabel = "Leistung",
                    ItemFields =
                    [
                        new BlockField { Id = "title", Label = "Titel", Type = FieldType.Text },
                        new BlockField { Id = "text", Label = "Beschreibung", Type = FieldType.Textarea },
                    ]
                },
            ]
        },
        new BlockDefinition
        {
            Type = "cta",
            Name = "Call-to-Action",
            Description = "Hervorgehobener Aufruf: große Aussage mit optionalem Text und Button (z. B. „Kontaktieren Sie uns“).",
            Svg = SvgCta,
            Partial = "Blocks/_Cta",
            Fields =
            [
                new BlockField { Id = "heading", Label = "Überschrift", Type = FieldType.Text },
                new BlockField { Id = "text", Label = "Text", Type = FieldType.Textarea },
                new BlockField { Id = "buttonText", Label = "Button-Text", Type = FieldType.Text },
                new BlockField { Id = "buttonUrl", Label = "Button-Link", Type = FieldType.Url },
            ]
        },
        new BlockDefinition
        {
            Type = "contactform",
            Name = "Kontaktformular",
            Description = "Formular für Name, E-Mail, Kategorie und Nachricht. Einsendungen erscheinen im Admin unter „Anfragen“.",
            Svg = SvgMail,
            Partial = "Blocks/_ContactForm",
            Fields =
            [
                new BlockField { Id = "heading", Label = "Überschrift", Type = FieldType.Text, Default = "Kontaktformular" },
                new BlockField { Id = "intro", Label = "Einleitung", Type = FieldType.Textarea },
                new BlockField { Id = "categories", Label = "Kategorien", Type = FieldType.Text,
                    Placeholder = "Allgemeine Anfrage, Service Anfrage", Help = "Komma-getrennt. Leer lassen für keine Auswahl." },
            ]
        },
        new BlockDefinition
        {
            Type = "image",
            Name = "Bild",
            Description = "Einzelnes Bild mit optionaler Bildunterschrift – als Upload oder per URL.",
            Svg = SvgImage,
            Partial = "Blocks/_Image",
            Fields =
            [
                new BlockField { Id = "image", Label = "Bild", Type = FieldType.Image },
                new BlockField { Id = "alt", Label = "Alternativtext", Type = FieldType.Text },
                new BlockField { Id = "caption", Label = "Bildunterschrift", Type = FieldType.Text },
                new BlockField { Id = "width", Label = "Breite", Type = FieldType.Select, Default = "normal",
                    Options = [ new("normal", "Normal"), new("narrow", "Schmal"), new("full", "Volle Breite") ] },
            ]
        },
    ];
}
