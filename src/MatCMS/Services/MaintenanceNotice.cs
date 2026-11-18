using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>What the maintenance mode looks like at one moment: on/off, the page's title and text, the admins' note and a fingerprint of the
/// PIN's hash (never the hash itself — it goes into a user row and into a page).</summary>
public sealed record MaintenanceState(bool On, string Title, string Message, string Note, string Pin)
{
    public static MaintenanceState Of(Func<string, string> get)
    {
        var pin = get(SettingKeys.MaintenancePin).Trim();
        return new(get(SettingKeys.MaintenanceEnabled).Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes",
            get(SettingKeys.MaintenanceTitle).Trim(), get(SettingKeys.MaintenanceMessage).Trim(), get(SettingKeys.MaintenanceNote).Trim(),
            pin.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pin)))[..12]);
    }

    /// <summary>Names one state for a form: the confirmation is for THIS state, a change made meanwhile asks again.</summary>
    [System.Text.Json.Serialization.JsonIgnore]   // computed from the JSON, so it must not be part of it
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json())))[..16];

    public string Json() => JsonSerializer.Serialize(this);

    public static MaintenanceState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<MaintenanceState>(json); } catch (JsonException) { return null; }
    }

    /// <summary>What differs from an earlier state, as keys of the dialog's lines (<c>maint.ack.change.{key}</c>).</summary>
    public IReadOnlyList<string> ChangesSince(MaintenanceState before)
    {
        var list = new List<string>();
        if (On != before.On) list.Add(On ? "on" : "off");
        if (Title != before.Title) list.Add("title");
        if (Message != before.Message) list.Add("message");
        if (Note != before.Note) list.Add("note");
        if (Pin != before.Pin) list.Add(Pin.Length == 0 ? "pinRemoved" : "pin");
        return list;
    }
}

/// <summary>A change of the maintenance mode an admin has not seen: what changed, who (null = from outside the back office — the cloud, a restore,
/// an interface) and when.</summary>
public sealed record MaintenanceChange(MaintenanceState State, IReadOnlyList<string> Changes, string? By, DateTime? AtUtc);

/// <summary>
/// "Somebody else touched the maintenance mode" (Matthias 2026-11-18: an admin who comes in after the maintenance was switched, extended or otherwise
/// changed by somebody else has to confirm it). Every back-office user keeps the state they last saw (<see cref="User.MaintenanceSeen"/>); the admin
/// layout asks <see cref="CheckAsync"/> on every page and shows a dialog that only "Gesehen" closes.
/// <para>Who made a change is known only for Settings → Wartung (<see cref="RecordAsync"/> writes <see cref="SettingKeys.MaintenanceLastChange"/>). Any
/// other writer — a cloud rollout, a restore, a content op — leaves a state that does not match that record and reads as "from outside". That is
/// deliberate: no writer has to remember to call this class.</para>
/// </summary>
public sealed class MaintenanceNotice
{
    private readonly AppDbContext _db;
    public MaintenanceNotice(AppDbContext db) => _db = db;

    private sealed record Last(int UserId, string By, DateTime At, MaintenanceState State);

    private async Task<(MaintenanceState Now, Last? Last)> ReadAsync()
    {
        string[] keys = [SettingKeys.MaintenanceEnabled, SettingKeys.MaintenanceTitle, SettingKeys.MaintenanceMessage, SettingKeys.MaintenanceNote,
            SettingKeys.MaintenancePin, SettingKeys.MaintenanceLastChange];
        var rows = await _db.SiteSettings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value ?? "");
        string Get(string k) => rows.TryGetValue(k, out var v) ? v : "";
        Last? last = null;
        try { last = JsonSerializer.Deserialize<Last>(Get(SettingKeys.MaintenanceLastChange)); } catch (JsonException) { }
        return (MaintenanceState.Of(Get), last);
    }

    private static int? UserId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>The change this user still has to confirm, or null. The first look ever only records the state; a state this user made
    /// themself is recorded silently too.</summary>
    public async Task<MaintenanceChange?> CheckAsync(ClaimsPrincipal user)
    {
        if (UserId(user) is not { } uid) return null;
        var row = await _db.Users.FirstOrDefaultAsync(u => u.Id == uid);
        if (row is null) return null;
        var (now, last) = await ReadAsync();
        var seen = MaintenanceState.Parse(row.MaintenanceSeen);
        if (seen == now) return null;
        var byThem = last is not null && last.State == now;
        if (seen is null || (byThem && last!.UserId == uid))
        {
            row.MaintenanceSeen = now.Json();
            await _db.SaveChangesAsync();
            return null;
        }
        var changes = now.ChangesSince(seen);
        return new(now, changes, byThem ? last!.By : null, byThem ? last!.At : null);
    }

    /// <summary>"Gesehen": stores the state the dialog showed — only while it is still the current one (otherwise the next page asks again).</summary>
    public async Task<bool> AcknowledgeAsync(ClaimsPrincipal user, string? fingerprint)
    {
        if (UserId(user) is not { } uid) return false;
        var row = await _db.Users.FirstOrDefaultAsync(u => u.Id == uid);
        var (now, _) = await ReadAsync();
        if (row is null || now.Fingerprint != fingerprint) return false;
        row.MaintenanceSeen = now.Json();
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Settings → Wartung was saved by this user: remember who and what (only when something really changed), and that they have seen it.</summary>
    public async Task RecordAsync(ClaimsPrincipal user)
    {
        if (UserId(user) is not { } uid) return;
        var row = await _db.Users.FirstOrDefaultAsync(u => u.Id == uid);
        if (row is null) return;
        var (now, last) = await ReadAsync();
        var seen = MaintenanceState.Parse(row.MaintenanceSeen);
        if (last?.State != now && seen != now)
        {
            var by = string.IsNullOrWhiteSpace(row.DisplayName) ? row.Username : row.DisplayName!;
            var json = JsonSerializer.Serialize(new Last(uid, by, DateTime.UtcNow, now));
            var setting = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.MaintenanceLastChange);
            if (setting is null) _db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.MaintenanceLastChange, Value = json });
            else setting.Value = json;
        }
        row.MaintenanceSeen = now.Json();
        await _db.SaveChangesAsync();
    }
}
