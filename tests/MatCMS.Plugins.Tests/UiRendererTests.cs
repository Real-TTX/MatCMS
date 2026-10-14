using MatCMS.Shared.Web;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The component renderer: what a plugin can rely on whatever it puts in a tree. Text is encoded, a link cannot
/// carry script, a form carries the antiforgery token, and the same tree draws in the back office and on the site.
/// </summary>
public class UiRendererTests
{
    private const string Token = "<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"abc\" />";

    private static string Admin(UiNode n) => UiRenderer.Render(n, new UiRenderOptions { Antiforgery = Token, T = k => "[" + k + "]" });
    private static string Public(UiNode n) => UiRenderer.Render(n, new UiRenderOptions { Target = UiTarget.Public, T = k => "[" + k + "]" });

    [Fact]
    public void Text_is_encoded_everywhere_a_plugin_can_put_text()
    {
        const string evil = "<script>alert(1)</script>";
        var tree = Ui.Stack(
            Ui.Heading(evil), Ui.Text(evil), Ui.Muted(evil), Ui.Alert(evil), Ui.Badge(evil), Ui.Empty(evil),
            Ui.Card(evil, Ui.Text("x")), Ui.Button(evil, "/x"), Ui.Link(evil, "/x"),
            Ui.KeyValue((evil, evil)), Ui.Stats(Ui.Stat(evil, evil, evil)),
            Ui.Input("n", evil, evil), Ui.TextArea("t", evil, evil), Ui.Select("s", evil, "a", Ui.Option("a", evil)),
            Ui.Checkbox("c", evil), Ui.Table(new[] { Ui.Column(evil) }, new[] { Ui.Row(evil) }));
        foreach (var html in new[] { Admin(tree), Public(tree) })
        {
            Assert.DoesNotContain("<script>", html);
            Assert.Contains("&lt;script&gt;", html);
        }
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html;base64,AAAA")]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example")]
    [InlineData("vbscript:x")]
    public void A_link_or_picture_cannot_carry_script_or_leave_the_site_by_a_protocol_trick(string href)
    {
        Assert.Equal("#", UiRenderer.SafeUrl(href));
        var html = Admin(Ui.Stack(Ui.Link("x", href), Ui.Button("y", href), Ui.Img(href), Ui.Tabs(Ui.Tab("t", href))));
        Assert.DoesNotContain(href, html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/shop/1", "/shop/1")]
    [InlineData("https://example.org/a?b=1", "https://example.org/a?b=1")]
    [InlineData("mailto:a@b.de", "mailto:a@b.de")]
    [InlineData("?page=2", "?page=2")]
    [InlineData("#top", "#top")]
    [InlineData("products/3", "products/3")]
    public void Ordinary_targets_pass(string href, string expected) => Assert.Equal(expected, UiRenderer.SafeUrl(href));

    [Fact]
    public void An_admin_form_carries_the_antiforgery_token_and_its_hidden_fields_and_a_public_form_does_not()
    {
        var form = Ui.Form(Ui.Input("title", "Titel"), Ui.Submit("Speichern", name: "action", value: "save")) with
        {
            Hidden = new Dictionary<string, string> { ["id"] = "7" }
        };
        var admin = Admin(form);
        Assert.Contains("__RequestVerificationToken", admin);
        Assert.Contains("name=\"id\" value=\"7\"", admin);
        Assert.Contains("name=\"action\" value=\"save\"", admin);
        Assert.DoesNotContain("__RequestVerificationToken", Public(form));
    }

    [Fact]
    public void A_row_action_is_its_own_post_form_with_the_token_and_asks_first_when_told_to()
    {
        var html = Admin(Ui.Action("Löschen", "delete", "5", UiButtonKind.Danger, confirm: "Wirklich?"));
        Assert.Contains("method=\"post\"", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.Contains("name=\"action\" value=\"delete\"", html);
        Assert.Contains("name=\"id\" value=\"5\"", html);
        Assert.Contains("data-confirm=\"Wirklich?\"", html);
        Assert.Contains("data-confirm-danger", html);
    }

    [Fact]
    public void A_password_field_never_sends_its_value_back_to_the_browser()
    {
        var html = Admin(Ui.Input("key", "Key", "geheim", UiInputType.Password));
        Assert.DoesNotContain("geheim", html);
        Assert.Contains("type=\"password\"", html);
    }

    [Fact]
    public void A_checkbox_posts_true_and_keeps_its_state()
    {
        var on = Admin(Ui.Checkbox("pub", "Veröffentlicht", true));
        var off = Admin(Ui.Checkbox("pub", "Veröffentlicht"));
        Assert.Contains("value=\"true\"", on);
        Assert.Contains(" checked", on);
        Assert.DoesNotContain(" checked", off);
    }

    [Fact]
    public void A_table_with_search_or_paging_joins_the_admin_list_script_and_an_empty_table_keeps_its_header()
    {
        var t = Ui.Table(new[] { Ui.Column("Name") }, new[] { Ui.RowFor("tasse mug", "Tasse") }, searchPlaceholder: "Suchen", pageSize: 25);
        var html = Admin(t);
        Assert.Contains("data-list", html);
        Assert.Contains("data-list-search", html);
        Assert.Contains("data-list-page-size=\"25\"", html);
        Assert.Contains("data-search=\"tasse mug\"", html);

        var empty = Admin(Ui.Table(new[] { Ui.Column("Name") }, Array.Empty<UiTableRow>(), emptyText: "Nichts da"));
        Assert.Contains("<th>Name</th>", empty);
        Assert.Contains("list-empty-row", empty);
        Assert.Contains("Nichts da", empty);
    }

    [Fact]
    public void The_same_tree_draws_with_the_admin_classes_in_the_back_office_and_the_sites_classes_on_the_public_page()
    {
        var tree = Ui.Card("Titel", Ui.Alert("hallo", UiTone.Success), Ui.Button("Los", "/x", UiButtonKind.Ghost), Ui.Input("a", "A"));
        var admin = Admin(tree);
        var pub = Public(tree);
        Assert.Contains("class=\"card\"", admin);
        Assert.Contains("btn-ghost", admin);
        Assert.Contains("class=\"field\"", admin);
        Assert.Contains("class=\"ui-card\"", pub);
        Assert.Contains("btn-outline", pub);
        Assert.Contains("class=\"form-field\"", pub);
        Assert.DoesNotContain("class=\"card\"", pub);
    }

    [Fact]
    public void Data_hooks_of_a_button_are_limited_to_well_formed_names()
    {
        var b = Ui.Button("x", "/y") with { Data = new Dictionary<string, string> { ["ai-seo"] = "1", ["onclick"] = "evil()", ["bad\" onfocus=\"x"] = "1" } };
        var html = Admin(b);
        Assert.Contains("data-ai-seo=\"1\"", html);
        Assert.Contains("data-onclick=\"evil()\"", html.Replace("&#39;", "'")); // prefixed, so it is not an event handler
        Assert.DoesNotContain("onfocus", html);
    }

    [Fact]
    public void Every_component_the_library_offers_renders_in_both_targets()
    {
        UiNode[] all =
        {
            Ui.Stack(Ui.Text("a")), Ui.Inline(Ui.Text("a")), Ui.Grid(200, Ui.Text("a")), Ui.Card("t", Ui.Text("a")),
            Ui.PageHead("sub", "title", Ui.Button("b", "/")), Ui.Heading("h"), Ui.Muted("m"), Ui.Link("l", "/"), Ui.Img("/a.png"),
            Ui.Html("<b>x</b>"), Ui.Chips(new[] { "a", "b" }), Ui.Alert("a"), Ui.Badge("b"), Ui.Empty("e"), Ui.KeyValue(("a", "b")), Ui.Stats(Ui.Stat("a", "1")),
            Ui.Button("b", "/"), Ui.Action("a", "x"), Ui.Dialog("d", "t", Ui.Text("x")),
            Ui.Form(Ui.Hidden("a", "b"), Ui.FormActions(Ui.Submit("s"))),
            Ui.Input("i", "I"), Ui.TextArea("t", "T"), Ui.Select("s", "S", "a", Ui.Option("a", "A")), Ui.Checkbox("c", "C"),
            Ui.Image("im", "Im"), Ui.MediaList("ml", "Ml"), Ui.MediaList("fl", "Fl", files: true), Ui.RichText("rt", "Rt"), Ui.Tags("tg", "Tg", "a, b", new[] { "c" }),
            Ui.Table(new[] { Ui.Column("c") }, new[] { Ui.Row("x", Ui.Badge("y")) }), Ui.Tabs(Ui.Tab("t", "/")), Ui.Pager(1, 3, "?p={0}")
        };
        foreach (var n in all)
        {
            Assert.NotEmpty(Admin(n));
            // A pager of one page and a dialog have nothing to say on the site, but must not throw.
            Public(n);
        }
        // The list above is the whole model: a component added to the library without a renderer case fails here.
        var known = typeof(UiNode).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(UiNode)) && !t.IsAbstract).Select(t => t.Name).ToHashSet();
        var covered = all.SelectMany(UiTree.Descendants).Select(n => n.GetType().Name).ToHashSet();
        covered.UnionWith(new[] { nameof(UiHidden) });
        Assert.Empty(known.Except(covered));
    }

    [Fact]
    public void The_lint_flags_raw_html_that_sidesteps_the_library_and_leaves_components_alone()
    {
        Assert.Empty(UiTree.Lint(Ui.Card("x", Ui.Text("ok"), Ui.Html("<b>fett</b>"))));
        Assert.NotEmpty(UiTree.Lint(Ui.Html("<style>.a{color:red}</style>")));
        Assert.NotEmpty(UiTree.Lint(Ui.Html("<div style=\"background:#fff\">x</div>")));
        Assert.NotEmpty(UiTree.Lint(Ui.Html("<button onclick=\"confirm('x')\">x</button>")));
    }
}
