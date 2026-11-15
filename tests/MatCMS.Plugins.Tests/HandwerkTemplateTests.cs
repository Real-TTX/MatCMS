using MatCMS.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The starter template "MatHandwerk" (the look of the 2026-11 craftsmen's sites): its menu style is a template parameter the
/// header carries as data-menu — "Unterstrich" unless the site picks "Schlicht" or "Pillen" — and its contact pieces are parameters too.</summary>
public class HandwerkTemplateTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public HandwerkTemplateTests(AppFactory app) => _app = app;

    private async Task UseAsync(string values)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var t in await db.Templates.ToListAsync()) t.IsActive = t.Name == "MatHandwerk";
        (await db.Templates.SingleAsync(t => t.Name == "MatHandwerk")).ParamValuesJson = values;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_menu_style_is_a_parameter_and_underline_is_the_default()
    {
        await UseAsync("{}");
        var html = await _app.Browser().GetStringAsync("/");
        Assert.Contains("<header class=\"mx-header\" data-mx-header data-menu=\"Unterstrich\">", html);
        Assert.Contains("href=\"tel:\"", html);                              // no phone given: the link is there, empty, and hidden by the CSS
        Assert.Contains(".mx-act--phone[href=\"tel:\"]", html);

        await UseAsync("""{"menuStyle":"Pillen","phone":"07143 123"}""");
        html = await _app.Browser().GetStringAsync("/");
        Assert.Contains("data-menu=\"Pillen\">", html);
        Assert.Contains("href=\"tel:07143 123\"", html);
    }
}
