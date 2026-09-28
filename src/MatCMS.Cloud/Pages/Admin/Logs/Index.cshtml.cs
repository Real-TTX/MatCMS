using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Logs;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly InstanceService _instances;
    public IndexModel(AppDbContext db, InstanceService instances) { _db = db; _instances = instances; }

    /// <summary>One log line, from either source — the cloud's own log carries an exception blob,
    /// an instance's mirrored log does not (it is the overview, not the full log). <see cref="SourceLabel"/>
    /// is only filled in the combined "Alle Quellen" view (null = the cloud's own log); the single-source
    /// views leave it null because their heading already says which source it is.</summary>
    public record LogRow(DateTime Time, string Level, string Message, string? Path, string? Method, int? StatusCode, string? Exception, string? SourceLabel = null);

    public List<LogRow> Rows { get; private set; } = new();
    public string? Level { get; private set; }
    public string? Category { get; private set; }
    public int Total { get; private set; }
    public int ErrorCount { get; private set; }
    public bool HasRequestLog { get; private set; }

    /// <summary>Null/"cloud" = the cloud's own log; "all" = cloud + every instance mirror combined;
    /// otherwise an instance PublicId whose mirror we show.</summary>
    public string? Source { get; private set; }
    public bool IsCloud => Source is null;
    public bool IsAll => Source == "all";
    /// <summary>A single connected instance is selected (not the cloud, not the combined view).</summary>
    public bool IsInstance => Source is not null && Source != "all";
    public string? SelectedInstanceName { get; private set; }
    public bool FullLogPending { get; private set; }
    public List<(string PublicId, string Name)> Instances { get; private set; } = new();

    // Config (Cloud's own log only).
    public bool RequestsOn { get; private set; }
    public int ErrorsDays { get; private set; }
    public int RequestsDays { get; private set; }

    private const int Keep = 2000;
    private const int Show = 300;

    public async Task<IActionResult> OnGetAsync(string? level, string? source, string? category)
    {
        Level = string.IsNullOrWhiteSpace(level) ? null : level;
        Category = string.IsNullOrWhiteSpace(category) ? null : category;
        Source = string.IsNullOrWhiteSpace(source) || source == "cloud" ? null : source;

        Instances = await _db.Instances.AsNoTracking()
            .OrderBy(i => i.Name)
            .Select(i => new ValueTuple<string, string>(i.PublicId, i.Name))
            .ToListAsync();

        if (IsCloud)
        {
            await LoadConfigAsync();
            await LoadCloudAsync();
        }
        else if (IsAll)
            await LoadAllAsync();
        else if (!await LoadInstanceAsync(Source!))
            return RedirectToPage("Index");

        return Page();
    }

    private async Task LoadConfigAsync()
    {
        var map = await _db.CloudSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.LogRequests || s.Key == SettingKeys.LogRetentionErrorsDays || s.Key == SettingKeys.LogRetentionRequestsDays)
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        RequestsOn = map.GetValueOrDefault(SettingKeys.LogRequests) == "on";
        ErrorsDays = int.TryParse(map.GetValueOrDefault(SettingKeys.LogRetentionErrorsDays), out var e) ? e : 90;
        RequestsDays = int.TryParse(map.GetValueOrDefault(SettingKeys.LogRetentionRequestsDays), out var r) ? r : 14;
    }

    private async Task LoadCloudAsync()
    {
        Total = await _db.Logs.CountAsync();
        if (Total > Keep)
        {
            var cutId = await _db.Logs.OrderByDescending(l => l.Id).Skip(Keep).Select(l => l.Id).FirstOrDefaultAsync();
            if (cutId > 0) { await _db.Logs.Where(l => l.Id <= cutId).ExecuteDeleteAsync(); Total = await _db.Logs.CountAsync(); }
        }
        ErrorCount = await _db.Logs.CountAsync(l => l.Level == "Error");
        HasRequestLog = await _db.Logs.AnyAsync(l => l.Category == "webrequest");

        var q = _db.Logs.AsNoTracking().AsQueryable();
        if (Level is not null) q = q.Where(l => l.Level == Level);
        if (Category is not null) q = q.Where(l => l.Category == Category);
        Rows = await q.OrderByDescending(l => l.Id).Take(Show)
            .Select(l => new LogRow(l.CreatedAt, l.Level, l.Message, l.Path, l.Method, l.StatusCode, l.Exception))
            .ToListAsync();
    }

    /// <summary>The combined "Alle Quellen" view: the cloud's own log AND every instance mirror, merged
    /// and time-sorted, each row tagged with where it came from. Each source is capped at <see cref="Show"/>
    /// first, so the merged newest <see cref="Show"/> is the true newest across all of them (the global
    /// newest N is always within the per-source newest N).</summary>
    private async Task LoadAllAsync()
    {
        Total = await _db.Logs.CountAsync() + await _db.InstanceLogs.CountAsync();
        ErrorCount = await _db.Logs.CountAsync(l => l.Level == "Error")
                   + await _db.InstanceLogs.CountAsync(l => l.Level == "Error");
        HasRequestLog = await _db.Logs.AnyAsync(l => l.Category == "webrequest")
                     || await _db.InstanceLogs.AnyAsync(l => l.Category == "webrequest");

        var cq = _db.Logs.AsNoTracking().AsQueryable();
        if (Level is not null) cq = cq.Where(l => l.Level == Level);
        if (Category is not null) cq = cq.Where(l => l.Category == Category);
        // SourceLabel null marks the cloud's own log; the view localises it to "Cloud".
        var cloudRows = await cq.OrderByDescending(l => l.Id).Take(Show)
            .Select(l => new LogRow(l.CreatedAt, l.Level, l.Message, l.Path, l.Method, l.StatusCode, l.Exception, null))
            .ToListAsync();

        var iq = from l in _db.InstanceLogs.AsNoTracking()
                 join i in _db.Instances on l.InstanceId equals i.Id
                 select new { l, i.Name };
        if (Level is not null) iq = iq.Where(x => x.l.Level == Level);
        if (Category is not null) iq = iq.Where(x => x.l.Category == Category);
        var instRows = await iq.OrderByDescending(x => x.l.Id).Take(Show)
            .Select(x => new LogRow(x.l.TimeUtc, x.l.Level, x.l.Message, x.l.Path, x.l.Method, x.l.StatusCode, x.l.Exception, x.Name))
            .ToListAsync();

        Rows = cloudRows.Concat(instRows).OrderByDescending(r => r.Time).Take(Show).ToList();
    }

    private async Task<bool> LoadInstanceAsync(string publicId)
    {
        var inst = await _db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.PublicId == publicId);
        if (inst is null) return false;
        SelectedInstanceName = inst.Name;
        FullLogPending = inst.LogFetchRequestId > 0;

        Total = await _db.InstanceLogs.CountAsync(l => l.InstanceId == inst.Id);
        ErrorCount = await _db.InstanceLogs.CountAsync(l => l.InstanceId == inst.Id && l.Level == "Error");
        HasRequestLog = await _db.InstanceLogs.AnyAsync(l => l.InstanceId == inst.Id && l.Category == "webrequest");

        var q = _db.InstanceLogs.AsNoTracking().Where(l => l.InstanceId == inst.Id);
        if (Level is not null) q = q.Where(l => l.Level == Level);
        if (Category is not null) q = q.Where(l => l.Category == Category);
        Rows = await q.OrderByDescending(l => l.Id).Take(Show)
            .Select(l => new LogRow(l.TimeUtc, l.Level, l.Message, l.Path, l.Method, l.StatusCode, l.Exception))
            .ToListAsync();
        return true;
    }

    /// <summary>Asks the selected instance for its full log (Variante B). It uploads on its next beat and
    /// the mirror is replaced with the snapshot (with stack traces).</summary>
    public async Task<IActionResult> OnPostRequestFullLogAsync(string source)
    {
        var inst = await _db.Instances.FirstOrDefaultAsync(i => i.PublicId == source);
        if (inst is not null)
        {
            await _instances.RequestFullLogAsync(inst);
            TempData["Flash"] = "Volles Protokoll angefordert – die Instanz lädt es beim nächsten Kontakt hoch.";
        }
        return RedirectToPage(new { source });
    }

    public async Task<IActionResult> OnPostSaveConfigAsync(bool requestsOn, int errorsDays, int requestsDays)
    {
        async Task SetAsync(string key, string value)
        {
            var row = await _db.CloudSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (row is null) _db.CloudSettings.Add(new Models.CloudSetting { Key = key, Value = value });
            else row.Value = value;
        }
        await SetAsync(SettingKeys.LogRequests, requestsOn ? "on" : "off");
        await SetAsync(SettingKeys.LogRetentionErrorsDays, Math.Clamp(errorsDays, 0, 3650).ToString());
        await SetAsync(SettingKeys.LogRetentionRequestsDays, Math.Clamp(requestsDays, 0, 3650).ToString());
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Protokoll-Einstellungen gespeichert.";
        return RedirectToPage();
    }

    /// <summary>Clears the cloud's OWN log. An instance's mirror is not clearable here — it refills from
    /// the site's next beat.</summary>
    public async Task<IActionResult> OnPostClearAsync()
    {
        await _db.Logs.ExecuteDeleteAsync();
        TempData["Flash"] = "Protokoll geleert.";
        return RedirectToPage();
    }
}
