using System.Text.RegularExpressions;

namespace MatCMS.Content;

/// <summary>
/// Resolves the <c>{{asset:filename}}</c> token used in a template's HTML/CSS/JS to the URL that
/// serves the attached <see cref="MatCMS.Models.TemplateAsset"/>, and guesses a content type from a
/// file name so served scripts/stylesheets/fonts arrive with a MIME a browser accepts.
/// </summary>
public static class TemplateAssets
{
    // Only a safe file-name shape — no slashes, so a token can never point outside the template's own
    // asset set.
    private static readonly Regex Token = new(@"\{\{asset:([A-Za-z0-9._-]+)\}\}", RegexOptions.Compiled);

    /// <summary>Replaces every <c>{{asset:name}}</c> with <c>/template-assets/{templateId}/{name}</c>.</summary>
    public static string? Resolve(string? text, int templateId) =>
        string.IsNullOrEmpty(text) ? text
            : Token.Replace(text, m => $"/template-assets/{templateId}/{m.Groups[1].Value}");

    /// <summary>Best-effort MIME by extension. Scripts and stylesheets especially must be right or the
    /// browser refuses to execute/apply them.</summary>
    public static string ContentTypeFor(string name)
    {
        var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".js" or ".mjs" => "text/javascript",
            ".css" => "text/css",
            ".json" => "application/json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".eot" => "application/vnd.ms-fontobject",
            _ => "application/octet-stream",
        };
    }
}
