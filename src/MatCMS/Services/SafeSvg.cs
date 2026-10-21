using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MatCMS.Services;

/// <summary>
/// Makes an SVG safe to keep in the media library. SVG was refused outright for years because it is a document, not a
/// picture: it can carry script (a <c>&lt;script&gt;</c>, an <c>onload</c>, a <c>javascript:</c> link) that runs when somebody
/// opens the file's own address. Rather than refusing logos and icons, the file is rebuilt from an ALLOW-list — drawing
/// elements and presentation attributes only; everything else, every <c>on…</c> attribute, every reference that leaves the
/// document, <c>foreignObject</c> (HTML inside SVG) and DTDs (entity expansion) are dropped. <c>/uploads</c> additionally
/// serves SVG with a CSP that forbids script, so even a gap here would not execute.
/// <para>Inside an <c>&lt;img&gt;</c> (how the site shows pictures) a browser never runs an SVG's script anyway; this is for the
/// direct address, and for the AI assistant, which may now write SVG itself.</para>
/// </summary>
public static class SafeSvg
{
    public const int MaxChars = 1_000_000;

    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "symbol", "use", "title", "desc", "metadata",
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "textPath",
        "linearGradient", "radialGradient", "stop", "pattern", "clipPath", "mask", "marker",
        "filter", "feGaussianBlur", "feOffset", "feBlend", "feColorMatrix", "feComposite", "feFlood", "feMerge", "feMergeNode",
        "feDropShadow", "feMorphology",
        "style",
    };

    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "id", "class", "style", "viewBox", "width", "height", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry",
        "d", "points", "transform", "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-linecap",
        "stroke-linejoin", "stroke-dasharray", "stroke-dashoffset", "stroke-opacity", "stroke-miterlimit", "opacity",
        "clip-path", "clip-rule", "mask", "filter", "font-family", "font-size", "font-weight", "font-style", "letter-spacing",
        "text-anchor", "dominant-baseline", "alignment-baseline", "dx", "dy", "rotate", "textLength", "lengthAdjust",
        "offset", "stop-color", "stop-opacity", "gradientUnits", "gradientTransform", "spreadMethod", "fx", "fy",
        "patternUnits", "patternContentUnits", "patternTransform", "preserveAspectRatio", "maskUnits", "maskContentUnits",
        "clipPathUnits", "markerWidth", "markerHeight", "refX", "refY", "orient", "markerUnits", "marker-start", "marker-mid",
        "marker-end", "stdDeviation", "in", "in2", "result", "mode", "values", "type", "operator", "k1", "k2", "k3", "k4",
        "flood-color", "flood-opacity", "radius", "visibility", "display", "vector-effect", "paint-order", "href",
        "role", "aria-label", "aria-hidden", "focusable", "version", "xml:space",
    };

    /// <summary>Dropped WITH everything inside: script, HTML, outside content, and the animation elements (an <c>&lt;animate&gt;</c>
    /// or <c>&lt;set&gt;</c> can rewrite an href to javascript: after the fact). Any other unknown element — a link <c>&lt;a&gt;</c>, a
    /// <c>&lt;switch&gt;</c> — is unwrapped: it goes, its drawing stays.</summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "foreignObject", "image", "iframe", "object", "embed", "video", "audio", "canvas", "handler", "listener",
        "animate", "animateMotion", "animateTransform", "animateColor", "set", "discard", "cursor", "font-face-uri",
    };

    private const string SvgNs = "http://www.w3.org/2000/svg", XlinkNs = "http://www.w3.org/1999/xlink";

    /// <summary>The cleaned SVG, or null when the text is not an SVG at all.</summary>
    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) return null;
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreProcessingInstructions = true, IgnoreComments = true,
            });
            doc = XDocument.Load(reader);
        }
        catch (XmlException) { return null; }
        if (doc.Root is not { } root || root.Name.LocalName != "svg") return null;

        var clean = Clean(root)!.OfType<XElement>().Single();
        clean.Name = XName.Get("svg", SvgNs);
        if (clean.Attribute(XNamespace.Xmlns + "xlink") is null && clean.Descendants().Attributes().Any(a => a.Name.Namespace == XlinkNs))
            clean.SetAttributeValue(XNamespace.Xmlns + "xlink", XlinkNs);
        return clean.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The cleaned element — or, for an unknown wrapper, its cleaned children (none for a dropped element).</summary>
    private static IEnumerable<XNode> Clean(XElement e)
    {
        var local = e.Name.LocalName;
        if (Dropped.Contains(local)) return [];
        if (!Elements.Contains(local)) return e.Elements().SelectMany(Clean).ToList();
        var copy = new XElement(XName.Get(local, SvgNs));
        foreach (var a in e.Attributes())
        {
            if (a.IsNamespaceDeclaration) continue;
            var name = a.Name.Namespace == XlinkNs ? "href" : a.Name.Namespace == XNamespace.Xml ? "xml:" + a.Name.LocalName : a.Name.LocalName;
            if (!Attributes.Contains(name)) continue;
            var value = a.Value;
            // A reference may only point INSIDE the document (#id) — never to a file, a site or javascript:.
            if (name == "href" && !value.TrimStart().StartsWith('#')) continue;
            if (name is "style" && !SafeCss(value)) continue;
            if (Regex.IsMatch(value, @"url\(\s*['""]?\s*(?!#)", RegexOptions.IgnoreCase)) continue;
            if (a.Name.Namespace == XlinkNs) copy.SetAttributeValue(XName.Get("href", XlinkNs), value);
            else if (name.StartsWith("xml:")) copy.SetAttributeValue(XNamespace.Xml + name[4..], value);
            else copy.SetAttributeValue(name, value);
        }
        if (local == "style")
        {
            var css = e.Value;
            if (SafeCss(css)) copy.Add(new XText(css));
            return [copy];
        }
        foreach (var n in e.Nodes())
        {
            if (n is XElement child) copy.Add(Clean(child));
            else if (n is XText t && local is "text" or "tspan" or "textPath" or "title" or "desc") copy.Add(new XText(t.Value));
        }
        return [copy];
    }

    /// <summary>CSS inside an SVG: no imports, no outside addresses, no script-ish leftovers.</summary>
    private static bool SafeCss(string css) =>
        !Regex.IsMatch(css, @"@import|expression\s*\(|javascript:|behavior\s*:|-moz-binding|url\(\s*['""]?\s*(?!#)", RegexOptions.IgnoreCase)
        && !css.Contains('<');
}
