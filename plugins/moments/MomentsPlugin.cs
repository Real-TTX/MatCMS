using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Moments;

/// <summary>
/// Moments — the guest area of a private event (a wedding, a birthday, a reunion): a personal access code per household instead of e-mail and password,
/// a photo gallery the guests fill from their phones (shown to the others once the hosts released it), people and keywords on photos, hearts and comments,
/// moderated in the back office. Most of the site's content can stay public; everything with the guests' faces and names lives here.
/// <para>A native plugin, written against <c>MatCMS.Plugin.Abstractions</c> only — no host type, no reflection-based JSON over its own types. It never
/// sends mail.</para>
/// </summary>
public sealed class MomentsPlugin : IPlugin
{
    public const string AccessBlock = "moments_access";

    /// <inheritdoc />
    public void Configure(IPluginBuilder b)
    {
        MomentsStore.Declare(b.Data);
        MomentsSettings.Declare(b.Settings);
        MomentsPublic.Configure(b);
        MomentsAdmin.Configure(b);
        MomentsMenu.Configure(b);

        b.Block(new BlockSpec(AccessBlock, "Gästebereich (Moments)", "Der Eingang zum Gästebereich: Gäste geben ihren Zugangscode ein; wer angemeldet ist, wird mit Namen begrüßt und sieht die neuesten Fotos.")
        {
            Category = "design",
            IconSvg = "<rect x=\"3\" y=\"5\" width=\"18\" height=\"14\" rx=\"2\"/><circle cx=\"9\" cy=\"10\" r=\"2\"/><path d=\"M21 16l-5-5-8 8\"/>",
            Fields = new[]
            {
                new FieldSpec("heading", "Überschrift") { Placeholder = "Anmelden", Help = "Über dem Feld für den Zugangscode. Leer = die Überschrift der Galerie." },
                new FieldSpec("intro", "Text", FieldKind.Textarea) { Help = "Leer = der Text aus den Einstellungen von Moments." },
                new FieldSpec("preview", "Für angemeldete Gäste", FieldKind.Select)
                {
                    Default = "6", Options = new[] { new FieldOption("6", "Begrüßung und die 6 neuesten Fotos"), new FieldOption("12", "Begrüßung und die 12 neuesten Fotos"), new FieldOption("0", "nur die Begrüßung") }
                }
            }
        }, AccessBlockAsync);

        // An account deleted elsewhere (Admin → Mitglieder): the household keeps its code and gets a new account at its next sign-in.
        b.On(HostEvents.MemberErase, async (ctx, payload) =>
        {
            var id = IdOf(payload);
            if (id <= 0) return;
            var store = new MomentsStore(ctx.Data);
            if (await store.GuestOfMemberAsync(id, default) is Guest g)
                await store.Guests.UpdateAsync(g.Id, new Dictionary<string, object?> { ["member_id"] = 0, ["account"] = "" }, default);
        });
        b.On(HostEvents.MemberExport, ExportAsync);
    }

    /// <summary>The block editor's preview: shows the code form, as a visitor sees it. False on a host older than the property.</summary>
    private static bool Preview(IPluginContext ctx)
    {
        try { return PreviewOf(ctx); } catch (MissingMethodException) { return false; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool PreviewOf(IPluginContext ctx) => ctx.IsEditorPreview;

    private static long IdOf(JsonNode? payload) => payload?["id"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : 0;

    /// <summary>The entrance on an ordinary page: the code form for a visitor, the greeting (and the newest photos) for a signed-in household.</summary>
    private static async Task<PluginResult> AccessBlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        string F(string k) => ctx.Block?.Get(k) ?? "";
        var store = new MomentsStore(ctx.Data);
        var v = Preview(ctx) ? null : await MomentsPublic.ViewerAsync(ctx, store, ct);
        var sb = new StringBuilder("<style>").Append(MomentsCss.Css).Append("</style><section class=\"section mo mo-block\"><div class=\"container\">");
        if (v is null)
            sb.Append(MomentsPublic.LoginCard(ctx, null, "", F("heading"), F("intro"), memberWithoutAccess: !Preview(ctx) && ctx.Member is not null, tag: "h2"));
        else
        {
            var name = v.Guest?.Name ?? MomentsSettings.Hosts(ctx);
            var uploads = v.Admin || ctx.Settings.GetBool(MomentsSettings.GuestUpload);
            sb.Append("<div class=\"mo-welcome\"><div><h2>Hallo ").Append(M.H(name)).Append("!</h2><p>").Append(M.Text(ctx.Settings.Get(MomentsSettings.Welcome))).Append("</p></div><div class=\"mo-welcome-actions\">")
              .Append("<a class=\"mo-btn\" href=\"").Append(MomentsPublic.Base).Append("\"><i class=\"ti ti-photo\" aria-hidden=\"true\"></i> Zu den Fotos</a>");
            if (uploads) sb.Append("<a class=\"mo-btn ghost\" href=\"").Append(MomentsPublic.Base).Append("/hochladen\"><i class=\"ti ti-camera-plus\" aria-hidden=\"true\"></i> Hochladen</a>");
            sb.Append("</div></div>");
            var count = int.TryParse(F("preview"), out var n) ? Math.Clamp(n, 0, 24) : 6;
            if (count > 0)
            {
                var latest = (await store.PhotosAsync(MomentsStore.Approved, ct)).OrderByDescending(p => p.ApprovedAt ?? p.Created).ThenByDescending(p => p.Id).Take(count).ToList();
                if (latest.Count > 0) sb.Append("<div class=\"mo-preview\">").Append(string.Concat(latest.Select(p => MomentsPublic.Tile(p, "?sort=neu", null)))).Append("</div>");
            }
            if (v.Guest is null) sb.Append("<p class=\"mo-muted small\">Ihr seht das als Gastgeber. Gäste ohne Anmeldung sehen hier das Feld für ihren Zugangscode.</p>");
        }
        return PluginResult.Fragment(sb.Append("</div></section>").ToString());
    }

    /// <summary>What the plugin holds about an account (the site's data export).</summary>
    private static async Task<JsonNode?> ExportAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var id = IdOf(payload);
        if (id <= 0) return null;
        var store = new MomentsStore(ctx.Data);
        if (await store.GuestOfMemberAsync(id, ct) is not Guest g) return null;
        var photos = new JsonArray();
        foreach (var p in await store.PhotosOfGuestAsync(g.Id, ct)) photos.Add(new JsonObject { ["file"] = p.FileName, ["caption"] = p.Caption, ["taken"] = p.Taken, ["status"] = p.Status, ["at"] = p.Created?.ToString("o") });
        var comments = new JsonArray();
        foreach (var c in (await store.CommentsAsync(ct)).Where(c => c.GuestId == g.Id)) comments.Add(new JsonObject { ["photo"] = c.PhotoId, ["text"] = c.Text, ["at"] = c.Created?.ToString("o") });
        return new JsonObject
        {
            ["plugin"] = "moments",
            ["data"] = new JsonObject
            {
                ["guest"] = new JsonObject { ["name"] = g.Name, ["people"] = g.People, ["table"] = g.Table, ["lastSeen"] = g.LastSeen?.ToString("o") },
                ["photos"] = photos, ["comments"] = comments
            }
        };
    }
}

/// <summary>
/// For a signed-in household the site's main menu gets the gallery's entry with its pages under it, and "Abmelden" as the last entry of the menu — the far
/// right of a header — where people look for it (asked per page view, with the visitor's own context — anybody else sees nothing of it). The host puts a
/// plugin's entries after the site's own, in their <see cref="MenuEntry.Order"/>.
/// </summary>
public static class MomentsMenu
{
    public static void Configure(IPluginBuilder b) => b.Menu(async (ctx, menu, ct) =>
    {
        if (menu != "header" || ctx.Member is null) return Array.Empty<MenuEntry>();
        var g = await new MomentsStore(ctx.Data).GuestOfMemberAsync(ctx.Member.Id, ct);
        if (g is null || !g.Active) return Array.Empty<MenuEntry>();
        const string B = MomentsPublic.Base;
        var children = new List<MenuEntry> { new("Alle Fotos", B) { Icon = "photo", Order = 1 } };
        if (ctx.Settings.GetBool(MomentsSettings.GuestUpload)) children.Add(new MenuEntry("Fotos hochladen", B + "/hochladen") { Icon = "camera-plus", Order = 2 });
        children.Add(new MenuEntry("Meine Fotos", B + "/meine") { Icon = "user-heart", Order = 3 });
        return new[]
        {
            new MenuEntry(MomentsSettings.Menu(ctx), B) { Icon = "photo-heart", Order = 900, Children = children },
            new MenuEntry("Abmelden", B + "/abmelden") { Icon = "logout", Order = 999 }
        };
    });
}
