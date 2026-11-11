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
        MomentsStrings.Register(b);
        MomentsStore.Declare(b.Data);
        MomentsSettings.Declare(b.Settings);
        MomentsPublic.Configure(b);
        MomentsAdmin.Configure(b);
        MomentsMenu.Configure(b);

        // name, description, field labels and option texts are keys of the string tables (the block editor looks them up in the admin's language)
        b.Block(new BlockSpec(AccessBlock, "moments.block.name", "moments.block.description")
        {
            Category = "design",
            IconSvg = "<rect x=\"3\" y=\"5\" width=\"18\" height=\"14\" rx=\"2\"/><circle cx=\"9\" cy=\"10\" r=\"2\"/><path d=\"M21 16l-5-5-8 8\"/>",
            Fields = new[]
            {
                // (a placeholder is shown as written by the editor — not a key)
                new FieldSpec("heading", "moments.block.heading") { Placeholder = "Anmelden", Help = "moments.block.headingHelp" },
                new FieldSpec("intro", "moments.block.intro", FieldKind.Textarea) { Help = "moments.block.introHelp" },
                new FieldSpec("preview", "moments.block.preview", FieldKind.Select)
                {
                    Default = "6", Options = new[] { new FieldOption("6", "moments.block.preview6"), new FieldOption("12", "moments.block.preview12"), new FieldOption("0", "moments.block.preview0") }
                }
            }
        }, AccessBlockAsync);

        // An account deleted elsewhere (Admin → Mitglieder): the household keeps its code and gets a new account at its next sign-in. Every household
        // that still held the number lets go of it (a stale one from a restore included).
        b.On(HostEvents.MemberErase, async (ctx, payload) =>
        {
            var id = IdOf(payload);
            if (id <= 0) return;
            var store = new MomentsStore(ctx.Data);
            foreach (var g in await store.GuestsWithMemberAsync(id, default))
                await store.Guests.UpdateAsync(g.Id, new Dictionary<string, object?> { ["member_id"] = 0, ["account"] = "" }, default);
        });

        // A backup carries no accounts: restored into another site, households still hold the numbers of their old accounts, and a new account there
        // may get one of them. On every start, a household whose number names no account, or an account with another address, lets go of it — its next
        // sign-in finds its own account by the address, or makes one.
        b.Lifecycle.OnLoad(ReleaseStaleAccountsAsync);
        b.On(HostEvents.MemberExport, ExportAsync);
    }

    /// <summary>The block editor's preview: shows the code form, as a visitor sees it. False on a host older than the property.</summary>
    private static bool Preview(IPluginContext ctx)
    {
        try { return PreviewOf(ctx); } catch (MissingMethodException) { return false; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool PreviewOf(IPluginContext ctx) => ctx.IsEditorPreview;

    internal static async Task ReleaseStaleAccountsAsync(IPluginContext ctx)
    {
        try
        {
            var store = new MomentsStore(ctx.Data);
            var released = 0;
            foreach (var g in (await store.GuestsAsync()).Where(g => g.MemberId > 0))
            {
                var account = await ctx.Members.FindAsync(g.MemberId);
                if (account is not null && g.Account.Length > 0 && string.Equals(account.Email, g.Account, StringComparison.OrdinalIgnoreCase)) continue;
                await store.Guests.UpdateAsync(g.Id, new Dictionary<string, object?> { ["member_id"] = 0 });
                released++;
            }
            if (released > 0) ctx.Log(released + (released == 1 ? " Haushalt hatte" : " Haushalte hatten") + " eine Kontonummer, die nicht (mehr) zu ihm gehört – gelöst.");
        }
        catch (Exception ex) { ctx.Log("Kontenabgleich beim Start nicht möglich: " + ex.Message); }
    }

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
            sb.Append("<div class=\"mo-welcome\"><div><h2>").Append(ctx.T("moments.hello", M.H(name))).Append("</h2><p>").Append(M.Text(ctx.Settings.Get(MomentsSettings.Welcome))).Append("</p></div><div class=\"mo-welcome-actions\">")
              .Append("<a class=\"mo-btn\" href=\"").Append(MomentsPublic.Base).Append("\"><i class=\"ti ti-photo\" aria-hidden=\"true\"></i> ").Append(ctx.T("moments.toPhotos")).Append("</a>");
            if (uploads) sb.Append("<a class=\"mo-btn ghost\" href=\"").Append(MomentsPublic.Base).Append("/hochladen\"><i class=\"ti ti-camera-plus\" aria-hidden=\"true\"></i> ").Append(ctx.T("moments.upload")).Append("</a>");
            sb.Append("</div></div>");
            var count = int.TryParse(F("preview"), out var n) ? Math.Clamp(n, 0, 24) : 6;
            if (count > 0)
            {
                var latest = (await store.PhotosAsync(MomentsStore.Approved, ct)).OrderByDescending(p => p.ApprovedAt ?? p.Created).ThenByDescending(p => p.Id).Take(count).ToList();
                if (latest.Count > 0) sb.Append("<div class=\"mo-preview\">").Append(string.Concat(latest.Select(p => MomentsPublic.Tile(ctx, p, "?sort=neu", null)))).Append("</div>");
            }
            if (v.Guest is null) sb.Append("<p class=\"mo-muted small\">").Append(ctx.T("moments.block.hostNote")).Append("</p>");
        }
        return PluginResult.Fragment(sb.Append("</div></section>").ToString());
    }

    /// <summary>What the plugin holds about an account (the site's data export).</summary>
    private static async Task<JsonNode?> ExportAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var id = IdOf(payload);
        if (id <= 0) return null;
        var store = new MomentsStore(ctx.Data);
        if (await ctx.Members.FindAsync(id, ct) is not MemberAccount account || await store.GuestOfMemberAsync(id, account.Email, ct) is not Guest g) return null;
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
        var g = await new MomentsStore(ctx.Data).GuestOfMemberAsync(ctx.Member.Id, ctx.Member.Email, ct);
        if (g is null || !g.Active) return Array.Empty<MenuEntry>();
        const string B = MomentsPublic.Base;
        var children = new List<MenuEntry> { new(ctx.T("moments.allPhotos"), B) { Icon = "photo", Order = 1 } };
        if (ctx.Settings.GetBool(MomentsSettings.GuestUpload)) children.Add(new MenuEntry(ctx.T("moments.uploadPhotos"), B + "/hochladen") { Icon = "camera-plus", Order = 2 });
        children.Add(new MenuEntry(ctx.T("moments.myPhotos"), B + "/meine") { Icon = "user-heart", Order = 3 });
        return new[]
        {
            new MenuEntry(MomentsSettings.Menu(ctx), B) { Icon = "photo-heart", Order = 900, Children = children },
            new MenuEntry(ctx.T("moments.signOut"), B + "/abmelden") { Icon = "logout", Order = 999 }
        };
    });
}
