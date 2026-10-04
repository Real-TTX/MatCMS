using System.Text;
using System.Text.Json.Nodes;

Log("Todo-Plugin geladen.");
AddAdminMenu("Todos", "/admin/plugin/todos", "✅");

JsonArray Load(PluginRequest req)
{
    var db = req.Service<AppDbContext>();
    var s = db.SiteSettings.FirstOrDefault(x => x.Key == "plugin.todos");
    if (s == null || string.IsNullOrWhiteSpace(s.Value)) return new JsonArray();
    try { return JsonNode.Parse(s.Value) as JsonArray ?? new JsonArray(); } catch { return new JsonArray(); }
}
void Save(PluginRequest req, JsonArray todos)
{
    var db = req.Service<AppDbContext>();
    var s = db.SiteSettings.FirstOrDefault(x => x.Key == "plugin.todos");
    if (s == null) db.SiteSettings.Add(new SiteSetting { Key = "plugin.todos", Value = todos.ToJsonString() });
    else s.Value = todos.ToJsonString();
    db.SaveChanges();
}
string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

AddAdminPage("todos", req =>
{
    var todos = Load(req);
    if (req.IsPost)
    {
        var action = req.F("action");
        var id = int.TryParse(req.F("id"), out var pid) ? pid : 0;
        if (action == "add")
        {
            var text = req.F("text").Trim();
            if (text.Length > 0)
            {
                int max = 0;
                foreach (var t in todos) { var v = t?["id"]?.GetValue<int>() ?? 0; if (v > max) max = v; }
                todos.Add(new JsonObject { ["id"] = max + 1, ["text"] = text, ["done"] = false });
            }
        }
        else if (action == "toggle")
        {
            foreach (var t in todos)
                if ((t?["id"]?.GetValue<int>() ?? -1) == id)
                    t!["done"] = !(t["done"]?.GetValue<bool>() ?? false);
        }
        else if (action == "delete")
        {
            JsonNode? rem = null;
            foreach (var t in todos) if ((t?["id"]?.GetValue<int>() ?? -1) == id) rem = t;
            if (rem != null) todos.Remove(rem);
        }
        Save(req, todos);
        Log($"Todo: {action} (id {id})");
        return "";
    }

    var sb = new StringBuilder();
    // Topbar already shows the plugin title ("Todos") — no duplicate <h1> here.
    sb.Append("<div class='page-head'><p class='muted'>Beispiel-Plugin: einfache Aufgabenliste.</p></div>");
    sb.Append("<div class='card'><form method='post' class='form-row' style='align-items:end;'><input type='hidden' name='action' value='add'/>");
    sb.Append("<div class='form-field' style='flex:1;'><label>Neues Todo</label><input name='text' required/></div>");
    sb.Append("<div><button class='btn' type='submit'>Hinzufügen</button></div></form>");
    sb.Append("<div class='block-list' style='margin-top:16px;'>");
    int count = 0;
    foreach (var t in todos)
    {
        count++;
        var id = t?["id"]?.GetValue<int>() ?? 0;
        var text = Enc(t?["text"]?.GetValue<string>());
        var done = t?["done"]?.GetValue<bool>() ?? false;
        var style = done ? "text-decoration:line-through;color:#999;" : "";
        sb.Append("<div class='block-item'>");
        sb.Append($"<form method='post' class='inline-form'><input type='hidden' name='action' value='toggle'/><input type='hidden' name='id' value='{id}'/><button class='btn btn-sm btn-ghost' type='submit'>{(done ? "☑" : "☐")}</button></form>");
        sb.Append($"<div class='b-info' style='flex:1;'><div class='b-type' style='{style}'>{text}</div></div>");
        sb.Append($"<form method='post' class='inline-form'><input type='hidden' name='action' value='delete'/><input type='hidden' name='id' value='{id}'/><button class='btn btn-sm btn-danger' type='submit'>✕</button></form>");
        sb.Append("</div>");
    }
    if (count == 0) sb.Append("<p class='muted'>Noch keine Todos.</p>");
    sb.Append("</div></div>");
    return sb.ToString();
});

AddBlock("todo-list", "Todo-Liste", "Zeigt offene Todos (Todo-Plugin).", req =>
{
    var todos = Load(req);
    var sb = new StringBuilder();
    sb.Append("<section class='section'><div class='container'><h2>Offene Todos</h2><ul>");
    int open = 0;
    foreach (var t in todos)
    {
        if (t?["done"]?.GetValue<bool>() ?? false) continue;
        open++;
        sb.Append("<li>" + Enc(t?["text"]?.GetValue<string>()) + "</li>");
    }
    if (open == 0) sb.Append("<li>Keine offenen Todos 🎉</li>");
    sb.Append("</ul></div></section>");
    return sb.ToString();
});
