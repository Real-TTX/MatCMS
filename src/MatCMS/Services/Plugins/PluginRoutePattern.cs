namespace MatCMS.Services.Plugins;

/// <summary>
/// A path template such as <c>/shop/{slug}</c> or <c>products/{id}</c>: literal segments, <c>{name}</c>
/// for exactly one segment, <c>{*name}</c> for the rest. Matched by the host, not by ASP.NET routing —
/// ASP.NET cannot remove an endpoint once it is registered, and a plugin must be unloadable.
/// </summary>
public sealed class PluginRoutePattern
{
    private readonly Segment[] _segments;

    private readonly record struct Segment(string Text, bool IsParameter, bool IsCatchAll);

    /// <summary>The template as written, normalised to start with "/".</summary>
    public string Template { get; }

    /// <summary>Literal segments count — more of them means "more specific", which decides precedence.</summary>
    public int Specificity { get; }

    /// <summary>True when the pattern ends in a <c>{*name}</c>.</summary>
    public bool HasCatchAll { get; }

    /// <summary>The first segment when it is a literal, else "" (admin pages use it to find their tab).</summary>
    public string FirstLiteral => _segments.Length > 0 && !_segments[0].IsParameter ? _segments[0].Text : "";

    private PluginRoutePattern(string template, Segment[] segments)
    {
        Template = template;
        _segments = segments;
        Specificity = segments.Count(s => !s.IsParameter);
        HasCatchAll = segments.Length > 0 && segments[^1].IsCatchAll;
    }

    /// <summary>Parses a template. Returns null (and says why) for one that cannot be matched unambiguously.</summary>
    public static PluginRoutePattern? TryParse(string? template, out string error)
    {
        error = "";
        var raw = (template ?? "").Trim().Replace('\\', '/');
        if (raw.Length == 0) { error = "empty pattern"; return null; }
        if (!raw.StartsWith('/')) raw = "/" + raw;
        if (raw.Length > 1 && raw.EndsWith('/')) raw = raw.TrimEnd('/');
        if (raw.Contains('?') || raw.Contains('#')) { error = "a pattern has no query or fragment"; return null; }

        var parts = raw == "/" ? Array.Empty<string>() : raw.Substring(1).Split('/');
        var segments = new List<Segment>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0) { error = "empty segment"; return null; }
            if (p == "." || p == "..") { error = "'.' and '..' are not allowed"; return null; }
            if (p.StartsWith('{') && p.EndsWith('}'))
            {
                var name = p.Substring(1, p.Length - 2);
                var catchAll = name.StartsWith('*');
                if (catchAll) name = name.Substring(1);
                if (!IsName(name)) { error = $"'{p}' is not a valid parameter"; return null; }
                if (!names.Add(name)) { error = $"parameter '{name}' used twice"; return null; }
                if (catchAll && i != parts.Length - 1) { error = "a {*catch-all} must be the last segment"; return null; }
                segments.Add(new Segment(name, true, catchAll));
            }
            else
            {
                if (p.Contains('{') || p.Contains('}')) { error = $"'{p}' mixes text and a parameter"; return null; }
                segments.Add(new Segment(p, false, false));
            }
        }
        return new PluginRoutePattern(raw, segments.ToArray());
    }

    private static bool IsName(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>Matches a path (no query, no culture prefix) and returns the captured values, or null.</summary>
    public Dictionary<string, string>? Match(string path)
    {
        var parts = Split(path);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var seg in _segments)
        {
            if (seg.IsCatchAll)
            {
                values[seg.Text] = i < parts.Length ? string.Join('/', parts.Skip(i)) : "";
                return values;
            }
            if (i >= parts.Length) return null;
            if (seg.IsParameter)
            {
                if (parts[i].Length == 0) return null;
                values[seg.Text] = parts[i];
            }
            else if (!string.Equals(seg.Text, parts[i], StringComparison.OrdinalIgnoreCase)) return null;
            i++;
        }
        return i == parts.Length ? values : null;
    }

    private static string[] Split(string? path)
    {
        var p = (path ?? "").Split('?')[0];
        p = p.Trim('/');
        if (p.Length == 0) return Array.Empty<string>();
        return p.Split('/').Select(s =>
        {
            try { return Uri.UnescapeDataString(s); } catch { return s; }
        }).ToArray();
    }

    /// <summary>Path prefixes no plugin may claim: the host's own areas. A public plugin route there would
    /// shadow the back office, the login or the uploaded files.</summary>
    public static readonly string[] Reserved =
        ["admin", "api", "login", "logout", "anmelden", "registrieren", "uploads", "thumbs", "plugin", "plugin-assets", "_content", "_framework", "error", "set-language", "sso", "oauth"];

    /// <summary>True when the first literal segment belongs to the host.</summary>
    public bool IsReserved =>
        FirstLiteral.Length > 0 && Reserved.Contains(FirstLiteral, StringComparer.OrdinalIgnoreCase)
        || FirstLiteral.StartsWith('_');

    /// <inheritdoc />
    public override string ToString() => Template;
}
