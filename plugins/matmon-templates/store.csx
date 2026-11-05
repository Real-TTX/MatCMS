// The data of the template store. Two tables:
//  entries  - one per template in the store (its address, the texts shown, the LIVE version, downloads). Created with the first
//             submission and invisible until a version of it is approved (version_id = 0) or while the operator hides it.
//  versions - every file ever submitted, with the texts it was submitted with. A version is pending, approved or rejected; approving
//             one copies its texts onto the entry and makes it the live one. So an update by the author never touches what visitors
//             see before the operator looked at it, and the history of approved files stays downloadable.
// The files themselves are private plugin files (v/{versionId}.json), served only through the plugin's own routes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

public static class MtStore
{
    public const string Entries = "entries";
    public const string Versions = "versions";
    public const string Pending = "pending", Approved = "approved", Rejected = "rejected";
    /// <summary>Path segments of the store's own pages; never a slug.</summary>
    public static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase) { "submit", "mine", "download", "new", "edit", "admin" };

    public static void Declare(IPluginBuilder b)
    {
        b.Data.Table(Entries, t => t
            .Text("slug", required: true, unique: true).Text("title", required: true).Text("summary").Text("description")
            .Text("tags").Text("tagkey").Int("member_id", def: "0").Text("author").Text("email")
            .Text("version").Int("version_id", def: "0").Int("downloads", def: "0").Int("template_count", def: "0").Text("sensor_types")
            .Bool("hidden", def: "0").Timestamp("created").Timestamp("updated")
            .Index("member_id").Index("version_id"));
        b.Data.Table(Versions, t => t
            .Int("entry_id", required: true).Text("version").Text("notes").Text("title").Text("summary").Text("description").Text("tags")
            .Text("file").Int("size", def: "0").Int("template_count", def: "0").Json("templates").Json("removed")
            .Text("status", required: true).Text("reason").Int("member_id", def: "0").Text("author").Text("email")
            .Timestamp("created").Timestamp("decided")
            .Index("entry_id").Index("status").Index("member_id"));
    }

    public static IDataTable E(IPluginContext ctx) => ctx.Data.Table(Entries);
    public static IDataTable V(IPluginContext ctx) => ctx.Data.Table(Versions);

    public static string FileName(long versionId) => "v/" + versionId.ToString(CultureInfo.InvariantCulture) + ".json";

    // ---- texts ----------------------------------------------------------------------------------------------------

    public static string Clip(string? s, int max)
    {
        var t = (s ?? "").Replace("\r\n", "\n").Trim();
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>Keywords as typed, without duplicates (case-insensitive), no commas inside one, at most ten.</summary>
    public static List<string> ParseTags(string? raw) =>
        (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Clip(x, 30)).Where(x => x.Length > 0)
            .DistinctBy(x => x.ToLowerInvariant()).Take(10).ToList();

    public static string TagKey(IEnumerable<string> tags) => "," + string.Join(",", tags.Select(x => x.ToLowerInvariant())) + ",";

    public static string Slugify(string title)
    {
        var map = new Dictionary<char, string> { ['ä'] = "ae", ['ö'] = "oe", ['ü'] = "ue", ['ß'] = "ss" };
        var sb = new StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
        {
            if (map.TryGetValue(ch, out var r)) sb.Append(r);
            else if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        if (s.Length > 60) s = s[..60].Trim('-');
        return s.Length == 0 ? "template" : s;
    }

    public static async Task<string> UniqueSlugAsync(IPluginContext ctx, string title, CancellationToken ct)
    {
        var baseSlug = Slugify(title);
        if (ReservedSlugs.Contains(baseSlug)) baseSlug += "-template";
        var slug = baseSlug;
        for (var n = 2; await E(ctx).FindAsync(DataQuery.Everything.Eq("slug", slug), ct) is not null; n++) slug = baseSlug + "-" + n;
        return slug;
    }

    public static IReadOnlyList<MtTemplateInfo> Templates(DataRow v)
    {
        var list = new List<MtTemplateInfo>();
        try
        {
            if (JsonNode.Parse(v.Text("templates")) is JsonArray a)
                foreach (var x in a.OfType<JsonObject>())
                    list.Add(new MtTemplateInfo((string?)x["name"] ?? "", (string?)x["sensor"] ?? "", (string?)x["scope"] ?? ""));
        }
        catch (System.Text.Json.JsonException) { }
        return list;
    }

    public static IReadOnlyList<string> Removed(DataRow v)
    {
        try { return JsonNode.Parse(v.Text("removed")) is JsonArray a ? a.Select(x => (string?)x ?? "").Where(x => x.Length > 0).ToList() : new List<string>(); }
        catch (System.Text.Json.JsonException) { return new List<string>(); }
    }

    public static string SensorTypes(IEnumerable<MtTemplateInfo> templates) =>
        string.Join(", ", templates.Select(t => t.SensorType).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20));

    // ---- reads ----------------------------------------------------------------------------------------------------

    public static DataQuery Visible => DataQuery.Everything.Where("version_id", DataOp.Gt, 0).Eq("hidden", false);

    public static Task<DataRow?> EntryBySlugAsync(IPluginContext ctx, string slug, CancellationToken ct) =>
        E(ctx).FindAsync(DataQuery.Everything.Eq("slug", (slug ?? "").ToLowerInvariant()), ct);

    public static bool IsVisible(DataRow e) => e.Int("version_id") > 0 && !e.Bool("hidden");

    public static Task<long> PendingCountAsync(IPluginContext ctx, CancellationToken ct) =>
        V(ctx).CountAsync(DataQuery.Everything.Eq("status", Pending), ct);

    // ---- writes ---------------------------------------------------------------------------------------------------

    public sealed record Submission(string Title, string Summary, string Description, List<string> Tags, string Version, string Notes);

    /// <summary>Stores a submitted file as a pending version: of a NEW entry (<paramref name="entry"/> null) or of the member's own one.</summary>
    public static async Task<(DataRow Entry, long VersionId)> SubmitAsync(IPluginContext ctx, PluginMember member, DataRow? entry, Submission s, MtParsed file, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        long entryId;
        if (entry is null)
        {
            var slug = await UniqueSlugAsync(ctx, s.Title, ct);
            entryId = await E(ctx).InsertAsync(new Dictionary<string, object?>
            {
                ["slug"] = slug, ["title"] = s.Title, ["summary"] = s.Summary, ["description"] = s.Description,
                ["tags"] = string.Join(", ", s.Tags), ["tagkey"] = TagKey(s.Tags),
                ["member_id"] = member.Id, ["author"] = member.DisplayName, ["email"] = member.Email,
                ["version"] = "", ["version_id"] = 0, ["created"] = now, ["updated"] = now
            }, ct);
        }
        else entryId = entry.Id;

        var templates = new JsonArray(file.Templates.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["sensor"] = t.SensorType, ["scope"] = t.Scope }).ToArray());
        var removed = new JsonArray(file.Removed.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        var versionId = await V(ctx).InsertAsync(new Dictionary<string, object?>
        {
            ["entry_id"] = entryId, ["version"] = s.Version, ["notes"] = s.Notes,
            ["title"] = s.Title, ["summary"] = s.Summary, ["description"] = s.Description, ["tags"] = string.Join(", ", s.Tags),
            ["file"] = "", ["size"] = file.Bytes.Length, ["template_count"] = file.Templates.Count,
            ["templates"] = templates, ["removed"] = removed,
            ["status"] = Pending, ["member_id"] = member.Id, ["author"] = member.DisplayName, ["email"] = member.Email, ["created"] = now
        }, ct);
        using (var ms = new System.IO.MemoryStream(file.Bytes)) await ctx.Files.SaveAsync(FileName(versionId), ms, ct);
        await V(ctx).UpdateAsync(versionId, new Dictionary<string, object?> { ["file"] = FileName(versionId) }, ct);
        return ((await E(ctx).GetAsync(entryId, ct))!, versionId);
    }

    /// <summary>Makes a pending version the live one of its entry and copies its texts over.</summary>
    public static async Task ApproveAsync(IPluginContext ctx, DataRow v, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var tags = ParseTags(v.Text("tags"));
        await V(ctx).UpdateAsync(v.Id, new Dictionary<string, object?> { ["status"] = Approved, ["reason"] = "", ["decided"] = now }, ct);
        await E(ctx).UpdateAsync(v.Int("entry_id"), new Dictionary<string, object?>
        {
            ["title"] = v.Text("title"), ["summary"] = v.Text("summary"), ["description"] = v.Text("description"),
            ["tags"] = string.Join(", ", tags), ["tagkey"] = TagKey(tags),
            ["version"] = v.Text("version"), ["version_id"] = v.Id, ["template_count"] = v.Int("template_count"),
            ["sensor_types"] = SensorTypes(Templates(v)), ["updated"] = now
        }, ct);
    }

    public static Task RejectAsync(IPluginContext ctx, DataRow v, string reason, CancellationToken ct) =>
        V(ctx).UpdateAsync(v.Id, new Dictionary<string, object?> { ["status"] = Rejected, ["reason"] = reason, ["decided"] = DateTime.UtcNow }, ct);

    /// <summary>Removes an entry with every version and file.</summary>
    public static async Task DeleteEntryAsync(IPluginContext ctx, long entryId, CancellationToken ct)
    {
        foreach (var v in await V(ctx).QueryAsync(DataQuery.Everything.Eq("entry_id", entryId), ct))
            await ctx.Files.DeleteAsync(FileName(v.Id), ct);
        await V(ctx).DeleteWhereAsync(DataQuery.Everything.Eq("entry_id", entryId), ct);
        await E(ctx).DeleteAsync(entryId, ct);
    }

    /// <summary>
    /// An account is being deleted: what was never published goes (pending and rejected files), what is published stays — it is the
    /// community's now — but no longer names the person or keeps the address.
    /// </summary>
    public static async Task EraseMemberAsync(IPluginContext ctx, long memberId, string formerName, CancellationToken ct)
    {
        if (memberId <= 0) return;
        foreach (var v in await V(ctx).QueryAsync(DataQuery.Everything.Eq("member_id", memberId), ct))
        {
            if (v.Text("status") != Approved)
            {
                await ctx.Files.DeleteAsync(FileName(v.Id), ct);
                await V(ctx).DeleteAsync(v.Id, ct);
            }
        }
        var anon = new Dictionary<string, object?> { ["member_id"] = 0, ["author"] = formerName, ["email"] = "" };
        await V(ctx).UpdateWhereAsync(DataQuery.Everything.Eq("member_id", memberId), anon, ct);
        foreach (var e in await E(ctx).QueryAsync(DataQuery.Everything.Eq("member_id", memberId), ct))
        {
            if (e.Int("version_id") == 0 && await V(ctx).CountAsync(DataQuery.Everything.Eq("entry_id", e.Id), ct) == 0) await E(ctx).DeleteAsync(e.Id, ct);
            else await E(ctx).UpdateAsync(e.Id, anon, ct);
        }
    }
}
