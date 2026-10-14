using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Services.Plugins;

/// <summary>The host raising its own events (<see cref="MatCMS.Plugin.HostEvents"/>) for plugins to listen to. Never throws and never
/// holds up the caller for long: whatever a plugin does about an event, the form is stored and the page is saved already.</summary>
public static class PluginHostEvents
{
    /// <summary>Raises <paramref name="name"/> with <paramref name="payload"/> in the language of the current request.</summary>
    public static async Task RaiseAsync(IServiceProvider services, string name, JsonNode? payload, CancellationToken ct = default)
    {
        try
        {
            var registry = services.GetService(typeof(PluginRegistry)) as PluginRegistry;
            if (registry is null || registry.ListenersOf(name).Count == 0) return;
            var bus = new PluginEventBus(registry, services, new PluginUser(), PluginRequestContext.CurrentLocale());
            await bus.EmitAsync(name, payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            (services.GetService(typeof(PluginRegistry)) as PluginRegistry)?.AddLog(null, $"Ereignis '{name}': {ex.Message}");
        }
    }
}
