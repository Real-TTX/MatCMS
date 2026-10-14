using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace MatCMS.Services;

/// <summary>
/// Draws one of the site's own Razor partials (a content block such as <c>Blocks/_Gallery</c>) to a string, outside any
/// page — for code that is not a view but needs what a view makes: a plugin that shows the CMS's gallery inside its own page.
/// </summary>
public sealed class RazorPartialRenderer
{
    private readonly IRazorViewEngine _engine;
    private readonly ITempDataProvider _tempData;
    private readonly IServiceProvider _services;
    private readonly IHttpContextAccessor _http;

    /// <summary>Creates the renderer.</summary>
    public RazorPartialRenderer(IRazorViewEngine engine, ITempDataProvider tempData, IServiceProvider services, IHttpContextAccessor http)
    {
        _engine = engine; _tempData = tempData; _services = services; _http = http;
    }

    /// <summary>Renders the partial <paramref name="name"/> (for example <c>Blocks/_Gallery</c>) with <paramref name="model"/>.</summary>
    public async Task<string> RenderAsync(string name, object model, CancellationToken ct = default)
    {
        var http = _http.HttpContext ?? new DefaultHttpContext { RequestServices = _services };
        var actionContext = new ActionContext(http, http.GetRouteData() ?? new RouteData(), new ActionDescriptor());

        // A partial is found by its full path: the name the block registry keeps ("Blocks/_Gallery") lives under Pages/Shared.
        var path = name.StartsWith('/') ? name : "/Pages/Shared/" + name + ".cshtml";
        var result = _engine.GetView(executingFilePath: null, viewPath: path, isMainPage: false);
        if (!result.Success) result = _engine.FindView(actionContext, name, isMainPage: false);
        if (!result.Success) throw new InvalidOperationException($"Die Ansicht '{name}' wurde nicht gefunden.");

        await using var sw = new StringWriter();
        var viewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var viewContext = new ViewContext(actionContext, result.View, viewData, new TempDataDictionary(http, _tempData), sw, new HtmlHelperOptions());
        await result.View.RenderAsync(viewContext);
        return sw.ToString();
    }
}
