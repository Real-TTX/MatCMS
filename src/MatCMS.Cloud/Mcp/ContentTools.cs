using System.ComponentModel;
using System.Text.Json;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>
/// MCP tools that CHANGE a connected site's content (Stage 2, Increment 1: creating pages). They do NOT
/// touch the site directly — the cloud can't reach in. A tool ENQUEUES a <see cref="ContentOp"/>; the
/// instance pulls it on its next heartbeat and applies it through its OWN validated writer
/// (<c>BlockGenerator</c> + the add-only page writer), then reports the outcome. So every write here is
/// asynchronous ("queued as op N, applied within ~a minute") and add-only, and the cloud ships intent —
/// never code. Gated on the key's restore right: writing to a live site is the "restore" of content.
/// </summary>
[McpServerToolType]
public class ContentTools
{
    private static async Task<Instance> ResolveWritableAsync(McpContext me, InstanceService instances, string instanceId, CancellationToken ct)
    {
        var key = me.Key;
        if (!key.CanRestore)
            throw new McpException("Dieser Schlüssel darf keine Inhalte ändern (fehlendes Wiederherstellen-Recht).");
        var instance = await instances.FindForKeyAsync(instanceId, key, ct: ct);
        if (instance is null)
            throw new McpException("Instanz nicht gefunden.");
        if (instance.Status != InstanceStatus.Approved)
            throw new McpException("Instanz ist nicht freigegeben.");
        return instance;
    }

    [McpServerTool(Name = "create_page"), Description(
        "Create a NEW page on a connected MatCMS site (add-only: if the slug already exists the instance skips it, never overwrites). " +
        "You supply the page's blocks; the instance re-validates them (only known block types and text fields survive) and publishes the page. " +
        "This is queued and applied on the site's next heartbeat (~a minute) — check the returned opId's outcome via get_content_op or the instance log. Requires a key with the restore right.")]
    public static async Task<object> CreatePage(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The page title shown to visitors, e.g. \"Über uns\".")] string title,
        [Description("URL slug, lowercase, no spaces, e.g. \"ueber-uns\". Must be unique on the site.")] string slug,
        [Description("A JSON array of blocks: [{\"type\":\"hero\",\"data\":{\"title\":\"…\",\"text\":\"…\"}}, …]. Use block types the site knows (e.g. hero, richtext, cards). Unknown types/fields are dropped by the instance.")] string blocksJson,
        [Description("Whether to add the page to the site navigation (default true).")] bool showInNav = true,
        [Description("Optional navigation label; falls back to the title.")] string? navLabel = null,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);

        // Validate the AI-supplied blocks are at least well-formed JSON here (fail fast with a clear
        // message); the real content validation is the instance's own BlockGenerator, which is the only
        // thing that decides what is actually applied.
        JsonElement blocks;
        try
        {
            blocks = JsonSerializer.Deserialize<JsonElement>(blocksJson);
        }
        catch (JsonException)
        {
            throw new McpException("blocksJson ist kein gültiges JSON. Erwartet: ein Array von {\"type\":…,\"data\":{…}}.");
        }
        if (blocks.ValueKind != JsonValueKind.Array)
            throw new McpException("blocksJson muss ein JSON-Array sein.");

        var payload = JsonSerializer.Serialize(new
        {
            title = (title ?? "").Trim(),
            slug = (slug ?? "").Trim(),
            blocks,
            showInNav,
            navLabel,
        });

        var op = await instances.EnqueueContentOpAsync(instance, "page.create", payload,
            overwrite: false, reason: "KI: Seite anlegen", ct);
        return new
        {
            ok = true,
            opId = op.Id,
            queued = true,
            message = "Seite eingereiht — die Website legt sie beim nächsten Kontakt (~1 Min.) an. Ergebnis via get_content_op abrufbar.",
        };
    }

    [McpServerTool(Name = "create_post"), Description(
        "Create a NEW blog post (\"Beitrag\") on a connected site (add-only: an existing slug is skipped). " +
        "The body and teaser are HTML and are SANITISED by the instance before storage (scripts/handlers removed). " +
        "Queued and applied on the next heartbeat (~a minute); check get_content_op(opId). Requires a key with the restore right.")]
    public static async Task<object> CreatePost(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The post heading, e.g. \"Sommer-Angebote 2026\".")] string title,
        [Description("Short teaser shown on cards/listings (plain text or simple HTML).")] string excerpt,
        [Description("The post body as HTML (will be sanitised: only safe formatting/links/images survive).")] string contentHtml,
        [Description("URL slug, lowercase, no spaces. Falls back to a slug of the title if empty.")] string? slug = null,
        [Description("Comma-separated tags, e.g. \"angebot,sommer\".")] string? tags = null,
        [Description("Publish immediately (true) or save as draft (false). Default true.")] bool publish = true,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);
        var payload = JsonSerializer.Serialize(new
        {
            title = (title ?? "").Trim(),
            slug = (slug ?? "").Trim(),
            excerpt = excerpt ?? "",
            contentHtml = contentHtml ?? "",
            tags = (tags ?? "").Trim(),
            publish,
        });
        var op = await instances.EnqueueContentOpAsync(instance, "post.create", payload,
            overwrite: false, reason: "KI: Beitrag anlegen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Beitrag eingereiht — wird beim nächsten Kontakt angelegt. Ergebnis via get_content_op." };
    }

    [McpServerTool(Name = "create_product"), Description(
        "Create a NEW shop product on a connected site (add-only: an existing slug is skipped). The shop plugin must be switched on there. " +
        "The description is HTML and is SANITISED by the instance before storage. The price is the GROSS price the customer pays, in cents (or as text like \"19,90\"). " +
        "Queued and applied on the next heartbeat (~a minute); check get_content_op(opId). Requires a key with the restore right.")]
    public static async Task<object> CreateProduct(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The product name, e.g. \"Kaffeetasse Rot\".")] string title,
        [Description("The gross price in cents, e.g. 1990 for 19,90 EUR.")] long priceCents,
        [Description("\"physical\" (shipped) or \"digital\" (a download; files are added in the shop's admin). Default physical.")] string? kind = null,
        [Description("One or two sentences for the product cards.")] string? summary = null,
        [Description("The description as HTML (sanitised: only safe formatting/links/images survive).")] string? descriptionHtml = null,
        [Description("URL slug, lowercase, no spaces. Falls back to a slug of the title if empty.")] string? slug = null,
        [Description("Tax rate in percent, e.g. 19 or 7. Falls back to the shop's default rate.")] string? taxPercent = null,
        [Description("Pieces in stock; leave empty for unlimited (and for downloads).")] long? stock = null,
        [Description("Article number.")] string? sku = null,
        [Description("Publish immediately (true) or keep it hidden (false). Default true.")] bool publish = true,
        [Description("The category's name, e.g. \"Kaffee\". It is created when the shop has no such category yet.")] string? category = null,
        [Description("Keywords, comma separated, e.g. \"Bio, Geschenk\". Visitors can filter by them, page blocks can pick by them.")] string? tags = null,
        [Description("Highlight the product (shown first where a block shows the highlighted products). Default false.")] bool featured = false,
        [Description("An earlier price in cents, shown struck through next to the price (a sale). Must be higher than priceCents; it is never what is charged.")] long? compareCents = null,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);
        if (priceCents < 0) throw new McpException("priceCents darf nicht negativ sein.");
        var payload = JsonSerializer.Serialize(new
        {
            title = (title ?? "").Trim(),
            slug = (slug ?? "").Trim(),
            price_cents = priceCents,
            kind = (kind ?? "physical").Trim(),
            summary = summary ?? "",
            description_html = descriptionHtml ?? "",
            tax_percent = string.IsNullOrWhiteSpace(taxPercent) ? null : taxPercent.Trim(),
            stock,
            sku = sku ?? "",
            publish,
            category = (category ?? "").Trim(),
            tags = (tags ?? "").Trim(),
            featured,
            compare_cents = compareCents is > 0 ? compareCents : null,
        });
        var op = await instances.EnqueueContentOpAsync(instance, "product.create", payload,
            overwrite: false, reason: "KI: Produkt anlegen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Produkt eingereiht — wird beim nächsten Kontakt angelegt. Ergebnis via get_content_op." };
    }

    [McpServerTool(Name = "create_coupon"), Description(
        "Create a NEW coupon code in the shop of a connected site (add-only: an existing code is skipped). The shop plugin must be switched on there. " +
        "kind \"percent\" takes percent (e.g. 10), \"fixed\" takes amountCents, \"shipping\" is free shipping. A coupon reduces the GOODS (never shipping, except \"shipping\"). " +
        "Queued and applied on the next heartbeat (~a minute); check get_content_op(opId). Requires a key with the restore right.")]
    public static async Task<object> CreateCoupon(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("\"percent\", \"fixed\" or \"shipping\" (free shipping). Default percent.")] string? kind = null,
        [Description("The code customers type, letters/digits/dash; case does not matter. Empty: a random code is made.")] string? code = null,
        [Description("For kind percent: the percentage, e.g. 10 for 10 %.")] string? percent = null,
        [Description("For kind fixed: the amount in cents, e.g. 500 for 5,00 EUR.")] long? amountCents = null,
        [Description("Minimum value of the goods in cents before the coupon applies. Default none.")] long? minOrderCents = null,
        [Description("Valid through this day, format yyyy-MM-dd. Default no end.")] string? expires = null,
        [Description("How often the code may be used in all. Default unlimited.")] int? maxUses = null,
        [Description("Only once per e-mail address. Default false.")] bool oncePerCustomer = false,
        [Description("Only goods of this category count; the category must exist. Default all goods.")] string? category = null,
        [Description("A note for the shop owner, e.g. \"Newsletter Mai\".")] string? note = null,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);
        var k = (kind ?? "percent").Trim().ToLowerInvariant();
        if (k is not ("percent" or "fixed" or "shipping")) throw new McpException("kind muss percent, fixed oder shipping sein.");
        if (k == "percent" && string.IsNullOrWhiteSpace(percent)) throw new McpException("Für percent fehlt \"percent\".");
        if (k == "fixed" && amountCents is not > 0) throw new McpException("Für fixed fehlt \"amountCents\" (größer als 0).");
        var payload = JsonSerializer.Serialize(new
        {
            kind = k,
            code = (code ?? "").Trim(),
            percent = string.IsNullOrWhiteSpace(percent) ? null : percent.Trim(),
            amount_cents = amountCents,
            min_order_cents = minOrderCents,
            expires = string.IsNullOrWhiteSpace(expires) ? null : expires.Trim(),
            max_uses = maxUses,
            once_per_customer = oncePerCustomer,
            category = (category ?? "").Trim(),
            note = (note ?? "").Trim(),
        });
        var op = await instances.EnqueueContentOpAsync(instance, "coupon.create", payload,
            overwrite: false, reason: "KI: Gutschein anlegen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Gutschein eingereiht — wird beim nächsten Kontakt angelegt. Ergebnis via get_content_op." };
    }

    // ---- the shop: orders, variants, attributes (the site's shop plugin answers; queued like every content op) ----

    [McpServerTool(Name = "list_shop_orders"), Description(
        "Read orders of the shop of a connected site: ONE order by number, or the newest ones (optionally of one status: awaiting_payment, paid, shipped, completed, cancelled, refunded; optionally since a day yyyy-MM-dd; at most 100). " +
        "The answer holds customers' names, e-mail and delivery addresses, so it needs a key with the restore right. QUEUED: returns an opId; poll get_content_op(opId) whose `result` holds { orders:[{ number, status, totals in cents, items, shippingAddress, tracking … }], total }.")]
    public static async Task<object> ListShopOrders(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("One order's number, e.g. \"SH-1001\". Leave empty for a list.")] string? number = null,
        [Description("Only orders of this status.")] string? status = null,
        [Description("Only orders since this day, format yyyy-MM-dd.")] string? since = null,
        [Description("How many (1-100). Default 25.")] int? limit = null,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);
        var payload = JsonSerializer.Serialize(new { number = (number ?? "").Trim(), status = (status ?? "").Trim(), since = (since ?? "").Trim(), limit });
        var op = await instances.EnqueueContentOpAsync(instance, "order.list", payload, overwrite: false, reason: "KI: Bestellungen lesen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Angefragt — Ergebnis via get_content_op(opId)." };
    }

    [McpServerTool(Name = "update_shop_order"), Description(
        "Move ONE order of a connected site's shop along, the way the shop's admin does: action \"paid\" (mark as paid: invoice, stock, mails), \"ship\" (with carrier and tracking number; the customer is notified unless notify=false), " +
        "\"complete\", \"cancel\", \"note\" (an internal note) or \"tracking\" (only set carrier/tracking). A move the order cannot make (e.g. completing an unpaid order) is refused with the reason. " +
        "Queued and applied on the next heartbeat (~a minute); check get_content_op(opId). Requires a key with the restore right.")]
    public static async Task<object> UpdateShopOrder(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The order's number, e.g. \"SH-1001\".")] string number,
        [Description("paid, ship, complete, cancel, note or tracking.")] string action,
        [Description("For ship/tracking: the tracking number.")] string? tracking = null,
        [Description("For ship/tracking: the carrier, e.g. dhl, dpd, ups, hermes, gls, fedex, post-at, swiss-post or custom.")] string? carrier = null,
        [Description("For note: the text.")] string? text = null,
        [Description("Tell the customer by mail (ship: default true; cancel: default false).")] bool? notify = null,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);
        var a = (action ?? "").Trim().ToLowerInvariant();
        if (a is not ("paid" or "ship" or "complete" or "cancel" or "note" or "tracking")) throw new McpException("action muss paid, ship, complete, cancel, note oder tracking sein.");
        if (string.IsNullOrWhiteSpace(number)) throw new McpException("number fehlt.");
        var payload = JsonSerializer.Serialize(new { number = number.Trim(), action = a, tracking = (tracking ?? "").Trim(), carrier = (carrier ?? "").Trim(), text = (text ?? "").Trim(), notify });
        var op = await instances.EnqueueContentOpAsync(instance, "order.update", payload, overwrite: true, reason: "KI: Bestellung bearbeiten", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Bestellung-Aktion eingereiht — wird beim nächsten Kontakt ausgeführt. Ergebnis via get_content_op." };
    }

    [McpServerTool(Name = "set_product_variants"), Description(
        "Set the variants (size, colour …) of an EXISTING product of a connected site's shop by the product's slug. variantsJson is a JSON array of { name, deltaCents, stock, sku, image } — a variant with a known name is updated in place (stock, orders and carts keep pointing at it), a new name is added. " +
        "With replace=true the variants not in the list are switched off (never deleted). optionsJson ({\"Größe\":[\"S\",\"M\"]}) lets the storefront show a separate picker per option; name the variants \"S / Rot\" (values joined by \" / \"). " +
        "Queued and applied on the next heartbeat (~a minute); check get_content_op(opId). Requires a key with the restore right.")]
    public static async Task<object> SetProductVariants(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The product's slug.")] string product,
        [Description("JSON array: [{\"name\":\"M\",\"deltaCents\":200,\"stock\":5,\"sku\":\"T-M\",\"image\":\"/uploads/x.jpg\"}]. deltaCents is the surcharge on the product's price; leave stock out for unlimited.")] string variantsJson,
        [Description("Switch off the product's variants that are not in the list. Default false.")] bool replace = false,
        [Description("Optional JSON object option name → values, e.g. {\"Größe\":[\"S\",\"M\"],\"Farbe\":[\"Rot\"]}.")] string? optionsJson = null,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);
        System.Text.Json.Nodes.JsonNode? variants, options = null;
        try { variants = System.Text.Json.Nodes.JsonNode.Parse(variantsJson ?? ""); if (!string.IsNullOrWhiteSpace(optionsJson)) options = System.Text.Json.Nodes.JsonNode.Parse(optionsJson); }
        catch (JsonException) { throw new McpException("variantsJson/optionsJson ist kein gültiges JSON."); }
        if (variants is not System.Text.Json.Nodes.JsonArray list || list.Count == 0) throw new McpException("variantsJson muss ein nicht leeres Array sein.");
        foreach (var v in list.OfType<System.Text.Json.Nodes.JsonObject>())
            if (v["deltaCents"] is { } d) { v["delta_cents"] = d.DeepClone(); v.Remove("deltaCents"); }
        var payload = new System.Text.Json.Nodes.JsonObject { ["product"] = (product ?? "").Trim(), ["variants"] = list, ["replace"] = replace };
        if (options is System.Text.Json.Nodes.JsonObject) payload["options"] = options;
        var op = await instances.EnqueueContentOpAsync(instance, "variant.set", payload.ToJsonString(), overwrite: true, reason: "KI: Varianten setzen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Varianten eingereiht — wird beim nächsten Kontakt gesetzt. Ergebnis via get_content_op." };
    }

    [McpServerTool(Name = "create_shop_attribute"), Description(
        "Define a NEW product attribute (Eigenschaft) in the shop of a connected site (add-only: an existing address is skipped): a choice (colour with options), free text, a number (with a unit) or yes/no. Attributes can be shown on the product page and offered as filters. " +
        "Queued and applied on the next heartbeat (~a minute); check get_content_op(opId). Requires a key with the restore right.")]
    public static async Task<object> CreateShopAttribute(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The attribute's name, e.g. \"Farbe\".")] string name,
        [Description("choice (default), text, number or bool.")] string? kind = null,
        [Description("For a choice: the options, comma separated, e.g. \"Rot, Blau, Grün\".")] string? options = null,
        [Description("For a number: the unit shown behind it, e.g. kg.")] string? unit = null,
        [Description("Offer it as a filter in the catalogue. Default true.")] bool filterable = true,
        [Description("Limit it to this category's slug (and the categories below); it must exist. Default: every product.")] string? category = null,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);
        var k = (kind ?? "choice").Trim().ToLowerInvariant();
        if (k is not ("choice" or "text" or "number" or "bool")) throw new McpException("kind muss choice, text, number oder bool sein.");
        if (string.IsNullOrWhiteSpace(name)) throw new McpException("name fehlt.");
        var payload = JsonSerializer.Serialize(new
        {
            name = name.Trim(), kind = k, options = (options ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            unit = (unit ?? "").Trim(), filterable, category = (category ?? "").Trim()
        });
        var op = await instances.EnqueueContentOpAsync(instance, "attribute.create", payload, overwrite: false, reason: "KI: Eigenschaft anlegen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Eigenschaft eingereiht — wird beim nächsten Kontakt angelegt. Ergebnis via get_content_op." };
    }

    [McpServerTool(Name = "set_product_attributes"), Description(
        "Give an EXISTING product of a connected site's shop values for its attributes, by the product's slug. valuesJson is an object attribute-address → value: a choice takes one option or a list (only the attribute's own options are stored), a number a number (\"1,5\"), a text a text, a yes/no true/false. " +
        "An unknown attribute or an invalid value is reported in the result, not stored. Queued and applied on the next heartbeat (~a minute); check get_content_op(opId). Requires a key with the restore right.")]
    public static async Task<object> SetProductAttributes(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The product's slug.")] string product,
        [Description("JSON object, e.g. {\"farbe\":[\"Rot\",\"Blau\"],\"gewicht\":\"1,5\",\"bio\":true}.")] string valuesJson,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);
        System.Text.Json.Nodes.JsonNode? values;
        try { values = System.Text.Json.Nodes.JsonNode.Parse(valuesJson ?? ""); }
        catch (JsonException) { throw new McpException("valuesJson ist kein gültiges JSON."); }
        if (values is not System.Text.Json.Nodes.JsonObject obj || obj.Count == 0) throw new McpException("valuesJson muss ein nicht leeres Objekt sein.");
        var payload = new System.Text.Json.Nodes.JsonObject { ["product"] = (product ?? "").Trim(), ["values"] = obj };
        var op = await instances.EnqueueContentOpAsync(instance, "product.attributes", payload.ToJsonString(), overwrite: true, reason: "KI: Eigenschaften setzen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Eigenschaften eingereiht — wird beim nächsten Kontakt gesetzt. Ergebnis via get_content_op." };
    }

    [McpServerTool(Name = "create_form"), Description(
        "Create a NEW form (\"Formular\") on a connected site (add-only: an existing slug is skipped). " +
        "You supply the fields as a JSON array (the form-builder definition). Queued and applied on the next heartbeat (~a minute); check get_content_op(opId). Requires a key with the restore right.")]
    public static async Task<object> CreateForm(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The form name shown in the admin, e.g. \"Kontakt\".")] string name,
        [Description("A JSON array of field definitions, e.g. [{\"type\":\"text\",\"label\":\"Name\",\"required\":true},{\"type\":\"email\",\"label\":\"E-Mail\"}].")] string fieldsJson,
        [Description("URL-safe slug used to reference the form from a form block. Falls back to a slug of the name if empty.")] string? slug = null,
        [Description("Optional confirmation message shown after a successful submission.")] string? successMessage = null,
        [Description("Optional submit button label (default \"Absenden\").")] string? submitLabel = null,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);
        JsonElement fields;
        try { fields = JsonSerializer.Deserialize<JsonElement>(fieldsJson); }
        catch (JsonException) { throw new McpException("fieldsJson ist kein gültiges JSON. Erwartet: ein Array von Feld-Definitionen."); }
        if (fields.ValueKind != JsonValueKind.Array)
            throw new McpException("fieldsJson muss ein JSON-Array sein.");

        var payload = JsonSerializer.Serialize(new
        {
            name = (name ?? "").Trim(),
            slug = (slug ?? "").Trim(),
            fields,
            successMessage,
            submitLabel,
        });
        var op = await instances.EnqueueContentOpAsync(instance, "form.create", payload,
            overwrite: false, reason: "KI: Formular anlegen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Formular eingereiht — wird beim nächsten Kontakt angelegt. Ergebnis via get_content_op." };
    }

    [McpServerTool(Name = "set_setting"), Description(
        "Set ONE site setting (key/value) on a connected site — e.g. \"site.title\", \"seo.description\". " +
        "The instance refuses cloud-link keys (cloud.*) for safety. Queued and applied on the next heartbeat (~a minute); check get_content_op(opId). Requires a key with the restore right.")]
    public static async Task<object> SetSetting(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The setting key, e.g. \"site.title\". Cloud-link keys (cloud.*) are rejected.")] string key,
        [Description("The value to set (empty string clears it).")] string value,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);
        if (string.IsNullOrWhiteSpace(key)) throw new McpException("key ist erforderlich.");
        var payload = JsonSerializer.Serialize(new { key = key.Trim(), value = value ?? "" });
        var op = await instances.EnqueueContentOpAsync(instance, "setting.set", payload,
            overwrite: true, reason: "KI: Einstellung setzen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Einstellung eingereiht — wird beim nächsten Kontakt gesetzt. Ergebnis via get_content_op." };
    }

    [McpServerTool(Name = "get_content_op"), Description(
        "Get the status/outcome of a content operation (e.g. from create_page) by its opId: pending, or done with outcome (applied / skipped-exists / failed) and a detail message.")]
    public static async Task<object> GetContentOp(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The opId returned by the write tool (e.g. create_page).")] int opId,
        CancellationToken ct)
    {
        var key = me.Key;
        var instance = await instances.FindForKeyAsync(instanceId, key, ct: ct);
        if (instance is null)
            throw new McpException("Instanz nicht gefunden.");

        var op = await instances.FindContentOpAsync(instance.Id, opId, ct);
        if (op is null) throw new McpException("Vorgang nicht gefunden.");
        return new
        {
            opId = op.Id,
            kind = op.Kind,
            done = op.DoneAt != null,
            outcome = op.Outcome,
            detail = op.Detail,
            // For a read op (list_pages/get_page): the content the instance serialized back, as raw JSON.
            result = op.ResultJson is null ? null : (object?)JsonSerializer.Deserialize<JsonElement>(op.ResultJson),
            createdAt = op.CreatedAt,
            doneAt = op.DoneAt,
        };
    }

    // Read scope only — reading a site's content is not a write, so it does NOT require the restore right.
    private static async Task<Instance> ResolveReadableAsync(McpContext me, InstanceService instances, string instanceId, CancellationToken ct)
    {
        var instance = await instances.FindForKeyAsync(instanceId, me.Key, ct: ct);
        if (instance is null)
            throw new McpException("Instanz nicht gefunden.");
        if (instance.Status != InstanceStatus.Approved)
            throw new McpException("Instanz ist nicht freigegeben.");
        return instance;
    }

    [McpServerTool(Name = "list_pages"), Description(
        "Ask a connected site for its list of pages (slug, title, locale, published). Because the cloud cannot reach into a site, this is QUEUED: it returns an opId; the site answers on its next heartbeat (~a minute). Poll get_content_op(opId) — its `result` holds the page list once done.")]
    public static async Task<object> ListPages(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        CancellationToken ct)
    {
        var instance = await ResolveReadableAsync(me, instances, instanceId, ct);
        var op = await instances.EnqueueContentOpAsync(instance, "pages.list", "{}", overwrite: false, reason: "KI: Seiten lesen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Angefragt — Ergebnis via get_content_op(opId)." };
    }

    [McpServerTool(Name = "get_page"), Description(
        "Ask a connected site for one page's blocks (to inspect before editing). QUEUED like list_pages: returns an opId; poll get_content_op(opId) whose `result` holds { title, slug, blocks:[{type,data}] } once the site has answered (~a minute).")]
    public static async Task<object> GetPage(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The page slug to read, e.g. \"ueber-uns\".")] string slug,
        CancellationToken ct)
    {
        var instance = await ResolveReadableAsync(me, instances, instanceId, ct);
        var payload = JsonSerializer.Serialize(new { slug = (slug ?? "").Trim() });
        var op = await instances.EnqueueContentOpAsync(instance, "page.read", payload, overwrite: false, reason: "KI: Seite lesen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Angefragt — Ergebnis via get_content_op(opId)." };
    }

    [McpServerTool(Name = "update_page_blocks"), Description(
        "Replace the blocks of an EXISTING page on a connected site (overwrite). Read it first with get_page, edit the blocks, then send the full new block list here. The site re-validates the blocks (unknown types/fields dropped) and replaces the page's content. QUEUED and applied on the next heartbeat; check get_content_op(opId). Requires a key with the restore right (this overwrites live content).")]
    public static async Task<object> UpdatePageBlocks(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The slug of the existing page to update.")] string slug,
        [Description("The FULL new JSON array of blocks (replaces all current blocks): [{\"type\":…,\"data\":{…}}, …].")] string blocksJson,
        CancellationToken ct)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);

        JsonElement blocks;
        try { blocks = JsonSerializer.Deserialize<JsonElement>(blocksJson); }
        catch (JsonException) { throw new McpException("blocksJson ist kein gültiges JSON-Array."); }
        if (blocks.ValueKind != JsonValueKind.Array)
            throw new McpException("blocksJson muss ein JSON-Array sein.");

        var payload = JsonSerializer.Serialize(new { slug = (slug ?? "").Trim(), blocks });
        var op = await instances.EnqueueContentOpAsync(instance, "page.updateBlocks", payload,
            overwrite: true, reason: "KI: Seitenblöcke ändern", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Änderung eingereiht — wird beim nächsten Kontakt angewendet. Ergebnis via get_content_op." };
    }

    [McpServerTool(Name = "analyze_cleanup"), Description(
        "Ask a connected site what media (images), components and plugins are UNUSED — the site's own \"Aufräumen\" analysis, computed with its full content (conservative: a file counts as used if its name appears anywhere). QUEUED like a read: returns an opId; poll get_content_op(opId) whose `result` holds { unusedMedia:[{id,url,fileName,sizeBytes}], unusedComponents:[{type,name}], plugins:[{key,name,enabled}], unusedMediaBytes }. Read-only — does NOT need the restore right. Use the ids/types/keys from the result with apply_cleanup.")]
    public static async Task<object> AnalyzeCleanup(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        CancellationToken ct)
    {
        var instance = await ResolveReadableAsync(me, instances, instanceId, ct);
        var op = await instances.EnqueueContentOpAsync(instance, "cleanup.analyze", "{}", overwrite: false, reason: "KI: Aufräumen analysieren", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Analyse angefragt — Ergebnis via get_content_op(opId)." };
    }

    [McpServerTool(Name = "apply_cleanup"), Description(
        "Delete the chosen UNUSED media/components/plugins on a connected site (from analyze_cleanup). Pass only what should be removed — the site re-checks each media/component is still unused before deleting (plugins are removed as given). DESTRUCTIVE and irreversible: only call after showing the exact list and getting the user's explicit go. QUEUED and applied on the next heartbeat; check get_content_op(opId). Requires a key with the restore right; if the site's profile has \"Backup vor KI-Änderung\", a backup is taken first automatically.")]
    public static async Task<object> ApplyCleanup(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("Media ids to delete (from analyze_cleanup's unusedMedia[].id). Empty to delete none.")] int[]? mediaIds = null,
        [Description("Component types to delete (from unusedComponents[].type). Empty to delete none.")] string[]? componentTypes = null,
        [Description("Plugin keys to delete (from plugins[].key). Empty to delete none. No auto \"unused\" check — pass only deliberate choices.")] string[]? pluginKeys = null,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, instances, instanceId, ct);
        var payload = JsonSerializer.Serialize(new
        {
            mediaIds = mediaIds ?? [],
            componentTypes = componentTypes ?? [],
            pluginKeys = pluginKeys ?? [],
        });
        var op = await instances.EnqueueContentOpAsync(instance, "cleanup.apply", payload,
            overwrite: true, reason: "KI: Aufräumen anwenden", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Aufräumen eingereiht — wird beim nächsten Kontakt angewendet. Ergebnis via get_content_op." };
    }
}
