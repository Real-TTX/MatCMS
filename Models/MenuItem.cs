namespace MatCMS.Models;

public class MenuItem
{
    public int Id { get; set; }

    /// <summary>"header" or "footer".</summary>
    public string Menu { get; set; } = "header";

    /// <summary>Content locale this menu belongs to (e.g. "de", "en"). Menus are served per-locale.</summary>
    public string Locale { get; set; } = "de";

    public string Label { get; set; } = "";

    /// <summary>Internal path (e.g. "/kontakt") or absolute URL.</summary>
    public string Url { get; set; } = "";

    public int SortOrder { get; set; }
    public bool OpenInNewTab { get; set; }
}
