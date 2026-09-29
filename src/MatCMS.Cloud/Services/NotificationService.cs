using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>The events a notification can be subscribed to — the columns of the matrix.</summary>
public static class NotifyEvents
{
    public const string Offline = "offline";
    public const string Update = "update";
    public const string UpdateFailed = "updateFailed";
    public const string Migration = "migration";
    public const string NodeOffline = "nodeOffline";
    public const string Removal = "removal";

    public static readonly string[] All = { Offline, Update, UpdateFailed, Migration, NodeOffline, Removal };

    /// <summary>Events about the whole fleet or its infrastructure, not about one of an operator's sites — an
    /// Operator never receives them (the matrix shows "—" there).</summary>
    public static readonly HashSet<string> FleetOnly = new() { NodeOffline, Removal };

    public static bool IsKnown(string e) => All.Contains(e);
}

/// <summary>One row of the matrix. <see cref="Key"/>: <c>g:admins</c> | <c>g:operators</c> | <c>u:&lt;userId&gt;</c> |
/// <c>e:&lt;address&gt;</c>.</summary>
public sealed class NotifyRow
{
    public string Key { get; set; } = "";
    public List<string> Events { get; set; } = new();
}

public sealed class NotifyMatrix
{
    public List<NotifyRow> Rows { get; set; } = new();
}

/// <summary>
/// Who is told about what — the notification matrix: rows are recipients (the groups "Administratoren" and
/// "Operatoren", single users, own addresses), columns are <see cref="NotifyEvents"/>. One implementation behind
/// the Benachrichtigungen page, <c>/api/v1/notifications</c> and the MCP tools, and the ONLY place that turns an
/// event into mail addresses.
/// <para>The rule that makes the Operator row safe: an Operator — as a group or as a single user — only ever
/// hears about the instances it is assigned (<see cref="UserInstance"/>), and never about fleet events.</para>
/// <para>A profile can still switch an event off for its instances (<c>Profile.NotifyOffline/NotifyUpdate</c>),
/// and its <c>NotifyRecipients</c> are ADDITIONAL addresses for its instances — not a replacement any more.</para>
/// </summary>
public class NotificationService
{
    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;
    private readonly EmailService _mail;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(AppDbContext db, CloudContext cloud, EmailService mail, ILogger<NotificationService> log)
    {
        _db = db; _cloud = cloud; _mail = mail; _log = log;
    }

    public const string GroupAdmins = "g:admins";
    public const string GroupOperators = "g:operators";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The stored matrix — or, before one was ever saved, the old settings translated into it, so an
    /// update does not silently stop anybody's mail.</summary>
    public NotifyMatrix Load()
    {
        var raw = _cloud.Get(SettingKeys.NotifyMatrix);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { return Normalise(JsonSerializer.Deserialize<NotifyMatrix>(raw, Json) ?? new()); } catch { }
        }
        return FromLegacy();
    }

    /// <summary>
    /// The pre-matrix settings: one recipient list (or, when empty, EVERY user with an address) plus two switches.
    /// A switch never set counted as off for instances without a profile but as on for every profile — so here
    /// only an explicit "0" turns an event off.
    /// </summary>
    private NotifyMatrix FromLegacy()
    {
        bool On(string key) => _cloud.Get(key) is not { } v || v.Trim() is not ("0" or "false");
        var instanceEvents = new List<string>();
        if (On(SettingKeys.NotifyOffline)) instanceEvents.Add(NotifyEvents.Offline);
        if (On(SettingKeys.NotifyUpdate)) instanceEvents.Add(NotifyEvents.Update);
        instanceEvents.Add(NotifyEvents.UpdateFailed);
        instanceEvents.Add(NotifyEvents.Migration);
        var all = instanceEvents.Concat(new[] { NotifyEvents.NodeOffline, NotifyEvents.Removal }).ToList();

        var legacy = EmailService.ParseRecipients(_cloud.Get(SettingKeys.NotifyRecipients));
        var m = new NotifyMatrix();
        if (legacy.Count > 0)
            foreach (var e in legacy) m.Rows.Add(new NotifyRow { Key = "e:" + e.ToLowerInvariant(), Events = all.ToList() });
        else
        {
            m.Rows.Add(new NotifyRow { Key = GroupAdmins, Events = all.ToList() });
            m.Rows.Add(new NotifyRow { Key = GroupOperators, Events = instanceEvents.ToList() });
        }
        return m;
    }

    /// <summary>Known events only, fleet events never on an Operator group, addresses lower-cased and unique.</summary>
    public static NotifyMatrix Normalise(NotifyMatrix m)
    {
        var rows = new List<NotifyRow>();
        foreach (var r in m.Rows)
        {
            var key = (r.Key ?? "").Trim();
            if (key.StartsWith("e:", StringComparison.OrdinalIgnoreCase))
            {
                var addr = key[2..].Trim().ToLowerInvariant();
                if (!IsEmail(addr)) continue;
                key = "e:" + addr;
            }
            else if (key != GroupAdmins && key != GroupOperators && !(key.StartsWith("u:") && int.TryParse(key[2..], out _))) continue;
            if (rows.Any(x => x.Key == key)) continue;
            var ev = (r.Events ?? new()).Where(NotifyEvents.IsKnown).Distinct().ToList();
            if (key == GroupOperators) ev.RemoveAll(NotifyEvents.FleetOnly.Contains);
            rows.Add(new NotifyRow { Key = key, Events = ev });
        }
        return new NotifyMatrix { Rows = rows };
    }

    public static bool IsEmail(string s) =>
        s.Length is > 3 and < 255 && s.Contains('@') && !s.Contains(' ') && s.IndexOf('@') > 0 && s.LastIndexOf('.') > s.IndexOf('@');

    public async Task SaveAsync(NotifyMatrix m) =>
        await _cloud.SaveAsync(new Dictionary<string, string?> { [SettingKeys.NotifyMatrix] = JsonSerializer.Serialize(Normalise(m), Json) });

    /// <summary>
    /// The addresses an event goes to. <paramref name="inst"/> is the instance it is about (null = a fleet event).
    /// Operators — group or single — get instance events only for instances they are assigned, fleet events never.
    /// </summary>
    public async Task<List<string>> RecipientsAsync(string ev, Instance? inst, NotifyMatrix? matrix = null, CancellationToken ct = default)
    {
        var m = matrix ?? Load();
        var fleet = NotifyEvents.FleetOnly.Contains(ev);
        var rows = m.Rows.Where(r => r.Events.Contains(ev)).ToList();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rows.Count > 0)
        {
            var users = await _db.Users.AsNoTracking().Include(u => u.Instances)
                .Where(u => u.Email != null && u.Email != "").ToListAsync(ct);
            bool Sees(User u) => u.Role == User.RoleAdmin
                                 || (!fleet && inst is not null && u.Instances.Any(x => x.InstanceId == inst.Id));
            foreach (var r in rows)
            {
                if (r.Key == GroupAdmins)
                    foreach (var u in users.Where(u => u.Role == User.RoleAdmin)) result.Add(u.Email!);
                else if (r.Key == GroupOperators)
                    foreach (var u in users.Where(u => u.Role != User.RoleAdmin && Sees(u))) result.Add(u.Email!);
                else if (r.Key.StartsWith("u:") && int.TryParse(r.Key[2..], out var uid))
                {
                    var u = users.FirstOrDefault(x => x.Id == uid);
                    if (u is not null && Sees(u)) result.Add(u.Email!);
                }
                else if (r.Key.StartsWith("e:")) result.Add(r.Key[2..]);
            }
        }
        // A profile's own addresses hear about ITS instances, in addition to the matrix.
        if (!fleet && inst?.Profile?.NotifyRecipients is { Length: > 0 } extra)
            foreach (var e in EmailService.ParseRecipients(extra)) result.Add(e);
        return result.ToList();
    }

    /// <summary>Resolves and sends at once (for events outside the monitor's tick, e.g. a finished move).
    /// Never throws; no SMTP or no recipients = logged and skipped.</summary>
    public async Task SendAsync(string ev, Instance? inst, string subject, string body, CancellationToken ct = default)
    {
        try
        {
            if (!await _mail.IsConfiguredAsync()) return;
            if (inst is not null && inst.Profile is null && inst.ProfileId is not null)
                await _db.Entry(inst).Reference(i => i.Profile).LoadAsync(ct);
            var to = await RecipientsAsync(ev, inst, null, ct);
            if (to.Count == 0) { _log.LogInformation("Notification '{Subject}' ({Event}): no recipients", subject, ev); return; }
            var (ok, error) = await _mail.SendAsync(to, subject, body);
            if (!ok) _log.LogWarning("Notification '{Subject}' could not be sent: {Error}", subject, error);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Notification '{Subject}' failed", subject); }
    }
}
