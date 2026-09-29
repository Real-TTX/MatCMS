using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The list behind the switcher in the top bar: the cloud itself, then every instance.
///
/// <para>The cloud is the FIRST entry, not a separate control, because "which site am I looking at"
/// and "am I looking at the control plane" are the same question — and answering them with two
/// different widgets means an operator has to know which one they are in before they can leave it.</para>
///
/// <para>Its own scoped service rather than a property on each page model: the switcher now sits in
/// the layout, so every admin page needs the list, and none of them should have to remember to load
/// it. Cached per request, since the layout may ask more than once while rendering.</para>
/// </summary>
public class ContextSwitcher
{
    private readonly AppDbContext _db;
    private readonly OperatorScope _scope;
    private List<Instance>? _cache;

    public ContextSwitcher(AppDbContext db, OperatorScope scope) { _db = db; _scope = scope; }

    /// <summary>
    /// Every instance worth switching to — for an Operator only the ones assigned to it.
    /// <para>Rejected ones stay out — they were refused, so they are not somewhere to go. Offline ones
    /// stay IN: offline is exactly when somebody goes looking, and a list that hides them answers the
    /// wrong question.</para>
    /// <para>The scope filter is load-bearing: this list sits in the LAYOUT, on every page, and it used to
    /// show an Operator every site's name, address and state (found while testing the Operator dashboard —
    /// an Operator with no instance at all saw the whole fleet here).</para>
    /// </summary>
    public async Task<List<Instance>> InstancesAsync(CancellationToken ct = default)
    {
        if (_cache is not null) return _cache;
        var q = _db.Instances.AsNoTracking().Where(i => i.Status != InstanceStatus.Rejected);
        if (!_scope.IsAdmin)
        {
            var allowed = await _scope.AllowedInstanceIdsAsync();
            q = q.Where(i => allowed.Contains(i.Id));
        }
        return _cache = await q.OrderBy(i => i.Name).ToListAsync(ct);
    }
}
