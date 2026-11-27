using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Assistant;

/// <summary>
/// The assistant and the plugins: the actions plugins DESCRIBE (<c>IPluginBuilder.Operation(PluginOperationSpec, …)</c>) — one at a time
/// (<c>plugin_action</c>) or many of the same kind at once (<c>plugin_batch</c>, a product list from an attachment) —, the plan the editor
/// approves before a bulk write (<c>propose_plan</c>), and the whole text of a long attachment (<c>read_attachment</c>).
/// <para>The rules are code, not prompt: <see cref="MaxWritesWithoutPlan"/> plugin writes per run without an approved plan, then a refusal that
/// tells the model to show its plan first; once a plan is proposed nothing else is written in that run (the editor has not answered yet).
/// A plan counts as approved for the run that follows the editor's answer to it. Every write is journalled one by one with the plugin's own
/// <c>undo</c>, so "Rückgängig" takes back a batch of 200 products like a single one. <c>PersonalData</c> and <c>Internal</c> operations are
/// never offered (<see cref="Plugins.PluginContributions.AssistantOperations"/>): every answer the assistant reads goes to an outside model.</para>
/// </summary>
public sealed partial class AssistantTools
{
    /// <summary>Plugin writes (a batch counts each item) a run may make before it must have shown its plan and got an answer.</summary>
    public const int MaxWritesWithoutPlan = 5;

    /// <summary>Items in one <c>plugin_batch</c> call, and writes a run may make even with an approved plan.</summary>
    public const int MaxBatchItems = 50, MaxWritesPerRun = 2000;

    /// <summary>The previous run of this conversation ended with a plan the editor has now answered (set by the agent at the start of a run).</summary>
    public bool PlanApproved { get; set; }

    /// <summary>This run proposed a plan: the agent ends it as "waiting for approval", and no further write runs in it.</summary>
    public bool PlanProposed { get; private set; }

    private int _pluginWrites;

    private static (string Name, string Args, string What, bool Writes)[] PluginCatalog =>
    [
        ("plugin_action", "{\"name\": \"shop.product.create\", \"args\": {…}}", "EINE Aktion eines Plugins ausführen (Blogbeiträge, Shop-Produkte, Kategorien, Termine …) – die verfügbaren stehen unten unter „Plugin-Aktionen“ mit ihren Argumenten.", true),
        ("plugin_batch", "{\"name\": \"shop.product.create\", \"items\": [{…}, {…}]}", $"DIESELBE Plugin-Aktion für viele Einträge auf einmal (bis {MaxBatchItems} pro Aufruf), z. B. Produkte aus einer Liste anlegen. Antwort: pro Eintrag Status und Meldung; ein Fehler hält die anderen nicht auf.", true),
        ("propose_plan", "{\"title\": \"…\", \"steps\": [\"…\"], \"table\"?: {\"columns\": [\"Titel\", \"Preis\"], \"rows\": [[\"…\", \"…\"]]}, \"count\"?: 120}", "Dem Nutzer zeigen, was du tun wirst, BEVOR du viel anlegst oder änderst: Schritte und eine Vorschau-Tabelle (die ersten Einträge). Danach endet der Auftrag und wartet auf seine Freigabe.", false),
        ("read_attachment", "{\"file\": \"liste.xlsx\", \"from\"?: 1, \"lines\"?: 200}", "Den vollständigen Text eines Anhangs zeilenweise lesen (Tabellen, lange Dokumente), auch Teile, die nicht in der Nachricht standen.", false),
    ];

    // ============================================================================================== the prompt

    /// <summary>The plugin actions as prompt text, grouped by plugin — only the described ones that carry no customers' data.</summary>
    public string PluginActionsText()
    {
        var ops = Plugins.AssistantOperations;
        if (ops.Count == 0) return "";
        var sb = new StringBuilder("\n## Plugin-Aktionen (mit plugin_action oder plugin_batch ausführen, Name genau wie hier)\n");
        foreach (var group in ops.GroupBy(o => o.Owner))
        {
            sb.Append("### ").Append(group.First().Info.Name).Append(" (").Append(group.Key).Append(")\n");
            foreach (var o in group)
                sb.Append("- ").Append(o.QualifiedName).Append(o.Spec!.Writes ? " [ändert]" : "")
                  .Append(" — ").Append(o.Spec.Description).Append(" Argumente: ").Append(o.Spec.Arguments).Append('\n');
        }
        return sb.ToString();
    }

    private Plugins.PluginContributions Plugins => _services.GetRequiredService<Plugins.PluginContributions>();

    /// <summary>
    /// Which plugin operations a caller other than the assistant may run — the site's MCP server sets it from the API key's rights. Null = the
    /// assistant's own set (<see cref="Plugins.PluginContributions.AssistantOperations"/>: never customers' data, every answer it reads goes to a
    /// model provider). Internal operations are in neither set.
    /// </summary>
    public Func<MatCMS.Services.PluginRegistry.OperationEntry, bool>? OperationFilter { get; set; }

    /// <summary>The assistant's operation of this name — qualified (<c>shop.product.create</c>) or a short name only one plugin offers.</summary>
    private MatCMS.Services.PluginRegistry.OperationEntry AssistantOperation(string name)
    {
        var allowed = OperationFilter is { } filter ? Plugins.ExternalOperations.Where(filter).ToList() : Plugins.AssistantOperations;
        if (Plugins.FindOperation(name) is { } found && allowed.Contains(found)) return found;
        throw new ToolError($"Unbekannte Plugin-Aktion „{name}“",
            $"Die Aktion \"{name}\" gibt es nicht (oder ihr Plugin ist aus, oder sie ist für den Assistenten gesperrt). Verfügbar: {string.Join(", ", allowed.Select(o => o.QualifiedName))}.");
    }

    // ============================================================================================== one action, many actions

    private async Task<ToolOutcome> PluginActionAsync(JsonObject args, CancellationToken ct)
    {
        var op = AssistantOperation(Str(args["name"]).Trim());
        var name = op.QualifiedName;
        if (op.Spec!.Writes) GateWrites(name, 1);
        var one = await RunOneAsync(op, args["args"] as JsonObject ?? new JsonObject(), ct);
        if (one.Status == "failed") throw new ToolError($"{name}: fehlgeschlagen", one.Message.Length > 0 ? one.Message : "Die Aktion ist fehlgeschlagen.");
        var result = new JsonObject { ["status"] = one.Status, ["message"] = one.Message };
        if (one.Data is not null) result["data"] = one.Data;
        var warnings = new List<string>();
        if (one.Changed && !one.Undoable) warnings.Add($"Die Aktion {name} lässt sich nicht automatisch rückgängig machen.");
        var summary = $"{op.Info.Name}: {(one.Message.Length > 0 ? one.Message : name)}";
        return Ok(Cap(summary, 200), result, warnings, null, changed: one.Changed);
    }

    private async Task<ToolOutcome> PluginBatchAsync(JsonObject args, CancellationToken ct)
    {
        var op = AssistantOperation(Str(args["name"]).Trim());
        var name = op.QualifiedName;
        if (args["items"] is not JsonArray items || items.Count == 0)
            throw new ToolError("Stapel ohne Einträge", "items muss eine Liste von Argument-Objekten sein: [{…}, {…}].");
        var list = items.OfType<JsonObject>().ToList();
        var warnings = new List<string>();
        if (list.Count != items.Count) warnings.Add($"{items.Count - list.Count} Einträge waren keine Objekte und wurden ausgelassen.");
        if (list.Count > MaxBatchItems)
        {
            warnings.Add($"Nur die ersten {MaxBatchItems} von {list.Count} Einträgen wurden ausgeführt – schicke den Rest in einem weiteren plugin_batch.");
            list = list.Take(MaxBatchItems).ToList();
        }
        if (op.Spec!.Writes) GateWrites(name, list.Count);

        var rows = new JsonArray();
        int applied = 0, skipped = 0, failed = 0, notUndoable = 0;
        for (var i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            Outcome one;
            try { one = await RunOneAsync(op, list[i], ct); }
            catch (Exception e) when (e is InvalidOperationException or System.Text.Json.JsonException or FormatException or DbUpdateException)
            {
                Discard();
                one = new Outcome("failed", e.GetBaseException().Message, null, false, false);
            }
            switch (one.Status) { case "applied": applied++; break; case "failed": failed++; break; default: skipped++; break; }
            if (one.Changed && !one.Undoable) notUndoable++;
            var row = new JsonObject { ["i"] = i, ["status"] = one.Status, ["message"] = Cap(one.Message, 300) };
            // The model needs ids and addresses of what it made (to put it into categories, link it …), not the whole record of every row.
            if (one.Data is JsonNode d && d.ToJsonString().Length <= 400) row["data"] = d;
            rows.Add(row);
        }
        if (notUndoable > 0) warnings.Add($"{notUndoable} Einträge lassen sich nicht automatisch rückgängig machen.");
        if (failed > 0) warnings.Add($"{failed} Einträge sind fehlgeschlagen – die Meldungen stehen bei den Einträgen.");
        var verb = op.Spec.Writes ? "ausgeführt" : "gelesen";
        var summary = $"{op.Info.Name}: {name} für {list.Count} Einträge – {applied} {verb}" + (skipped > 0 ? $", {skipped} übersprungen" : "") + (failed > 0 ? $", {failed} fehlgeschlagen" : "");
        var result = new JsonObject
        {
            ["name"] = name, ["total"] = list.Count, ["applied"] = applied, ["skipped"] = skipped, ["failed"] = failed, ["items"] = rows,
        };
        return new ToolOutcome(failed < list.Count, Cap(summary, 240), result, warnings, null, op.Spec.Writes && applied > 0);
    }

    private sealed record Outcome(string Status, string Message, JsonNode? Data, bool Changed, bool Undoable);

    /// <summary>Runs one operation and journals its undo. Refusals of the plugin come back as status "failed" with its message.</summary>
    private async Task<Outcome> RunOneAsync(MatCMS.Services.PluginRegistry.OperationEntry op, JsonObject input, CancellationToken ct)
    {
        var answer = await Plugins.RunOperationAsync(_services, op.QualifiedName, input.DeepClone(), ct);
        var o = answer as JsonObject ?? new JsonObject { ["status"] = "applied", ["message"] = answer?.ToString() ?? "" };
        var status = Str(o["status"]) is { Length: > 0 } s ? s : "applied";
        var message = Str(o["message"]);
        var changed = op.Spec!.Writes && status == "applied";
        var undoable = false;
        if (changed)
        {
            _pluginWrites++;
            if (o["undo"] is JsonObject undo && Str(undo["name"]).Length > 0)
            {
                Journal("op", op.QualifiedName + "#" + Guid.NewGuid().ToString("N")[..8], undo.ToJsonString());
                await _db.SaveChangesAsync(ct);
                undoable = true;
            }
        }
        return new Outcome(status, message, o["data"]?.DeepClone(), changed, undoable);
    }

    /// <summary>Refuses a plugin write the run may not make yet: outside a locked scope, after it proposed a plan (the editor has not
    /// answered), or beyond <see cref="MaxWritesWithoutPlan"/> without an approved plan.</summary>
    private void GateWrites(string name, int count)
    {
        RequireUnlocked($"die Plugin-Aktion „{name}“ ausführen");
        if (PlanProposed)
            throw new ToolError("Plan wartet auf Freigabe", "Du hast gerade einen Plan vorgeschlagen. Schreibe jetzt nichts, sondern beende mit \"done\": true und warte auf die Antwort des Nutzers.");
        if (!PlanApproved && _pluginWrites + count > MaxWritesWithoutPlan)
            throw new ToolError("Erst den Plan zeigen",
                $"Das wären mehr als {MaxWritesWithoutPlan} Änderungen an Plugin-Daten in diesem Auftrag. Zeige dem Nutzer zuerst mit propose_plan, was du anlegen oder ändern wirst " +
                "(Schritte, eine Vorschau-Tabelle der ersten Einträge, die Anzahl), und warte auf seine Freigabe.");
        if (_pluginWrites + count > MaxWritesPerRun)
            throw new ToolError("Zu viele Änderungen", $"Ein Auftrag darf höchstens {MaxWritesPerRun} Plugin-Einträge ändern. Sag dem Nutzer, wie weit du bist; er kann einen neuen Auftrag für den Rest geben.");
    }

    // ============================================================================================== the plan

    private ToolOutcome ProposePlan(JsonObject args)
    {
        var title = Cap(Str(args["title"]).Trim(), 160);
        var steps = (args["steps"] as JsonArray ?? new JsonArray()).Select(s => Cap(Str(s).Trim(), 400)).Where(s => s.Length > 0).Take(20).ToList();
        if (title.Length == 0 && steps.Count == 0) throw new ToolError("Plan ohne Inhalt", "Ein Plan braucht title und steps (eine Liste kurzer Sätze).");
        var plan = new JsonObject
        {
            ["title"] = title.Length > 0 ? title : "Plan",
            ["steps"] = new JsonArray(steps.Select(s => (JsonNode)s).ToArray()),
        };
        if (Int(args["count"]) is int count) plan["count"] = count;
        // The preview table as the page draws it: at most 8 columns and 30 rows, short cells. Everything is TEXT — the page encodes it.
        if (args["table"] is JsonObject table && table["columns"] is JsonArray cols)
        {
            var columns = cols.Select(c => Cap(Str(c).Trim(), 60)).Take(8).ToList();
            var rows = new JsonArray();
            foreach (var r in (table["rows"] as JsonArray ?? new JsonArray()).OfType<JsonArray>().Take(30))
                rows.Add(new JsonArray(r.Take(columns.Count).Select(c => (JsonNode)Cap(Str(c).Trim(), 120)).ToArray()));
            if (columns.Count > 0) plan["table"] = new JsonObject { ["columns"] = new JsonArray(columns.Select(c => (JsonNode)c).ToArray()), ["rows"] = rows };
        }
        PlanProposed = true;
        return Ok($"Plan: {plan["title"]}", new JsonObject
        {
            ["status"] = "proposed", ["plan"] = plan,
            ["note"] = "Der Nutzer sieht den Plan jetzt mit einem Knopf zum Ausführen. Schicke keine weiteren Aktionen; beende mit \"done\": true und einer kurzen Frage, ob du so vorgehen sollst.",
        });
    }

    // ============================================================================================== attachments

    private async Task<ToolOutcome> ReadAttachmentAsync(JsonObject args, CancellationToken ct)
    {
        var name = Str(args["file"] ?? args["name"]).Trim();
        var from = Math.Max(1, Int(args["from"]) ?? 1);
        var count = Math.Clamp(Int(args["lines"]) ?? 200, 1, 400);
        var steps = await _db.AiSteps.AsNoTracking().Where(s => s.ConversationId == ConversationId && s.Kind == "user" && s.DataJson != null)
            .OrderByDescending(s => s.Id).Select(s => s.DataJson!).ToListAsync(ct);
        var known = new List<string>();
        foreach (var json in steps)
            foreach (var f in (JsonNode.Parse(json)?["files"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var fileName = Str(f["name"]);
                if (Str(f["textFile"]).Length > 0) known.Add(fileName);
                if (!string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase) || Str(f["textFile"]).Length == 0) continue;
                var read = _files.ReadKeptLines(Str(f["textFile"]), from, count, 30000)
                    ?? throw new ToolError($"Anhang „{name}“ nicht mehr da", "Der Text dieses Anhangs ist nicht mehr gespeichert. Bitte den Nutzer, die Datei erneut anzuhängen.");
                var to = from + read.lines.Count - 1;
                return Ok($"„{fileName}“ gelesen: Zeilen {from}–{to} von {read.total}", new JsonObject
                {
                    ["file"] = fileName, ["from"] = from, ["to"] = to, ["total"] = read.total, ["more"] = to < read.total,
                    ["lines"] = new JsonArray(read.lines.Select(l => (JsonNode)l).ToArray()),
                });
            }
        throw new ToolError($"Anhang „{name}“ unbekannt",
            known.Count > 0 ? $"Keinen Anhang \"{name}\" mit Text gefunden. Vorhanden: {string.Join(", ", known.Distinct())}." : "In diesem Gespräch gibt es keinen Anhang mit Text.");
    }
}
