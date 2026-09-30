using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Data;

/// <summary>Brings an empty database up to a usable state. Idempotent: every step checks first, so
/// it runs on every start without touching existing data.</summary>
public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var auth = services.GetRequiredService<AuthService>();

        if (!await db.Users.AnyAsync())
        {
            db.Users.Add(new User
            {
                Username = "admin",
                Email = "admin@localhost",
                DisplayName = "Administrator",
                Role = "Admin",
                PasswordHash = auth.HashPassword("admin")
            });
        }

        // Defaults for the settings the UI reads before anything has been configured.
        var defaults = new Dictionary<string, string>
        {
            [SettingKeys.CloudName] = "MatCMS.Cloud",
            [SettingKeys.NotifyOffline] = "1",
            [SettingKeys.NotifyUpdate] = "1",
            [SettingKeys.AutoUpdateLocal] = "0"
        };

        var existing = await db.CloudSettings.Select(s => s.Key).ToListAsync();
        foreach (var (key, value) in defaults)
        {
            if (!existing.Contains(key))
                db.CloudSettings.Add(new CloudSetting { Key = key, Value = value });
        }

        await db.SaveChangesAsync();

        // A first profile with a ready join code, so enrolling an instance works straight after
        // install without the operator having to understand profiles first.
        if (!await db.Profiles.AnyAsync())
        {
            db.Profiles.Add(new Profile
            {
                Name = "Standard",
                Description = "Automatisch angelegt. Instanzen, die sich mit diesem Join-Code melden, landen hier.",
                JoinCode = ProfileService.NewJoinCode(),
                IsDefault = true,
                AutoApprove = true
            });
            await db.SaveChangesAsync();
            // The first profile starts with the recommended settings, like every profile created later.
            var first = await db.Profiles.FirstAsync();
            foreach (var e in InstanceSettingCatalog.Recommended)
                db.ProfileSettings.Add(new ProfileSetting { ProfileId = first.Id, Key = e.Key, Value = e.Recommended });
            await db.SaveChangesAsync();
        }

        await MoveBackupDefaultsIntoProfileAsync(db);
    }

    /// <summary>
    /// Backup quota and retention used to have a cloud-wide default under Einstellungen → Backups. They are a
    /// profile matter now: the DEFAULT profile's values are the fallback for every other profile. Once, at startup,
    /// any old cloud-wide value is copied into the default profile — only into a field it does not set itself, so
    /// nobody's quota or retention changes — and the old setting row is removed. Idempotent.
    /// </summary>
    private static async Task MoveBackupDefaultsIntoProfileAsync(AppDbContext db)
    {
        var keys = new[] { SettingKeys.BackupQuotaGb, SettingKeys.BackupKeepDaily, SettingKeys.BackupKeepWeekly,
                           SettingKeys.BackupKeepMonthly, SettingKeys.BackupMaxCount };
        var rows = await db.CloudSettings.Where(s => keys.Contains(s.Key)).ToListAsync();
        if (rows.Count == 0) return;
        var def = await db.Profiles.FirstOrDefaultAsync(p => p.IsDefault);
        if (def is null) return;                       // keep the rows until there is a profile to hold them
        string? V(string k) => rows.FirstOrDefault(r => r.Key == k)?.Value;
        static int? Tier(string? s) => int.TryParse(s, out var n) && n >= 0 ? n : null;

        if (def.BackupQuotaGb is null && BackupStore.ParseGb(V(SettingKeys.BackupQuotaGb)) is double gb && gb > 0) def.BackupQuotaGb = gb;
        def.BackupKeepDaily ??= Tier(V(SettingKeys.BackupKeepDaily));
        def.BackupKeepWeekly ??= Tier(V(SettingKeys.BackupKeepWeekly));
        def.BackupKeepMonthly ??= Tier(V(SettingKeys.BackupKeepMonthly));
        def.BackupMaxCount ??= Tier(V(SettingKeys.BackupMaxCount));
        db.CloudSettings.RemoveRange(rows);
        await db.SaveChangesAsync();
    }
}
