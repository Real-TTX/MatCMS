namespace MatCMS.Content;

/// <summary>
/// Curated set of icons offered for toolbar ("Obere Leiste") menu items. Each entry is the inner
/// markup of a 24×24 <c>fill="currentColor"</c> SVG, so they inherit the surrounding text color.
/// </summary>
public static class MenuIcons
{
    public sealed record Icon(string Key, string Label, string Svg);

    public static readonly IReadOnlyList<Icon> All =
    [
        new("mail",     "E-Mail",    "<path d='M2 5.5A1.5 1.5 0 0 1 3.5 4h17A1.5 1.5 0 0 1 22 5.5v13A1.5 1.5 0 0 1 20.5 20h-17A1.5 1.5 0 0 1 2 18.5v-13Zm2.7.5L12 11.3 19.3 6H4.7Z'/>"),
        new("phone",    "Telefon",   "<path d='M6.6 2.5 3 6.1c-.4.4-.5 1-.3 1.5C4.9 13.9 10.1 19.1 16.4 21.6c.5.2 1.1.1 1.5-.3l3.6-3.6c.5-.5.5-1.3 0-1.8l-3.1-3.1a1.3 1.3 0 0 0-1.4-.3l-2.4.9-4.7-4.7.9-2.4c.2-.5.1-1-.3-1.4L7.4 2.5c-.5-.5-1.3-.5-1.8 0Z'/>"),
        new("whatsapp", "WhatsApp",  "<path d='M12 2a10 10 0 0 0-8.5 15.2L2 22l4.9-1.5A10 10 0 1 0 12 2Zm5 13.6c-.2.6-1.2 1.1-1.7 1.2-.5.1-1 .1-3.2-.7-2.7-1.1-4.4-3.8-4.5-4-.1-.2-1-1.4-1-2.6s.6-1.8.9-2.1c.2-.2.5-.3.7-.3h.5c.2 0 .4 0 .6.5l.8 2c.1.2.1.4 0 .5l-.4.5c-.2.2-.3.3-.1.6.2.3.8 1.3 1.7 2.1 1.2 1 2.1 1.3 2.4 1.5.2.1.4.1.5-.1l.7-.8c.2-.2.3-.2.5-.1l1.9.9c.2.1.4.2.4.3.1.1.1.6-.1 1.2Z'/>"),
        new("calendar", "Kalender",  "<path d='M7 2v2H5.5A2.5 2.5 0 0 0 3 6.5V19a2.5 2.5 0 0 0 2.5 2.5h13A2.5 2.5 0 0 0 21 19V6.5A2.5 2.5 0 0 0 18.5 4H17V2h-2v2H9V2H7Zm12 7v10H5V9h14Z'/>"),
        new("clock",    "Uhrzeit",   "<path d='M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm1 5h-2v6l5 3 1-1.7-4-2.3V7Z'/>"),
        new("mappin",   "Standort",  "<path d='M12 2C7.9 2 4.5 5.4 4.5 9.5c0 5.3 6.4 11.6 7 12.1.3.3.7.3 1 0 .6-.5 7-6.8 7-12.1C19.5 5.4 16.1 2 12 2Zm0 10a2.5 2.5 0 1 1 0-5 2.5 2.5 0 0 1 0 5Z'/>"),
        new("chat",     "Chat",      "<path d='M4 3h16c1.1 0 2 .9 2 2v11c0 1.1-.9 2-2 2H8l-5 4V5c0-1.1.9-2 2-2Z'/>"),
        new("user",     "Konto",     "<path d='M12 12a5 5 0 1 0 0-10 5 5 0 0 0 0 10Zm0 2c-5 0-9 2.5-9 5.5V22h18v-2.5c0-3-4-5.5-9-5.5Z'/>"),
        new("info",     "Info",      "<path d='M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm1 15h-2v-6h2v6Zm0-8h-2V7h2v2Z'/>"),
        new("download", "Download",  "<path d='M11 3v9.6L7.7 9.3 6.3 10.7 12 16.4l5.7-5.7-1.4-1.4L13 12.6V3h-2ZM4 19h16v2H4z'/>"),
        new("cart",     "Warenkorb", "<path d='M7 4H3v2h3l3.6 7.6-1.4 2.5c-.6 1.1.2 2.4 1.4 2.4h9v-2h-8.4l1-1.9h6.5c.7 0 1.4-.4 1.7-1.1l3-6.4H8.5L7 4Zm2 15a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3Zm9 0a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3Z'/>"),
        new("star",     "Stern",     "<path d='m12 2 2.9 6.3 6.9.7-5.1 4.6 1.4 6.8L12 17.6 5.9 20.4l1.4-6.8-5.1-4.6 6.9-.7L12 2Z'/>"),
        new("link",     "Link",      "<path d='M14 3v2h3.6l-9.3 9.3 1.4 1.4L19 6.4V10h2V3h-7ZM5 5c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2v-5h-2v5H5V7h5V5H5Z'/>"),
    ];

    private static readonly Dictionary<string, Icon> ByKey = All.ToDictionary(i => i.Key);

    public static bool IsValid(string? key) => !string.IsNullOrEmpty(key) && ByKey.ContainsKey(key);

    /// <summary>The SVG inner markup for a key, or the "link" fallback.</summary>
    public static string Svg(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var i) ? i.Svg : ByKey["link"].Svg;
}
