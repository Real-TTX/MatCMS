using System.Text.Json;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The record of the last cloud self-update, kept as <c>appdata/self-update.json</c>. A FILE on the shared
/// data volume rather than a database row on purpose: it is written by three different processes — the
/// cloud that asks, the helper container that does the work, and whichever cloud is running afterwards
/// (the new one, or the old one after a rollback) — and the helper must never open the database while a
/// cloud might be migrating it. The helper appends to <see cref="Log"/> as it goes, so even a helper that
/// dies half-way leaves a trail of how far it got.
/// </summary>
public class SelfUpdateState
{
    public const string FileName = "self-update.json";

    /// <summary>started | running | succeeded | current | rolled-back | failed</summary>
    public string State { get; set; } = "";
    public string? Message { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? FromVersion { get; set; }
    public string? FromImage { get; set; }
    public string? ToImage { get; set; }
    public List<string> Log { get; set; } = new();

    /// <summary>True while a helper may still be working (so the UI does not offer a second run).</summary>
    public bool InFlight => State is "started" or "running";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static SelfUpdateState? Load(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<SelfUpdateState>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    /// <summary>Writes via a temp file + move, so a reader never sees a half-written file.</summary>
    public void Save(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var path = Path.Combine(dataDir, FileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best effort — the container logs still carry every line */ }
    }
}

/// <summary>Whether a self-update is in flight right now, for the back-office lock in <c>Program.cs</c>. Reads the
/// state file at most every two seconds; a run older than <see cref="MaxLock"/> never locks (see there).</summary>
public static class SelfUpdateLock
{
    public static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);
    private static DateTime _readAt = DateTime.MinValue;
    private static bool _locked;
    private static readonly object Gate = new();

    public static bool IsLocked(string dataDir)
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _readAt < TimeSpan.FromSeconds(2)) return _locked;
            var s = SelfUpdateState.Load(dataDir);
            _locked = s is { InFlight: true } && s.StartedAt > DateTime.UtcNow - MaxLock;
            _readAt = DateTime.UtcNow;
            return _locked;
        }
    }

    /// <summary>Called right after a run was requested, so the very next request already sees the lock.</summary>
    public static void Invalidate() { lock (Gate) _readAt = DateTime.MinValue; }
}
