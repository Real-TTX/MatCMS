using System.Globalization;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Bot;

/// <summary>The bot's settings: declared once (the host draws the form, in groups) and read per request.</summary>
internal sealed record BotConfig
{
    public string Name { get; init; } = "Assistent";
    public string Greeting { get; init; } = "";
    public string Avatar { get; init; } = "";
    public bool UseAccent { get; init; } = true;
    public string Color { get; init; } = "#2563eb";
    public string Position { get; init; } = "right";
    public bool Bubble { get; init; } = true;
    public string Placeholder { get; init; } = "";
    public string Instruction { get; init; } = "";
    public string Fallback { get; init; } = "";
    public bool ShowSources { get; init; } = true;
    public bool MemberData { get; init; }
    public int MaxAnswerTokens { get; init; } = 1500;
    public int MaxQuestionChars { get; init; } = 1000;
    public int PerVisitorDay { get; init; } = 30;
    public long PerVisitorTokens { get; init; } = 60_000;
    public long PerMonthTokens { get; init; } = 2_000_000;
    public string LimitText { get; init; } = "";
    public int KeepDays { get; init; }
    public bool Consent { get; init; }
    public string ConsentTemplate { get; init; } = "thirdcountry";
    public string ConsentProvider { get; init; } = "";
    public string ConsentCountry { get; init; } = "";
    public string PrivacyUrl { get; init; } = "/datenschutz";
    public string ConsentText { get; init; } = "";
    public string ConsentButton { get; init; } = "";

    public static readonly string[] ConsentTemplates = { "eu", "dpf", "thirdcountry", "custom" };

    public static void Declare(ISettingsBuilder s)
    {
        s.Add(new SettingSpec("name", "bot.set.name") { Default = "Assistent", Help = "bot.set.nameHelp", Group = "bot.set.gLook", Required = true });
        s.Add(new SettingSpec("greeting", "bot.set.greeting", SettingKind.TextArea) { Help = "bot.set.greetingHelp" });
        s.Add(new SettingSpec("avatar", "bot.set.avatar", SettingKind.Image) { Help = "bot.set.avatarHelp" });
        s.Add(new SettingSpec("useAccent", "bot.set.useAccent", SettingKind.Bool) { Default = "true", Help = "bot.set.useAccentHelp" });
        s.Add(new SettingSpec("color", "bot.set.color", SettingKind.Color) { Default = "#2563eb" });
        s.Add(new SettingSpec("bubble", "bot.set.bubble", SettingKind.Bool) { Default = "true", Help = "bot.set.bubbleHelp" });
        s.Add(new SettingSpec("position", "bot.set.position", SettingKind.Choice)
        {
            Default = "right", Options = new[] { new FieldOption("right", "bot.set.posRight"), new FieldOption("left", "bot.set.posLeft") }
        });
        s.Add(new SettingSpec("placeholder", "bot.set.placeholder"));

        s.Add(new SettingSpec("instruction", "bot.set.instruction", SettingKind.TextArea) { Help = "bot.set.instructionHelp", Group = "bot.set.gBehave" });
        s.Add(new SettingSpec("fallback", "bot.set.fallback", SettingKind.TextArea) { Help = "bot.set.fallbackHelp" });
        s.Add(new SettingSpec("sources", "bot.set.sources", SettingKind.Bool) { Default = "true" });

        s.Add(new SettingSpec("memberData", "bot.set.memberData", SettingKind.Bool) { Default = "false", Help = "bot.set.memberDataHelp", Group = "bot.set.gData" });

        s.Add(new SettingSpec("maxAnswer", "bot.set.maxAnswer", SettingKind.Number) { Default = "1500", Min = 200, Max = 16000, Help = "bot.set.maxAnswerHelp", Group = "bot.set.gLimits" });
        s.Add(new SettingSpec("maxQuestion", "bot.set.maxQuestion", SettingKind.Number) { Default = "1000", Min = 50, Max = 8000 });
        s.Add(new SettingSpec("perVisitor", "bot.set.perVisitor", SettingKind.Number) { Default = "30", Min = 0, Max = 10000, Help = "bot.set.zeroUnlimited" });
        s.Add(new SettingSpec("perVisitorTokens", "bot.set.perVisitorTokens", SettingKind.Number) { Default = "60000", Min = 0, Help = "bot.set.zeroUnlimited" });
        s.Add(new SettingSpec("perMonth", "bot.set.perMonth", SettingKind.Number) { Default = "2000000", Min = 0, Help = "bot.set.perMonthHelp" });
        s.Add(new SettingSpec("limitText", "bot.set.limitText", SettingKind.TextArea));

        s.Add(new SettingSpec("consent", "bot.set.consent", SettingKind.Bool) { Default = "false", Help = "bot.set.consentHelp", Group = "bot.set.gConsent" });
        s.Add(new SettingSpec("consentTemplate", "bot.set.consentTemplate", SettingKind.Choice)
        {
            Default = "thirdcountry", Help = "bot.set.consentTemplateHelp",
            Options = ConsentTemplates.Select(t => new FieldOption(t, "bot.set.ct." + t)).ToArray()
        });
        s.Add(new SettingSpec("consentProvider", "bot.set.consentProvider") { Help = "bot.set.consentProviderHelp" });
        s.Add(new SettingSpec("consentCountry", "bot.set.consentCountry") { Help = "bot.set.consentCountryHelp" });
        s.Add(new SettingSpec("privacyUrl", "bot.set.privacyUrl") { Default = "/datenschutz" });
        s.Add(new SettingSpec("consentText", "bot.set.consentText", SettingKind.TextArea) { Help = "bot.set.consentTextHelp" });
        s.Add(new SettingSpec("consentButton", "bot.set.consentButton"));

        s.Add(new SettingSpec("keepDays", "bot.set.keepDays", SettingKind.Number) { Default = "0", Min = 0, Max = 365, Help = "bot.set.keepDaysHelp", Group = "bot.set.gHistory" });
    }

    public static BotConfig From(IPluginSettings s)
    {
        static long Num(string v, long def) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 ? (long)d : def;
        static bool On(string v, bool def) => v.Length == 0 ? def : v is "true" or "1" or "on";
        var position = s.Get("position") == "left" ? "left" : "right";
        var color = s.Get("color").Trim();
        return new BotConfig
        {
            Name = s.Get("name").Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 60)] : "Assistent",
            Greeting = s.Get("greeting").Trim(),
            Avatar = SafeImage(s.Get("avatar")),
            UseAccent = On(s.Get("useAccent"), true),
            Color = System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$") ? color : "#2563eb",
            Position = position,
            Bubble = On(s.Get("bubble"), true),
            Placeholder = s.Get("placeholder").Trim(),
            Instruction = s.Get("instruction").Trim(),
            Fallback = s.Get("fallback").Trim(),
            ShowSources = On(s.Get("sources"), true),
            MemberData = On(s.Get("memberData"), false),
            MaxAnswerTokens = (int)Math.Clamp(Num(s.Get("maxAnswer"), 1500), 200, 16000),
            MaxQuestionChars = (int)Math.Clamp(Num(s.Get("maxQuestion"), 1000), 50, 8000),
            PerVisitorDay = (int)Math.Min(Num(s.Get("perVisitor"), 30), 10000),
            PerVisitorTokens = Num(s.Get("perVisitorTokens"), 60_000),
            PerMonthTokens = Num(s.Get("perMonth"), 2_000_000),
            LimitText = s.Get("limitText").Trim(),
            KeepDays = (int)Math.Clamp(Num(s.Get("keepDays"), 0), 0, 365),
            Consent = On(s.Get("consent"), false),
            ConsentTemplate = ConsentTemplates.Contains(s.Get("consentTemplate")) ? s.Get("consentTemplate") : "thirdcountry",
            ConsentProvider = s.Get("consentProvider").Trim(),
            ConsentCountry = s.Get("consentCountry").Trim(),
            PrivacyUrl = SafeLink(s.Get("privacyUrl")) is { Length: > 0 } pu ? pu : "/datenschutz",
            ConsentText = s.Get("consentText").Trim(),
            ConsentButton = s.Get("consentButton").Trim()
        };
    }

    /// <summary>A link the consent text may point at: a local path or an https address.</summary>
    public static string SafeLink(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0 || u.IndexOfAny(new[] { '"', '\'', '<', '>', '(', ')', '\\', ' ' }) >= 0) return "";
        return (u.StartsWith('/') && !u.StartsWith("//")) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? u : "";
    }

    /// <summary>
    /// The consent text the visitor sees, in his language: the operator's own text, or the chosen template with provider, country and the privacy
    /// statement filled in. Markdown-light like the answers (the widget turns only **bold**, lists and safe links into markup).
    /// </summary>
    public string ConsentFor(string lang)
    {
        var raw = ConsentTemplate == "custom" && ConsentText.Length > 0 ? ConsentText : BotStrings.For(lang, "bot.consent." + (ConsentTemplate == "custom" ? "thirdcountry" : ConsentTemplate));
        var provider = ConsentProvider.Length > 0 ? ConsentProvider : BotStrings.For(lang, "bot.consent.someProvider");
        var country = ConsentCountry.Length > 0 ? ConsentCountry : BotStrings.For(lang, "bot.consent.someCountry");
        return raw.Replace("{provider}", provider).Replace("{country}", country).Replace("{privacy}", PrivacyUrl);
    }

    /// <summary>Changes whenever the text the visitor agreed to changes — an old consent then no longer counts, he is asked again.</summary>
    public string ConsentVersion(string lang) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ConsentFor(lang))))[..12].ToLowerInvariant();

    /// <summary>A picture address the widget may show: a local path or an https address, nothing that could carry script.</summary>
    public static string SafeImage(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0 || u.IndexOfAny(new[] { '"', '\'', '<', '>', '(', ')', '\\', ' ' }) >= 0) return "";
        return (u.StartsWith('/') && !u.StartsWith("//")) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? u : "";
    }
}
