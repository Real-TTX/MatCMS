using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>
/// The backup policy this profile rolls out: whether its sites back themselves up, how often, how
/// much history they keep, what goes in — and whether the finished file is handed to the cloud.
///
/// <para>The schedule travels as ONE key (<c>backup.schedule</c>) holding the JSON the instance's own
/// <c>BackupManager</c> reads, rather than as a field per option. That format is the instance's, and
/// re-modelling it here would mean two definitions of the same thing drifting apart; the site's
/// backup page and a rolled-out policy have to mean exactly the same thing or the operator is being
/// lied to on one of the two screens.</para>
///
/// <para>The granular lists in that format (which templates, which pages, which forms) are written
/// EMPTY on purpose — empty means "the whole section". They name items that exist on one site and
/// nowhere else, so a profile that distributed them would leave every other instance backing up
/// nothing at all, silently and with a green tick.</para>
/// </summary>
public class BackupModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;

    public BackupModel(AppDbContext db, ProfileService profiles)
    {
        _db = db; _profiles = profiles;
    }

    /// <summary>Same key the instance's BackupManager reads its schedule from.</summary>
    public const string ScheduleKey = "backup.schedule";

    /// <summary>Whether finished backups are uploaded to the cloud.</summary>
    public const string ToCloudKey = "backup.toCloud";

    public Profile Owner { get; private set; } = new();
    public List<ProfileSetting> Settings { get; private set; } = [];

    public bool IsNew { get; private set; }

    /// <summary>The schedule as stored, or the defaults — so a fresh group opens on something
    /// sensible rather than on zeroes nobody would want rolled out.</summary>
    public Schedule Current { get; private set; } = new();

    public bool ToCloud { get; private set; }

    /// <summary>
    /// Mirrors the instance's <c>BackupScheduleConfig</c> — the property NAMES are the contract here,
    /// because that is what the JSON carries and the instance deserialises case-sensitively.
    /// </summary>
    public sealed class Schedule
    {
        public bool Enabled { get; set; }
        public int IntervalHours { get; set; } = 24;
        public int Retain { get; set; } = 7;
        public bool Templates { get; set; } = true;
        public bool Pages { get; set; } = true;
        public bool Menus { get; set; } = true;
        public bool Settings { get; set; } = true;
        public bool Submissions { get; set; } = true;
        public bool Forms { get; set; } = true;
        public bool Assets { get; set; } = true;
        /// <summary>Plugin code and its script files. Worth its own tick because it is the only backup
        /// section whose contents exist nowhere but the instance's own database.</summary>
        public bool Plugins { get; set; } = true;
        // Always empty — see the class comment. Written so the instance sees the field it expects
        // rather than falling back to whatever it had stored before.
        public List<string> TemplateNames { get; set; } = [];
        public List<string> PageKeys { get; set; } = [];
        public List<string> FormSlugs { get; set; } = [];
    }

    public async Task<IActionResult> OnGetAsync(int profileId)
    {
        var owner = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId);
        if (owner is null) return RedirectToPage("Index");
        Owner = owner;
        IsNew = !owner.SyncBackup;

        Settings = await _db.ProfileSettings.AsNoTracking().Where(s => s.ProfileId == profileId).ToListAsync();

        var raw = Settings.FirstOrDefault(s => s.Key == ScheduleKey)?.Value;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            // A stored value that no longer parses falls back to the defaults instead of throwing:
            // the page still has to open, or the group could never be corrected.
            try { Current = JsonSerializer.Deserialize<Schedule>(raw) ?? new Schedule(); }
            catch { Current = new Schedule(); }
        }
        ToCloud = Settings.FirstOrDefault(s => s.Key == ToCloudKey)?.Value == "1";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        int profileId, bool enabled, int intervalHours, int retain,
        bool templates, bool pages, bool menus, bool settings, bool submissions, bool forms, bool assets,
        bool plugins, bool toCloud)
    {
        var profile = await _db.Profiles.FindAsync(profileId);
        if (profile is null) return RedirectToPage("Index");

        // Being on this page means the profile rolls this out; the switch that decides THAT is the
        // add dialog and the row's delete, not a checkbox halfway down a form.
        profile.SyncBackup = true;

        var cfg = new Schedule
        {
            Enabled = enabled,
            // Clamped rather than trusted: the instance runs `Math.Max(1, IntervalHours)` anyway, so a
            // zero here would silently become hourly on every site instead of what was typed.
            IntervalHours = Math.Clamp(intervalHours, 1, 24 * 30),
            Retain = Math.Clamp(retain, 1, 100),
            Templates = templates, Pages = pages, Menus = menus, Settings = settings,
            Submissions = submissions, Forms = forms, Assets = assets, Plugins = plugins,
        };

        await UpsertAsync(profileId, ScheduleKey, JsonSerializer.Serialize(cfg));
        await UpsertAsync(profileId, ToCloudKey, toCloud ? "1" : "0");

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = "Backup-Einstellungen gespeichert.";
        return RedirectToPage("Edit", new { id = profileId, tab = "settings" });
    }

    /// <summary>Stops rolling the policy out. The stored values survive, and — more importantly —
    /// nothing is switched off on the instances: a site that was backing itself up keeps doing so.
    /// Taking a payload out of a profile has never meant undoing it on live sites.</summary>
    public async Task<IActionResult> OnPostRemoveAsync(int profileId)
    {
        var profile = await _db.Profiles.FindAsync(profileId);
        if (profile is null) return RedirectToPage("Index");

        profile.SyncBackup = false;
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = "Backup-Einstellungen werden von diesem Profil nicht mehr ausgerollt.";
        return RedirectToPage("Edit", new { id = profileId, tab = "settings" });
    }

    private async Task UpsertAsync(int profileId, string key, string value)
    {
        var row = await _db.ProfileSettings.FirstOrDefaultAsync(s => s.ProfileId == profileId && s.Key == key);
        if (row is null)
        {
            row = new ProfileSetting { ProfileId = profileId, Key = key };
            _db.ProfileSettings.Add(row);
        }
        row.Value = value;
        row.IsSecret = false;
    }
}
