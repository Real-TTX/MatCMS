namespace MatCMS.Models;

/// <summary>
/// A named navigation menu. Items live in <see cref="MenuItem"/> keyed by <see cref="Key"/>.
/// Built-in menus (header/footer/toolbar) can't be deleted; the user can add any number of others.
/// </summary>
public class Menu
{
    public int Id { get; set; }

    /// <summary>Stable slug used by menu items and template placeholders (e.g. "header", "main").</summary>
    public string Key { get; set; } = "";

    /// <summary>Display name shown in the admin (e.g. "Hauptmenü").</summary>
    public string Name { get; set; } = "";

    public int SortOrder { get; set; }

    /// <summary>Built-in menus (header/footer/toolbar) are protected from deletion.</summary>
    public bool BuiltIn { get; set; }
}
