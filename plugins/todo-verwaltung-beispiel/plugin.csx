using System.Text;
using System.Text.Json.Nodes;

Log("Todo-Plugin geladen.");
AddAdminMenu("Todos", "/admin/plugin/todos", "ti-checklist");

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

// ---- operations: the same list for the cloud, MCP and the site's AI assistant -------------------------------------------------
// The example of how a plugin describes its data (rule: plugins/OPERATIONS.md). Every write answers with an "undo" naming an
// INTERNAL operation — only the host runs it, when the user presses "Rückgängig". The text is plain (the block encodes it).
object TodoGate() => "matcms.plugin.todo-verwaltung-beispiel";   // interned: the same object for every run of the script
JsonArray LoadDb(AppDbContext db)
{
    var s = db.SiteSettings.FirstOrDefault(x => x.Key == "plugin.todos");
    if (s == null || string.IsNullOrWhiteSpace(s.Value)) return new JsonArray();
    try { return JsonNode.Parse(s.Value) as JsonArray ?? new JsonArray(); } catch { return new JsonArray(); }
}
void SaveDb(AppDbContext db, JsonArray todos)
{
    var s = db.SiteSettings.FirstOrDefault(x => x.Key == "plugin.todos");
    if (s == null) db.SiteSettings.Add(new SiteSetting { Key = "plugin.todos", Value = todos.ToJsonString() });
    else s.Value = todos.ToJsonString();
    db.SaveChanges();
}
AppDbContext OpDb(IPluginContext ctx) => (AppDbContext)ctx.Unsafe.Services.GetService(typeof(AppDbContext));
JsonObject Answer(string status, string message, JsonNode data = null, JsonNode undo = null)
{
    var o = new JsonObject { ["status"] = status, ["message"] = message };
    if (data != null) o["data"] = data;
    if (undo != null) o["undo"] = undo;
    return o;
}
JsonObject Undo(string name, JsonObject args) => new JsonObject { ["name"] = name, ["args"] = args };
int IdOf(JsonNode n) { try { return n?["id"]?.GetValue<int>() ?? 0; } catch { return int.TryParse(n?["id"]?.ToString(), out var v) ? v : 0; } }
string TextOf(JsonNode n) { try { return n?["text"]?.GetValue<string>() ?? ""; } catch { return n?["text"]?.ToString() ?? ""; } }
bool DoneOf(JsonNode n) { try { return n?["done"]?.GetValue<bool>() ?? false; } catch { return false; } }
bool? Flag(JsonNode n, string k) { try { return n?[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null; } catch { return null; } }
JsonObject View(JsonNode t) => new JsonObject { ["id"] = IdOf(t), ["text"] = TextOf(t), ["done"] = DoneOf(t) };
// null = fine, else the reason. The admin's form needs a text; 500 characters are plenty for a todo.
string CheckText(string text) => text.Trim().Length == 0 ? "Text fehlt." : text.Trim().Length > 500 ? "Text ist länger als 500 Zeichen." : null;

Plugin.Operation(new PluginOperationSpec("todo.list", "Todos auflisten: id, Text, erledigt. open: true = nur offene.", """{"open"?: true}""", Writes: false), (ctx, args, ct) =>
{
    var openOnly = Flag(args, "open") ?? false;
    var all = LoadDb(OpDb(ctx)).Where(t => !openOnly || !DoneOf(t)).Select(t => (JsonNode)View(t)).ToArray();
    return Task.FromResult<JsonNode>(Answer("applied", all.Length + " Todos", new JsonArray(all)));
});

Plugin.Operation(new PluginOperationSpec("todo.read", "Ein Todo lesen.", """{"id": 3}""", Writes: false), (ctx, args, ct) =>
{
    var t = LoadDb(OpDb(ctx)).FirstOrDefault(x => IdOf(x) == IdOf(args));
    return Task.FromResult<JsonNode>(t == null ? Answer("failed", "Todo nicht gefunden.") : Answer("applied", TextOf(t), View(t)));
});

Plugin.Operation(new PluginOperationSpec("todo.create", "Ein Todo anlegen (offen). text: reiner Text, höchstens 500 Zeichen.", """{"text": "Fenster putzen"}"""), (ctx, args, ct) =>
{
    var text = args?["text"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    if (CheckText(text) is { } bad) return Task.FromResult<JsonNode>(Answer("failed", bad));
    var db = OpDb(ctx);
    int id;
    lock (TodoGate())
    {
        var todos = LoadDb(db);
        id = todos.Select(IdOf).DefaultIfEmpty(0).Max() + 1;
        todos.Add(new JsonObject { ["id"] = id, ["text"] = text.Trim(), ["done"] = false });
        SaveDb(db, todos);
    }
    ctx.Log("Todo: add (id " + id + ", Plugin-Aktion)");
    return Task.FromResult<JsonNode>(Answer("applied", "Todo " + id + " angelegt.", new JsonObject { ["id"] = id }, Undo("todo.remove", new JsonObject { ["id"] = id })));
});

Plugin.Operation(new PluginOperationSpec("todo.update", "Ein Todo ändern; nicht genannte Felder bleiben. done: true = erledigt.",
    """{"id": 3, "text"?: "…", "done"?: true}"""), (ctx, args, ct) =>
{
    var text = args?["text"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    if (text != null && CheckText(text) is { } bad) return Task.FromResult<JsonNode>(Answer("failed", bad));
    var done = Flag(args, "done");
    var db = OpDb(ctx);
    JsonObject before;
    lock (TodoGate())
    {
        var todos = LoadDb(db);
        var t = todos.FirstOrDefault(x => IdOf(x) == IdOf(args));
        if (t == null) return Task.FromResult<JsonNode>(Answer("failed", "Todo nicht gefunden."));
        before = View(t);
        if (text != null) t["text"] = text.Trim();
        if (done != null) t["done"] = done.Value;
        SaveDb(db, todos);
    }
    return Task.FromResult<JsonNode>(Answer("applied", "Todo " + before["id"] + " geändert.", null, Undo("todo.restore", new JsonObject { ["todo"] = before })));
});

Plugin.Operation(new PluginOperationSpec("todo.delete", "Ein Todo löschen.", """{"id": 3}"""), (ctx, args, ct) =>
{
    var db = OpDb(ctx);
    JsonObject gone; int at;
    lock (TodoGate())
    {
        var todos = LoadDb(db);
        at = todos.ToList().FindIndex(x => IdOf(x) == IdOf(args));
        if (at < 0) return Task.FromResult<JsonNode>(Answer("failed", "Todo nicht gefunden."));
        gone = View(todos[at]);
        todos.RemoveAt(at);
        SaveDb(db, todos);
    }
    return Task.FromResult<JsonNode>(Answer("applied", "Todo " + gone["id"] + " gelöscht.", null, Undo("todo.restore", new JsonObject { ["todo"] = gone, ["position"] = at })));
});

// ---- the undos: only the host runs them (Internal)
Plugin.Operation(new PluginOperationSpec("todo.remove", "Nimmt ein angelegtes Todo wieder heraus (Rückgängig).", Internal: true), (ctx, args, ct) =>
{
    var db = OpDb(ctx);
    lock (TodoGate())
    {
        var todos = LoadDb(db);
        var t = todos.FirstOrDefault(x => IdOf(x) == IdOf(args));
        if (t == null) return Task.FromResult<JsonNode>(Answer("skipped-exists", "Todo ist schon weg."));
        todos.Remove(t);
        SaveDb(db, todos);
    }
    return Task.FromResult<JsonNode>(Answer("applied", "Todo wieder herausgenommen."));
});

Plugin.Operation(new PluginOperationSpec("todo.restore", "Stellt ein Todo aus einer Rückgängig-Aufzeichnung wieder her.", Internal: true), (ctx, args, ct) =>
{
    if (args?["todo"] is not JsonObject saved || IdOf(saved) <= 0) return Task.FromResult<JsonNode>(Answer("failed", "Kein Todo in der Aufzeichnung."));
    var db = OpDb(ctx);
    lock (TodoGate())
    {
        var todos = LoadDb(db);
        var t = todos.FirstOrDefault(x => IdOf(x) == IdOf(saved));
        if (t != null) { t["text"] = TextOf(saved); t["done"] = DoneOf(saved); }
        else
        {
            int pos = todos.Count;
            try { pos = args["position"]?.GetValue<int>() ?? todos.Count; } catch { }
            todos.Insert(Math.Clamp(pos, 0, todos.Count), View(saved));
        }
        SaveDb(db, todos);
    }
    return Task.FromResult<JsonNode>(Answer("applied", "Todo " + IdOf(saved) + " wiederhergestellt."));
});
