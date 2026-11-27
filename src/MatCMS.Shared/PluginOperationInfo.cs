namespace MatCMS.Shared;

/// <summary>
/// One operation a loaded plugin offers, as an outside caller lists it: the cloud's MCP (the site reports its catalogue on the heartbeat,
/// <see cref="HeartbeatRequest.PluginOperations"/>), the site's own MCP server, the assistant. The ONE shape of a plugin operation outside the
/// plugin — whoever lists plugin operations lists these, so an agent sees the same names and words everywhere.
/// </summary>
public sealed class PluginOperationInfo
{
    /// <summary>The qualified name <c>{plugin}.{operation}</c> (<c>shop.product.create</c>) — what a caller sends to run it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The plugin's key (<c>shop</c>).</summary>
    public string Plugin { get; set; } = "";

    /// <summary>The plugin's display name (<c>Shop</c>).</summary>
    public string PluginName { get; set; } = "";

    /// <summary>What it does, in words an AI reads. Empty for an operation its plugin did not describe.</summary>
    public string Description { get; set; } = "";

    /// <summary>The arguments as an example JSON object (<c>{"title": "…", "price": "12,90"}</c>).</summary>
    public string Arguments { get; set; } = "{}";

    /// <summary>It changes the site (a caller needs the right to write).</summary>
    public bool Writes { get; set; } = true;

    /// <summary>It reads or changes customers' / members' data (orders, bookings, member lists): a caller needs the stronger right, and its
    /// answer never goes to an outside AI model on the site's own initiative.</summary>
    public bool PersonalData { get; set; }

    /// <summary>A fingerprint of a catalogue (names, flags, descriptions, arguments): the site sends the catalogue again only when it changed.</summary>
    public static string Hash(IEnumerable<PluginOperationInfo> ops)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var o in ops) sb.Append(o.Name).Append('\u0001').Append(o.Writes).Append(o.PersonalData).Append(o.Description).Append('\u0001').Append(o.Arguments).Append('\u0002');
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
