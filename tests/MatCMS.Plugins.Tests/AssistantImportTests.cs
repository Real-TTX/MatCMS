using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Services;
using MatCMS.Services.Assistant;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The assistant turns an attached product list into shop products: the table is read (XLSX, CSV), more than a handful of plugin writes are
/// refused until the editor has seen a plan, the plan ends the run and waits, the approved run writes in batches with every entry visible,
/// and "Rückgängig" takes the whole batch back. Plus the qualified operation names every caller uses now.
/// </summary>
public class AssistantImportTests : IClassFixture<AssistantApp>
{
    private readonly AssistantApp _app;
    public AssistantImportTests(AssistantApp app) => _app = app;

    private static string Act(params (string tool, string args)[] actions) =>
        "{\"message\":\"\",\"actions\":[" + string.Join(",", actions.Select(a => $"{{\"tool\":\"{a.tool}\",\"args\":{a.args}}}")) + "],\"done\":false}";

    private async Task<(int id, JsonArray steps)> SendAsync(HttpClient c, string message, int? conv = null, params (string name, byte[] bytes)[] files)
    {
        var html = await c.GetStringAsync("/admin/ai");
        var form = new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(html)!), "__RequestVerificationToken" },
            { new StringContent(message), "message" },
            { new StringContent("false"), "locked" },
        };
        if (conv is int id) form.Add(new StringContent(id.ToString()), "id");
        foreach (var (name, bytes) in files) form.Add(new ByteArrayContent(bytes), "files", name);
        var res = await c.PostAsync("/admin/ai?handler=Send", form);
        var answer = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
        Assert.True(answer["ok"]!.GetValue<bool>(), answer["error"]?.ToString());
        var convId = answer["id"]!.GetValue<int>();
        for (var n = 0; n < 600; n++)
        {
            var state = JsonNode.Parse(await c.GetStringAsync($"/admin/ai?handler=Steps&id={convId}"))!.AsObject();
            if (state["status"]!.GetValue<string>() == "idle") return (convId, state["steps"]!.AsArray());
            await Task.Delay(50);
        }
        throw new TimeoutException("the run did not finish");
    }

    private static List<JsonObject> Steps(JsonArray steps, int run, string kind) =>
        steps.Select(s => s!.AsObject()).Where(s => s["run"]!.GetValue<int>() == run && s["kind"]!.GetValue<string>() == kind).ToList();

    private static string EndState(JsonArray steps, int run) => Steps(steps, run, "end").Last()["data"]!["state"]!.GetValue<string>();

    private async Task EnableShopAsync()
    {
        using var scope = _app.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var shop = db.Plugins.Single(p => p.Key == "shop");
        if (shop.Enabled) return;
        shop.Enabled = true;
        db.SaveChanges();
        await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(shop.Id);
    }

    private List<string> ProductSlugs() => _app.Use(db => db.Database.SqlQueryRaw<string>("SELECT slug AS Value FROM p_shop_products").ToList());

    /// <summary>A real .xlsx as Excel writes it: shared strings, a number cell, a date-formatted cell, a sheet behind a relationship.</summary>
    public static byte[] Xlsx(IReadOnlyList<string[]> rows)
    {
        var strings = new List<string>();
        int S(string v) { var i = strings.IndexOf(v); if (i < 0) { strings.Add(v); i = strings.Count - 1; } return i; }
        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Count; r++)
        {
            sheet.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var cell = $"{(char)('A' + c)}{r + 1}";
                var v = rows[r][c];
                if (r > 0 && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                    sheet.Append($"<c r=\"{cell}\"><v>{v}</v></c>");
                else sheet.Append($"<c r=\"{cell}\" t=\"s\"><v>{S(v)}</v></c>");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");
        var shared = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{strings.Count}\">");
        foreach (var s in strings) shared.Append("<si><t>").Append(System.Security.SecurityElement.Escape(s)).Append("</t></si>");
        shared.Append("</sst>");
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Put(string path, string xml) { using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); w.Write(xml); }
            Put("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Put("xl/workbook.xml", "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Preisliste\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Put("xl/sharedStrings.xml", shared.ToString());
            Put("xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return ms.ToArray();
    }

    [Fact]
    public void An_xlsx_is_read_as_table_lines_with_its_cells_in_their_columns_and_a_csv_with_semicolons_and_quotes()
    {
        var text = SpreadsheetText.ReadXlsx(Xlsx([["Titel", "Preis", "Kategorie"], ["Grüner Tee", "7.9", "Tee"], ["Rooibos | süß", "6.5", ""]]));
        Assert.Contains("## Tabelle „Preisliste“ (3 Zeilen)", text);
        Assert.Contains("Zeile 1: Titel | Preis | Kategorie", text);
        Assert.Contains("Zeile 2: Grüner Tee | 7.9 | Tee", text);
        Assert.Contains("Zeile 3: Rooibos / süß | 6.5", text);       // a "|" inside a cell cannot break the columns

        var csv = AssistantFiles.ReadDelimited(Encoding.UTF8.GetBytes("Titel;Preis;Text\n\"Tee; grün\";7,90;\"Er sagt \"\"gut\"\"\"\n"));
        Assert.Contains("Zeile 2: Tee; grün | 7,90 | Er sagt \"gut\"", csv);
        // An older Excel writes Windows-1252: the umlauts must come through.
        var latin = AssistantFiles.ReadDelimited(Encoding.Latin1.GetBytes("Titel;Preis\nKräutertee;5,00\n"));
        Assert.Contains("Kräutertee", latin);
        Assert.Equal(2, SpreadsheetText.Column("C7"));
        Assert.Equal(27, SpreadsheetText.Column("AB1"));
    }

    [Fact]
    public async Task A_product_list_is_planned_first_then_created_in_a_batch_every_entry_visible_and_undo_takes_the_whole_batch_back()
    {
        await EnableShopAsync();
        var admin = await _app.AdminAsync();
        var rows = new List<string[]> { new[] { "Titel", "Preis", "Kategorie" } };
        for (var i = 1; i <= 8; i++) rows.Add([$"Importtee {i}", (3 + i).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), "Importtee"]);
        var items = string.Join(",", Enumerable.Range(1, 8).Select(i => $"{{\"title\":\"Importtee {i}\",\"price_cents\":{(3 + i) * 100},\"category\":\"importtee\"}}"));

        // Run 1: the model tries to write all 8 at once — refused, no plan yet; then it proposes the plan, which ends the run.
        _app.Ai.Say(
            Act(("plugin_batch", $"{{\"name\":\"shop.product.create\",\"items\":[{items}]}}")),
            Act(("propose_plan", """{"title":"8 Tees anlegen","steps":["Kategorie Importtee anlegen","8 Produkte anlegen"],"count":8,"table":{"columns":["Titel","Preis"],"rows":[["Importtee 1","4,00 €"],["Importtee 2","5,00 €"]]}}"""),
                ("plugin_action", """{"name":"shop.category.create","args":{"name":"Zu früh"}}""")));
        var (conv, steps) = await SendAsync(admin, "Leg die Produkte aus der Liste im Shop an.", null, ("preisliste.xlsx", Xlsx(rows)));

        var userMessage = _app.Ai.Requests.Last().Messages.Last(m => m.Role == "user" && m.Content.Contains("preisliste.xlsx")).Content;
        Assert.Contains("Tabelle mit", userMessage);
        Assert.Contains("Zeile 2: Importtee 1 | 4 | Importtee", userMessage);
        var system = _app.Ai.Requests.Last().Messages[0].Content;
        Assert.Contains("shop.product.create", system);                      // qualified names in the prompt
        Assert.Contains("propose_plan", system);
        Assert.Contains("Eingeschaltete Plugins:", system);

        var acts = Steps(steps, 1, "action").Select(s => s["data"]!.AsObject()).ToList();
        Assert.False(acts[0]["ok"]!.GetValue<bool>());
        Assert.Contains("propose_plan", acts[0]["result"]!.ToJsonString());
        Assert.Equal("propose_plan", acts[1]["tool"]!.GetValue<string>());
        Assert.Equal("8 Tees anlegen", acts[1]["result"]!["plan"]!["title"]!.GetValue<string>());
        Assert.Equal("Importtee 1", acts[1]["result"]!["plan"]!["table"]!["rows"]![0]![0]!.GetValue<string>());
        Assert.False(acts[2]["ok"]!.GetValue<bool>());                          // nothing is written after the plan in the same run
        Assert.Equal("plan", EndState(steps, 1));
        Assert.DoesNotContain(ProductSlugs(), s => s.StartsWith("importtee"));

        // Run 2: the editor approved. Category first, then the batch — one of them twice (skipped), one without a title (failed).
        var twice = "{\"title\":\"Importtee 1\",\"price_cents\":400,\"category\":\"importtee\"}";
        _app.Ai.Say(
            Act(("plugin_action", """{"name":"shop.category.create","args":{"name":"Importtee"}}"""),
                ("plugin_batch", $"{{\"name\":\"shop.product.create\",\"items\":[{items},{twice},{{\"price_cents\":100}}]}}")),
            "{\"message\":\"Erledigt: 8 angelegt, 1 übersprungen, 1 fehlgeschlagen.\",\"actions\":[],\"done\":true}");
        (_, steps) = await SendAsync(admin, "Plan freigegeben – bitte genau so ausführen.", conv);

        var batch = Steps(steps, 2, "action").Select(s => s["data"]!.AsObject()).Single(a => a["tool"]!.GetValue<string>() == "plugin_batch");
        Assert.True(batch["ok"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(8, batch["result"]!["applied"]!.GetValue<int>());
        Assert.Equal(1, batch["result"]!["skipped"]!.GetValue<int>());
        Assert.Equal(1, batch["result"]!["failed"]!.GetValue<int>());
        Assert.Equal(10, batch["result"]!["items"]!.AsArray().Count);
        Assert.Equal("done", EndState(steps, 2));
        Assert.Equal(8, ProductSlugs().Count(s => s.StartsWith("importtee")));

        // One undo takes the batch and the category back.
        var token = AppFactory.Token(await admin.GetStringAsync("/admin/ai"))!;
        var r = await admin.PostAsync("/admin/ai?handler=Undo", new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = conv.ToString(), ["__RequestVerificationToken"] = token }));
        Assert.True(JsonNode.Parse(await r.Content.ReadAsStringAsync())!["ok"]!.GetValue<bool>());
        Assert.DoesNotContain(ProductSlugs(), s => s.StartsWith("importtee"));
        Assert.DoesNotContain("importtee", _app.Use(db => db.Database.SqlQueryRaw<string>("SELECT slug AS Value FROM p_shop_categories").ToList()));
    }

    [Fact]
    public async Task A_long_table_rides_only_with_its_beginning_and_the_rest_is_read_with_read_attachment()
    {
        var admin = await _app.AdminAsync();
        var csv = new StringBuilder("Nr;Name\n");
        for (var i = 1; i <= 1500; i++) csv.Append(i).Append(";Artikel Nummer ").Append(i).Append('\n');
        _app.Ai.Say(Act(("read_attachment", """{"file":"artikel.csv","from":1400,"lines":50}"""), ("read_attachment", """{"file":"nicht-da.csv"}""")));
        var (_, steps) = await SendAsync(admin, "Lies die Liste.", null, ("artikel.csv", Encoding.UTF8.GetBytes(csv.ToString())));

        var message = _app.Ai.Requests.First(q => q.Messages.Any(m => m.Content.Contains("artikel.csv"))).Messages.Last(m => m.Content.Contains("artikel.csv")).Content;
        Assert.Contains("read_attachment", message);                           // the model is told how to read on
        Assert.DoesNotContain("Artikel Nummer 1400", message);                   // … because the end is not in the message
        Assert.True(message.Length < AssistantFiles.MaxInlineTableChars + 2000);

        var acts = Steps(steps, 1, "action").Select(s => s["data"]!.AsObject()).ToList();
        Assert.True(acts[0]["ok"]!.GetValue<bool>(), acts[0].ToJsonString());
        var lines = acts[0]["result"]!["lines"]!.AsArray();
        Assert.Equal(50, lines.Count);
        Assert.Contains("Artikel Nummer 1399", lines[0]!.GetValue<string>()); // line 1400 of the text = data row 1399 (row 1 is the header)
        Assert.Equal(1501, acts[0]["result"]!["total"]!.GetValue<int>());
        Assert.False(acts[1]["ok"]!.GetValue<bool>());
        Assert.Contains("artikel.csv", acts[1]["result"]!.ToJsonString());      // names the attachments there are
    }

    [Fact]
    public async Task Operation_names_are_qualified_by_plugin_short_names_still_work_while_unique_and_an_undo_names_its_own_plugin()
    {
        await EnableShopAsync();
        using var scope = _app.App.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<PluginContributions>();
        Assert.True(ops.HasOperation("shop.product.create"));
        Assert.True(ops.HasOperation("product.create"));                       // what the cloud's older tools send
        Assert.False(ops.HasOperation("blog.product.create"));
        Assert.DoesNotContain(ops.PublicOperations, o => o.Spec?.Internal == true);
        Assert.Contains(ops.Catalog(), o => o.Name == "shop.order.list" && o.PersonalData && !o.Writes);
        Assert.DoesNotContain(ops.Catalog(), o => o.Name == "shop.product.remove");
        Assert.DoesNotContain(ops.AssistantOperations, o => o.Spec!.PersonalData);

        var made = (JsonObject)(await ops.RunOperationAsync(scope.ServiceProvider, "shop.category.create", new JsonObject { ["name"] = "Namensraum" }))!;
        Assert.Equal("applied", made["status"]!.GetValue<string>());
        Assert.Equal("shop.category.remove", made["undo"]!["name"]!.GetValue<string>());
        await ops.RunOperationAsync(scope.ServiceProvider, made["undo"]!["name"]!.GetValue<string>(), made["undo"]!["args"], allowInternal: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.RunOperationAsync(scope.ServiceProvider, "shop.category.remove", new JsonObject { ["id"] = 1 }));
    }
}
