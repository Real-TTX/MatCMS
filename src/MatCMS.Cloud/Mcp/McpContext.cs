using MatCMS.Cloud.Models;

namespace MatCMS.Cloud.Mcp;

/// <summary>
/// The authenticated operator key for the current MCP request. The <c>/mcp</c> auth middleware validates
/// the <c>Authorization: Bearer</c> key on every request and stashes the resolved <see cref="ApiKey"/> in
/// <c>HttpContext.Items</c>; tools read it here to scope and gate every action — exactly what
/// <c>ApiCallerAsync</c> does for the <c>/api/v1</c> handlers, so there is one idea of "who is calling".
/// </summary>
public class McpContext
{
    /// <summary>The <c>HttpContext.Items</c> slot the auth middleware writes the resolved key into.</summary>
    public const string ItemKey = "mcp.apikey";

    private readonly IHttpContextAccessor _http;
    public McpContext(IHttpContextAccessor http) => _http = http;

    /// <summary>The caller's key. Throws if a tool is somehow reached without the middleware — a bug, not a
    /// client error, since the middleware refuses an unauthenticated request before any tool runs.</summary>
    public ApiKey Key => _http.HttpContext?.Items[ItemKey] as ApiKey
        ?? throw new InvalidOperationException("MCP tool invoked without an authenticated operator key.");
}
