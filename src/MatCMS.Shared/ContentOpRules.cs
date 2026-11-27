namespace MatCMS.Shared;

/// <summary>
/// What the cloud's content-op channel (MCP <c>set_setting</c>, <c>get_content_op</c>) may do, decided in ONE place for
/// both sides: the cloud refuses early with a clear message, and the site refuses again in <c>ContentOpApplier</c>,
/// because the site is the boundary (an older cloud, or one that forgot, must not get past it).
/// </summary>
public static class ContentOpRules
{
    private static readonly string[] RefusedPrefixes = ["code.", "security.", "sso.", "smtp."];

    private static readonly string[] RefusedKeys =
    [
        "members.selfRegistration", // who may make an account
        "site.canonicalUrl",        // the origin of every member mail link (reset, confirmation, invitation)
        "site.behindHttpsProxy",
        "site.domainRoutes",        // can send a part of the site to another domain
        "mail.transport",           // with smtp.*: where ALL mail goes, reset mails included
        "ContactRecipient",         // where form submissions go
    ];

    /// <summary>
    /// Setting keys a content op must never write. An AI client drives that channel and reads untrusted text while it does
    /// (page content, orders, visitor messages), so the keys where a steered model does the most damage are left to a
    /// person: the code fields go into every page unfiltered (a script on a live site); the security, sign-in and mail keys
    /// decide who gets in and where a password-reset link or a visitor's message ends up. They stay settable in the site's own
    /// admin, through the profile rollout and by a restore. The cloud's own address push uses the op "site.address".
    /// </summary>
    public static bool IsRefusedSetting(string key)
    {
        var k = (key ?? "").Trim();
        return RefusedPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || RefusedKeys.Any(r => r.Equals(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Op kinds whose RESULT may be read with read scope alone: the content of pages and the cleanup analysis, which any
    /// key that may read the site sees anyway. Every other result needs the restore right — that is the right its op
    /// needed to be queued, and some of them (<c>order.list</c>) carry customers' personal data.
    /// </summary>
    public static bool ResultNeedsOnlyReadScope(string kind) => kind is "pages.list" or "page.read" or "cleanup.analyze";
}
