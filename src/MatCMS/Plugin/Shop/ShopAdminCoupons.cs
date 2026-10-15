using System.Globalization;
using System.Security.Cryptography;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>The shop's back office, part three: coupon codes — a list with how often each was used, and the form that makes or changes one.</summary>
internal static class ShopAdminCoupons
{
    private const string Section = ShopAdmin.Section;
    private static string Intl(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string CouponUrl(long id) => $"{Section}/coupons/{id}";

    /// <summary>A code nobody has to read out loud twice: eight characters without 0/O and 1/I.</summary>
    internal static string RandomCode()
    {
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, 8).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
    }

    private static (string Key, UiTone Tone) StateOf(Coupon c)
    {
        var now = DateTime.UtcNow;
        if (!c.Active) return ("shop.adm.cpInactive", UiTone.Muted);
        if (c.ExpiresUtc is { } e && e < now) return ("shop.adm.cpExpired", UiTone.Muted);
        if (c.MaxUses > 0 && c.Used >= c.MaxUses) return ("shop.adm.cpUsedUp", UiTone.Warning);
        if (c.StartsUtc is { } s && s > now) return ("shop.adm.cpScheduled", UiTone.Info);
        return ("shop.adm.active", UiTone.Success);
    }

    public static async Task<PluginResult> CouponsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        if (ctx.Request.IsPost && ctx.Request.Action == "delete" && long.TryParse(ctx.Request.F("id"), out var del))
        {
            await store.DeleteCouponAsync(del, ct);
            return PluginResult.Redirect($"{Section}/coupons", ctx.T("shop.adm.cpDeleted"));
        }
        var cfg = ShopConfig.From(ctx.Settings);
        var coupons = await store.CouponsAsync(ct);
        var cats = (await store.CategoriesAsync(ct)).ToDictionary(c => c.Slug, c => c.Name);
        string D(DateTime? d) => d is null ? "" : d.Value.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("shop.adm.cpCode")), Ui.Column(ctx.T("shop.adm.cpWorth")), Ui.Column(ctx.T("shop.adm.cpMin"), right: true), Ui.Column(ctx.T("shop.adm.cpUsed"), right: true),
                    Ui.Column(ctx.T("shop.adm.cpValid")), Ui.Column(ctx.T("shop.adm.status")), Ui.Column("", actions: true) },
            coupons.Select(c =>
            {
                var (key, tone) = StateOf(c);
                return Ui.RowFor($"{c.Code} {c.Note}",
                    Ui.Inline(Ui.Text(c.Code) with { Inline = true, Strong = true }, c.Category.Length > 0 ? Ui.Text(cats.TryGetValue(c.Category, out var n) ? n : c.Category) with { Inline = true, Muted = true } : null),
                    ShopCoupons.Describe(ctx, c, cfg.Currency),
                    c.MinOrderCents > 0 ? Money.Format(c.MinOrderCents, cfg.Currency, "de") : "—",
                    c.MaxUses > 0 ? $"{c.Used} / {c.MaxUses}" : Intl(c.Used),
                    c.StartsUtc is null && c.ExpiresUtc is null ? "—" : $"{D(c.StartsUtc)} – {D(c.ExpiresUtc)}".Trim(' ', '–', ' '),
                    Ui.Badge(ctx.T(key), tone),
                    Ui.Inline(
                        Ui.Button(ctx.T("action.edit"), CouponUrl(c.Id), UiButtonKind.Ghost, small: true),
                        Ui.Action(ctx.T("action.delete"), "delete", Intl(c.Id), UiButtonKind.Danger, confirm: ctx.T("shop.adm.cpDeleteConfirm", c.Code), icon: "ti-trash")));
            }),
            searchPlaceholder: ctx.T("action.search"), pageSize: 25, emptyText: ctx.T("shop.adm.noCoupons"));
        return PluginResult.Page(Ui.Stack(Ui.PageHead(ctx.T("shop.adm.cpHelp"), ctx.T("shop.adm.coupons"), Ui.Button(ctx.T("shop.adm.newCoupon"), CouponUrl(0), small: true)), table), ctx.T("shop.adm.coupons"));
    }

    public static async Task<PluginResult> CouponAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var cfg = ShopConfig.From(ctx.Settings);
        var id = long.TryParse(ctx.Request.R("id"), out var parsed) ? parsed : 0;
        var coupon = id == 0 ? new Coupon { Kind = CouponKind.Percent, Value = 1000 } : await store.CouponAsync(id, ct);
        if (coupon is null) return PluginResult.Redirect($"{Section}/coupons");
        string? error = null;

        if (ctx.Request.IsPost && ctx.Request.Action == "save")
        {
            coupon = Read(ctx, ctx.Request, coupon, out error);
            if (error is null)
            {
                if (coupon.Code.Length == 0) coupon = coupon with { Code = RandomCode() };
                var (ok, savedId) = await store.SaveCouponAsync(coupon, ct);
                if (ok) return PluginResult.Redirect(CouponUrl(savedId), ctx.T("shop.adm.cpSaved"));
                error = ctx.T("shop.adm.cpCodeTaken", coupon.Code);
            }
        }

        var cats = await store.CategoriesAsync(ct);
        string D(DateTime? d) => d is null ? "" : d.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var kids = new List<UiNode?>();
        if (error is not null) kids.Add(Ui.Alert(error, UiTone.Error));
        kids.Add(Ui.Hidden("action", "save"));
        kids.Add(Ui.Input("code", ctx.T("shop.adm.cpCode"), coupon.Code) with { Help = ctx.T("shop.adm.cpCodeHelp"), MaxLength = 32 });
        kids.Add(Ui.Select("kind", ctx.T("shop.adm.cpKind"), coupon.Kind, Ui.Option(CouponKind.Percent, ctx.T("shop.coupon.kind.percent")), Ui.Option(CouponKind.Fixed, ctx.T("shop.coupon.kind.fixed")), Ui.Option(CouponKind.Shipping, ctx.T("shop.coupon.kind.shipping"))));
        kids.Add(Ui.Input("value", ctx.T("shop.adm.cpValue"), coupon.Kind == CouponKind.Fixed ? Money.ForInput(coupon.Value) : coupon.Kind == CouponKind.Percent ? Money.Percent((int)coupon.Value) : "") with { Help = ctx.T("shop.adm.cpValueHelp", cfg.Currency) });
        kids.Add(Ui.Input("min", ctx.T("shop.adm.cpMinOrder", cfg.Currency), coupon.MinOrderCents > 0 ? Money.ForInput(coupon.MinOrderCents) : "") with { Help = ctx.T("shop.adm.cpMinHelp") });
        kids.Add(Ui.Select("category", ctx.T("shop.adm.category"), coupon.Category, new[] { Ui.Option("", ctx.T("shop.adm.cpAllCategories")) }.Concat(cats.Select(c => Ui.Option(c.Slug, c.Name))).ToArray()));
        kids.Add(Ui.Input("max_uses", ctx.T("shop.adm.cpMaxUses"), coupon.MaxUses > 0 ? Intl(coupon.MaxUses) : "", UiInputType.Number) with { Min = "0", Help = ctx.T("shop.adm.cpMaxHelp") });
        kids.Add(Ui.Checkbox("per_customer", ctx.T("shop.adm.cpOnce"), coupon.OncePerCustomer) with { Help = ctx.T("shop.adm.cpOnceHelp") });
        kids.Add(Ui.Input("starts", ctx.T("shop.adm.cpStarts"), D(coupon.StartsUtc), UiInputType.Date));
        kids.Add(Ui.Input("expires", ctx.T("shop.adm.cpExpires"), D(coupon.ExpiresUtc), UiInputType.Date) with { Help = ctx.T("shop.adm.cpExpiresHelp") });
        kids.Add(Ui.Input("note", ctx.T("shop.adm.cpNote"), coupon.Note) with { Help = ctx.T("shop.adm.cpNoteHelp") });
        kids.Add(Ui.Checkbox("active", ctx.T("shop.adm.activeLabel"), coupon.Active || id == 0));
        kids.Add(Ui.FormActions(Ui.Submit(ctx.T("action.save")), Ui.Button(ctx.T("action.cancel"), $"{Section}/coupons", UiButtonKind.Ghost)));

        var nodes = new List<UiNode?>
        {
            Ui.PageHead(null, ctx.T(id == 0 ? "shop.adm.newCoupon" : "shop.adm.editCoupon"), Ui.Button(ctx.T("shop.adm.back"), $"{Section}/coupons", UiButtonKind.Ghost, icon: "ti-arrow-left", small: true)),
            Ui.Card(Ui.Form(kids.ToArray()))
        };
        if (id > 0) nodes.Add(Ui.Muted(ctx.T("shop.adm.cpStats", coupon.Used, await store.OrdersWithCouponAsync(coupon.Code, ct))));
        return PluginResult.Page(Ui.Stack(nodes.ToArray()), ctx.T(id == 0 ? "shop.adm.newCoupon" : "shop.adm.editCoupon"));
    }

    /// <summary>The coupon as the form describes it. Sets <paramref name="error"/> on the first thing wrong — the form comes back with what was typed.</summary>
    internal static Coupon Read(IPluginContext ctx, RequestData f, Coupon current, out string? error)
    {
        error = null;
        var kind = CouponKind.Normalise(f.F("kind"));
        var code = Coupon.NormaliseCode(f.F("code"));
        long value = 0;
        if (kind == CouponKind.Percent)
        {
            if (!Money.TryParsePercent(f.F("value"), out var bp) || bp <= 0) error = ctx.T("shop.adm.cpErrPercent");
            else value = bp;
        }
        else if (kind == CouponKind.Fixed)
        {
            if (!Money.TryParse(f.F("value"), out value) || value <= 0) { error = ctx.T("shop.adm.cpErrAmount"); value = 0; }
        }
        long min = 0;
        if (f.F("min").Trim().Length > 0 && (!Money.TryParse(f.F("min"), out min) || min < 0)) { error ??= ctx.T("shop.adm.cpErrMin"); min = 0; }
        var maxUses = int.TryParse(f.F("max_uses"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mu) && mu > 0 ? Math.Min(mu, 10_000_000) : 0;
        DateTime? starts = null, expires = null;
        if (f.F("starts").Trim().Length > 0)
        {
            if (DateTime.TryParseExact(f.F("starts").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s)) starts = DateTime.SpecifyKind(s, DateTimeKind.Local).ToUniversalTime();
            else error ??= ctx.T("shop.adm.cpErrDate");
        }
        if (f.F("expires").Trim().Length > 0)
        {
            // "Valid until the 31st" means through the whole 31st.
            if (DateTime.TryParseExact(f.F("expires").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e)) expires = DateTime.SpecifyKind(e.AddDays(1).AddSeconds(-1), DateTimeKind.Local).ToUniversalTime();
            else error ??= ctx.T("shop.adm.cpErrDate");
        }
        if (starts is { } a && expires is { } b && b < a) error ??= ctx.T("shop.adm.cpErrRange");
        return current with
        {
            Code = code, Kind = kind, Value = value, MinOrderCents = min, MaxUses = maxUses, OncePerCustomer = f.Form.ContainsKey("per_customer"), StartsUtc = starts, ExpiresUtc = expires,
            Category = f.F("category").Trim(), Active = f.Form.ContainsKey("active"), Note = f.F("note").Trim()
        };
    }
}
