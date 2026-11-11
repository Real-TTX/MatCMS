using System.Text.Json.Nodes;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Profiles inherit from profiles ("Erbt von", <see cref="Profile.ParentId"/>). The rule in one sentence: <b>a child adds
/// and overrides, it never takes away</b> — everything a parent rolls out reaches the child's instances too, and what the
/// child carries itself wins per identity (setting key, user name, plugin key, component type, template name, mail key).
/// <list type="bullet">
/// <item>A group switch (settings, SMTP, users, plugins, …) is ON when it is on anywhere in the chain. Each profile's rows
/// count only while ITS OWN switch for that group is on — the rule "a group nobody switched on rolls out nothing" holds per
/// layer, so a parent's stored-but-switched-off SMTP never leaks into a child.</item>
/// <item>Mail: the NEAREST profile with the mail group on decides the source (cloud relay / global SMTP / own SMTP) and the
/// "site name as sender" switch. Own SMTP on top of SMTP values from above only overrides what it fills in — a child that
/// needs only another sender address enters only that address.</item>
/// <item>Modes, the template to activate, the native-plugin decision, AI instruction and budget: the nearest profile that
/// says something. AI action guides merge per action. Remove-default-admin and backup-before-AI are on when on anywhere.</item>
/// <item>Not inherited: name, join code, approval, default flag, notifications and auto-update — they describe the profile
/// itself and its own instances. Backup quota and retention fall back to the parent before the default profile.</item>
/// </list>
/// <para>The chain is resolved in memory over the tiny set of profiles, root first. A broken link — a missing parent, a
/// loop, more than <see cref="MaxDepth"/> profiles — ends the chain there: an instance's configuration is never refused
/// because of it (and <see cref="ParentProblem"/> stops anybody from making one).</para>
/// </summary>
public static class ProfileInheritance
{
    /// <summary>The longest chain, the profile itself included.</summary>
    public const int MaxDepth = 5;

    /// <summary>The chain of <paramref name="self"/>: root first, the profile itself last.</summary>
    public static List<Profile> Chain(Profile self, IReadOnlyDictionary<int, Profile> all)
    {
        var chain = new List<Profile> { self };
        var seen = new HashSet<int> { self.Id };
        var next = self.ParentId;
        while (next is int pid && chain.Count < MaxDepth && all.TryGetValue(pid, out var parent) && seen.Add(pid))
        {
            chain.Add(parent);
            next = parent.ParentId;
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>The chain read from the database (ancestors detached; <paramref name="self"/> as passed, so a caller's
    /// unsaved edits count).</summary>
    public static async Task<List<Profile>> ChainAsync(AppDbContext db, Profile self, CancellationToken ct = default)
    {
        if (self.ParentId is null) return new List<Profile> { self };
        var all = await db.Profiles.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        return Chain(self, all);
    }

    /// <summary>What the instances of <paramref name="self"/> get, as a profile (see <see cref="Effective"/>).</summary>
    public static async Task<Profile> EffectiveAsync(AppDbContext db, Profile self, CancellationToken ct = default) =>
        Effective(await ChainAsync(db, self, ct));

    /// <summary>
    /// The profile at the end of <paramref name="chain"/> with every inherited decision folded in — a DETACHED copy (never save
    /// it). Without a parent it is the profile itself, so a cloud that uses no inheritance behaves exactly as before.
    /// </summary>
    public static Profile Effective(IReadOnlyList<Profile> chain)
    {
        var self = chain[^1];
        if (chain.Count == 1) return self;

        Profile? Nearest(Func<Profile, bool> says)
        {
            for (var i = chain.Count - 1; i >= 0; i--)
                if (says(chain[i])) return chain[i];
            return null;
        }

        var e = self.Clone();
        e.SyncSettings = chain.Any(p => p.SyncSettings);
        e.SyncTranslation = chain.Any(p => p.SyncTranslation);
        e.SyncBackup = chain.Any(p => p.SyncBackup);
        e.SyncUsers = chain.Any(p => p.SyncUsers);
        e.SyncPlugins = chain.Any(p => p.SyncPlugins);
        e.SyncComponents = chain.Any(p => p.SyncComponents);
        e.SyncTemplates = chain.Any(p => p.SyncTemplates);
        e.SyncMailTemplates = chain.Any(p => p.SyncMailTemplates);

        var mail = Nearest(p => p.SyncSmtp);
        e.SyncSmtp = mail is not null;
        if (mail is not null)
        {
            e.MailSource = mail.MailSource;
            e.MailFromSiteName = mail.MailFromSiteName;
        }

        // A mode belongs to its payload: the nearest profile that rolls the payload out says how.
        e.SettingsMode = (Nearest(p => p.SyncSettings || p.SyncSmtp || p.SyncTranslation || p.SyncBackup) ?? self).SettingsMode;
        e.UsersMode = (Nearest(p => p.SyncUsers) ?? self).UsersMode;
        e.PluginsMode = (Nearest(p => p.SyncPlugins) ?? self).PluginsMode;
        e.ComponentsMode = (Nearest(p => p.SyncComponents) ?? self).ComponentsMode;
        e.TemplatesMode = (Nearest(p => p.SyncTemplates) ?? self).TemplatesMode;
        e.MailTemplatesMode = (Nearest(p => p.SyncMailTemplates) ?? self).MailTemplatesMode;

        e.ActivateTemplateName = Nearest(p => p.SyncTemplates && !string.IsNullOrWhiteSpace(p.ActivateTemplateName))?.ActivateTemplateName;
        e.RemoveDefaultAdmin = chain.Any(p => p.RemoveDefaultAdmin);
        e.NativePlugins = Nearest(p => NativePluginsPolicy.Normalize(p.NativePlugins).Length > 0)?.NativePlugins ?? "";
        e.MatcmsStore = Nearest(p => NativePluginsPolicy.Normalize(p.MatcmsStore).Length > 0)?.MatcmsStore ?? "";

        e.SyncAi = chain.Any(p => p.SyncAi);
        e.AiInstruction = Nearest(p => p.SyncAi && !string.IsNullOrWhiteSpace(p.AiInstruction))?.AiInstruction;
        e.AiMonthlyTokenBudget = Nearest(p => p.SyncAi && p.AiMonthlyTokenBudget is > 0)?.AiMonthlyTokenBudget;
        e.AiActionGuidesJson = MergeGuides(chain.Where(p => p.SyncAi).Select(p => p.AiActionGuidesJson));
        e.BackupBeforeAiChange = chain.Any(p => p.BackupBeforeAiChange);

        // What the CLOUD grants (not rolled out): empty on the child = the parent's, then the default profile's as before.
        e.BackupQuotaGb = Nearest(p => p.BackupQuotaGb is > 0)?.BackupQuotaGb;
        e.BackupKeepDaily = Nearest(p => p.BackupKeepDaily is >= 0)?.BackupKeepDaily;
        e.BackupKeepWeekly = Nearest(p => p.BackupKeepWeekly is >= 0)?.BackupKeepWeekly;
        e.BackupKeepMonthly = Nearest(p => p.BackupKeepMonthly is >= 0)?.BackupKeepMonthly;
        e.BackupMaxCount = Nearest(p => p.BackupMaxCount is >= 0)?.BackupMaxCount;
        return e;
    }

    /// <summary>The per-action AI guides of every layer, the nearer profile winning per action. Unreadable JSON is skipped
    /// rather than breaking the whole configuration.</summary>
    private static string? MergeGuides(IEnumerable<string?> layers)
    {
        var merged = new JsonObject();
        foreach (var json in layers)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            JsonObject? map;
            try { map = JsonNode.Parse(json) as JsonObject; } catch (System.Text.Json.JsonException) { continue; }
            if (map is null) continue;
            foreach (var (k, v) in map) merged[k] = v?.DeepClone();
        }
        return merged.Count == 0 ? null : merged.ToJsonString();
    }

    /// <summary>Every profile below <paramref name="id"/> (children, their children, …).</summary>
    public static HashSet<int> Descendants(int id, IEnumerable<Profile> all)
    {
        var list = all.ToList();
        var found = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(id);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in list.Where(p => p.ParentId == current))
                if (child.Id != id && found.Add(child.Id)) queue.Enqueue(child.Id);
        }
        return found;
    }

    /// <summary>Why <paramref name="parentId"/> cannot become the parent of <paramref name="id"/> — itself, a profile below it
    /// (a loop), a missing one, or a chain longer than <see cref="MaxDepth"/>. Null = fine (and null parent is always fine).</summary>
    public static string? ParentProblem(int id, int? parentId, IReadOnlyDictionary<int, Profile> all)
    {
        if (parentId is null) return null;
        if (parentId == id) return "Ein Profil kann nicht von sich selbst erben.";
        if (!all.TryGetValue(parentId.Value, out var parent)) return "Das Elternprofil gibt es nicht.";
        if (Descendants(id, all.Values).Contains(parentId.Value))
            return $"„{parent.Name}“ erbt selbst von diesem Profil – das wäre ein Kreis.";
        var above = Chain(parent, all).Count;
        var below = Height(id, all.Values);
        if (above + below > MaxDepth)
            return $"Zu viele Stufen: höchstens {MaxDepth} Profile übereinander.";
        return null;
    }

    /// <summary>Levels from <paramref name="id"/> down to its deepest descendant, itself counted.</summary>
    private static int Height(int id, IEnumerable<Profile> all)
    {
        var list = all.ToList();
        int Walk(int at, int depth) =>
            depth > MaxDepth ? depth : 1 + list.Where(p => p.ParentId == at && p.Id != id).Select(c => Walk(c.Id, depth + 1)).DefaultIfEmpty(0).Max();
        return Walk(id, 1);
    }

    /// <summary>The sender NAME an instance's mails carry when the mail group is in effect, or null for "whatever the mail
    /// configuration says". The operator's name for this one instance wins, then — with the profile's switch — the site's
    /// own name. Line breaks are removed and the length capped: it ends up in a mail header.</summary>
    public static string? SenderName(Instance instance, Profile effective, string? reportedSiteName)
    {
        static string? Clean(string? s)
        {
            var t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length == 0 ? null : t.Length > 100 ? t[..100] : t;
        }
        if (Clean(instance.MailFromName) is { } own) return own;
        if (!effective.SyncSmtp || !effective.MailFromSiteName) return null;
        return Clean(reportedSiteName) ?? Clean(instance.Name);
    }
}
