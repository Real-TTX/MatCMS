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
    private List<Instance>? _cache;

    public ContextSwitcher(AppDbContext db) => _db = db;

    /// <summary>
    /// Every instance worth switching to.
    /// <para>Rejected ones stay out — they were refused, so they are not somewhere to go. Offline ones
    /// stay IN: offline is exactly when somebody goes looking, and a list that hides them answers the
    /// wrong question.</para>
    /// </summary>
    public async Task<List<Instance>> InstancesAsync(CancellationToken ct = default) =>
        _cache ??= await _db.Instances.AsNoTracking()
            .Where(i => i.Status != InstanceStatus.Rejected)
            .OrderBy(i => i.Name)
            .ToListAsync(ct);
}
