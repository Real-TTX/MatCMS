using MatCMS.Plugin;

namespace MatCMS.Services.Mcp;

/// <summary>
/// One tool of the site's MCP server. <see cref="Area"/> + <see cref="Access"/> is the right a key needs (<c>core.pages:write</c>);
/// an empty area means the tool is gated by plugin rights instead (<c>list_plugin_operations</c>, <c>run_plugin_operation</c>) or by
/// "any write right" (<c>undo_last_change</c>). The hints are what MCP clients show their user before a call (and what a careful client
/// asks about): read-only tools run without asking, destructive ones get a confirmation.
/// </summary>
public sealed record SiteMcpTool(
    string Name, string Title, string Description, string Schema, string Area, ApiAccess Access,
    bool ReadOnly = false, bool Destructive = false, bool Idempotent = false);

/// <summary>
/// The tools of the site's MCP server (<see cref="SiteMcpServer"/>). Almost all of them ARE tools of the site's AI assistant
/// (<see cref="Assistant.AssistantTools"/>), called with the same arguments: one write path with one set of checks (<c>AssistantBlocks</c> for
/// every field kind and nested blocks, <c>AssistantMarkup</c>, <c>SafeHtml</c>, <c>SafeSvg</c>), one undo journal. The descriptions are English
/// (AI clients are), the answers keep the assistant's German texts; a client reads both.
/// <para>NOT offered (v1): plugin code (create/update_plugin), form submissions, member accounts, backups — each is a door of its own.</para>
/// </summary>
public static class SiteMcpCatalog
{
    private const string PageRef = """{"type":["integer","string"],"description":"Page id or slug (ids are in site_overview)."}""";
    private const string Locale = """{"type":"string","description":"Language code, e.g. \"de\" or \"en\" (default: the site's default language)."}""";
    private const string Block = """
        {"type":"object","description":"A block: {\"type\": \"hero\", \"data\": {fieldId: value, …}, \"children\": [blocks]} — types, fields and which children a container takes are in block_catalog. Pictures only from the media library (list_media, upload_media).",
         "properties":{"type":{"type":"string"},"data":{"type":"object"},"children":{"type":"array","items":{"type":"object"}}},"required":["type"]}
        """;
    private static string Blocks => """{"type":"array","items":""" + Block + "}";
    private const string After = """{"type":["integer","string"],"description":"A block id to insert behind, or \"start\" / \"end\" (default: end)."}""";
    private const string MenuItems = """
        {"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"url":{"type":"string","description":"A local address (/kontakt) or https://…"},
         "children":{"type":"array","items":{"type":"object"}},"newTab":{"type":"boolean"}},"required":["label","url"]}}
        """;
    private const string FormFields = """
        {"type":"array","description":"Fields: {\"id\", \"type\", \"label\", \"required\"?, \"placeholder\"?, \"options\"?: [\"A\",\"B\"] or [{\"value\",\"label\"}], \"fields\"? (only for group)}. Types: text, textarea, email, phone, number, date, daterange, select, multiselect, richselect, title, description, group.",
         "items":{"type":"object"}}
        """;
    private const string ComponentFields = """
        {"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"label":{"type":"string"},"type":{"type":"string","enum":["text","textarea","richtext","image","url"]}},"required":["id","label","type"]}}
        """;

    private static string Obj(string properties, params string[] required) =>
        "{\"type\":\"object\",\"properties\":{" + properties + "}" + (required.Length > 0 ? ",\"required\":[" + string.Join(",", required.Select(r => "\"" + r + "\"")) + "]" : "") + "}";

    private const string Pages = "core.pages", Media = "core.media", Forms = "core.forms", Design = "core.design", Settings = "core.settings";

    public static readonly SiteMcpTool[] All =
    [
        // ---------------------------------------------------------------------------------------------------------- reading
        new("site_overview", "Site overview",
            "Start here. The site's name, languages, every page (id, slug, title, locale, published, translations), the menus, the active design and the forms.",
            Obj(""), Pages, ApiAccess.Read, ReadOnly: true, Idempotent: true),
        new("block_catalog", "Block catalogue",
            "Every block type this site offers, with ALL its fields (kind, options, limits) and which blocks a container takes as children. Read it before writing blocks — a field that does not fit is dropped with a warning.",
            Obj(""), Pages, ApiAccess.Read, ReadOnly: true, Idempotent: true),
        new("read_page", "Read a page",
            "One page with all its blocks as a tree: id, type, data (every field), children.",
            Obj("\"page\":" + PageRef + ",\"locale\":" + Locale, "page"), Pages, ApiAccess.Read, ReadOnly: true, Idempotent: true),
        new("list_media", "List media",
            "Pictures and files of the media library (url, alt, tags, size). Use only these addresses for pictures.",
            Obj("\"search\":{\"type\":\"string\"},\"tag\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}"), Media, ApiAccess.Read, ReadOnly: true, Idempotent: true),
        new("read_theme", "Read the design",
            "The active design: colours, fonts, button style, widths, template parameters, custom CSS.",
            Obj(""), Design, ApiAccess.Read, ReadOnly: true, Idempotent: true),
        new("list_components", "List components",
            "The site's own components (block types made on this site): type, name, fields.",
            Obj(""), Design, ApiAccess.Read, ReadOnly: true, Idempotent: true),
        new("read_component", "Read a component",
            "One component with its fields and its HTML template.",
            Obj("\"component\":{\"type\":\"string\",\"description\":\"The component's type (or id).\"}", "component"), Design, ApiAccess.Read, ReadOnly: true, Idempotent: true),
        new("list_templates", "List templates",
            "Every template (design) of the site: id, name, which one is active.",
            Obj(""), Design, ApiAccess.Read, ReadOnly: true, Idempotent: true),
        new("read_template", "Read a template",
            "The files of a template (default: the active one): body.html (layout with {{content}}), styles.css, login.html, article.html, maintenance.html, plugin parts, parameters. script.js is read-only.",
            Obj("\"template\":{\"type\":\"integer\"}"), Design, ApiAccess.Read, ReadOnly: true, Idempotent: true),
        new("read_form", "Read a form",
            "One form with all its fields (the list of forms is in site_overview).",
            Obj("\"form\":{\"type\":\"string\",\"description\":\"The form's slug.\"}", "form"), Forms, ApiAccess.Read, ReadOnly: true, Idempotent: true),

        // ---------------------------------------------------------------------------------------------------------- pages and blocks
        new("create_page", "Create a page",
            "A new page with its blocks. translationOf = id of the page this one translates (give locale too). Every write can be taken back with undo_last_change.",
            Obj("\"title\":{\"type\":\"string\"},\"slug\":{\"type\":\"string\"},\"locale\":" + Locale + ",\"translationOf\":{\"type\":\"integer\"},\"metaDescription\":{\"type\":\"string\"},\"published\":{\"type\":\"boolean\"},\"addToMenu\":{\"type\":\"boolean\"},\"blocks\":" + Blocks, "title"),
            Pages, ApiAccess.Write),
        new("update_page", "Change a page's settings",
            "Title, slug, meta description, published, menu label or the page's own CSS — not its blocks.",
            Obj("\"page\":" + PageRef + ",\"title\":{\"type\":\"string\"},\"slug\":{\"type\":\"string\"},\"metaDescription\":{\"type\":\"string\"},\"published\":{\"type\":\"boolean\"},\"navLabel\":{\"type\":\"string\"},\"customCss\":{\"type\":\"string\"}", "page"),
            Pages, ApiAccess.Write, Idempotent: true),
        new("set_page_blocks", "Replace all blocks of a page",
            "Rebuilds a page: ALL its blocks are replaced by these. For a small change use update_block.",
            Obj("\"page\":" + PageRef + ",\"blocks\":" + Blocks, "page", "blocks"), Pages, ApiAccess.Write, Destructive: true, Idempotent: true),
        new("add_blocks", "Insert blocks",
            "Inserts blocks at the top level of a page or into a container (parent), behind a block or at the start/end.",
            Obj("\"page\":" + PageRef + ",\"blocks\":" + Blocks + ",\"parent\":{\"type\":\"integer\",\"description\":\"Container block id.\"},\"after\":" + After, "page", "blocks"),
            Pages, ApiAccess.Write),
        new("update_block", "Change a block",
            "Changes fields of ONE block: fields not named stay, null clears a field, a list is replaced as a whole.",
            Obj("\"block\":{\"type\":\"integer\"},\"data\":{\"type\":\"object\",\"description\":\"{fieldId: value, …}\"}", "block", "data"), Pages, ApiAccess.Write, Idempotent: true),
        new("move_block", "Move a block",
            "Moves a block (parent null = top level of its page).",
            Obj("\"block\":{\"type\":\"integer\"},\"parent\":{\"type\":[\"integer\",\"null\"]},\"after\":" + After, "block"), Pages, ApiAccess.Write, Idempotent: true),
        new("delete_block", "Delete a block",
            "Deletes a block with everything inside it.",
            Obj("\"block\":{\"type\":\"integer\"}", "block"), Pages, ApiAccess.Write, Destructive: true, Idempotent: true),
        new("delete_page", "Delete a page",
            "Deletes a page (never the home page).",
            Obj("\"page\":" + PageRef, "page"), Pages, ApiAccess.Write, Destructive: true, Idempotent: true),
        new("set_menu", "Replace a menu",
            "Sets a whole menu anew (header, footer, …) for one language. Plugin entries (cart, sign-in) stay.",
            Obj("\"menu\":{\"type\":\"string\",\"description\":\"header, footer or another menu key from site_overview.\"},\"locale\":" + Locale + ",\"items\":" + MenuItems, "menu", "items"),
            Pages, ApiAccess.Write, Destructive: true, Idempotent: true),

        // ---------------------------------------------------------------------------------------------------------- media
        new("upload_media", "Upload a file",
            "Puts a picture or document into the media library (png, jpg, gif, webp, svg, pdf, docx …) and answers its url. An identical file already there is reused. SVG is cleaned to drawing elements.",
            Obj("\"fileName\":{\"type\":\"string\",\"description\":\"With extension, e.g. team.jpg\"},\"dataBase64\":{\"type\":\"string\",\"description\":\"The file's bytes, base64.\"},\"alt\":{\"type\":\"string\"}", "fileName", "dataBase64"),
            Media, ApiAccess.Write),
        new("create_svg", "Draw an SVG",
            "Draws a graphic (logo, icon, illustration) as SVG into the media library. Only drawing elements survive; scripts, outside links and embedded pictures are removed.",
            Obj("\"name\":{\"type\":\"string\"},\"svg\":{\"type\":\"string\"},\"alt\":{\"type\":\"string\"}", "name", "svg"), Media, ApiAccess.Write),

        // ---------------------------------------------------------------------------------------------------------- forms
        new("create_form", "Create a form",
            "A new form. Put it on a page with the block {\"type\": \"form\", \"data\": {\"form\": slug}}. Where submissions are mailed is set by the site's admin, not here.",
            Obj("\"name\":{\"type\":\"string\"},\"slug\":{\"type\":\"string\"},\"fields\":" + FormFields + ",\"successMessage\":{\"type\":\"string\"},\"submitLabel\":{\"type\":\"string\"}", "name", "fields"),
            Forms, ApiAccess.Write),
        new("update_form", "Change a form",
            "Changes a form; fields replaces the whole list (a changed field id separates it from earlier submissions). Slug and notification stay.",
            Obj("\"form\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"fields\":" + FormFields + ",\"successMessage\":{\"type\":\"string\"},\"submitLabel\":{\"type\":\"string\"}", "form"),
            Forms, ApiAccess.Write, Idempotent: true),

        // ---------------------------------------------------------------------------------------------------------- design
        new("update_theme", "Change the design",
            "Colours, fonts, button style, widths and parameters of the active template; customCss replaces the template's own CSS, appendCss adds to it.",
            Obj("\"values\":{\"type\":\"object\",\"description\":\"AccentColor, SecondaryColor, HeadingColor, TextColor, BackgroundColor, AltBackground, HeaderBackground, HeaderTextColor, HeadingFont, BodyFont, ButtonStyle, ButtonRadius, ContainerWidth, HeaderPadding\"},\"params\":{\"type\":\"object\"},\"customCss\":{\"type\":\"string\"},\"appendCss\":{\"type\":\"string\"}"),
            Design, ApiAccess.Write, Idempotent: true),
        new("create_component", "Create a component",
            "A new block type: HTML with {{fieldId}} placeholders (values are encoded, richtext raw). No <script>, no on… attributes. Usable as a block with type = its type at once.",
            Obj("\"name\":{\"type\":\"string\"},\"type\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"fields\":" + ComponentFields + ",\"html\":{\"type\":\"string\"}", "name", "fields", "html"),
            Design, ApiAccess.Write),
        new("update_component", "Change a component",
            "Changes a component (fields replaces the list; a renamed field id makes existing blocks lose that value).",
            Obj("\"component\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"fields\":" + ComponentFields + ",\"html\":{\"type\":\"string\"}", "component"),
            Design, ApiAccess.Write, Idempotent: true),
        new("create_template", "Copy a template",
            "A new template as a copy (default: of the active one) to try changes safely; make it the design with activate_template.",
            Obj("\"name\":{\"type\":\"string\"},\"from\":{\"type\":\"integer\"}", "name"), Design, ApiAccess.Write),
        new("update_template", "Change template files",
            "Replaces template files (empty = the system's default). The layout needs {{content}}. No <script>, no on… attributes; script.js is locked.",
            Obj("\"template\":{\"type\":\"integer\"},\"files\":{\"type\":\"object\",\"description\":\"{\\\"body.html\\\": \\\"…\\\", \\\"styles.css\\\": \\\"…\\\", …}\"}", "files"),
            Design, ApiAccess.Write, Destructive: true, Idempotent: true),
        new("activate_template", "Activate a template",
            "Makes a template the site's active design.",
            Obj("\"template\":{\"type\":\"integer\"}", "template"), Design, ApiAccess.Write, Idempotent: true),

        // ---------------------------------------------------------------------------------------------------------- settings
        new("update_settings", "Change basic settings",
            "Site name, footer text, logo and favicon (addresses from the media library, SVG too), contact recipient.",
            Obj("\"siteName\":{\"type\":\"string\"},\"footerText\":{\"type\":\"string\"},\"logoUrl\":{\"type\":\"string\"},\"faviconUrl\":{\"type\":\"string\"},\"contactRecipient\":{\"type\":\"string\"}"),
            Settings, ApiAccess.Write, Idempotent: true),

        // ---------------------------------------------------------------------------------------------------------- plugins and undo
        new(SiteMcpServer.ListOperations, "List plugin operations",
            "The operations the site's plugins offer to this key (blog posts, shop products, …): name, what it does, whether it changes data, example arguments.",
            Obj(""), "", ApiAccess.Read, ReadOnly: true, Idempotent: true),
        new(SiteMcpServer.RunOperation, "Run a plugin operation",
            "Runs one operation from list_plugin_operations with its arguments.",
            Obj("\"name\":{\"type\":\"string\",\"description\":\"e.g. blog.post.create (as list_plugin_operations names it)\"},\"args\":{\"type\":\"object\"}", "name"), "", ApiAccess.Write, Destructive: true),
        new(SiteMcpServer.UndoLast, "Undo the last change",
            "Takes back the newest change this key made (one call at a time, newest first): blocks, pages, menus, design, forms, media and plugin operations that can be undone.",
            Obj(""), "", ApiAccess.Write, Destructive: true),
    ];
}
