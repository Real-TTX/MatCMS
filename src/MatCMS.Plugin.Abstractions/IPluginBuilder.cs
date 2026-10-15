namespace MatCMS.Plugin;

/// <summary>A handler: reads the request from <paramref name="ctx"/>, answers with a <see cref="PluginResult"/>.
/// Every handler is asynchronous and cancellable; the lambda forms in <see cref="PluginBuilderExtensions"/>
/// cover the common cases without the ceremony.</summary>
public delegate Task<PluginResult> PluginHandler(IPluginContext ctx, CancellationToken ct);

/// <summary>HTTP methods a route answers.</summary>
[Flags]
public enum PluginMethod
{
    /// <summary>GET (and HEAD).</summary>
    Get = 1,
    /// <summary>POST.</summary>
    Post = 2
}

/// <summary>Options of a public route.</summary>
public sealed record PublicRouteOptions
{
    /// <summary>The methods the route answers. Default: GET and POST.</summary>
    public PluginMethod Methods { get; init; } = PluginMethod.Get | PluginMethod.Post;

    /// <summary>
    /// Where a POST goes after the handler ran and returned something other than a redirect: the form's
    /// <c>__return</c> field when it is a safe local path, otherwise "/". Keeps a visitor submission from
    /// leaving the browser on a re-submittable POST page.
    /// </summary>
    public bool PostRedirectGet { get; init; }

    /// <summary>
    /// The route answers only on the site's default-language address (<c>/blog/x</c>), not under a language prefix
    /// (<c>/en/blog/x</c>, which then is a 404). For a plugin that does not publish its content in several languages — without this a
    /// prefixed address would show the same page in a language it does not have.
    /// </summary>
    public bool DefaultLanguageOnly { get; init; }

    /// <summary>
    /// Only a signed-in customer (a member of the guest area) gets the page. Anybody else is sent to <c>/anmelden?returnUrl=…</c> — exactly what a
    /// members-only page does — and the handler never runs. <see cref="IPluginContext.Member"/> is then never null inside it. The administrator
    /// is NOT let through here (unlike on a members-only page): a plugin whose handler reads <c>ctx.Member</c> has no use for somebody who has none.
    /// </summary>
    public bool RequireMember { get; init; }

    /// <summary>With <see cref="RequireMember"/>: the member must also hold this role. A member without it gets the same sign-in page with the
    /// "no access for this account" message.</summary>
    public string? RequiredRole { get; init; }

    /// <summary>
    /// The route checks the antiforgery token on a POST (a missing or wrong one answers 400 and the handler does not run) and offers one in
    /// <see cref="IPluginContext.Antiforgery"/> for the page's forms. Use it for everything a signed-in customer can change; leave it off for
    /// anonymous submissions with nothing to forge (they are rate-limited instead). The answer is marked <c>no-store</c>, because a page that
    /// carries a token must not be cached and handed to somebody else.
    /// </summary>
    public bool Antiforgery { get; init; }

    /// <summary>
    /// How many POSTs one visitor address may send to this plugin's public routes per minute before the host answers 429. The default (20) suits a contact or
    /// review form; a shop's cart and checkout are used by real customers, several of whom may share one address (an office, a campus, a mobile network), so it
    /// asks for more. The limit is counted per plugin and address, and is still a limit — an open write endpoint must not be hammered into filling the database.
    /// </summary>
    public int PostsPerMinute { get; init; } = 20;

    /// <summary>
    /// A route for another server to call (a payment provider's notification): the body is NOT read as a form but handed over raw in
    /// <see cref="RequestData.Body"/> (at most <see cref="RequestData.MaxWebhookBody"/> bytes; more is a 413), the per-address POST limit of public
    /// forms does not apply (a provider calls from a few addresses, in bursts), and the answer is the handler's, without a layout. Authenticating
    /// the call — a signature over the body — is the handler's job. POST only.
    /// </summary>
    public bool Webhook { get; init; }
}

/// <summary>Computes the small counter next to a sidebar entry ("3 new reviews"). Return null or "" for none. Runs on
/// every admin page, so keep it to a cheap count.</summary>
public delegate Task<string?> PluginBadgeProvider(IPluginContext ctx, CancellationToken ct);

/// <summary>Where a plugin's entry sits in the back-office sidebar.</summary>
public enum AdminGroup
{
    /// <summary>With the content entries (pages, posts, menus, forms, media).</summary>
    Content,
    /// <summary>With design and configuration (templates, components, plugins, settings).</summary>
    Design,
    /// <summary>With administration and system (statistics, users, backup, logs).</summary>
    System,
    /// <summary>A block of its own below the others. The default.</summary>
    Plugins
}

/// <summary>The kinds of field a block can declare; they map to the editors of the page editor.</summary>
public enum FieldKind
{
    /// <summary>Single-line text.</summary>
    Text,
    /// <summary>Multi-line text.</summary>
    Textarea,
    /// <summary>Formatted text.</summary>
    RichText,
    /// <summary>An image from the media library.</summary>
    Image,
    /// <summary>A link.</summary>
    Url,
    /// <summary>One choice out of <see cref="FieldSpec.Options"/>.</summary>
    Select,
    /// <summary>A repeatable list.</summary>
    List,
    /// <summary>Several choices out of <see cref="FieldSpec.Options"/> (or <see cref="FieldSpec.OptionsSource"/>), stored as ONE string joined with ", ".</summary>
    MultiSelect
}

/// <summary>One choice of a <see cref="FieldKind.Select"/> field.</summary>
public sealed record FieldOption(string Value, string Label);

/// <summary>An editable field of a block.</summary>
/// <param name="Id">The key under which the value is stored and read (<c>ctx.Block.Get(Id)</c>).</param>
/// <param name="Label">The label in the block editor.</param>
/// <param name="Kind">The editor to use.</param>
public sealed record FieldSpec(string Id, string Label, FieldKind Kind = FieldKind.Text)
{
    /// <summary>Placeholder text.</summary>
    public string? Placeholder { get; init; }
    /// <summary>Help text under the field.</summary>
    public string? Help { get; init; }
    /// <summary>The value of a new block.</summary>
    public string? Default { get; init; }
    /// <summary>The choices of a <see cref="FieldKind.Select"/>.</summary>
    public IReadOnlyList<FieldOption> Options { get; init; } = Array.Empty<FieldOption>();
    /// <summary>
    /// The name of a list the plugin registered with <see cref="IPluginBuilder.OptionsSource"/>: the choices are read when the editor opens, so a
    /// block can offer "the shop's categories" and not a text box to type a slug into. Replaces <see cref="Options"/>.
    /// </summary>
    public string? OptionsSource { get; init; }
    /// <summary>Show this field only while the field with this id has <see cref="ShowWhenValue"/> (a block with many options stays readable).</summary>
    public string? ShowWhenField { get; init; }
    /// <summary>The value <see cref="ShowWhenField"/> must have.</summary>
    public string? ShowWhenValue { get; init; }
}

/// <summary>A content block a plugin offers in the page editor.</summary>
/// <param name="Type">Stable block type; stored in pages, so never change it once used.</param>
/// <param name="Name">Name in the block picker.</param>
/// <param name="Description">One line under the name.</param>
public sealed record BlockSpec(string Type, string Name, string Description)
{
    /// <summary>The editable fields. Empty means a block without settings.</summary>
    public IReadOnlyList<FieldSpec> Fields { get; init; } = Array.Empty<FieldSpec>();
    /// <summary>The group in the block picker: <c>layout</c>, <c>text</c>, <c>media</c>, <c>design</c>, <c>form</c> or <c>embed</c>. Null puts the block under "Plugins".</summary>
    public string? Category { get; init; }
    /// <summary>The picker's icon as the inner markup of a 24×24 stroke SVG (the editor draws it). Null = the neutral plugin icon.</summary>
    public string? IconSvg { get; init; }
}

/// <summary>
/// Everything a plugin can contribute. A native plugin receives it in <see cref="IPlugin.Configure"/>, a
/// script as the global <c>Plugin</c> — the same type with the same members, which is what makes the two
/// kinds of plugin interchangeable.
/// <para>Registering has no side effects and may be called in any order. Nothing registered here outlives
/// the plugin: unloading removes all of it at once.</para>
/// </summary>
public interface IPluginBuilder
{
    /// <summary>The plugin being configured.</summary>
    PluginInfo Info { get; }

    /// <summary>
    /// A public page or endpoint. <paramref name="pattern"/> is a path template: literal segments,
    /// <c>{name}</c> for one segment and <c>{*name}</c> for the rest, e.g. <c>/shop/{slug}</c>. It is
    /// matched before the CMS's own pages, in every active language (<c>/en/shop/{slug}</c> too); the
    /// language is available as <see cref="IPluginContext.Locale"/>. A page answers inside the site layout.
    /// </summary>
    void Route(string pattern, PluginHandler handler, PublicRouteOptions? options = null);

    /// <summary>Back-office entries.</summary>
    IAdminBuilder Admin { get; }

    /// <summary>A content block for the page editor. The render handler answers with <see cref="PluginResult.Fragment(string)"/>.</summary>
    void Block(BlockSpec spec, PluginHandler render);

    /// <summary>
    /// A list of choices a block field can draw from (<see cref="FieldSpec.OptionsSource"/>), computed each time the page editor opens. Labels are
    /// display text, not string keys. A provider that throws gives an empty list; it never stops the editor.
    /// </summary>
    void OptionsSource(string name, Func<IPluginContext, CancellationToken, Task<IReadOnlyList<FieldOption>>> provider);

    /// <summary>Raw HTML for the &lt;head&gt; of every public page.</summary>
    void Head(string html);

    /// <summary>Raw HTML before the closing &lt;body&gt; of every public page.</summary>
    void Body(string html);

    /// <summary>Loads a JavaScript file of this plugin's <c>assets/</c> on every public page.
    /// Scripts run in registration order; <paramref name="defer"/> lets the browser parse first.</summary>
    void Script(string assetFile, bool inHead = false, bool defer = false);

    /// <summary>Loads a stylesheet of this plugin's <c>assets/</c> on every public page.</summary>
    void Style(string assetFile);

    /// <summary>The settings the plugin offers. The admin draws the form, validates it and stores the values.</summary>
    ISettingsBuilder Settings { get; }

    /// <summary>The tables the plugin keeps its data in.</summary>
    IDataBuilder Data { get; }

    /// <summary>Install, update and load handlers.</summary>
    ILifecycleBuilder Lifecycle { get; }

    /// <summary>Listens for an event, by name: one the host raises (<see cref="HostEvents"/>) or one another plugin emits.</summary>
    void On(string name, PluginEventHandler handler);

    /// <summary>A job that runs on a schedule while the plugin is loaded. Names are unique within a plugin.</summary>
    void Job(string name, JobSchedule schedule, PluginJobHandler handler);

    /// <summary>
    /// The plugin's texts for one language, looked up by <see cref="IPluginContext.T"/>. A label that is a key of these
    /// tables (a section's name, a tab, a setting) is translated by the host, so the plugin writes <c>Section("shop.title")</c>
    /// and not a hard-coded sentence. The host falls back to the default language, then to the key itself.
    /// </summary>
    void Strings(string culture, IReadOnlyDictionary<string, string> table);

    /// <summary>Adds the plugin's pages to the sitemap.</summary>
    void Sitemap(PluginSitemapProvider provider);

    /// <summary>Names the media files the plugin uses, so the cleanup does not offer them as unused.</summary>
    void MediaReferences(PluginMediaProvider provider);

    /// <summary>An operation the cloud or an agent (MCP) may ask for by name, with a JSON argument.</summary>
    void Operation(string name, PluginOperationHandler handler);

    /// <summary>
    /// A mail the plugin sends, with its default wording. It appears under <i>Admin → Mailvorlagen</i> from the first load on and the operator
    /// may edit it; <see cref="IPluginMail.SendAsync"/> then sends the edited version. The key must start with the plugin's key and a dot.
    /// </summary>
    void MailTemplate(MailTemplateSpec spec);
}

/// <summary>Back-office contributions.</summary>
public interface IAdminBuilder
{
    /// <summary>
    /// One sidebar entry with its pages. There is one entry per plugin section; further pages are tabs
    /// on the page itself, never more sidebar lines.
    /// </summary>
    /// <param name="label">Sidebar text and page title.</param>
    /// <param name="icon">A Tabler icon name such as <c>ti-shopping-cart</c>.</param>
    /// <param name="group">Where in the sidebar the entry sits.</param>
    /// <param name="order">Position inside the group; lower comes first.</param>
    IAdminSection Section(string label, string icon = "ti-plug", AdminGroup group = AdminGroup.Plugins, int order = 100);

    /// <summary>A figure tile on the dashboard (<c>Ui.Stat("Beiträge", "12", "3 Entwürfe", href: "/admin/p/blog")</c>). Null = no tile.</summary>
    void Tile(PluginTileProvider provider);

    /// <summary>Rows for the dashboard's "recently edited" list.</summary>
    void RecentlyEdited(PluginRecentProvider provider);

    /// <summary>Lines for "Braucht Aufmerksamkeit" (low stock, an unpaid order older than a week, …).</summary>
    void Attention(PluginAttentionProvider provider);
}

/// <summary>A sidebar entry and the pages behind it.</summary>
public interface IAdminSection
{
    /// <summary>
    /// A page of the section, served at <c>/admin/p/{key}/{pattern}</c>. With a <paramref name="tab"/> it
    /// appears as a tab; without one it is a detail page (<c>products/{id}</c>) that belongs to the tab
    /// whose pattern it starts with. The first page with a tab is the section's start page.
    /// </summary>
    IAdminSection Page(string pattern, string? tab, PluginHandler handler);

    /// <summary>A counter shown next to the section's sidebar entry. At most one per section.</summary>
    IAdminSection Badge(PluginBadgeProvider provider);
}

/// <summary>Convenience overloads: synchronous handlers and handlers without a cancellation token.</summary>
public static class PluginBuilderExtensions
{
    /// <summary>An event handler that only reacts and does not answer.</summary>
    public static void On(this IPluginBuilder b, string name, Func<IPluginContext, System.Text.Json.Nodes.JsonNode?, Task> handler) =>
        b.On(name, async (ctx, payload, _) => { await handler(ctx, payload); return null; });

    /// <summary>A job without the cancellation token.</summary>
    public static void Job(this IPluginBuilder b, string name, JobSchedule schedule, Func<IPluginContext, Task> handler) =>
        b.Job(name, schedule, (ctx, _) => handler(ctx));

    /// <summary>Handlers of the lifecycle that need no cancellation token.</summary>
    public static void OnInstall(this ILifecycleBuilder l, Func<IPluginContext, Task> handler) => l.OnInstall((ctx, _) => handler(ctx));

    /// <summary>The same for loading.</summary>
    public static void OnLoad(this ILifecycleBuilder l, Func<IPluginContext, Task> handler) => l.OnLoad((ctx, _) => handler(ctx));

    /// <summary>The same for an update.</summary>
    public static void OnUpdate(this ILifecycleBuilder l, Func<IPluginContext, string, Task> handler) => l.OnUpdate((ctx, from, _) => handler(ctx, from));

    /// <summary>Texts from a JSON object (<c>{"shop.title":"Shop"}</c>), e.g. an embedded resource.</summary>
    public static void StringsJson(this IPluginBuilder b, string culture, string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject
                   ?? throw new ArgumentException("The strings must be a JSON object.", nameof(json));
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in node) if (kv.Value is not null) table[kv.Key] = kv.Value.ToString();
        b.Strings(culture, table);
    }

    /// <summary>A public route with a synchronous handler.</summary>
    public static void Route(this IPluginBuilder b, string pattern, Func<IPluginContext, PluginResult> handler, PublicRouteOptions? options = null) =>
        b.Route(pattern, (ctx, _) => Task.FromResult(handler(ctx)), options);

    /// <summary>A public route with an asynchronous handler that ignores cancellation.</summary>
    public static void Route(this IPluginBuilder b, string pattern, Func<IPluginContext, Task<PluginResult>> handler, PublicRouteOptions? options = null) =>
        b.Route(pattern, (ctx, _) => handler(ctx), options);

    /// <summary>A block with a synchronous render.</summary>
    public static void Block(this IPluginBuilder b, BlockSpec spec, Func<IPluginContext, PluginResult> render) =>
        b.Block(spec, (ctx, _) => Task.FromResult(render(ctx)));

    /// <summary>A block whose render answers with plain HTML.</summary>
    public static void Block(this IPluginBuilder b, BlockSpec spec, Func<IPluginContext, string> render) =>
        b.Block(spec, (ctx, _) => Task.FromResult(PluginResult.Fragment(render(ctx))));

    /// <summary>An admin page with a synchronous handler.</summary>
    public static IAdminSection Page(this IAdminSection s, string pattern, string? tab, Func<IPluginContext, PluginResult> handler) =>
        s.Page(pattern, tab, (ctx, _) => Task.FromResult(handler(ctx)));

    /// <summary>An admin page with an asynchronous handler that ignores cancellation.</summary>
    public static IAdminSection Page(this IAdminSection s, string pattern, string? tab, Func<IPluginContext, Task<PluginResult>> handler) =>
        s.Page(pattern, tab, (ctx, _) => handler(ctx));

    /// <summary>A counter with a synchronous provider.</summary>
    public static IAdminSection Badge(this IAdminSection s, Func<IPluginContext, string?> provider) =>
        s.Badge((ctx, _) => Task.FromResult(provider(ctx)));
}
