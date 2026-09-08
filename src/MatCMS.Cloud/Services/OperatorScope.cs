using System.Security.Claims;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Resolves the logged-in cloud user and — for the <see cref="User.RoleOperator"/> role — the set of
/// instances they are allowed to see and act on. Admins are unscoped (they reach everything).
/// <para>An Operator with no assigned instances reaches nothing, deliberately (mirrors a scoped API
/// key). Every instance list query and every per-instance action gate goes through here, so the rule
/// lives in one place.</para>
/// </summary>
public class OperatorScope
{
    private readonly IHttpContextAccessor _http;
    private readonly AppDbContext _db;
    private HashSet<int>? _allowed;

    public OperatorScope(IHttpContextAccessor http, AppDbContext db)
    {
        _http = http;
        _db = db;
    }

    private ClaimsPrincipal? Principal => _http.HttpContext?.User;

    public bool IsAdmin => Principal?.IsInRole(User.RoleAdmin) == true;
    public bool IsOperator => Principal?.IsInRole(User.RoleOperator) == true;

    public int? UserId =>
        int.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>The instance ids an Operator may touch. Empty for an Operator with no assignments.
    /// Admins never call this (they are unscoped); it returns empty for them defensively.</summary>
    public async Task<HashSet<int>> AllowedInstanceIdsAsync()
    {
        if (IsAdmin) return _allowed ??= new();   // unused for admins
        var uid = UserId;
        if (uid is null) return _allowed ??= new();
        return _allowed ??= (await _db.UserInstances.AsNoTracking()
            .Where(x => x.UserId == uid.Value)
            .Select(x => x.InstanceId)
            .ToListAsync()).ToHashSet();
    }

    /// <summary>May the current user act on this instance id? Admins: always. Operators: only if
    /// assigned. Use this as the single choke point in every instance page/handler.</summary>
    public async Task<bool> CanAccessInstanceAsync(int instanceId)
    {
        if (IsAdmin) return true;
        if (!IsOperator) return false;
        return (await AllowedInstanceIdsAsync()).Contains(instanceId);
    }
}
