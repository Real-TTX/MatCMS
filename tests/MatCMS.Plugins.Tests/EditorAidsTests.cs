using System.Text.Json.Nodes;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>What the script editor offers beyond typing: "Prüfen" (compile without running) and the completion catalogue.</summary>
public class EditorAidsTests
{
    [Fact]
    public void A_correct_script_has_no_findings_and_a_wrong_one_names_the_line_and_the_problem()
    {
        Assert.Empty(PluginRunner.Check("Plugin.Head(\"<meta name=x>\");", null, null));

        var found = PluginRunner.Check("var a = 1;\nPlugin.Nope(\"x\");\nvar b = 2;", null, null);
        var error = Assert.Single(found, d => d.Severity == "error");
        Assert.Equal(("plugin.csx", 2, "CS1061"), (error.File, error.Line, error.Id));
        Assert.Contains("Nope", error.Message);
    }

    [Fact]
    public void Checking_never_runs_the_script()
    {
        var marker = Path.Combine(Path.GetTempPath(), "matcms-check-" + Guid.NewGuid().ToString("N"));
        PluginRunner.Check($"System.IO.File.WriteAllText(@\"{marker}\", \"ran\");", null, null);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void An_error_in_a_loaded_file_is_reported_in_that_file_and_include_folders_do_not_shift_the_lines_of_the_entry()
    {
        var files = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["lib/hilfe.csx"] = "int Zwei() => 2;\nint Drei() => drei;\n"      // line 2 is wrong
        });
        var mapping = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["lib"] = "include" });
        var found = PluginRunner.Check("using System;\nvar x = Zwei();\nvar y = Unbekannt;\n", files, mapping);

        var inFile = Assert.Single(found, d => d.File.EndsWith("hilfe.csx"));
        Assert.Equal(2, inFile.Line);
        var inEntry = Assert.Single(found, d => d.File == "plugin.csx" && d.Severity == "error");
        Assert.Equal(3, inEntry.Line);                                  // the author's line 3, not shifted by the injected #load
    }

    [Fact]
    public void The_completion_catalogue_follows_a_chain_of_the_plugin_API_and_carries_the_documentation()
    {
        var catalog = PluginApiCatalog.Build();
        var roots = catalog["roots"]!.AsObject();
        Assert.Equal("IPluginBuilder", roots["Plugin"]!.GetValue<string>());
        Assert.Equal("IPluginContext", roots["ctx"]!.GetValue<string>());
        Assert.NotNull(roots["Ui"]);

        JsonArray Members(string type) => catalog["types"]![type]!.AsArray();
        string? Returns(string type, string member) => Members(type).FirstOrDefault(m => m!["name"]!.GetValue<string>() == member)?["returns"]?.GetValue<string>();

        // Plugin.Admin.Section(...).Page(...).Badge(...) — every link resolves.
        Assert.Equal("IAdminBuilder", Returns("IPluginBuilder", "Admin"));
        Assert.Equal("IAdminSection", Returns("IAdminBuilder", "Section"));
        Assert.Equal("IAdminSection", Returns("IAdminSection", "Page"));       // the sync extension overloads are part of the type
        Assert.Equal("IAdminSection", Returns("IAdminSection", "Badge"));
        Assert.Equal("IDataTable", Returns("IPluginData", "Table"));
        Assert.Equal("UiCard", Returns("Ui", "Card"));
        Assert.Contains(Members("UiTone"), m => m!["name"]!.GetValue<string>() == "Success");
        Assert.Contains(Members("JobSchedule"), m => m!["name"]!.GetValue<string>() == "Daily");

        // a documentation line from the API's own XML comments
        var route = Members("IPluginBuilder").First(m => m!["name"]!.GetValue<string>() == "Route")!;
        Assert.Contains("public page", route["doc"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }
}
