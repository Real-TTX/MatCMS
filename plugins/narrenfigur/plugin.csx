using System.Text.Json.Nodes;

// Narrenfigur — a small Fasnet figure in a corner of the public site (first: the Wurmberghexe of the Schalksteinnarren Besigheim).
// Visitors click her and equip her: confetti bag (she throws), broom (she flies across the page), bells (she rings), mulled wine.
//
// The plugin itself is thin: two asset files (assets/figur.js draws and plays the figure, assets/figur.css is her look), a line of
// body HTML that loads them on every public page, and one route the script asks for the settings. The body HTML is built ONCE when
// the plugin loads, so nothing a setting decides may be written into it — the script reads /narrenfigur/config instead, which is
// answered per request. Nothing is stored on the server; a visitor's choice lives in the browser (localStorage).
//
// Languages: the back office (settings) is German and English through the tables below; the figure's own words are in figur.js,
// also in both, picked by <html lang>.
//
// A further figure (the Schalksteinnarr) is a new drawing in figur.js (FIGURES) and a new option of the setting "figure".

(string Key, string De, string En)[] NfRows =
{
    ("nf.set.figure", "Figur", "Figure"),
    ("nf.set.figureHelp", "Welche Figur auf der Website steht.", "Which figure stands on the website."),
    ("nf.figure.hexe", "Wurmberghexe", "Wurmberg witch"),
    ("nf.set.side", "Ecke", "Corner"),
    ("nf.side.right", "Unten rechts", "Bottom right"),
    ("nf.side.left", "Unten links", "Bottom left"),
    ("nf.set.homeOnly", "Nur auf der Startseite", "On the start page only"),
    ("nf.set.homeOnlyHelp", "Aus: Die Figur steht auf jeder Seite der Website.", "Off: the figure stands on every page of the website."),
    ("nf.set.phone", "Auch auf dem Handy zeigen", "Show on phones too"),
};
Plugin.Strings("de", NfRows.ToDictionary(r => r.Key, r => r.De));
Plugin.Strings("en", NfRows.ToDictionary(r => r.Key, r => r.En));

Plugin.Settings
    .Choice("figure", "nf.set.figure", "hexe", new[] { new FieldOption("hexe", "nf.figure.hexe") }, "nf.set.figureHelp")
    .Choice("side", "nf.set.side", "right", new[] { new FieldOption("right", "nf.side.right"), new FieldOption("left", "nf.side.left") })
    .Bool("homeOnly", "nf.set.homeOnly", false, "nf.set.homeOnlyHelp")
    .Bool("phone", "nf.set.phone", true);

// What the script needs of the settings. Only the declared values go out: a saved value that is none of them falls back.
Plugin.Route("/narrenfigur/config", ctx => PluginResult.Json(new JsonObject
{
    ["figure"] = "hexe",   // the only one so far; a second figure reads ctx.Settings.Get("figure") here
    ["side"] = ctx.Settings.Get("side") == "left" ? "left" : "right",
    ["homeOnly"] = ctx.Settings.GetBool("homeOnly"),
    ["phone"] = ctx.Settings.GetBool("phone"),
}).WithHeader("Cache-Control", "no-cache"));

// The version in the address makes a browser fetch the files again after an update.
Plugin.Body("<link rel=\"stylesheet\" href=\"/plugin-assets/narrenfigur/figur.css?v=1.0\">"
    + "<script src=\"/plugin-assets/narrenfigur/figur.js?v=1.0\" defer></script>");
