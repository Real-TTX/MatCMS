// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using System.Text.Json.Serialization;

namespace MatPlatform;

// What an app adds to a shell (docs/PLATFORM.md "Contributions"): the home screen of Mattiverse draws it in its own look and knows no app.
// Addresses and icons are relative to the app; the shell puts the app's address in front.

/// <summary>The contributions of an app: <c>contributes</c> of the MatApiDefinition.</summary>
public sealed record MatContributions
{
    /// <summary>Icons on the home screen. None: the shell shows one icon for <c>app.home</c>.</summary>
    public IReadOnlyList<MatLauncher> Launchers { get; init; } = [];

    /// <summary>Tiles on the home screen, drawn from values of the app.</summary>
    public IReadOnlyList<MatWidget> Widgets { get; init; } = [];

    /// <summary>The menu of an icon (long press or right click).</summary>
    public IReadOnlyList<MatQuickAction> QuickActions { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => Launchers.Count == 0 && Widgets.Count == 0 && QuickActions.Count == 0;
}

/// <summary>Who sees a launcher or a widget: <c>everyone</c> who may use the app, the <c>admin</c>s, or the <c>person</c> who is looking (with values of that person).</summary>
public static class MatAudience
{
    public const string Everyone = "everyone";
    public const string Admin = "admin";
    public const string Person = "person";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Everyone, Admin, Person };
}

/// <summary>An icon on the home screen.</summary>
/// <param name="Id">Unique in the app, camelCase.</param>
/// <param name="Label">The name under the icon.</param>
/// <param name="Icon">A picture of the app (<c>/img/docs.svg</c>), relative to the app.</param>
/// <param name="Url">Where it opens, relative to the app.</param>
public sealed record MatLauncher(string Id, string Label, string Icon, string Url)
{
    /// <summary>The colour behind the picture, when the picture needs one (<c>#205a8e</c>).</summary>
    public string? Accent { get; init; }

    /// <summary><see cref="MatAudience"/>; null = everyone.</summary>
    public string? Audience { get; init; }

    /// <summary>2.4.0: the role keys of the app whose people see it, on top of <see cref="Audience"/> (null: no narrowing). A shell that does not know the person's role reads <see cref="Audience"/> alone.</summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>A key of <c>state</c> whose number sits on the corner of the icon (unread mails).</summary>
    public string? Badge { get; init; }
}

/// <summary>The kinds of widget and their sizes.</summary>
public static class MatWidgetKinds
{
    /// <summary>One value of <c>state</c>: a number, a text, a time or a status.</summary>
    public const string Stat = "stat";

    /// <summary>One value of <c>state</c> with its maximum (a <c>meter</c> tile).</summary>
    public const string Meter = "meter";

    /// <summary>Up to four values of <c>state</c>.</summary>
    public const string Tiles = "tiles";

    /// <summary>Up to eight rows from <c>GET /api/widgets/{id}</c>.</summary>
    public const string List = "list";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Stat, Meter, Tiles, List };

    /// <summary><c>small</c> 2×1, <c>medium</c> 2×2, <c>wide</c> 4×2 cells of the home screen.</summary>
    public static readonly IReadOnlySet<string> Sizes = new HashSet<string>(StringComparer.Ordinal) { "small", "medium", "wide" };

    /// <summary>The fewest seconds between two refreshes.</summary>
    public const int MinRefresh = 30;
}

/// <summary>A tile on the home screen. Its values come from <c>GET /api/state</c> (<c>stat</c>, <c>meter</c>, <c>tiles</c>) or <c>GET /api/widgets/{id}</c> (<c>list</c>).</summary>
/// <param name="Id">Unique in the app, camelCase.</param>
/// <param name="Label">The heading of the tile.</param>
/// <param name="Kind">One of <see cref="MatWidgetKinds.All"/>.</param>
/// <param name="Size">One of <see cref="MatWidgetKinds.Sizes"/>.</param>
public sealed record MatWidget(string Id, string Label, string Kind, string Size = "small")
{
    /// <summary>The keys of <c>state</c> it shows (none for <c>list</c>).</summary>
    public IReadOnlyList<string> Items { get; init; } = [];

    /// <summary>An icon of the MatUi sprite next to the heading.</summary>
    public string? Icon { get; init; }

    /// <summary>Where a click goes, relative to the app.</summary>
    public string? Url { get; init; }

    /// <summary>Seconds between two refreshes, at least <see cref="MatWidgetKinds.MinRefresh"/>; null = when the home screen is opened.</summary>
    public int? Refresh { get; init; }

    /// <summary><see cref="MatAudience"/>; null = everyone.</summary>
    public string? Audience { get; init; }

    /// <summary>2.4.0: the role keys of the app whose people see it, on top of <see cref="Audience"/> (null: no narrowing). A shell that does not know the person's role reads <see cref="Audience"/> alone.</summary>
    public IReadOnlyList<string>? Roles { get; init; }
}

/// <summary>An entry of the menu of a launcher: an address of the app or a post action.</summary>
/// <param name="Launcher">The id of the launcher.</param>
/// <param name="Label">The text of the entry.</param>
/// <param name="Icon">An icon of the MatUi sprite.</param>
public sealed record MatQuickAction(string Launcher, string Label, string Icon)
{
    /// <summary>Where it goes, relative to the app (or <see cref="Action"/>).</summary>
    public string? Url { get; init; }

    /// <summary>The id of a post action (<c>POST /api/actions/{id}</c>), instead of <see cref="Url"/>.</summary>
    public string? Action { get; init; }
}

/// <summary>A row of a <c>list</c> widget (<c>GET /api/widgets/{id}</c>).</summary>
/// <param name="Title">The first line.</param>
public sealed record MatWidgetItem(string Title)
{
    public string? Subtitle { get; init; }

    /// <summary>An icon of the MatUi sprite.</summary>
    public string? Icon { get; init; }

    /// <summary>Where a click goes, relative to the app.</summary>
    public string? Url { get; init; }

    public DateTimeOffset? Time { get; init; }

    /// <summary><c>ok</c>, <c>info</c>, <c>warn</c> or <c>error</c>.</summary>
    public string? Level { get; init; }
}
