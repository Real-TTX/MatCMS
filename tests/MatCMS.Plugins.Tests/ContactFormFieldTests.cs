using System.Text.Json.Nodes;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The message of a contact form is a text AREA: in the form every site starts with, on a site that still has the old one-liner, and in a form
/// an AI writes through the cloud (whatever word it uses for the type).</summary>
public class ContactFormFieldTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public ContactFormFieldTests(AppFactory app) => _app = app;

    private async Task<FormElement> MessageAsync()
    {
        using var scope = _app.Services.CreateScope();
        var form = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Forms.AsNoTracking().FirstAsync(f => f.Slug == "kontakt");
        return FormDefinition.Flatten(FormDefinition.Parse(form.DefinitionJson)).Single(e => e.Id == "nachricht");
    }

    [Fact]
    public async Task A_new_site_asks_for_the_message_in_a_text_area_and_an_old_one_liner_is_fixed_once()
    {
        _ = _app.Services;                                                   // start the app: the seeder runs
        Assert.Equal("textarea", (await MessageAsync()).Type);
        Assert.Contains("<textarea", await _app.Browser().GetStringAsync("/kontakt"));

        // A site from before the fix: the one-liner, no marker — the next start fixes it, and only once.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var form = await db.Forms.FirstAsync(f => f.Slug == "kontakt");
            var els = FormDefinition.Parse(form.DefinitionJson);
            els.Single(e => e.Id == "nachricht").Type = "text";
            form.DefinitionJson = FormDefinition.Serialize(els);
            db.SiteSettings.RemoveRange(db.SiteSettings.Where(s => s.Key == "fix.contactMessageTextarea"));
            await db.SaveChangesAsync();
        }
        await DbSeeder.SeedAsync(_app.Services);
        Assert.Equal("textarea", (await MessageAsync()).Type);

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var form = await db.Forms.FirstAsync(f => f.Slug == "kontakt");
            var els = FormDefinition.Parse(form.DefinitionJson);
            els.Single(e => e.Id == "nachricht").Type = "text";             // the operator wants the one-liner back
            form.DefinitionJson = FormDefinition.Serialize(els);
            await db.SaveChangesAsync();
        }
        await DbSeeder.SeedAsync(_app.Services);
        Assert.Equal("text", (await MessageAsync()).Type);                     // and keeps it
    }

    [Fact]
    public void A_form_written_by_an_ai_gets_the_builders_types_and_ids()
    {
        var fields = JsonNode.Parse("""
            [{"type":"text","label":"Name"},{"type":"mail","label":"E-Mail"},{"type":"text","label":"Nachricht"},
             {"type":"long_text","label":"Details"},{"type":"heading","label":"Termin"},{"type":"text","label":"Name"}]
            """)!.AsArray();
        var result = JsonNode.Parse(ContentOpApplier.NormalizeFormFields(fields))!.AsArray();
        Assert.Equal(new[] { "text", "email", "textarea", "textarea", "title", "text" }, result.Select(f => f!["type"]!.GetValue<string>()));
        Assert.Equal(new[] { "name", "e_mail", "nachricht", "details", "termin", "name_2" }, result.Select(f => f!["id"]!.GetValue<string>()));
    }
}
