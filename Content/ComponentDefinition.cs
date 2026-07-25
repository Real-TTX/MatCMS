using System.Text.Json;
using MatCMS.Models;

namespace MatCMS.Content;

/// <summary>Bridges user-defined <see cref="Component"/>s to the block system.</summary>
public static class ComponentDefinition
{
    private const string DefaultSvg =
        @"<rect x=""3"" y=""3"" width=""18"" height=""18"" rx=""2""/><path d=""M8 8h8""/><path d=""M8 12h5""/>";

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Build a block definition for a component so it appears in the picker/editor.</summary>
    public static BlockDefinition FromComponent(Component c) => new()
    {
        Type = c.Type,
        Name = c.Name,
        Description = c.Description,
        Svg = DefaultSvg,
        Partial = "Blocks/_Component",
        Fields = ParseFields(c.FieldsJson),
        ComponentTemplate = c.TemplateHtml
    };

    /// <summary>Parse the stored field definitions into editor <see cref="BlockField"/>s.</summary>
    public static List<BlockField> ParseFields(string? json)
    {
        try
        {
            var defs = JsonSerializer.Deserialize<List<FieldDef>>(json ?? "[]", Opts) ?? new();
            return defs
                .Where(f => !string.IsNullOrWhiteSpace(f.Id))
                .Select(f => new BlockField { Id = f.Id, Label = string.IsNullOrWhiteSpace(f.Label) ? f.Id : f.Label!, Type = MapType(f.Type) })
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Render a component block: substitute {{fieldId}} placeholders with the block's values.</summary>
    public static string Render(BlockDefinition def, BlockData data)
    {
        var html = def.ComponentTemplate ?? "";
        foreach (var f in def.Fields)
        {
            var val = data.Str(f.Id);
            // Rich text is emitted as-is; everything else is HTML-encoded to prevent injection.
            var replacement = f.Type == FieldType.RichText ? val : System.Net.WebUtility.HtmlEncode(val);
            html = html.Replace("{{" + f.Id + "}}", replacement);
        }
        return html;
    }

    private static FieldType MapType(string? t) => t switch
    {
        "textarea" => FieldType.Textarea,
        "richtext" => FieldType.RichText,
        "image" => FieldType.Image,
        "url" => FieldType.Url,
        _ => FieldType.Text
    };

    private sealed class FieldDef
    {
        public string Id { get; set; } = "";
        public string? Label { get; set; }
        public string? Type { get; set; }
    }
}
