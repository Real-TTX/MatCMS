using System.Globalization;
using System.Text;
using static MatCMS.Plugin.MatcmsStore.StoreHtml;

namespace MatCMS.Plugin.MatcmsStore;

/// <summary>
/// The publishers' side: register (an account of the SITE, created by the store itself — the site's own self-registration can stay off), become a
/// publisher with an account one already has, upload a package (a new entry or a new version), edit an entry's texts and pictures. Nothing a
/// publisher does is public before the operator approved a version (<see cref="StoreAdmin"/>).
/// </summary>
internal static class StoreAccount
{
    public const int MaxPictures = 8;
    private const long MaxPictureBytes = 3 * 1024 * 1024;

    private static PluginResult AccountPage(IPluginContext ctx, string title, string body, int status = 200) =>
        Page(ctx, title, $"<p class=\"mst-crumbs\"><a href=\"{StoreUrls.Home}\">{E(StoreSettings.StoreTitle(ctx))}</a> › <a href=\"{StoreUrls.Account}\">{E(ctx.T("store.account"))}</a></p>" + body,
            status: status, noIndex: true);

    private static string Ok(IPluginContext ctx) => ctx.Request.Q("ok") is { Length: > 0 } ok && ok.All(c => char.IsAsciiLetterLower(c))
        ? Notice(ctx.T("store.ok." + ok)) : "";

    /// <summary>The signed-in member's publisher, or a page that says why they cannot publish yet.</summary>
    private static async Task<(Publisher? Pub, PluginResult? Stop)> PublisherOrStopAsync(IPluginContext ctx, StoreDb db, string here, CancellationToken ct)
    {
        var member = ctx.Member!;
        var pub = await db.PublisherOfMemberAsync(member.Id, ct);
        if (pub is null) return (null, PluginResult.Redirect(StoreUrls.Account));
        if (pub.Blocked) return (null, AccountPage(ctx, ctx.T("store.account"), Notice(ctx.T("store.err.blocked"), "err"), 403));
        if (ctx.Settings.GetBool(StoreSettings.RequireTwoFactor) && !member.TwoFactorEnabled)
            return (null, AccountPage(ctx, ctx.T("store.account"),
                $"<h1>{E(ctx.T("store.account"))}</h1>" + Notice(ctx.T("store.need2fa"), "info")
                + $"<p><a class=\"btn\" href=\"{E(MemberPaths.Security + "?pflicht=1&returnUrl=" + Uri.EscapeDataString(here))}\">{E(ctx.T("store.set2fa"))}</a></p>"));
        return (pub, null);
    }

    // ---- registering ---------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> RegisterAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Member is not null) return PluginResult.Redirect(StoreUrls.Account);
        if (!ctx.Settings.GetBool(StoreSettings.Registration))
            return AccountPage(ctx, ctx.T("store.register"), $"<h1>{E(ctx.T("store.register"))}</h1>" + Notice(ctx.T("store.registrationClosed"), "info"));

        string? error = null;
        var name = ctx.Request.F("name").Trim();
        var email = ctx.Request.F("email").Trim();
        var website = ctx.Request.F("website_url").Trim();
        if (ctx.Request.IsPost)
        {
            var pw = ctx.Request.F("password");
            if (ctx.Request.F("website").Length > 0) error = ctx.T("store.err.spam");                     // the honeypot
            else if (name.Length is < 2 or > 60) error = ctx.T("store.err.name");
            else if (website.Length > 0 && StoreText.SafeUrl(website).Length == 0) error = ctx.T("store.err.website");
            else if (pw != ctx.Request.F("password2")) error = ctx.T("store.err.passwordMatch");
            else if (ctx.Request.F("terms") != "1") error = ctx.T("store.err.terms");
            else
            {
                var res = await ctx.Members.CreateAsync(email, pw, name, ct);
                switch (res.Status)
                {
                    case MemberCreateStatus.Created when res.Account is { } acc:
                        await new StoreDb(ctx.Data).CreatePublisherAsync(acc.Id, name, StoreText.SafeUrl(website), ct);
                        await ctx.Members.SignInAsync(acc, persistent: true, ct);
                        return PluginResult.Redirect(StoreUrls.Account + "?ok=welcome");
                    case MemberCreateStatus.EmailTaken: error = ctx.T("store.err.emailTaken"); break;
                    case MemberCreateStatus.InvalidEmail: error = ctx.T("store.err.email"); break;
                    case MemberCreateStatus.WeakPassword: error = ctx.T("store.err.password"); break;
                    default: error = ctx.T("store.err.register"); break;
                }
            }
        }
        var sb = new StringBuilder($"<h1>{E(ctx.T("store.register"))}</h1><p class=\"mst-intro\">{E(ctx.T("store.registerIntro"))}</p>");
        if (error is not null) sb.Append(Notice(error, "err"));
        sb.Append("<form method=\"post\" class=\"mst-form\">").Append(ctx.Antiforgery)
          .Append(Field("name", ctx.T("store.field.publisherName"), name, required: true, max: 60, hint: ctx.T("store.field.publisherNameHint")))
          .Append(Field("email", ctx.T("store.field.email"), email, type: "email", required: true, autocomplete: "email"))
          .Append(Field("password", ctx.T("store.field.password"), "", type: "password", required: true, autocomplete: "new-password"))
          .Append(Field("password2", ctx.T("store.field.password2"), "", type: "password", required: true, autocomplete: "new-password"))
          .Append(Field("website_url", ctx.T("store.field.website"), website, type: "url"))
          .Append("<input type=\"text\" name=\"website\" value=\"\" tabindex=\"-1\" autocomplete=\"off\" aria-hidden=\"true\" style=\"position:absolute;left:-9999px\">")
          .Append(Terms(ctx))
          .Append("<div class=\"mst-actions\"><button class=\"btn\" type=\"submit\">").Append(E(ctx.T("store.registerSend"))).Append("</button><a href=\"")
          .Append(E(MemberPaths.SignIn + "?returnUrl=" + Uri.EscapeDataString(StoreUrls.Account))).Append("\">").Append(E(ctx.T("store.haveAccount"))).Append("</a></div></form>");
        return AccountPage(ctx, ctx.T("store.register"), sb.ToString(), error is null ? 200 : 422);
    }

    private static string Terms(IPluginContext ctx)
    {
        var sb = new StringBuilder();
        if (ctx.Settings.Get(StoreSettings.Terms) is { Length: > 0 } terms)
        {
            sb.Append("<details class=\"mst-notice mst-info\"><summary>").Append(E(ctx.T("store.termsShow"))).Append("</summary>");
            foreach (var p in terms.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) sb.Append("<p>").Append(E(p)).Append("</p>");
            sb.Append("</details>");
        }
        sb.Append("<div class=\"mst-check-row\"><input type=\"checkbox\" id=\"st-terms\" name=\"terms\" value=\"1\" required><label for=\"st-terms\">")
          .Append(E(ctx.T("store.field.terms"))).Append("</label></div>");
        return sb.ToString();
    }

    private static string Field(string name, string label, string value, string type = "text", bool required = false, int max = 0, string? hint = null, string? autocomplete = null) =>
        $"<label for=\"st-{name}\">{E(label)}</label><input type=\"{type}\" id=\"st-{name}\" name=\"{name}\" value=\"{E(value)}\"{(required ? " required" : "")}"
        + $"{(max > 0 ? $" maxlength=\"{max}\"" : "")}{(autocomplete is null ? "" : $" autocomplete=\"{autocomplete}\"")}>"
        + (hint is null ? "" : $"<p class=\"mst-hint\">{E(hint)}</p>");

    private static string Area(string name, string label, string value, string? hint = null, int rows = 6) =>
        $"<label for=\"st-{name}\">{E(label)}</label><textarea id=\"st-{name}\" name=\"{name}\" rows=\"{rows}\">{E(value)}</textarea>"
        + (hint is null ? "" : $"<p class=\"mst-hint\">{E(hint)}</p>");

    // ---- the account ---------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> AccountAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var member = ctx.Member!;
        var pub = await db.PublisherOfMemberAsync(member.Id, ct);

        if (pub is null)
        {
            // A member of the site without a publisher profile yet: becoming one is a form of its own.
            string? error = null;
            var name = ctx.Request.IsPost ? ctx.Request.F("name").Trim() : member.DisplayName;
            var website = ctx.Request.F("website_url").Trim();
            if (ctx.Request.IsPost && ctx.Request.Action == "become")
            {
                if (!ctx.Settings.GetBool(StoreSettings.Registration)) error = ctx.T("store.registrationClosed");
                else if (name.Length is < 2 or > 60) error = ctx.T("store.err.name");
                else if (website.Length > 0 && StoreText.SafeUrl(website).Length == 0) error = ctx.T("store.err.website");
                else if (ctx.Request.F("terms") != "1") error = ctx.T("store.err.terms");
                else
                {
                    await db.CreatePublisherAsync(member.Id, name, StoreText.SafeUrl(website), ct);
                    return PluginResult.Redirect(StoreUrls.Account + "?ok=welcome");
                }
            }
            var become = new StringBuilder($"<h1>{E(ctx.T("store.become"))}</h1><p class=\"mst-intro\">{E(ctx.T("store.becomeIntro"))}</p>");
            if (error is not null) become.Append(Notice(error, "err"));
            become.Append("<form method=\"post\" class=\"mst-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"become\">")
                .Append(Field("name", ctx.T("store.field.publisherName"), name, required: true, max: 60, hint: ctx.T("store.field.publisherNameHint")))
                .Append(Field("website_url", ctx.T("store.field.website"), website, type: "url")).Append(Terms(ctx))
                .Append("<div class=\"mst-actions\"><button class=\"btn\" type=\"submit\">").Append(E(ctx.T("store.becomeSend"))).Append("</button></div></form>");
            return AccountPage(ctx, ctx.T("store.become"), become.ToString(), error is null ? 200 : 422);
        }

        if (ctx.Request.IsPost && ctx.Request.Action == "profile" && !pub.Blocked)
        {
            var website = ctx.Request.F("website_url").Trim();
            if (website.Length == 0 || StoreText.SafeUrl(website).Length > 0)
            {
                await db.Publishers.UpdateAsync(pub.Id, new Dictionary<string, object?>
                {
                    ["website"] = StoreText.SafeUrl(website), ["about"] = StoreText.Cut(ctx.Request.F("about").Trim(), 1000)
                }, ct);
                return PluginResult.Redirect(StoreUrls.Account + "?ok=saved");
            }
        }

        var sb = new StringBuilder($"<div class=\"mst-head\"><div><h1>{E(ctx.T("store.account"))}</h1><p class=\"mst-intro\">{E(ctx.T("store.accountIntro", pub.Name))}</p></div>");
        if (!pub.Blocked) sb.Append($"<a class=\"btn\" href=\"{StoreUrls.Upload}\">{E(ctx.T("store.upload"))}</a>");
        sb.Append("</div>").Append(Ok(ctx));
        if (pub.Blocked) sb.Append(Notice(ctx.T("store.err.blocked"), "err"));
        else if (ctx.Settings.GetBool(StoreSettings.RequireTwoFactor) && !member.TwoFactorEnabled)
            sb.Append(Notice(ctx.T("store.need2fa"), "info"))
              .Append($"<p><a class=\"btn btn-outline\" href=\"{E(MemberPaths.Security + "?pflicht=1&returnUrl=" + Uri.EscapeDataString(StoreUrls.Account))}\">{E(ctx.T("store.set2fa"))}</a></p>");

        var items = await db.ItemsOfPublisherAsync(pub.Id, ct);
        sb.Append("<h2>").Append(E(ctx.T("store.myItems"))).Append("</h2>");
        if (items.Count == 0) sb.Append("<p class=\"mst-empty\">").Append(E(ctx.T("store.noItems"))).Append("</p>");
        else
        {
            sb.Append("<table class=\"mst-table\"><thead><tr><th>").Append(E(ctx.T("store.col.item"))).Append("</th><th>").Append(E(ctx.T("store.col.status")))
              .Append("</th><th>").Append(E(ctx.T("store.col.versions"))).Append("</th><th>").Append(E(ctx.T("store.col.downloads"))).Append("</th></tr></thead><tbody>");
            foreach (var item in items)
            {
                var versions = await db.VersionsOfAsync(item.Id, ct);
                var vs = string.Join(", ", versions.Take(4).Select(v => v.Version + " (" + ctx.T("store.vstatus." + v.Status) + ")"));
                sb.Append("<tr><td><a href=\"").Append(E(StoreUrls.AccountItem(item.Key))).Append("\">").Append(E(item.Name)).Append("</a><div class=\"mst-hint\">")
                  .Append(E(item.Key)).Append("</div></td><td><span class=\"mst-status\">").Append(E(ctx.T("store.istatus." + item.Status))).Append("</span></td><td>")
                  .Append(E(vs)).Append("</td><td>").Append(E(N(item.Downloads))).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        sb.Append("<h2>").Append(E(ctx.T("store.profile"))).Append("</h2><form method=\"post\" class=\"mst-form\">").Append(ctx.Antiforgery)
          .Append("<input type=\"hidden\" name=\"action\" value=\"profile\"><p class=\"mst-hint\">").Append(E(ctx.T("store.profileName", pub.Name, StoreUrls.Publisher(pub.Slug)))).Append("</p>")
          .Append(Field("website_url", ctx.T("store.field.website"), pub.Website, type: "url"))
          .Append(Area("about", ctx.T("store.field.about"), pub.About, rows: 4))
          .Append("<div class=\"mst-actions\"><button class=\"btn btn-outline\" type=\"submit\">").Append(E(ctx.T("store.save"))).Append("</button></div></form>");
        return AccountPage(ctx, ctx.T("store.account"), sb.ToString());
    }

    // ---- uploading -----------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> UploadAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var (pub, stop) = await PublisherOrStopAsync(ctx, db, StoreUrls.Upload, ct);
        if (stop is not null) return stop;

        string? error = null;
        if (ctx.Request.IsPost)
        {
            var (key, err) = await AcceptAsync(ctx, db, pub!, ct);
            if (err is null) return PluginResult.Redirect(StoreUrls.AccountItem(key!) + "?ok=submitted");
            error = err;
        }
        var maxMb = StoreSettings.MaxPackageBytes(ctx) / (1024 * 1024);
        var sb = new StringBuilder($"<h1>{E(ctx.T("store.upload"))}</h1><p class=\"mst-intro\">{E(ctx.T("store.uploadIntro", maxMb))}</p>");
        if (error is not null) sb.Append(Notice(error, "err"));
        sb.Append("<form method=\"post\" enctype=\"multipart/form-data\" class=\"mst-form\">").Append(ctx.Antiforgery)
          .Append($"<label for=\"st-package\">{E(ctx.T("store.field.package"))}</label><input type=\"file\" id=\"st-package\" name=\"package\" accept=\".zip,.json\" required>")
          .Append($"<p class=\"mst-hint\">{E(ctx.T("store.field.packageHint"))}</p>")
          .Append(Field("version", ctx.T("store.field.version"), ctx.Request.F("version"), hint: ctx.T("store.field.versionHint"), max: 20))
          .Append(Area("notes", ctx.T("store.field.notes"), ctx.Request.F("notes"), ctx.T("store.field.notesHint"), 4))
          .Append($"<h2>{E(ctx.T("store.newEntryTexts"))}</h2><p class=\"mst-hint\">{E(ctx.T("store.newEntryTextsHint"))}</p>")
          .Append(Field("summary", ctx.T("store.field.summary"), ctx.Request.F("summary"), max: 200))
          .Append(Area("description", ctx.T("store.field.description"), ctx.Request.F("description"), ctx.T("store.field.descriptionHint"), 8))
          .Append(Field("tags", ctx.T("store.field.tags"), ctx.Request.F("tags"), hint: ctx.T("store.field.tagsHint")))
          .Append(Field("license", ctx.T("store.field.license"), ctx.Request.F("license"), max: 60))
          .Append(Field("homepage", ctx.T("store.field.homepage"), ctx.Request.F("homepage"), type: "url"))
          .Append($"<label for=\"st-pictures\">{E(ctx.T("store.field.pictures"))}</label><input type=\"file\" id=\"st-pictures\" name=\"pictures\" accept=\"image/png,image/jpeg,image/webp\" multiple>")
          .Append($"<p class=\"mst-hint\">{E(ctx.T("store.field.picturesHint", MaxPictures))}</p>")
          .Append($"<div class=\"mst-actions\"><button class=\"btn\" type=\"submit\">{E(ctx.T("store.uploadSend"))}</button><a href=\"{StoreUrls.Account}\">{E(ctx.T("store.cancel"))}</a></div></form>");
        return AccountPage(ctx, ctx.T("store.upload"), sb.ToString(), error is null ? 200 : 422);
    }

    /// <summary>Takes an uploaded package: checks it, claims or continues its entry, stores file and version (in review). Answers the entry's key or why not.</summary>
    private static async Task<(string? Key, string? Error)> AcceptAsync(IPluginContext ctx, StoreDb db, Publisher pub, CancellationToken ct)
    {
        if (ctx.Request.File("package") is not { Length: > 0 } file) return (null, ctx.T("store.err.noPackage"));
        if (file.Length > StoreSettings.MaxPackageBytes(ctx)) return (null, ctx.T("store.err.pkgTooBig", StoreSettings.MaxPackageBytes(ctx) / (1024 * 1024)));
        byte[] bytes;
        using (var s = file.OpenRead())
        using (var ms = new MemoryStream())
        {
            await s.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        var (info, why) = StorePackage.Read(bytes, ctx.Request.F("version"));
        if (info is null) return (null, ctx.T(why!));
        if (StoreUrls.Taken.Contains(info.Key)) return (null, ctx.T("store.err.keyTaken", info.Key));

        var item = await db.ItemByKeyAsync(info.Key, ct);
        if (item is null && StoreSettings.IsReserved(ctx, info.Key) && !pub.Verified) return (null, ctx.T("store.err.keyReserved", info.Key));
        if (item is not null && item.PublisherId != pub.Id) return (null, ctx.T("store.err.keyTaken", info.Key));
        if (item is { Status: ItemStatus.Withdrawn }) return (null, ctx.T("store.err.itemWithdrawn"));
        if (item is not null)
        {
            var versions = await db.VersionsOfAsync(item.Id, ct);
            // A key stays what it was: a plugin's key never becomes a template, a template's never a component.
            if (versions.Count > 0 && StorePackage.Group(versions[0].Kind) != StorePackage.Group(info.Kind)) return (null, ctx.T("store.err.kindChanged", info.Key));
            if (versions.Any(v => v.Version == info.Version)) return (null, ctx.T("store.err.versionExists", info.Version));
            var highest = versions.Where(v => v.Status != VersionStatus.Rejected).Select(v => v.Version).OrderByDescending(StoreText.VersionKey).FirstOrDefault();
            if (highest is not null && StoreText.VersionKey(info.Version).CompareTo(StoreText.VersionKey(highest)) <= 0)
                return (null, ctx.T("store.err.versionLower", info.Version, highest));
        }

        var name = "packages/" + info.Key + "/" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + (info.IsJson ? ".json" : ".zip");
        using (var ms = new MemoryStream(bytes)) await ctx.Files.SaveAsync(name, ms, ct);
        try
        {
            var now = DateTime.UtcNow;
            long itemId;
            if (item is null)
            {
                itemId = await db.Items.InsertAsync(new Dictionary<string, object?>
                {
                    ["key"] = info.Key, ["name"] = info.Name, ["publisher_id"] = pub.Id, ["status"] = ItemStatus.Draft,
                    ["summary"] = StoreText.Cut(ctx.Request.F("summary").Trim(), 200),
                    ["description"] = StoreText.Cut(ctx.Request.F("description").Trim() is { Length: > 0 } d ? d : info.Description, 8000),
                    ["tags"] = StoreText.CleanTags(ctx.Request.F("tags")), ["license"] = StoreText.Cut(ctx.Request.F("license").Trim(), 60),
                    ["homepage"] = StoreText.SafeUrl(ctx.Request.F("homepage").Trim()), ["created"] = now, ["updated"] = now
                }, ct);
            }
            else itemId = item.Id;
            await db.Versions.InsertAsync(new Dictionary<string, object?>
            {
                ["item_id"] = itemId, ["version"] = info.Version, ["kind"] = info.Kind, ["api"] = info.Api, ["min_cms"] = info.MinCms, ["size"] = info.Size,
                ["sha256"] = info.Sha256, ["file"] = name, ["notes"] = StoreText.Cut(ctx.Request.F("notes").Trim(), 4000), ["status"] = VersionStatus.Review,
                ["created"] = now, ["files"] = string.Join('\n', info.Files.Take(300)), ["look"] = info.Look
            }, ct);
            await AddPicturesAsync(ctx, db, itemId, ct);

            if (ctx.Settings.Get(StoreSettings.NotifyEmail) is { Length: > 3 } to)
                await ctx.Mail.SendAsync(StoreMail.Submitted, [to], new Dictionary<string, string>
                {
                    ["publisher"] = pub.Name, ["name"] = info.Name, ["key"] = info.Key, ["version"] = info.Version, ["kind"] = info.Kind,
                    ["admin_url"] = ctx.Site.Origin.TrimEnd('/') + StoreUrls.Admin + "review"
                }, ct: ct);
            return (info.Key, null);
        }
        catch
        {
            await ctx.Files.DeleteAsync(name, ct);
            throw;
        }
    }

    /// <summary>Stores the pictures posted under "pictures", up to the limit; type by the first bytes, never what the browser claims.</summary>
    private static async Task<int> AddPicturesAsync(IPluginContext ctx, StoreDb db, long itemId, CancellationToken ct)
    {
        var have = (await db.PicturesOfAsync(itemId, ct)).Count;
        var added = 0;
        foreach (var f in ctx.Request.Files.Where(f => f.Field == "pictures" && f.Length > 0))
        {
            if (have + added >= MaxPictures || f.Length > MaxPictureBytes) continue;
            byte[] bytes;
            using (var s = f.OpenRead())
            using (var ms = new MemoryStream())
            {
                await s.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
            if (StoreText.Sniff(bytes) is not { } type) continue;
            var ext = type switch { "image/png" => "png", "image/jpeg" => "jpg", _ => "webp" };
            var name = "pictures/" + itemId.ToString(CultureInfo.InvariantCulture) + "/" + Guid.NewGuid().ToString("N") + "." + ext;
            using (var ms = new MemoryStream(bytes)) await ctx.Files.SaveAsync(name, ms, ct);
            await db.Pictures.InsertAsync(new Dictionary<string, object?>
            {
                ["item_id"] = itemId, ["file"] = name, ["type"] = type, ["caption"] = "", ["sort"] = (long)(have + added + 1) * 10
            }, ct);
            added++;
        }
        return added;
    }

    // ---- one entry -----------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> ItemAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var key = ctx.Request.R("key").ToLowerInvariant();
        var here = StoreUrls.AccountItem(key);
        var (pub, stop) = await PublisherOrStopAsync(ctx, db, here, ct);
        if (stop is not null) return stop;
        if (await db.ItemByKeyAsync(key, ct) is not { } item || item.PublisherId != pub!.Id)
            return AccountPage(ctx, ctx.T("store.notFound"), $"<h1>{E(ctx.T("store.notFound"))}</h1>", 404);

        if (ctx.Request.IsPost)
        {
            var id = long.TryParse(ctx.Request.F("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            var pic = id > 0 ? await db.PictureAsync(id, ct) : null;
            if (pic is not null && pic.ItemId != item.Id) pic = null;
            switch (ctx.Request.Action)
            {
                case "save":
                    var homepage = ctx.Request.F("homepage").Trim();
                    await db.UpdateItemAsync(item.Id, new Dictionary<string, object?>
                    {
                        ["name"] = ctx.Request.F("name").Trim() is { Length: >= 2 } nm ? StoreText.Cut(nm, 80) : item.Name,
                        ["summary"] = StoreText.Cut(ctx.Request.F("summary").Trim(), 200), ["description"] = StoreText.Cut(ctx.Request.F("description").Trim(), 8000),
                        ["tags"] = StoreText.CleanTags(ctx.Request.F("tags")), ["license"] = StoreText.Cut(ctx.Request.F("license").Trim(), 60),
                        ["homepage"] = StoreText.SafeUrl(homepage)
                    }, ct);
                    return PluginResult.Redirect(here + "?ok=saved");
                case "pictures":
                    var added = await AddPicturesAsync(ctx, db, item.Id, ct);
                    if (added > 0) await db.UpdateItemAsync(item.Id, new Dictionary<string, object?>(), ct);
                    return PluginResult.Redirect(here + "?ok=" + (added > 0 ? "pictures" : "nopictures"));
                case "caption" when pic is not null:
                    await db.Pictures.UpdateAsync(pic.Id, new Dictionary<string, object?> { ["caption"] = StoreText.Cut(ctx.Request.F("caption").Trim(), 140) }, ct);
                    return PluginResult.Redirect(here + "?ok=saved");
                case "cover" when pic is not null:
                    var all = (await db.PicturesOfAsync(item.Id, ct)).OrderBy(p => p.Id == pic.Id ? 0 : 1).ThenBy(p => p.Sort).ToList();
                    for (var i = 0; i < all.Count; i++) await db.Pictures.UpdateAsync(all[i].Id, new Dictionary<string, object?> { ["sort"] = (long)(i + 1) * 10 }, ct);
                    return PluginResult.Redirect(here + "?ok=saved");
                case "answer":
                    return PluginResult.Redirect(here + "?ok=" + (await StoreReviews.AnswerAsync(ctx, item, ct) ? "saved" : "nothing"));
                case "delpic" when pic is not null:
                    await db.Pictures.DeleteAsync(pic.Id, ct);
                    await ctx.Files.DeleteAsync(pic.File, ct);
                    return PluginResult.Redirect(here + "?ok=deleted");
            }
        }

        var sb = new StringBuilder($"<div class=\"mst-head\"><div><h1>{E(item.Name)}</h1><p class=\"mst-intro\">{E(item.Key)} · {E(ctx.T("store.istatus." + item.Status))}</p></div>");
        if (item.Status == ItemStatus.Published) sb.Append($"<a class=\"btn btn-outline\" href=\"{E(StoreUrls.Item(item.Key))}\">{E(ctx.T("store.viewPublic"))}</a>");
        sb.Append("</div>").Append(Ok(ctx));

        sb.Append("<h2>").Append(E(ctx.T("store.col.versions"))).Append("</h2><table class=\"mst-table\"><thead><tr><th>").Append(E(ctx.T("store.fact.version")))
          .Append("</th><th>").Append(E(ctx.T("store.col.status"))).Append("</th><th>").Append(E(ctx.T("store.col.submitted"))).Append("</th><th>")
          .Append(E(ctx.T("store.col.note"))).Append("</th></tr></thead><tbody>");
        foreach (var v in await db.VersionsOfAsync(item.Id, ct))
            sb.Append("<tr><td>").Append(E(v.Version)).Append(" <span class=\"mst-hint\">").Append(E(ctx.T(StoreText.KindKey(v.Kind))))
              .Append("</span></td><td><span class=\"mst-status\">").Append(E(ctx.T("store.vstatus." + v.Status))).Append("</span></td><td>").Append(E(Day(v.Created)))
              .Append("</td><td>").Append(E(v.Reason.Length > 0 ? v.Reason : v.Notes)).Append("</td></tr>");
        sb.Append("</tbody></table><div class=\"mst-actions\"><a class=\"btn\" href=\"").Append(StoreUrls.Upload).Append("\">").Append(E(ctx.T("store.newVersion"))).Append("</a></div>");

        sb.Append("<h2>").Append(E(ctx.T("store.texts"))).Append("</h2><form method=\"post\" class=\"mst-form\">").Append(ctx.Antiforgery)
          .Append("<input type=\"hidden\" name=\"action\" value=\"save\">")
          .Append(Field("name", ctx.T("store.field.name"), item.Name, required: true, max: 80))
          .Append(Field("summary", ctx.T("store.field.summary"), item.Summary, max: 200))
          .Append(Area("description", ctx.T("store.field.description"), item.Description, ctx.T("store.field.descriptionHint"), 10))
          .Append(Field("tags", ctx.T("store.field.tags"), item.Tags, hint: ctx.T("store.field.tagsHint")))
          .Append(Field("license", ctx.T("store.field.license"), item.License, max: 60))
          .Append(Field("homepage", ctx.T("store.field.homepage"), item.Homepage, type: "url"))
          .Append("<div class=\"mst-actions\"><button class=\"btn\" type=\"submit\">").Append(E(ctx.T("store.save"))).Append("</button></div></form>");

        var pics = await db.PicturesOfAsync(item.Id, ct);
        sb.Append("<h2>").Append(E(ctx.T("store.field.pictures"))).Append("</h2><div class=\"mst-pics\">");
        for (var i = 0; i < pics.Count; i++)
        {
            var p = pics[i];
            var idText = p.Id.ToString(CultureInfo.InvariantCulture);
            sb.Append("<figure><img src=\"").Append(E(StoreUrls.Image(item.Key, p.Id) + "?v=" + idText)).Append("\" alt=\"\"><figcaption>")
              .Append("<form method=\"post\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"caption\"><input type=\"hidden\" name=\"id\" value=\"").Append(idText)
              .Append("\"><input type=\"text\" name=\"caption\" maxlength=\"140\" value=\"").Append(E(p.Caption)).Append("\" placeholder=\"").Append(E(ctx.T("store.caption")))
              .Append("\" style=\"width:100%\"> <button class=\"btn btn-outline\" type=\"submit\">").Append(E(ctx.T("store.save"))).Append("</button></form> ");
            if (i > 0)
                sb.Append("<form method=\"post\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"cover\"><input type=\"hidden\" name=\"id\" value=\"").Append(idText)
                  .Append("\"><button class=\"btn btn-outline\" type=\"submit\">").Append(E(ctx.T("store.makeCover"))).Append("</button></form> ");
            else sb.Append("<span class=\"mst-status\">").Append(E(ctx.T("store.cover"))).Append("</span> ");
            sb.Append("<form method=\"post\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"delpic\"><input type=\"hidden\" name=\"id\" value=\"").Append(idText)
              .Append("\"><button class=\"btn btn-outline\" type=\"submit\">").Append(E(ctx.T("store.delete"))).Append("</button></form></figcaption></figure>");
        }
        sb.Append("</div>");
        if (pics.Count < MaxPictures)
            sb.Append("<form method=\"post\" enctype=\"multipart/form-data\" class=\"mst-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"pictures\">")
              .Append("<input type=\"file\" name=\"pictures\" accept=\"image/png,image/jpeg,image/webp\" multiple><p class=\"mst-hint\">")
              .Append(E(ctx.T("store.field.picturesHint", MaxPictures))).Append("</p><div class=\"mst-actions\"><button class=\"btn btn-outline\" type=\"submit\">")
              .Append(E(ctx.T("store.addPictures"))).Append("</button></div></form>");
        sb.Append(await StoreReviews.PublisherSectionAsync(ctx, item, ct));
        return AccountPage(ctx, item.Name, sb.ToString());
    }
}
