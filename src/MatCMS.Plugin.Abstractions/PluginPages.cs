namespace MatCMS.Plugin;

/// <summary>
/// A page a plugin lets the site's editors lay out in the BLOCK EDITOR — the shop's product page, its cart, its customer area. The page is an ordinary CMS page in the editor (sections, columns,
/// headings, texts, images, the plugin's own blocks), but it is not served at an address of its own: the plugin's route answers and draws the page's blocks with the data of that request.
/// <para>The data reaches the blocks as <b>variables</b>: a heading with the text <c>{{product.title}}</c> shows the product's name, a block can be shown only <c>if</c> a variable has a value,
/// and the plugin's own blocks read the same variables (<see cref="IPluginContext.PageScope"/>). A page that was never created, or was switched off, is simply not used: the plugin draws its
/// own default (<see cref="IPluginContext.RenderPageAsync"/> answers null).</para>
/// </summary>
/// <param name="Name">The role: <c>product</c>, <c>cart</c> … (lower case, letters, digits, minus). Together with the plugin it names the page and prefixes its variables.</param>
/// <param name="Title">The page's name in the editor and in the page list — a string key of the plugin.</param>
public sealed record PageRoleSpec(string Name, string Title)
{
    /// <summary>One line under the title: what the page is for.</summary>
    public string? Description { get; init; }
    /// <summary>The variables the page offers, listed in the editor to click into a field. Names are written without the role: <c>title</c> is <c>{{product.title}}</c> on the page of role <c>product</c>.</summary>
    public IReadOnlyList<PageVariable> Variables { get; init; } = Array.Empty<PageVariable>();
    /// <summary>
    /// The blocks a new page starts with (also what "back to default" restores), per content language. The result is a tree of blocks: a block type, its settings (text with variables) and its
    /// children. Called once for every language the site has when the plugin is first loaded.
    /// </summary>
    public Func<PageSeedContext, IReadOnlyList<PageBlockSeed>>? Layout { get; init; }
    /// <summary>
    /// The address of a real example the editor shows next to the settings (<c>/shop/hausmischung</c>); null when there is none yet. The editor opens it with <c>?editor=1</c>, so the page is drawn
    /// by the plugin's own route, with real data, in the site's own layout.
    /// </summary>
    public Func<IPluginContext, CancellationToken, Task<string?>>? Preview { get; init; }
    /// <summary>
    /// The variables of that example, for the editor's live preview (which draws the page from the unsaved draft without going through the route). Null: the live preview shows no data.
    /// </summary>
    public Func<IPluginContext, CancellationToken, Task<PageScope?>>? Scope { get; init; }
}

/// <summary>One variable of a plugin page.</summary>
/// <param name="Name">Its name without the role (<c>title</c>).</param>
/// <param name="Label">What it is, as a string key of the plugin (<c>Der Name des Produkts</c>).</param>
public sealed record PageVariable(string Name, string Label)
{
    /// <summary>The value is ready-made HTML the plugin vouches for (a picture gallery, a form). It is for the block that draws HTML; anywhere else its markup shows as text.</summary>
    public bool Html { get; init; }
}

/// <summary>What a page layout is made for.</summary>
/// <param name="Locale">The content language (<c>de</c>, <c>en</c>).</param>
/// <param name="T">A text of the plugin in that language (a string key).</param>
public sealed record PageSeedContext(string Locale, Func<string, string> T);

/// <summary>One block of a page's starting layout.</summary>
/// <param name="Type">The block type (<c>el-section</c>, <c>el-heading</c>, a plugin's own block …).</param>
public sealed record PageBlockSeed(string Type)
{
    /// <summary>The block's settings by field name; texts may contain variables.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
    /// <summary>Blocks inside this one (a section's columns, a column's elements).</summary>
    public IReadOnlyList<PageBlockSeed> Children { get; init; } = Array.Empty<PageBlockSeed>();
}

/// <summary>The data a page is drawn with: variable values (without the role prefix — the host adds it) and the names of those that hold HTML.</summary>
public sealed class PageScope
{
    /// <summary>Nothing.</summary>
    public static readonly PageScope Empty = new(new Dictionary<string, string>());

    /// <summary>Creates a scope.</summary>
    /// <param name="values">Name → value.</param>
    /// <param name="htmlNames">The names whose values are HTML the plugin built (everything else is text and is encoded where HTML is drawn).</param>
    public PageScope(IReadOnlyDictionary<string, string> values, IEnumerable<string>? htmlNames = null)
    {
        Values = values;
        HtmlNames = (htmlNames ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The values.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }
    /// <summary>The names that hold HTML.</summary>
    public IReadOnlySet<string> HtmlNames { get; }
}
