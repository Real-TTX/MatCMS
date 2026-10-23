using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using MatCMS.Services.Plugins;

namespace MatCMS.Plugin.Bot;

/// <summary>
/// The AI bot: a chat assistant for a site's visitors, built into the CMS and OFF until the operator switches it on. It answers from what the site publishes
/// and — only where the operator allows it and only for the signed-in member — from that member's own data (the shop's orders). Name, look, behaviour and
/// limits are its settings; the model is the site's AI connection (an own AI profile or the cloud), the bot never holds a key.
/// <para>Written against the plugin API only. What it needed and the API did not have became API for every plugin: <see cref="IPluginSite.AskAiAsync"/>,
/// <see cref="IPluginSite.SearchPagesAsync"/>, and two event conventions — <c>site.search</c> (a plugin's content for a search) and <c>member.data</c>
/// (what a plugin knows about ONE member, named by the host).</para>
/// </summary>
public sealed class BotPlugin : IPlugin
{
    /// <summary>The plugin's identity in the list of built-in plugins.</summary>
    public static BuiltInPlugin Definition { get; } = new("bot", "AI Bot",
        "Chat-Assistent für Besucher: antwortet aus den Inhalten der Website und angemeldeten Kunden zu ihren eigenen Bestellungen. Name, Aussehen und Token-Grenzen einstellbar.",
        "1.0", () => new BotPlugin(), EnabledOnNewSite: false);

    /// <summary>Changes with every build of the CMS, so a browser never keeps an old widget script.</summary>
    private static readonly string AssetVersion = typeof(BotPlugin).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    private const string SvgChat = @"<path d=""M21 12a8 8 0 0 1-11.5 7.2L4 20l1-4.4A8 8 0 1 1 21 12z""/><path d=""M8.5 11h.01M12 11h.01M15.5 11h.01""/>";

    /// <inheritdoc />
    public void Configure(IPluginBuilder b)
    {
        BotStrings.Register(b);
        BotConfig.Declare(b.Settings);
        BotStore.Declare(b.Data);

        // The widget: one script on every public page. It asks /ai-bot/config what to draw (the chat bubble, or only the panels the block placed).
        b.Body($"<script src=\"/js/ai-bot.js?v={AssetVersion}\" defer></script>");
        b.Route("/ai-bot/config", ConfigAsync, new PublicRouteOptions { Methods = PluginMethod.Get });
        // Anonymous like a contact form: nothing to forge (the answer goes back to the page that asked; a member's cookie is SameSite=Lax and not sent with a
        // cross-site POST), rate-limited per address on top of the bot's own daily limits.
        b.Route("/ai-bot/ask", BotAsk.AskAsync, new PublicRouteOptions { Methods = PluginMethod.Post, PostsPerMinute = 12 });

        b.Block(new BlockSpec("ai_bot", "bot.block.name", "bot.block.desc")
        {
            Category = "form", IconSvg = SvgChat,
            Fields = new[] { new FieldSpec("heading", "bot.block.heading"), new FieldSpec("intro", "bot.block.intro", FieldKind.Textarea) }
        }, RenderBlockAsync);

        b.Admin.Section("bot.nav", "ti-message-chatbot", AdminGroup.Plugins, 30)
            .Page("overview", "bot.tab.overview", BotAdmin.OverviewAsync)
            .Page("chats", "bot.tab.chats", BotAdmin.ChatsAsync)
            .Page("chats/{session}", null, BotAdmin.ChatAsync);

        b.Job("cleanup", JobSchedule.EveryInterval(TimeSpan.FromHours(6)), async (ctx, ct) => await new BotStore(ctx.Data).CleanAsync(BotConfig.From(ctx.Settings).KeepDays, ct));
    }

    /// <summary>What the widget needs: whether to draw the bubble, the look, its words in the page's language. Per visitor (a signed-in customer reads a
    /// different note), so never cached by anyone in between.</summary>
    private static Task<PluginResult> ConfigAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = BotConfig.From(ctx.Settings);
        var lang = BotAsk.Lang(ctx);
        string W(string key) => BotStrings.For(lang, key);
        // "Hallo, ich bin Assistent!" reads wrong: a bot that kept the default name greets without one.
        var greeting = cfg.Greeting.Length > 0 ? cfg.Greeting
            : cfg.Name == "Assistent" ? W("bot.w.greetingPlain")
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, W("bot.w.greeting"), cfg.Name);
        var body = new JsonObject
        {
            ["on"] = ctx.Site.AiEnabled,
            ["bubble"] = cfg.Bubble,
            ["name"] = cfg.Name,
            ["avatar"] = cfg.Avatar,
            ["color"] = cfg.UseAccent ? "" : cfg.Color,
            ["position"] = cfg.Position,
            ["maxChars"] = cfg.MaxQuestionChars,
            ["consent"] = cfg.Consent
                ? new JsonObject { ["text"] = cfg.ConsentFor(lang), ["button"] = cfg.ConsentButton.Length > 0 ? cfg.ConsentButton : W("bot.w.agree"), ["version"] = cfg.ConsentVersion(lang) }
                : null,
            ["t"] = new JsonObject
            {
                ["open"] = W("bot.w.open"), ["close"] = W("bot.w.close"), ["send"] = W("bot.w.send"), ["thinking"] = W("bot.w.thinking"),
                ["placeholder"] = cfg.Placeholder.Length > 0 ? cfg.Placeholder : W("bot.w.placeholder"),
                ["greeting"] = greeting, ["error"] = W("bot.w.error"), ["sources"] = W("bot.w.sources"), ["reset"] = W("bot.w.reset"),
                ["note"] = W("bot.w.note") + (cfg.MemberData && ctx.Member is not null ? " " + W("bot.w.noteMember") : "")
            }
        };
        return Task.FromResult(PluginResult.Json(body).WithHeader("Cache-Control", "private, no-store"));
    }

    /// <summary>The block: a place on the page where the widget opens its chat as a panel (heading and intro are the editor's).</summary>
    private static Task<PluginResult> RenderBlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var heading = ctx.Block?.Get("heading") ?? "";
        var intro = ctx.Block?.Get("intro") ?? "";
        var html = "<section class=\"section mat-bot-section\"><div class=\"container\">"
                   + (heading.Length > 0 ? $"<h2>{WebUtility.HtmlEncode(heading)}</h2>" : "")
                   + (intro.Length > 0 ? $"<p class=\"mat-bot-intro\">{WebUtility.HtmlEncode(intro)}</p>" : "")
                   + "<div class=\"mat-bot-inline\" data-mat-bot-inline></div></div></section>";
        return Task.FromResult(PluginResult.Fragment(html));
    }
}
