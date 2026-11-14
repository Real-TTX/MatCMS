using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Docs;

/// <summary>
/// The pictures of the docs in an export or import. A page names a picture by its address (<c>![…](/uploads/x.png)</c> in Markdown, <c>src="…"</c> in
/// rich text); an export puts each such file of the media library into the ZIP as <c>media/{name}</c>, an import puts the files of a <c>media</c>
/// folder into the library (an identical file already there is reused) and points the pages' references at their addresses here.
/// <para>The library is reached through <c>IPluginSite.ReadMediaAsync/FindMediaAsync/AddMediaAsync</c> (API 2.3). On an older host those members do not
/// exist: every call sits in a <c>NoInlining</c> helper that catches <see cref="MissingMethodException"/>, and the docs then move text without pictures.</para>
/// </summary>
internal sealed partial class DocsMedia
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg"];
    private readonly IPluginSite _site;
    private readonly Dictionary<string, string?> _found = new(StringComparer.OrdinalIgnoreCase);

    public DocsMedia(IPluginSite site) => _site = site;

    /// <summary>False once the host turned out not to have the media members (MatCMS before API 2.3).</summary>
    public bool Supported { get; private set; } = true;

    public static bool IsImage(string name) => Extensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    /// <summary>Every picture address a page's text names (Markdown pictures and links, HTML src/href), in order, each once.</summary>
    public static IReadOnlyList<string> References(string body)
    {
        var list = new List<string>();
        foreach (Match m in MdRx().Matches(body ?? "")) list.Add(m.Groups[2].Value);
        foreach (Match m in HtmlRx().Matches(body ?? "")) list.Add(m.Groups[2].Value);
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The file name a reference points at when it may be a file of an import: <c>/uploads/{name}</c>, or an address without scheme and without a
    /// leading "/" (<c>media/x.png</c>, <c>../media/x.png</c>). Null for everything else (another site, another folder of this one).
    /// </summary>
    public static string? NameOf(string url)
    {
        var u = (url ?? "").Trim();
        var cut = u.IndexOfAny(['?', '#']);
        if (cut >= 0) u = u[..cut];
        if (u.Length == 0 || u.Contains(':') || u.StartsWith("//", StringComparison.Ordinal)) return null;
        if (u.StartsWith('/') && !u.StartsWith("/uploads/", StringComparison.Ordinal)) return null;
        var name = u[(u.LastIndexOf('/') + 1)..];
        return name.Length > 0 && IsImage(name) ? name : null;
    }

    /// <summary>The text with every reference that <paramref name="map"/> knows replaced by its new address (the rest stays byte for byte).</summary>
    public static string Rewrite(string body, Func<string, string?> map)
    {
        string One(Match m) => map(m.Groups[2].Value) is { } to ? m.Groups[1].Value + to : m.Value;
        return HtmlRx().Replace(MdRx().Replace(body ?? "", One), One);
    }

    // ---- the library, through the host ---------------------------------------------------------------------------------

    /// <summary>A file of this site's library (null when there is none, or on an older host).</summary>
    public async Task<PluginMediaFile?> ReadAsync(string url, CancellationToken ct)
    {
        if (!Supported) return null;
        try { return await Read(_site, url, ct); }
        catch (MissingMethodException) { Supported = false; return null; }
    }

    /// <summary>The address of an identical file already in the library, or null (asked once per name).</summary>
    public async Task<string?> FindAsync(string name, byte[] content, CancellationToken ct)
    {
        if (!Supported) return null;
        if (_found.TryGetValue(name, out var known)) return known;
        try { return _found[name] = await Find(_site, name, content, ct); }
        catch (MissingMethodException) { Supported = false; return null; }
    }

    /// <summary>Puts a file into the library (or finds it there): its address, or the reason it was refused.</summary>
    public async Task<(string? Url, string? Error)> AddAsync(string name, byte[] content, CancellationToken ct)
    {
        if (!Supported) return (null, "unsupported");
        try
        {
            var r = await Add(_site, name, content, ct);
            if (r.Ok) _found[name] = r.Url;
            return r.Ok ? (r.Url, null) : (null, r.Error);
        }
        catch (MissingMethodException) { Supported = false; return (null, "unsupported"); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<PluginMediaFile?> Read(IPluginSite site, string url, CancellationToken ct) => site.ReadMediaAsync(url, ct);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<string?> Find(IPluginSite site, string name, byte[] content, CancellationToken ct) => site.FindMediaAsync(name, content, ct);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<PluginMediaResult> Add(IPluginSite site, string name, byte[] content, CancellationToken ct) => site.AddMediaAsync(name, content, ct);

    // Markdown: ![alt](url …) and [text](url …) — group 1 is everything up to the address, group 2 the address. HTML: src="…" / href="…".
    [GeneratedRegex(@"(!?\[[^\]\n]*\]\(\s*)([^\s)]+)")]
    private static partial Regex MdRx();
    [GeneratedRegex(@"(\b(?:src|href)\s*=\s*"")([^""]+)")]
    private static partial Regex HtmlRx();
}
