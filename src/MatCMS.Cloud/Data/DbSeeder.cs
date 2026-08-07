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
        }
    }
}
