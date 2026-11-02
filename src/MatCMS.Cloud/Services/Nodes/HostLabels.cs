using System.Text.Encodings.Web;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Html;

namespace MatCMS.Cloud.Services.Nodes;

/// <summary>
/// How a host is NAMED wherever one is shown. A node has its name; the cloud's own daemon ("Dieser Host") is not a row,
/// so its name is the setting <see cref="SettingKeys.HostingLocalName"/> — and because "which one is the cloud itself"
/// matters more than any name, a named local host always carries the tag "Dieser Host" behind it (a badge in HTML,
/// in brackets in plain text). Unnamed, it is simply "Dieser Host", as it always was.
/// </summary>
public sealed class HostLabels
{
    private readonly CloudContext _cloud;
    private readonly Localizer _t;

    public HostLabels(CloudContext cloud, Localizer t) { _cloud = cloud; _t = t; }

    /// <summary>The operator's name for the cloud's own host, or null when none was given.</summary>
    public string? LocalName => NodeService.LocalName(_cloud);

    /// <summary>The tag that marks the cloud's own host ("Dieser Host").</summary>
    public string LocalTag => _t["nodes.thisHost"];

    /// <summary>The bare name: a node's name, the local name, or "Dieser Host".</summary>
    public string Name(int? nodeId, string? nodeName) => nodeId is null ? LocalName ?? LocalTag : nodeName ?? "";
    public string Name(Node? n) => n is null ? Name(null, null) : n.Name;

    /// <summary>For places that hold only text (a drop-down option, a log line): "cloud2 (Dieser Host)".</summary>
    public string Text(int? nodeId, string? nodeName) =>
        nodeId is not null ? nodeName ?? "" : LocalName is { } l ? $"{l} ({LocalTag})" : LocalTag;
    public string Text(Node? n) => n is null ? Text(null, null) : n.Name;

    /// <summary>The name with the "Dieser Host" badge behind it when the local host has a name of its own.</summary>
    public IHtmlContent Html(int? nodeId, string? nodeName)
    {
        var name = HtmlEncoder.Default.Encode(Name(nodeId, nodeName));
        if (nodeId is not null || LocalName is null) return new HtmlString(name);
        return new HtmlString($"{name} <span class=\"badge badge-off\">{HtmlEncoder.Default.Encode(LocalTag)}</span>");
    }
    public IHtmlContent Html(Node? n) => n is null ? Html(null, null) : Html(n.Id, n.Name);
}
