using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Plugins;

/// <summary>
/// The one place that reaches into EF Core's internals, and why.
/// <para>EF keeps a STATIC cache of service providers (<c>ServiceProviderCache</c>). A provider's
/// <c>DbSetFinder</c> is keyed by the context type, so a plugin's <c>DbContext</c> type ends up in a static
/// the plugin can never clear — and the type holds the plugin's load context alive for the life of the
/// process. Measured, not guessed: with the cache cleared the context is collected after unloading, without it
/// it never is (see docs/plugin-system-v2.md, "Spike-Ergebnis").</para>
/// <para>This is reflection into a type EF does not promise, so it is isolated here, guarded, and covered by a
/// test that unloads a real plugin DLL with an EF context. If a future EF renames the field the method returns
/// <c>false</c> and the host reports "restart needed" for that plugin instead of pretending it was freed.</para>
/// </summary>
internal static class EfInternals
{
    /// <summary>Empties EF's static service-provider cache. True when it could be done.</summary>
    public static bool ClearServiceProviderCache()
    {
        try
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var type = typeof(DbContext).Assembly.GetType("Microsoft.EntityFrameworkCore.Internal.ServiceProviderCache");
            if (type is null) return false;
            var instance = type.GetField("Instance", F)?.GetValue(null) ?? type.GetProperty("Instance", F)?.GetValue(null);
            if (instance is null) return false;
            var cleared = false;
            foreach (var field in type.GetFields(F).Where(f => !f.IsStatic))
            {
                var value = field.GetValue(instance);
                var clear = value?.GetType().GetMethod("Clear", Type.EmptyTypes);
                if (clear is null) continue;
                clear.Invoke(value, null);
                cleared = true;
            }
            return cleared;
        }
        catch
        {
            return false;
        }
    }
}
