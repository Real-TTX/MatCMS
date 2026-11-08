using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Moments;

/// <summary>Who looks at the guest area: a household (signed in with its code), the hosts as back-office administrator, or both at once.</summary>
public sealed record Viewer(Guest? Guest, bool Admin)
{
    /// <summary>The author id of what this viewer writes: the household, or 0 for the hosts.</summary>
    public long Id => Guest?.Id ?? 0;
    public bool Owns(Photo p) => Admin || (Guest is not null && p.GuestId == Guest.Id);
    public bool CanSee(Photo p) => p.Approved || Owns(p);
}

/// <summary>
/// The guest area under /moments. A visitor who is not signed in gets the code form (or comes in through the personal link /moments/z/{code} on the
/// card); the code signs the household's own site account in — made on the first visit, with an address that receives nothing, since the plugin sends
/// no mail at all. Everything a guest typed is encoded on output; every photo is served only after the visitor was checked.
/// </summary>
public static class MomentsPublic
{
    public const string Base = "/moments";
    /// <summary>Tiles per part of the stream: the first part comes with the page, the next ones while scrolling.</summary>
    public const int PageSize = 48;

    public static void Configure(IPluginBuilder b)
    {
        // The host counts POSTs per plugin and address over every route together; at the party a whole family uploads over the venue's one address.
        var form = new PublicRouteOptions { Antiforgery = true, DefaultLanguageOnly = true, PostsPerMinute = 600 };
        var read = new PublicRouteOptions { Methods = PluginMethod.Get, DefaultLanguageOnly = true };
        // the code form also sits in a block on an ordinary page, which has no token to give: no antiforgery, guessing is limited by Throttle instead
        var code = new PublicRouteOptions { Methods = PluginMethod.Post, DefaultLanguageOnly = true, PostsPerMinute = 600 };
        b.Route(Base, GalleryAsync, read);
        b.Route(Base + "/anmelden", CodeAsync, code);
        b.Route(Base + "/z/{code}", LinkAsync, read);
        b.Route(Base + "/abmelden", SignOutAsync, read);
        b.Route(Base + "/foto/{id}", PhotoAsync, form);
        b.Route(Base + "/hochladen", UploadAsync, form);
        b.Route(Base + "/meine", MineAsync, read);
        b.Route(Base + "/bild/{id}/{size}", ImageAsync, read);
        b.Route(Base + "/download/{id}", DownloadAsync, read);
        b.Route(Base + "/karten", MomentsCards.PageAsync, read);
    }

    // ---- access -------------------------------------------------------------------------------------------------

    public static async Task<Viewer?> ViewerAsync(IPluginContext ctx, MomentsStore store, CancellationToken ct)
    {
        var admin = ctx.User?.IsAdmin == true;
        if (ctx.Member is not null && await store.GuestOfMemberAsync(ctx.Member.Id, ct) is Guest g && g.Active) return new Viewer(g, admin);
        return admin ? new Viewer(null, true) : null;
    }

    /// <summary>Where to go after signing in: only a page of the guest area (a link to a photo somebody shared), never anywhere else.</summary>
    internal static string SafeBack(string? s)
    {
        var v = (s ?? "").Trim();
        return v.Length is > 0 and < 300 && (v == Base || v.StartsWith(Base + "/", StringComparison.Ordinal) || v.StartsWith(Base + "?", StringComparison.Ordinal))
            && !v.Contains("//", StringComparison.Ordinal) && !v.Contains('\\') && !v.StartsWith(Base + "/z/", StringComparison.Ordinal) ? v : "";
    }

    /// <summary>The sign-in form's target: a code, or user name and password — whichever the hosts allow (<see cref="MomentsSettings.LoginMode"/>).</summary>
    private static async Task<PluginResult> CodeAsync(IPluginContext ctx, CancellationToken ct)
    {
        var back = SafeBack(ctx.Request.F("zurueck"));
        var withPassword = ctx.Request.F("benutzer").Length > 0 || ctx.Request.F("passwort").Length > 0;
        var typedLogin = M.Line(ctx.Request.F("benutzer"), 40);
        if (Throttle.Blocked(ctx.Request.ClientIp))
            return LoginPage(ctx, "Zu viele falsche Versuche. Bitte wartet ein paar Minuten und versucht es dann noch einmal.", back, passwordOpen: withPassword, login: typedLogin);
        var store = new MomentsStore(ctx.Data);
        if (withPassword && MomentsSettings.PasswordLogin(ctx))
        {
            var password = ctx.Request.F("passwort");
            var g = await store.GuestByLoginAsync(Passwords.CleanLogin(typedLogin) ?? "", ct);
            var ok = false;
            if (g is { HasPassword: true }) ok = Passwords.Verify(password, g.PasswordHash);
            else Passwords.Waste(password); // a name nobody has costs the same time as a wrong password
            if (!ok || g is null || !g.Active)
            {
                Throttle.Miss(ctx.Request.ClientIp);
                return LoginPage(ctx, "Benutzername oder Passwort stimmt nicht. Bitte prüft beides noch einmal – oder fragt uns.", back, passwordOpen: true, login: typedLogin);
            }
            return await SignInAsync(ctx, store, g, back, ct);
        }
        if (!MomentsSettings.CodeLogin(ctx)) return LoginPage(ctx, "Bitte meldet euch mit eurem Benutzernamen und Passwort an.", back);
        var code = Codes.Normalize(ctx.Request.F("code"));
        var guest = await store.GuestByCodeAsync(code, ct);
        if (guest is null || !guest.Active)
        {
            Throttle.Miss(ctx.Request.ClientIp);
            return LoginPage(ctx, "Diesen Code kennen wir leider nicht. Bitte prüft ihn noch einmal – er steht auf eurer Karte.", back);
        }
        return await SignInAsync(ctx, store, guest, back, ct);
    }

    /// <summary>The personal link of the card (and its QR code): signs the household in and opens the gallery.</summary>
    private static async Task<PluginResult> LinkAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (Throttle.Blocked(ctx.Request.ClientIp)) return LoginPage(ctx, "Zu viele falsche Versuche. Bitte wartet ein paar Minuten.", "");
        // the hosts switched to user name + password: an old card's link is no way in any more
        if (!MomentsSettings.CodeLogin(ctx)) return LoginPage(ctx, "Bitte meldet euch mit eurem Benutzernamen und Passwort an.", "");
        var store = new MomentsStore(ctx.Data);
        var guest = await store.GuestByCodeAsync(Codes.Normalize(ctx.Request.R("code")), ct);
        if (guest is null || !guest.Active)
        {
            Throttle.Miss(ctx.Request.ClientIp);
            return LoginPage(ctx, "Dieser Link gilt nicht (mehr). Meldet euch bitte hier an oder fragt uns nach einem neuen Zugang.", "");
        }
        if (ctx.Member is not null && ctx.Member.Id == guest.MemberId) return PluginResult.Redirect(Base);
        return await SignInAsync(ctx, store, guest, "", ct);
    }

    private static async Task<PluginResult> SignInAsync(IPluginContext ctx, MomentsStore store, Guest g, string back, CancellationToken ct)
    {
        var account = g.MemberId > 0 ? await ctx.Members.FindAsync(g.MemberId, ct) : null;
        if (account is not null && !account.IsActive) return LoginPage(ctx, "Dieser Zugang ist gesperrt. Bitte sprecht uns an.", back);
        if (account is null)
        {
            // The household's own account. The address only has to be unique and valid in form: ".invalid" is reserved and can never receive mail,
            // and InviteAsync sends none — the code is the only way in.
            var email = g.Account.Length > 0 ? g.Account : "gast-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + "@moments.invalid";
            var invited = await ctx.Members.InviteAsync(email, g.Name, Base, ct);
            account = invited.Account ?? await ctx.Members.FindByEmailAsync(email, ct);
            if (account is null)
            {
                ctx.Log("Konto für Gast " + g.Id + " konnte nicht angelegt werden: " + invited.Status);
                return LoginPage(ctx, "Die Anmeldung hat gerade nicht geklappt. Bitte versucht es gleich noch einmal.", back);
            }
            await store.Guests.UpdateAsync(g.Id, new Dictionary<string, object?> { ["member_id"] = account.Id, ["account"] = email }, ct);
        }
        try { await ctx.Members.SignInAsync(account, persistent: true, ct); }
        catch (InvalidOperationException ex)
        {
            ctx.Log("Anmeldung von Gast " + g.Id + " abgelehnt: " + ex.Message);
            return LoginPage(ctx, "Die Anmeldung mit dem Code geht für diesen Zugang nicht. Bitte sprecht uns an.", back);
        }
        await store.Guests.UpdateAsync(g.Id, new Dictionary<string, object?> { ["last_seen"] = DateTime.UtcNow }, ct);
        await store.Guests.IncrementAsync(g.Id, "visits", 1, ct: ct);
        return PluginResult.Redirect(back.Length > 0 ? back : Base + "?willkommen=1");
    }

    private static async Task<PluginResult> SignOutAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Member is not null) await ctx.Members.SignOutAsync(ct);
        return PluginResult.Redirect("/", "Ihr seid abgemeldet.");
    }

    // ---- frame --------------------------------------------------------------------------------------------------

    internal static PluginResult Frame(IPluginContext ctx, string body, string title) =>
        PluginResult.Page("<style>" + MomentsCss.Css + "</style><section class=\"section mo\"><div class=\"container\">" + body + "</div></section>", title);

    /// <summary>The page head: the event above, the page's title, who is signed in (a link to the own photos).</summary>
    private static string Head(IPluginContext ctx, Viewer v, string title, string? crumb = null)
    {
        var name = v.Guest?.Name ?? MomentsSettings.Hosts(ctx);
        var kicker = crumb is null ? M.H(MomentsSettings.Event(ctx)) : "<a href=\"" + Base + "\">" + M.H(MomentsSettings.GalleryTitle(ctx)) + "</a>";
        return "<header class=\"mo-head\"><div><p class=\"mo-kicker\">" + kicker + "</p><h1>" + M.H(title) + "</h1></div>" +
               "<a class=\"mo-me\" href=\"" + Base + "/meine\" title=\"Meine Fotos\"><span class=\"mo-avatar\">" + M.H(M.Initials(name)) + "</span><span class=\"mo-me-name\">" +
               M.H(name) + "</span></a></header>";
    }

    /// <summary>
    /// The sign-in card: the code field (with how the card's QR code works), user name and password, or the code with the other way folded under it —
    /// as the hosts set it. <paramref name="passwordOpen"/> unfolds the second way after a failed attempt with it.
    /// </summary>
    public static string LoginCard(IPluginContext ctx, string? error, string back, string? heading = null, string? intro = null, bool memberWithoutAccess = false, string tag = "h1",
        bool passwordOpen = false, string login = "")
    {
        var mode = MomentsSettings.Mode(ctx);
        // on a page of its own the card's heading is the page's h1; inside a block on an ordinary page the page has one already (h2)
        var sb = new StringBuilder("<div class=\"mo-login\"><p class=\"mo-kicker\">").Append(M.H(MomentsSettings.Event(ctx))).Append("</p><").Append(tag).Append(" class=\"mo-login-title\">")
            .Append(M.H(heading is { Length: > 0 } ? heading : MomentsSettings.GalleryTitle(ctx))).Append("</").Append(tag).Append("><p class=\"mo-login-intro\">")
            .Append(M.Text(intro is { Length: > 0 } ? intro : ctx.Settings.Get(MomentsSettings.LoginIntro))).Append("</p>");
        if (error is { Length: > 0 }) sb.Append("<p class=\"mo-alert bad\" role=\"alert\">").Append(M.H(error)).Append("</p>");
        if (memberWithoutAccess) sb.Append("<p class=\"mo-alert\">Mit diesem Konto gibt es keinen Zugang zum Gästebereich. Meldet euch mit eurem Zugang von der Karte an.</p>");
        var hidden = back.Length > 0 ? "<input type=\"hidden\" name=\"zurueck\" value=\"" + M.H(back) + "\">" : "";
        if (mode != MomentsSettings.ModePassword)
        {
            var (length, _) = MomentsSettings.CodeFormat(ctx);
            sb.Append("<form method=\"post\" action=\"").Append(Base).Append("/anmelden\" class=\"mo-code\">").Append(hidden)
              .Append("<label for=\"mo-code\">Euer Zugangscode</label><input id=\"mo-code\" name=\"code\" required maxlength=\"40\" autocomplete=\"one-time-code\" autocapitalize=\"characters\" ")
              .Append("autocorrect=\"off\" spellcheck=\"false\" placeholder=\"").Append(Codes.Shown(new string('X', length))).Append("\" class=\"mo-code-input\">")
              .Append("<button class=\"mo-btn\" type=\"submit\"><i class=\"ti ti-door-enter\" aria-hidden=\"true\"></i> Anmelden</button></form>")
              // the card's QR code is read by the phone's own camera, not by this page — say so, or people look for a scan button here
              .Append("<p class=\"mo-qr-hint\"><i class=\"ti ti-qrcode\" aria-hidden=\"true\"></i><span><strong>Noch einfacher mit dem QR-Code:</strong> ")
              .Append("Öffnet die Kamera eures Handys, haltet sie auf den QR-Code auf eurer Karte und tippt auf den Link, der erscheint – dann seid ihr sofort angemeldet.</span></p>");
        }
        if (mode == MomentsSettings.ModeBoth) sb.Append("<details class=\"mo-alt\"").Append(passwordOpen ? " open" : "").Append("><summary>Mit Benutzername und Passwort anmelden</summary>");
        if (mode != MomentsSettings.ModeCode)
            sb.Append("<form method=\"post\" action=\"").Append(Base).Append("/anmelden\" class=\"mo-code mo-pass\">").Append(hidden)
              .Append("<label for=\"mo-user\">Benutzername</label><input id=\"mo-user\" name=\"benutzer\" required maxlength=\"40\" autocomplete=\"username\" autocapitalize=\"none\" autocorrect=\"off\" spellcheck=\"false\" class=\"mo-text-input\" value=\"")
              .Append(M.H(login)).Append("\"><label for=\"mo-pass\">Passwort</label><input id=\"mo-pass\" name=\"passwort\" type=\"password\" required maxlength=\"128\" autocomplete=\"current-password\" class=\"mo-text-input\">")
              .Append("<button class=\"mo-btn\" type=\"submit\"><i class=\"ti ti-door-enter\" aria-hidden=\"true\"></i> Anmelden</button></form>");
        if (mode == MomentsSettings.ModeBoth) sb.Append("</details>");
        return sb.Append("</div>").ToString();
    }

    private static PluginResult LoginPage(IPluginContext ctx, string? error, string back, bool memberWithoutAccess = false, bool passwordOpen = false, string login = "") =>
        Frame(ctx, LoginCard(ctx, error, back, memberWithoutAccess: memberWithoutAccess, passwordOpen: passwordOpen, login: login), MomentsSettings.GalleryTitle(ctx));

    /// <summary>Not signed in: to the code form, and back here afterwards.</summary>
    private static PluginResult ToLogin(IPluginContext ctx)
    {
        var here = ctx.Request.Path + (ctx.Request.Query.Count > 0 ? "?" + string.Join("&", ctx.Request.Query.Select(kv => M.U(kv.Key) + "=" + M.U(kv.Value))) : "");
        var back = SafeBack(here);
        return PluginResult.Redirect(Base + (back.Length > 0 && back != Base ? "?zurueck=" + M.U(back) : ""));
    }

    private static string Notice(IPluginContext ctx) => ctx.Request.Q("ok") switch
    {
        "herz" => "",
        "kommentar" => Ok("Danke für deinen Kommentar!"),
        "kommentar-pruefung" => Ok("Danke! Dein Kommentar erscheint, sobald wir ihn freigegeben haben."),
        "markiert" => Ok("Markiert."),
        "entfernt" => Ok("Entfernt."),
        "kommentar-weg" => Ok("Der Kommentar ist gelöscht."),
        "text" => Ok("Die Bildunterschrift ist gespeichert."),
        "geloescht" => Ok("Das Foto ist gelöscht."),
        "freigegeben" => Ok("Freigegeben – jetzt sehen es alle."),
        "ausgeblendet" => Ok("Ausgeblendet – nur noch ihr seht es."),
        "highlight" => Ok("Gespeichert."),
        _ => ""
    };

    private static string Ok(string text) => "<p class=\"mo-alert ok\" role=\"status\">" + M.H(text) + "</p>";
    private static string Err(string text) => "<p class=\"mo-alert bad\" role=\"alert\">" + M.H(text) + "</p>";

    internal static string AuthorName(IPluginContext ctx, long guestId, IReadOnlyDictionary<long, Guest> guests) =>
        guestId == 0 ? MomentsSettings.Hosts(ctx) : guests.TryGetValue(guestId, out var g) ? g.Name : "ein Gast";

    // ---- the gallery --------------------------------------------------------------------------------------------

    /// <summary>
    /// The gallery's filter, read from the address and written back into every link (so "next photo" stays inside it). <see cref="Album"/> opens one
    /// album, <see cref="All"/> the stream of all photos — the start page shows the albums instead, once there are any.
    /// </summary>
    public sealed record Filter(long Tag, string From, string Sort, bool Hearts, int Page, long Album = 0, bool All = false)
    {
        public static Filter Of(RequestData r) => new(
            long.TryParse(r.Q("tag"), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : 0,
            r.Q("von") is var v && (v == "gastgeber" || long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out _)) ? v : "",
            r.Q("sort") is "neu" or "beliebt" ? r.Q("sort") : "zeit",
            r.Q("herzen") == "1",
            int.TryParse(r.Q("seite"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 1 ? Math.Min(p, 10_000) : 1,
            long.TryParse(r.Q("album"), NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : 0,
            r.Q("alle") == "1");

        /// <summary>Something narrows the photos (an album counts: it is a part of them).</summary>
        public bool Narrowed => Tag > 0 || From.Length > 0 || Hearts || Album > 0;
        /// <summary>The start page: nothing chosen yet.</summary>
        public bool Start => !Narrowed && !All && Page == 1;

        /// <summary>The query of this filter ("" or "?tag=3&amp;sort=neu"), without the page unless <paramref name="withPage"/>.</summary>
        public string Query(bool withPage = false, long? tag = null, string? from = null, string? sort = null, bool? hearts = null, int? page = null, long? album = null, bool? all = null)
        {
            var parts = new List<string>();
            var tg = tag ?? Tag; var fr = from ?? From; var so = sort ?? Sort; var he = hearts ?? Hearts; var pg = page ?? (withPage ? Page : 1);
            var al = album ?? Album; var everything = all ?? All;
            if (al > 0) parts.Add("album=" + M.N(al));
            else if (everything) parts.Add("alle=1");
            if (tg > 0) parts.Add("tag=" + M.N(tg));
            if (fr.Length > 0) parts.Add("von=" + M.U(fr));
            if (so != "zeit") parts.Add("sort=" + so);
            if (he) parts.Add("herzen=1");
            if (pg > 1) parts.Add("seite=" + pg.ToString(CultureInfo.InvariantCulture));
            return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
        }
    }

    /// <summary>Everything the gallery and the photo page share: the released photos in the filter's order, and what hangs on them.</summary>
    internal sealed record Listing(List<Photo> Photos, IReadOnlyList<Tag> Tags, ILookup<long, PhotoTag> MarksByPhoto, Dictionary<long, List<long>> Hearts,
        Dictionary<long, int> Comments, Dictionary<long, Guest> Guests, int Total = 0);

    internal static async Task<Listing> ListingAsync(IPluginContext ctx, MomentsStore store, Viewer v, Filter f, CancellationToken ct)
    {
        var photos = (await store.PhotosAsync(MomentsStore.Approved, ct)).ToList();
        var tags = await store.TagsAsync(ct);
        var marks = (await store.PhotoTagsAsync(ct)).ToLookup(m => m.PhotoId);
        var hearts = await store.HeartsAsync(ct);
        var comments = (await store.CommentsAsync(ct)).Where(c => c.Visible).GroupBy(c => c.PhotoId).ToDictionary(g => g.Key, g => g.Count());
        var guests = (await store.GuestsAsync(ct)).ToDictionary(g => g.Id);
        IEnumerable<Photo> list = photos;
        if (f.Album > 0) list = list.Where(p => p.AlbumId == f.Album);
        if (f.Tag > 0) list = list.Where(p => marks[p.Id].Any(m => m.TagId == f.Tag));
        if (f.From == "gastgeber") list = list.Where(p => p.GuestId == 0);
        else if (long.TryParse(f.From, out var from)) list = list.Where(p => p.GuestId == from);
        if (f.Hearts) list = list.Where(p => hearts.TryGetValue(p.Id, out var h) && h.Contains(v.Id));
        list = f.Sort switch
        {
            "neu" => list.OrderByDescending(p => p.ApprovedAt ?? p.Created).ThenByDescending(p => p.Id),
            "beliebt" => list.OrderByDescending(p => hearts.TryGetValue(p.Id, out var h) ? h.Count : 0).ThenBy(p => p.Taken, StringComparer.Ordinal).ThenBy(p => p.Id),
            _ => list.OrderBy(p => p.Taken, StringComparer.Ordinal).ThenBy(p => p.Id)
        };
        return new Listing(list.ToList(), tags, marks, hearts, comments, guests, photos.Count);
    }

    internal static string Tile(Photo p, string query, Listing? listing, string? badge = null, bool large = false)
    {
        var hearts = listing is not null && listing.Hearts.TryGetValue(p.Id, out var h) ? h.Count : 0;
        var comments = listing is not null && listing.Comments.TryGetValue(p.Id, out var c) ? c : 0;
        var alt = p.Caption.Length > 0 ? p.Caption : "Foto";
        var sb = new StringBuilder("<a class=\"mo-tile").Append(large ? " large" : "").Append("\" href=\"").Append(Base).Append("/foto/").Append(p.Id).Append(M.H(query)).Append("\">")
            .Append("<img src=\"").Append(Base).Append("/bild/").Append(p.Id).Append("/t\" alt=\"").Append(M.H(alt)).Append("\" loading=\"lazy\" decoding=\"async\">");
        if (badge is not null) sb.Append("<span class=\"mo-tile-badge\">").Append(M.H(badge)).Append("</span>");
        if (hearts + comments > 0)
        {
            sb.Append("<span class=\"mo-tile-meta\">");
            if (hearts > 0) sb.Append("<span><i class=\"ti ti-heart-filled\" aria-hidden=\"true\"></i> ").Append(hearts).Append("</span>");
            if (comments > 0) sb.Append("<span><i class=\"ti ti-message-circle\" aria-hidden=\"true\"></i> ").Append(comments).Append("</span>");
            sb.Append("</span>");
        }
        return sb.Append("</a>").ToString();
    }

    private static async Task<PluginResult> GalleryAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        var part = ctx.Request.Q("teil") == "1";
        if (v is null) return part ? PluginResult.Json(new JsonObject { ["html"] = "", ["next"] = "" }, 403) : LoginPage(ctx, null, SafeBack(ctx.Request.Q("zurueck")), memberWithoutAccess: ctx.Member is not null);
        var f = Filter.Of(ctx.Request);
        var listing = await ListingAsync(ctx, store, v, f, ct);
        // the script asks for the next part of the stream while the visitor scrolls
        if (part)
        {
            var (html, next) = Stream(f, listing, continued: true);
            return PluginResult.Json(new JsonObject { ["html"] = html, ["next"] = next });
        }
        var albums = await store.AlbumsAsync(ct);
        var album = f.Album > 0 ? albums.FirstOrDefault(a => a.Id == f.Album) : null;
        if (f.Album > 0 && album is null) return PluginResult.Redirect(Base);
        var title = MomentsSettings.GalleryTitle(ctx);
        var heading = album?.Name ?? (f.All ? "Alle Fotos" : title);
        var sb = new StringBuilder(Head(ctx, v, heading, album is not null || f.All ? "" : null)).Append(Notice(ctx));
        var uploads = v.Admin || ctx.Settings.GetBool(MomentsSettings.GuestUpload);

        if (f.Start)
        {
            var name = v.Guest?.Name ?? MomentsSettings.Hosts(ctx);
            sb.Append("<section class=\"mo-welcome\"><div><h2>").Append(ctx.Request.Q("willkommen") == "1" ? "Willkommen, " : "Hallo ").Append(M.H(name)).Append("!</h2><p>")
              .Append(M.Text(ctx.Settings.Get(MomentsSettings.Welcome))).Append("</p>");
            if (v.Guest is { Table.Length: > 0 } g && ctx.Settings.GetBool(MomentsSettings.ShowTable))
                sb.Append("<p class=\"mo-table\"><i class=\"ti ti-armchair\" aria-hidden=\"true\"></i> Euer Tisch: <strong>").Append(M.H(g.Table)).Append("</strong></p>");
            sb.Append("</div><div class=\"mo-welcome-actions\">");
            if (uploads) sb.Append("<a class=\"mo-btn\" href=\"").Append(Base).Append("/hochladen\"><i class=\"ti ti-camera-plus\" aria-hidden=\"true\"></i> Fotos hochladen</a>");
            sb.Append("<a class=\"mo-btn ghost\" href=\"").Append(Base).Append("/meine\"><i class=\"ti ti-user-heart\" aria-hidden=\"true\"></i> Meine Fotos</a></div></section>");
            if (v.Admin && await store.PendingCountAsync(ct) is var pending and > 0)
                sb.Append("<p class=\"mo-alert\"><i class=\"ti ti-hourglass\" aria-hidden=\"true\"></i> ").Append(M.Plural(pending, "Foto wartet", "Fotos warten"))
                  .Append(" auf eure Freigabe. <a href=\"/admin/p/moments/review\">Jetzt ansehen</a></p>");
            var featured = listing.Photos.Where(p => p.Featured).ToList();
            if (featured.Count > 0)
                sb.Append("<section class=\"mo-highlights\"><h2><i class=\"ti ti-sparkles\" aria-hidden=\"true\"></i> Highlights</h2><div class=\"mo-strip\">")
                  .Append(string.Concat(featured.Take(12).Select(p => Tile(p, "", listing, large: true)))).Append("</div></section>");
            // with albums, the start page is an overview — the albums in the hosts' order, then who is on the photos and the keywords
            if (albums.Count > 0 && listing.Total > 0 || albums.Count > 0 && v.Admin)
                return Frame(ctx, sb.Append(Overview(ctx, v, listing, albums, uploads)).ToString(), title);
        }

        // the bar: how the photos are ordered, how many there are, what narrows them — only once there is something to order (an empty gallery with
        // sort buttons, "0 Fotos" and a lone "Meine Herzen" looked broken)
        if (listing.Total > 0)
        {
            sb.Append("<nav class=\"mo-bar\" aria-label=\"Sortierung\"><div class=\"mo-seg\">");
            foreach (var (key, label) in new[] { ("zeit", "Chronologisch"), ("neu", "Neueste"), ("beliebt", "Beliebteste") })
                sb.Append("<a href=\"").Append(Base).Append(M.H(f.Query(sort: key))).Append("\"").Append(f.Sort == key ? " class=\"on\" aria-current=\"true\"" : "").Append(">").Append(label).Append("</a>");
            sb.Append("</div><span class=\"mo-count\">").Append(M.Plural(listing.Photos.Count, "Foto", "Fotos")).Append("</span></nav>");
            sb.Append(Chips(ctx, f, listing, v));
        }

        if (listing.Photos.Count == 0)
        {
            sb.Append("<div class=\"mo-empty\"><i class=\"ti ti-photo\" aria-hidden=\"true\"></i><p>")
              .Append(album is not null ? "In diesem Album ist noch kein Foto." : f.Narrowed ? "Hier ist (noch) kein Foto." : "Noch sind keine Fotos da.").Append("</p>");
            if (uploads && (album is null && !f.Narrowed || album is not null && (v.Admin || album.Guests)))
                sb.Append("<a class=\"mo-btn\" href=\"").Append(Base).Append("/hochladen").Append(album is not null && v.Admin ? "?album=" + M.N(album.Id) : "")
                  .Append("\"><i class=\"ti ti-camera-plus\" aria-hidden=\"true\"></i> Die ersten hochladen</a>");
            if (f.Narrowed || f.All) sb.Append("<a class=\"mo-btn ghost\" href=\"").Append(Base).Append("\"><i class=\"ti ti-photo\" aria-hidden=\"true\"></i> Zur Übersicht</a>");
            sb.Append("</div>");
            return Frame(ctx, sb.ToString(), heading);
        }

        // the stream: the first part now, the rest while scrolling (the script asks for ?…&teil=1); without a script the pages stay as links
        var (first, nextUrl) = Stream(f, listing, continued: false);
        sb.Append("<div class=\"mo-grid\" data-mo-grid>").Append(first).Append("</div>");
        var pages = (listing.Photos.Count + PageSize - 1) / PageSize;
        var page = Math.Clamp(f.Page, 1, Math.Max(1, pages));
        if (nextUrl.Length > 0)
            sb.Append("<div class=\"mo-more\" data-mo-more=\"").Append(M.H(nextUrl)).Append("\" aria-live=\"polite\"><span class=\"mo-spin\" aria-hidden=\"true\"></span> Weitere Fotos werden geladen …</div>");
        if (pages > 1)
        {
            sb.Append("<nav class=\"mo-pager\" aria-label=\"Seiten\">");
            if (page > 1) sb.Append("<a class=\"mo-btn ghost\" href=\"").Append(Base).Append(M.H(f.Query(page: page - 1))).Append("\"><i class=\"ti ti-chevron-left\" aria-hidden=\"true\"></i> Zurück</a>");
            sb.Append("<span>Seite ").Append(page).Append(" von ").Append(pages).Append("</span>");
            if (page < pages) sb.Append("<a class=\"mo-btn ghost\" href=\"").Append(Base).Append(M.H(f.Query(page: page + 1))).Append("\">Weiter <i class=\"ti ti-chevron-right\" aria-hidden=\"true\"></i></a>");
            sb.Append("</nav>");
        }
        return Frame(ctx, sb.Append(MomentsScript.Gallery).ToString(), heading);
    }

    /// <summary>
    /// One part of the photo stream (<see cref="PageSize"/> tiles) and the address of the next ("" at the end). Chronologically there is a heading for every
    /// hour, with the day as soon as the photos span more than one; a <paramref name="continued"/> part does not repeat the hour the part before ended in.
    /// </summary>
    private static (string Html, string Next) Stream(Filter f, Listing listing, bool continued)
    {
        var pages = (listing.Photos.Count + PageSize - 1) / PageSize;
        if (pages == 0) return ("", "");
        var page = Math.Clamp(f.Page, 1, pages);
        var start = (page - 1) * PageSize;
        var query = f.Query();
        var days = listing.Photos.Select(p => p.Taken.Length >= 10 ? p.Taken[..10] : "").Distinct().Count();
        var lastHour = continued && start > 0 && f.Sort == "zeit" && M.TryTaken(listing.Photos[start - 1].Taken, out var before)
            ? before.ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture) : "";
        var sb = new StringBuilder();
        foreach (var p in listing.Photos.Skip(start).Take(PageSize))
        {
            if (f.Sort == "zeit" && M.TryTaken(p.Taken, out var d))
            {
                var hour = d.ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture);
                if (hour != lastHour) { sb.Append("<h3 class=\"mo-hour\">").Append(M.H(M.Hour(d, days > 1))).Append("</h3>"); lastHour = hour; }
            }
            sb.Append(Tile(p, query, listing));
        }
        return (sb.ToString(), page < pages ? Base + f.Query(page: page + 1) + "&teil=1" : "");
    }

    /// <summary>
    /// The start page once there are albums: the albums in the hosts' order (cover, name, count) and "Alle Fotos" after them, then who is on the photos and
    /// the keywords — the structure first, the photos one click further. An album without a photo the visitor may see is left out (the hosts see it, to
    /// fill it).
    /// </summary>
    private static string Overview(IPluginContext ctx, Viewer v, Listing listing, IReadOnlyList<Album> albums, bool uploads)
    {
        var sb = new StringBuilder("<section class=\"mo-section\"><h2>Alben</h2><div class=\"mo-albums\">");
        Photo? Cover(IEnumerable<Photo> photos) => photos.OrderByDescending(p => p.Featured)
            .ThenByDescending(p => listing.Hearts.TryGetValue(p.Id, out var h) ? h.Count : 0).ThenBy(p => p.Taken, StringComparer.Ordinal).FirstOrDefault();
        void Card(string href, string name, int count, Photo? cover, string? note = null)
        {
            sb.Append("<a class=\"mo-album\" href=\"").Append(M.H(href)).Append("\"><span class=\"mo-album-cover\">");
            if (cover is not null) sb.Append("<img src=\"").Append(Base).Append("/bild/").Append(cover.Id).Append("/t\" alt=\"\" loading=\"lazy\" decoding=\"async\">");
            else sb.Append("<i class=\"ti ti-photo\" aria-hidden=\"true\"></i>");
            sb.Append("</span><span class=\"mo-album-text\"><strong>").Append(M.H(name)).Append("</strong><small>").Append(M.Plural(count, "Foto", "Fotos"))
              .Append(note is null ? "" : " · " + M.H(note)).Append("</small></span></a>");
        }
        foreach (var a in albums)
        {
            var photos = listing.Photos.Where(p => p.AlbumId == a.Id).ToList();
            if (photos.Count == 0 && !v.Admin) continue;
            Card(Base + "?album=" + M.N(a.Id), a.Name, photos.Count, Cover(photos), v.Admin && a.Guests ? "hier landen die Fotos der Gäste" : null);
        }
        if (listing.Total > 0) Card(Base + "?alle=1", "Alle Fotos", listing.Total, Cover(listing.Photos));
        sb.Append("</div></section>");

        var visible = listing.Photos.Select(p => p.Id).ToHashSet();
        var counts = listing.MarksByPhoto.Where(g => visible.Contains(g.Key)).SelectMany(g => g).GroupBy(m => m.TagId).ToDictionary(g => g.Key, g => g.Count());
        var used = listing.Tags.Where(t => counts.ContainsKey(t.Id)).OrderByDescending(t => counts[t.Id]).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        void TagRow(string heading, string icon, string cls, IEnumerable<Tag> tags)
        {
            var list = tags.ToList();
            if (list.Count == 0) return;
            sb.Append("<section class=\"mo-section\"><h2>").Append(heading).Append("</h2><div class=\"mo-chiprow\">");
            foreach (var t in list.Take(60))
                sb.Append("<a class=\"mo-chip").Append(cls).Append("\" href=\"").Append(Base).Append("?alle=1&amp;tag=").Append(t.Id).Append("\"><i class=\"ti ti-").Append(icon).Append("\" aria-hidden=\"true\"></i> ")
                  .Append(M.H(t.Name)).Append(" <small>").Append(counts[t.Id]).Append("</small></a>");
            sb.Append("</div></section>");
        }
        TagRow("Wer ist drauf?", "user", " person", used.Where(t => t.Kind == MomentsStore.Person));
        TagRow("Stichworte", "tag", "", used.Where(t => t.Kind != MomentsStore.Person));
        if (listing.Hearts.Values.Any(h => h.Contains(v.Id)))
            sb.Append("<p class=\"mo-chiprow\"><a class=\"mo-chip\" href=\"").Append(Base).Append("?alle=1&amp;herzen=1\"><i class=\"ti ti-heart\" aria-hidden=\"true\"></i> Meine Herzen</a></p>");
        if (listing.Total == 0)
            sb.Append("<div class=\"mo-empty\"><i class=\"ti ti-photo\" aria-hidden=\"true\"></i><p>Noch sind keine Fotos da.</p>")
              .Append(uploads ? "<a class=\"mo-btn\" href=\"" + Base + "/hochladen\"><i class=\"ti ti-camera-plus\" aria-hidden=\"true\"></i> Die ersten hochladen</a>" : "").Append("</div>");
        return sb.ToString();
    }

    /// <summary>What narrows the gallery: the active filter (with its ×), then people and keywords with their counts, the own hearts and who uploaded.</summary>
    private static string Chips(IPluginContext ctx, Filter f, Listing listing, Viewer v)
    {
        var sb = new StringBuilder();
        var counts = listing.MarksByPhoto.SelectMany(g => g).GroupBy(m => m.TagId).ToDictionary(g => g.Key, g => g.Count());
        if (f.Narrowed)
        {
            sb.Append("<div class=\"mo-active\">");
            if (f.Tag > 0 && listing.Tags.FirstOrDefault(t => t.Id == f.Tag) is Tag t)
                sb.Append(ActiveChip(t.Kind == MomentsStore.Person ? "user" : "tag", t.Name, f.Query(tag: 0)));
            if (f.From.Length > 0)
                sb.Append(ActiveChip("camera", "von " + (f.From == "gastgeber" ? MomentsSettings.Hosts(ctx) : long.TryParse(f.From, out var gid) && listing.Guests.TryGetValue(gid, out var gg) ? gg.Name : "einem Gast"), f.Query(from: "")));
            if (f.Hearts) sb.Append(ActiveChip("heart", "Meine Herzen", f.Query(hearts: false)));
            sb.Append("</div>");
        }
        var used = listing.Tags.Where(t => counts.ContainsKey(t.Id) && t.Id != f.Tag).OrderByDescending(t => counts[t.Id]).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var people = used.Where(t => t.Kind == MomentsStore.Person).ToList();
        var topics = used.Where(t => t.Kind != MomentsStore.Person).ToList();
        // "Meine Herzen" only for whoever gave one — the chip led to an empty page otherwise
        var heartsChip = !f.Hearts && listing.Hearts.Values.Any(h => h.Contains(v.Id));
        if (people.Count + topics.Count == 0 && !heartsChip) return sb.ToString();
        sb.Append("<div class=\"mo-filters\">");
        if (heartsChip) sb.Append("<a class=\"mo-chip\" href=\"").Append(Base).Append(M.H(f.Query(hearts: true))).Append("\"><i class=\"ti ti-heart\" aria-hidden=\"true\"></i> Meine Herzen</a>");
        foreach (var t in people.Take(40))
            sb.Append("<a class=\"mo-chip person\" href=\"").Append(Base).Append(M.H(f.Query(tag: t.Id))).Append("\"><i class=\"ti ti-user\" aria-hidden=\"true\"></i> ").Append(M.H(t.Name))
              .Append(" <small>").Append(counts[t.Id]).Append("</small></a>");
        foreach (var t in topics.Take(24))
            sb.Append("<a class=\"mo-chip\" href=\"").Append(Base).Append(M.H(f.Query(tag: t.Id))).Append("\"><i class=\"ti ti-tag\" aria-hidden=\"true\"></i> ").Append(M.H(t.Name))
              .Append(" <small>").Append(counts[t.Id]).Append("</small></a>");
        return sb.Append("</div>").ToString();
    }

    private static string ActiveChip(string icon, string label, string query) =>
        "<a class=\"mo-chip on\" href=\"" + Base + M.H(query) + "\" title=\"Filter entfernen\"><i class=\"ti ti-" + icon + "\" aria-hidden=\"true\"></i> " + M.H(label) +
        " <i class=\"ti ti-x\" aria-hidden=\"true\"></i></a>";

    // ---- one photo ----------------------------------------------------------------------------------------------

    private static async Task<PluginResult> PhotoAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        if (v is null) return ToLogin(ctx);
        if (!long.TryParse(ctx.Request.R("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || await store.PhotoAsync(id, ct) is not Photo p || !v.CanSee(p))
            return PluginResult.NotFound();
        var f = Filter.Of(ctx.Request);
        string? error = null;
        if (ctx.Request.IsPost)
        {
            // the filter travels in the form (a hidden field), so the redirect lands on the same photo inside the same gallery view
            var back = Filter.Of(new RequestData { Query = ParseQuery(ctx.Request.F("q")) });
            var (done, err) = await PostOnPhotoAsync(ctx, store, v, p, ct);
            // the heart from the page's script: answered in place, so the page does not jump to the top on a phone
            if (ctx.Request.F("ajax") == "1" && ctx.Request.Action == "heart")
            {
                var all = (await store.HeartsAsync(ct)).TryGetValue(p.Id, out var hs) ? hs : new List<long>();
                return PluginResult.Json(new JsonObject { ["ok"] = err is null, ["error"] = err, ["on"] = all.Contains(v.Id), ["count"] = all.Count });
            }
            if (err is null)
            {
                // (a redirect cannot carry a #fragment — the page's script scrolls to what the "ok" names)
                if (done == "geloescht")
                {
                    var target = v.Guest is not null && p.GuestId == v.Guest.Id ? Base + "/meine" : Base + back.Query();
                    return PluginResult.Redirect(target + (target.Contains('?') ? "&" : "?") + "ok=geloescht");
                }
                var q = back.Query();
                return PluginResult.Redirect(Base + "/foto/" + p.Id + q + (done.Length > 0 ? Sep(q) + "ok=" + done : ""));
            }
            error = err;
            f = back;
            p = await store.PhotoAsync(p.Id, ct) ?? p;
        }
        return Frame(ctx, await PhotoPageAsync(ctx, store, v, p, f, error, ct), p.Caption.Length > 0 ? p.Caption : MomentsSettings.GalleryTitle(ctx));
    }

    private static string Sep(string query) => query.Length == 0 ? "?" : "&";

    private static IReadOnlyDictionary<string, string> ParseQuery(string q)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (q ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            d[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return d;
    }

    /// <summary>One action on a photo. Returns what to say afterwards ("" = nothing) or an error.</summary>
    private static async Task<(string Done, string? Error)> PostOnPhotoAsync(IPluginContext ctx, MomentsStore store, Viewer v, Photo p, CancellationToken ct)
    {
        var r = ctx.Request;
        switch (r.Action)
        {
            case "heart":
                if (!p.Approved) return ("", "Herzen gibt es, sobald das Foto freigegeben ist.");
                await store.ToggleHeartAsync(p.Id, v.Id, ct);
                return ("herz", null);
            case "comment":
            {
                if (!p.Approved && !v.Admin) return ("", "Kommentieren geht, sobald das Foto freigegeben ist.");
                var text = M.Clip(r.F("text"), 1000);
                if (text.Length == 0) return ("", "Bitte schreib etwas.");
                var pending = ctx.Settings.GetBool(MomentsSettings.CommentApproval) && !v.Admin;
                await store.Comments.InsertAsync(new Dictionary<string, object?>
                {
                    ["photo_id"] = p.Id, ["guest_id"] = v.Id, ["text"] = text, ["status"] = pending ? MomentsStore.Pending : MomentsStore.Visible, ["created"] = DateTime.UtcNow
                }, ct);
                return (pending ? "kommentar-pruefung" : "kommentar", null);
            }
            case "uncomment":
            {
                if (!long.TryParse(r.F("comment"), out var cid) || await store.CommentAsync(cid, ct) is not Comment c || c.PhotoId != p.Id) return ("", "Diesen Kommentar gibt es nicht mehr.");
                if (!v.Admin && !(v.Guest is not null && c.GuestId == v.Guest.Id)) return ("", "Das ist nicht dein Kommentar.");
                await store.Comments.DeleteAsync(c.Id, ct);
                return ("kommentar-weg", null);
            }
            case "tag":
            {
                if (!v.Admin && !ctx.Settings.GetBool(MomentsSettings.GuestTags)) return ("", "Markieren ist ausgeschaltet.");
                var name = M.Line(r.F("tag"), 40);
                if (name.Length < 2) return ("", "Bitte gib einen Namen oder ein Stichwort ein.");
                var tag = await store.TagByNameAsync(name, ct);
                if (tag is null)
                {
                    var person = (await store.GuestsAsync(ct)).SelectMany(g => g.PeopleList).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    if (person is null && !v.Admin && !ctx.Settings.GetBool(MomentsSettings.NewTags)) return ("", "Bitte wähle eine Person oder ein Stichwort aus der Liste.");
                    tag = await store.EnsureTagAsync(person ?? name, person is null ? MomentsStore.Topic : MomentsStore.Person, ct);
                }
                if ((await store.TagsOfPhotoAsync(p.Id, ct)).Count >= 30) return ("", "Mehr Markierungen passen an ein Foto nicht.");
                await store.AddTagAsync(p.Id, tag.Id, v.Id, ct);
                return ("markiert", null);
            }
            case "untag":
            {
                if (!long.TryParse(r.F("mark"), out var mid)) return ("", null);
                var mark = (await store.TagsOfPhotoAsync(p.Id, ct)).FirstOrDefault(m => m.Id == mid);
                if (mark is null) return ("entfernt", null);
                if (!v.Owns(p) && !(v.Guest is not null && mark.By == v.Guest.Id)) return ("", "Diese Markierung kann nur entfernen, wer sie gesetzt hat.");
                await store.PhotoTags.DeleteAsync(mark.Id, ct);
                return ("entfernt", null);
            }
            case "caption":
                if (!v.Owns(p)) return ("", "Nur wer das Foto hochgeladen hat, kann die Bildunterschrift ändern.");
                await store.Photos.UpdateAsync(p.Id, new Dictionary<string, object?> { ["caption"] = M.Line(r.F("caption"), 200) }, ct);
                return ("text", null);
            case "delete":
                if (!v.Owns(p)) return ("", "Nur wer das Foto hochgeladen hat, kann es löschen.");
                await store.DeletePhotoAsync(ctx.Files, p, ct);
                return ("geloescht", null);
            case "approve" when v.Admin:
                await store.SetStatusAsync(p, MomentsStore.Approved, ct);
                return ("freigegeben", null);
            case "hide" when v.Admin:
                await store.SetStatusAsync(p, MomentsStore.Hidden, ct);
                return ("ausgeblendet", null);
            case "feature" when v.Admin:
                await store.Photos.UpdateAsync(p.Id, new Dictionary<string, object?> { ["featured"] = !p.Featured }, ct);
                return ("highlight", null);
        }
        return ("", null);
    }

    private static async Task<string> PhotoPageAsync(IPluginContext ctx, MomentsStore store, Viewer v, Photo p, Filter f, string? error, CancellationToken ct)
    {
        var listing = await ListingAsync(ctx, store, v, f, ct);
        var query = f.Query();
        var index = listing.Photos.FindIndex(x => x.Id == p.Id);
        var prev = index > 0 ? listing.Photos[index - 1] : null;
        var next = index >= 0 && index < listing.Photos.Count - 1 ? listing.Photos[index + 1] : null;
        var hearts = listing.Hearts.TryGetValue(p.Id, out var h) ? h : new List<long>();
        var mine = hearts.Contains(v.Id);
        var author = AuthorName(ctx, p.GuestId, listing.Guests);
        var form = "<input type=\"hidden\" name=\"q\" value=\"" + M.H(query) + "\">" + ctx.Antiforgery;

        var sb = new StringBuilder("<div class=\"mo-photo-page\"><p class=\"mo-back\"><a href=\"").Append(Base).Append(M.H(f.Query()))
            .Append("\"><i class=\"ti ti-chevron-left\" aria-hidden=\"true\"></i> Alle Fotos</a>");
        if (index >= 0) sb.Append("<span class=\"mo-pos\">").Append(index + 1).Append(" / ").Append(listing.Photos.Count).Append("</span>");
        sb.Append("</p>").Append(Notice(ctx));
        if (error is not null) sb.Append(Err(error));
        if (p.Pending) sb.Append("<p class=\"mo-alert\"><i class=\"ti ti-hourglass\" aria-hidden=\"true\"></i> Wartet auf Freigabe – bis dahin sehen es nur ").Append(v.Admin ? "ihr und der Gast, der es hochgeladen hat." : "du und die Gastgeber.").Append("</p>");
        if (p.Status == MomentsStore.Hidden) sb.Append("<p class=\"mo-alert\"><i class=\"ti ti-eye-off\" aria-hidden=\"true\"></i> Ausgeblendet – die Gäste sehen es nicht.</p>");

        var alt = p.Caption.Length > 0 ? p.Caption : "Foto von " + author;
        sb.Append("<figure class=\"mo-stage\" data-mo-stage><img src=\"").Append(Base).Append("/bild/").Append(p.Id).Append("/v\" alt=\"").Append(M.H(alt)).Append("\"");
        if (p.Width > 0 && p.Height > 0) sb.Append(" width=\"").Append(p.Width).Append("\" height=\"").Append(p.Height).Append("\"");
        sb.Append(">");
        if (prev is not null) sb.Append("<a class=\"mo-nav prev\" rel=\"prev\" href=\"").Append(Base).Append("/foto/").Append(prev.Id).Append(M.H(query)).Append("\" aria-label=\"Vorheriges Foto\"><i class=\"ti ti-chevron-left\"></i></a>");
        if (next is not null) sb.Append("<a class=\"mo-nav next\" rel=\"next\" href=\"").Append(Base).Append("/foto/").Append(next.Id).Append(M.H(query)).Append("\" aria-label=\"Nächstes Foto\"><i class=\"ti ti-chevron-right\"></i></a>");
        sb.Append("</figure>");

        sb.Append("<div class=\"mo-detail\"><div class=\"mo-info\" id=\"mo-info\">");
        sb.Append("<div class=\"mo-by\"><span class=\"mo-avatar\">").Append(M.H(M.Initials(author))).Append("</span><div><strong>").Append(M.H(author)).Append("</strong><small>")
          .Append(M.H(M.Taken(p.Taken))).Append("</small></div></div>");
        if (p.AlbumId > 0 && await store.AlbumAsync(p.AlbumId, ct) is Album inAlbum)
            sb.Append("<p class=\"mo-in-album\"><i class=\"ti ti-album\" aria-hidden=\"true\"></i> Album <a href=\"").Append(Base).Append("?album=").Append(inAlbum.Id).Append("\">")
              .Append(M.H(inAlbum.Name)).Append("</a></p>");
        if (p.Caption.Length > 0) sb.Append("<p class=\"mo-caption\">").Append(M.H(p.Caption)).Append("</p>");

        // heart, download, and what the uploader and the hosts may do
        sb.Append("<div class=\"mo-actions\" id=\"herz\">");
        if (p.Approved)
        {
            var who = string.Join(", ", hearts.Select(g => AuthorName(ctx, g, listing.Guests)).Distinct());
            sb.Append("<form method=\"post\" class=\"mo-inline\">").Append(form).Append("<button class=\"mo-heart").Append(mine ? " on" : "").Append("\" name=\"action\" value=\"heart\" type=\"submit\" aria-pressed=\"")
              .Append(mine ? "true" : "false").Append("\"").Append(who.Length > 0 ? " title=\"" + M.H(who) + "\"" : "").Append("><i class=\"ti ti-heart").Append(mine ? "-filled" : "").Append("\" aria-hidden=\"true\"></i> ")
              .Append(hearts.Count > 0 ? hearts.Count.ToString(CultureInfo.InvariantCulture) : "Gefällt mir").Append("</button></form>");
        }
        if (MomentsSettings.DownloadMode(ctx) != MomentsSettings.DownloadOff || v.Owns(p))
            sb.Append("<a class=\"mo-btn ghost\" href=\"").Append(Base).Append("/download/").Append(p.Id).Append("\" download><i class=\"ti ti-download\" aria-hidden=\"true\"></i> Herunterladen</a>");
        if (v.Admin)
        {
            if (!p.Approved) sb.Append(Act(form, "approve", "ti-check", "Freigeben", primary: true));
            else sb.Append(Act(form, "hide", "ti-eye-off", "Ausblenden"));
            sb.Append(Act(form, "feature", p.Featured ? "ti-star-off" : "ti-star", p.Featured ? "Kein Highlight mehr" : "Als Highlight"));
        }
        if (v.Owns(p))
            sb.Append("<details class=\"mo-confirm\"><summary class=\"mo-btn ghost danger\"><i class=\"ti ti-trash\" aria-hidden=\"true\"></i> Löschen</summary><form method=\"post\">").Append(form)
              .Append("<p>Das Foto wird für alle gelöscht.</p><button class=\"mo-btn danger\" name=\"action\" value=\"delete\" type=\"submit\">Ja, löschen</button></form></details>");
        sb.Append("</div>");
        if (v.Owns(p))
            sb.Append("<details class=\"mo-edit\"><summary><i class=\"ti ti-pencil\" aria-hidden=\"true\"></i> ").Append(p.Caption.Length > 0 ? "Bildunterschrift ändern" : "Bildunterschrift schreiben")
              .Append("</summary><form method=\"post\" class=\"mo-row\">").Append(form).Append("<input type=\"hidden\" name=\"action\" value=\"caption\"><input name=\"caption\" maxlength=\"200\" value=\"")
              .Append(M.H(p.Caption)).Append("\" placeholder=\"Was passiert hier?\" aria-label=\"Bildunterschrift\"><button class=\"mo-btn\" type=\"submit\">Speichern</button></form></details>");

        // marks: who and what is on it
        var marks = listing.MarksByPhoto[p.Id].ToList();
        var tagById = listing.Tags.ToDictionary(t => t.Id);
        var canTag = v.Admin || ctx.Settings.GetBool(MomentsSettings.GuestTags);
        sb.Append("<section class=\"mo-tags\" id=\"mo-tags\"><h2>Wer und was ist drauf?</h2><div class=\"mo-chiprow\">");
        foreach (var m in marks.Where(m => tagById.ContainsKey(m.TagId)).OrderBy(m => tagById[m.TagId].Kind == MomentsStore.Person ? 0 : 1).ThenBy(m => tagById[m.TagId].Name, StringComparer.OrdinalIgnoreCase))
        {
            var t = tagById[m.TagId];
            sb.Append("<span class=\"mo-chip").Append(t.Kind == MomentsStore.Person ? " person" : "").Append("\"><a href=\"").Append(Base).Append("?tag=").Append(t.Id).Append("\"><i class=\"ti ti-")
              .Append(t.Kind == MomentsStore.Person ? "user" : "tag").Append("\" aria-hidden=\"true\"></i> ").Append(M.H(t.Name)).Append("</a>");
            if (v.Owns(p) || (v.Guest is not null && m.By == v.Guest.Id))
                sb.Append("<form method=\"post\">").Append(form).Append("<input type=\"hidden\" name=\"mark\" value=\"").Append(m.Id).Append("\"><button name=\"action\" value=\"untag\" type=\"submit\" aria-label=\"")
                  .Append(M.H(t.Name)).Append(" entfernen\" title=\"Entfernen\"><i class=\"ti ti-x\"></i></button></form>");
            sb.Append("</span>");
        }
        if (marks.Count == 0) sb.Append("<span class=\"mo-muted\">Noch niemand markiert.</span>");
        sb.Append("</div>");
        if (canTag)
        {
            var names = (await store.GuestsAsync(ct)).Where(g => g.Active).SelectMany(g => g.PeopleList).Concat(listing.Tags.Select(t => t.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append("<form method=\"post\" class=\"mo-row\">").Append(form).Append("<input type=\"hidden\" name=\"action\" value=\"tag\"><input name=\"tag\" list=\"mo-names\" maxlength=\"40\" autocomplete=\"off\" placeholder=\"")
              .Append(ctx.Settings.GetBool(MomentsSettings.NewTags) || v.Admin ? "Name oder Stichwort, z. B. Tanzfläche" : "Name oder Stichwort aus der Liste").Append("\" aria-label=\"Person oder Stichwort\"><button class=\"mo-btn\" type=\"submit\"><i class=\"ti ti-tag\" aria-hidden=\"true\"></i> Markieren</button></form><datalist id=\"mo-names\">");
            foreach (var n in names) sb.Append("<option value=\"").Append(M.H(n)).Append("\">");
            sb.Append("</datalist>");
        }
        sb.Append("</section></div>");

        // comments
        var guests = listing.Guests;
        var comments = (await store.CommentsOfPhotoAsync(p.Id, ct)).Where(c => c.Visible || v.Admin || (v.Guest is not null && c.GuestId == v.Guest.Id)).ToList();
        sb.Append("<section class=\"mo-comments\" id=\"mo-comments\"><h2>Kommentare").Append(comments.Count > 0 ? " (" + comments.Count + ")" : "").Append("</h2>");
        if (comments.Count == 0) sb.Append("<p class=\"mo-muted\">Noch keine Kommentare.</p>");
        sb.Append("<ul class=\"mo-commentlist\">");
        foreach (var c in comments)
        {
            var name = AuthorName(ctx, c.GuestId, guests);
            sb.Append("<li").Append(c.Visible ? "" : " class=\"waiting\"").Append("><span class=\"mo-avatar small\">").Append(M.H(M.Initials(name))).Append("</span><div><p class=\"mo-comment-head\"><strong>")
              .Append(M.H(name)).Append("</strong> <small>").Append(M.H(M.Ago(c.Created))).Append(c.Status == MomentsStore.Pending ? " · wartet auf Freigabe" : c.Status == MomentsStore.Hidden ? " · ausgeblendet" : "")
              .Append("</small></p><p>").Append(M.Text(c.Text)).Append("</p></div>");
            if (v.Admin || (v.Guest is not null && c.GuestId == v.Guest.Id))
                sb.Append("<form method=\"post\">").Append(form).Append("<input type=\"hidden\" name=\"comment\" value=\"").Append(c.Id)
                  .Append("\"><button name=\"action\" value=\"uncomment\" type=\"submit\" class=\"mo-x\" aria-label=\"Kommentar löschen\" title=\"Löschen\"><i class=\"ti ti-trash\"></i></button></form>");
            sb.Append("</li>");
        }
        sb.Append("</ul>");
        if (p.Approved || v.Admin)
            sb.Append("<form method=\"post\" class=\"mo-comment-form\">").Append(form).Append("<input type=\"hidden\" name=\"action\" value=\"comment\"><textarea name=\"text\" rows=\"2\" maxlength=\"1000\" required placeholder=\"Schreib etwas dazu …\" aria-label=\"Kommentar\"></textarea>")
              .Append("<button class=\"mo-btn\" type=\"submit\"><i class=\"ti ti-send\" aria-hidden=\"true\"></i> Senden</button></form>");
        sb.Append("</section></div></div>").Append(MomentsScript.Stage);
        return sb.ToString();
    }

    private static string Act(string form, string action, string icon, string label, bool primary = false) =>
        "<form method=\"post\" class=\"mo-inline\">" + form + "<button class=\"mo-btn" + (primary ? "" : " ghost") + "\" name=\"action\" value=\"" + action + "\" type=\"submit\"><i class=\"ti " + icon +
        "\" aria-hidden=\"true\"></i> " + M.H(label) + "</button></form>";

    // ---- the own photos -----------------------------------------------------------------------------------------

    private static async Task<PluginResult> MineAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        if (v is null) return ToLogin(ctx);
        var mine = await store.PhotosOfGuestAsync(v.Id, ct);
        var listing = await ListingAsync(ctx, store, v, new Filter(0, "", "zeit", false, 1), ct);
        var hearted = listing.Hearts.Count(kv => kv.Value.Contains(v.Id));
        var sb = new StringBuilder(Head(ctx, v, "Meine Fotos", crumb: "")).Append(Notice(ctx));
        sb.Append("<div class=\"mo-welcome-actions left\">");
        if (v.Admin || ctx.Settings.GetBool(MomentsSettings.GuestUpload))
            sb.Append("<a class=\"mo-btn\" href=\"").Append(Base).Append("/hochladen\"><i class=\"ti ti-camera-plus\" aria-hidden=\"true\"></i> Fotos hochladen</a>");
        if (hearted > 0) sb.Append("<a class=\"mo-btn ghost\" href=\"").Append(Base).Append("?herzen=1\"><i class=\"ti ti-heart\" aria-hidden=\"true\"></i> Meine Herzen (").Append(hearted).Append(")</a>");
        if (v.Guest is not null) sb.Append("<a class=\"mo-btn ghost\" href=\"").Append(Base).Append("/abmelden\"><i class=\"ti ti-logout\" aria-hidden=\"true\"></i> Abmelden</a>");
        sb.Append("</div>");
        if (mine.Count == 0) sb.Append("<div class=\"mo-empty\"><i class=\"ti ti-camera\" aria-hidden=\"true\"></i><p>Du hast noch keine Fotos hochgeladen.</p></div>");
        else
        {
            var waiting = mine.Count(x => x.Pending);
            sb.Append("<p class=\"mo-muted\">").Append(M.Plural(mine.Count, "Foto", "Fotos"));
            if (waiting > 0) sb.Append(", davon ").Append(waiting).Append(" noch nicht freigegeben – die anderen Gäste sehen sie, sobald die Gastgeber sie freigegeben haben");
            sb.Append(".</p><div class=\"mo-grid\">");
            foreach (var p in mine)
                sb.Append(Tile(p, "", listing, p.Pending ? "wartet auf Freigabe" : p.Status == MomentsStore.Hidden ? "ausgeblendet" : null));
            sb.Append("</div>");
        }
        return Frame(ctx, sb.ToString(), "Meine Fotos");
    }

    // ---- upload -------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> UploadAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        if (v is null) return ctx.Request.IsPost ? PluginResult.Json(new JsonObject { ["ok"] = false, ["error"] = "Bitte melde dich erst an." }, 403) : ToLogin(ctx);
        var open = v.Admin || ctx.Settings.GetBool(MomentsSettings.GuestUpload);
        if (ctx.Request.IsPost && ctx.Request.Action == "upload")
        {
            var ajax = ctx.Request.F("ajax") == "1";
            if (!open) return Answer(ctx, ajax, "Das Hochladen ist geschlossen.", false);
            var result = await MomentsUpload.SaveAsync(ctx, store, v, ct);
            return Answer(ctx, ajax, result.Error, result.Pending);
        }
        var sb = new StringBuilder(Head(ctx, v, "Fotos hochladen", crumb: ""));
        if (!open) sb.Append("<div class=\"mo-empty\"><i class=\"ti ti-lock\" aria-hidden=\"true\"></i><p>Das Hochladen ist geschlossen. Danke für all eure Bilder!</p></div>");
        else sb.Append(MomentsUpload.Panel(ctx, v, await store.AlbumsAsync(ct),
            long.TryParse(ctx.Request.Q("album"), NumberStyles.None, CultureInfo.InvariantCulture, out var preselect) ? preselect : 0));
        return Frame(ctx, sb.ToString(), "Fotos hochladen");
    }

    /// <summary>The script sends one photo per request and wants JSON; without script the form comes whole and the answer is a page.</summary>
    private static PluginResult Answer(IPluginContext ctx, bool ajax, string? error, bool pending)
    {
        if (ajax) return PluginResult.Json(new JsonObject { ["ok"] = error is null, ["error"] = error, ["pending"] = pending });
        if (error is not null) return Frame(ctx, Err(error) + "<p><a class=\"mo-btn\" href=\"" + Base + "/hochladen\">Zurück</a></p>", "Fotos hochladen");
        return PluginResult.Redirect(Base + "/meine");
    }

    // ---- files --------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> ImageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        if (v is null) return PluginResult.Status(403);
        if (!long.TryParse(ctx.Request.R("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || await store.PhotoAsync(id, ct) is not Photo p || !v.CanSee(p))
            return PluginResult.NotFound();
        var file = ctx.Request.R("size") switch
        {
            "t" => p.Thumb.Length > 0 ? p.Thumb : p.View.Length > 0 ? p.View : p.File,
            "v" => p.View.Length > 0 ? p.View : p.File,
            "o" when v.Owns(p) || MomentsSettings.DownloadMode(ctx) == MomentsSettings.DownloadOriginal => p.File,
            _ => ""
        };
        return file.Length == 0 ? PluginResult.NotFound() : await ServeAsync(ctx, file, null, ct);
    }

    private static async Task<PluginResult> DownloadAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        if (v is null) return ToLogin(ctx);
        if (!long.TryParse(ctx.Request.R("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || await store.PhotoAsync(id, ct) is not Photo p || !v.CanSee(p))
            return PluginResult.NotFound();
        var mode = v.Owns(p) ? MomentsSettings.DownloadOriginal : MomentsSettings.DownloadMode(ctx);
        var file = mode switch { MomentsSettings.DownloadOriginal => p.File, MomentsSettings.DownloadView => p.View.Length > 0 ? p.View : p.File, _ => "" };
        if (file.Length == 0) return PluginResult.NotFound();
        var name = DownloadName(MomentsSettings.Event(ctx)) + "-" + M.N(p.Id) + Path.GetExtension(file);
        return await ServeAsync(ctx, file, name, ct);
    }

    private static string DownloadName(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_' or '&' && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        var r = sb.ToString().Trim('-');
        return r.Length == 0 ? "Foto" : r.Length > 40 ? r[..40] : r;
    }

    /// <summary>A photo file from the plugin's private store: never sniffed, never cached by a shared cache, a download when a name is given.</summary>
    internal static async Task<PluginResult> ServeAsync(IPluginContext ctx, string file, string? downloadName, CancellationToken ct)
    {
        var stream = await ctx.Files.OpenAsync(file, ct);
        if (stream is null) return PluginResult.NotFound();
        return PluginResult.FromStream(stream, MomentsImage.ContentType(Path.GetExtension(file).ToLowerInvariant()), downloadName)
            .WithHeader("X-Content-Type-Options", "nosniff").WithHeader("Cache-Control", "private, max-age=604800");
    }
}
