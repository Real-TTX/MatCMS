using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Plugin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MatCMS.Services.Plugins;

/// <summary>
/// The host's side of plugin data, layer 1: turns a plugin's table declarations into real tables (reconcile), reads and
/// writes rows for <see cref="IPluginData"/>, and knows the tables for backup, restore and "delete plugin".
/// <para><b>What can reach the database through here.</b> Table and column names only ever come from the plugin's own
/// declaration (validated against a strict pattern and then looked up, never concatenated from a request); every value is a
/// parameter. A plugin's tables are named <c>p_{key}_{name}</c>, so they cannot collide with the CMS's own, and a record in
/// <c>__plugin_tables</c> remembers who owns which — two plugins cannot end up on one table. The one exception is a built-in
/// plugin that <i>adopts</i> a table the CMS created earlier (the blog's <c>Posts</c>); an ordinary plugin cannot declare that.</para>
/// </summary>
public sealed class PluginDataService
{
    /// <summary>The bookkeeping table: physical name → owning plugin, plus the schema that was last applied.</summary>
    public const string MetaTable = "__plugin_tables";

    private static readonly Regex Ident = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>The stored form of a DateTime: the format EF Core uses for SQLite, so a table the CMS created earlier and a
    /// plugin table read the same text.</summary>
    internal const string TimeFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

    /// <summary>The physical name for a plugin's table: <c>p_{key}_{name}</c> with the key's hyphens turned to underscores.</summary>
    public static string PhysicalName(string pluginKey, TableSpec spec) =>
        spec.Adopts ?? "p_" + Regex.Replace(pluginKey ?? "", "[^A-Za-z0-9]", "_").ToLowerInvariant() + "_" + spec.Name;

    private static string Q(string identifier)
    {
        if (!Ident.IsMatch(identifier)) throw new InvalidOperationException($"'{identifier}' is not a valid name.");
        return "\"" + identifier + "\"";
    }

    // ---- reconcile ------------------------------------------------------------------------------

    /// <summary>
    /// Makes the database match the declaration: creates a missing table, adds a missing column or index. Never drops,
    /// renames or retypes. Runs in one transaction — a failure leaves the database as it was.
    /// </summary>
    public async Task ReconcileAsync(AppDbContext db, string pluginKey, IReadOnlyList<TableSpec> specs, CancellationToken ct = default)
    {
        if (specs.Count == 0) return;
        var conn = await OpenAsync(db, ct);
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await ExecAsync(db, $"CREATE TABLE IF NOT EXISTS {Q(MetaTable)} (\"PhysicalName\" TEXT NOT NULL PRIMARY KEY, \"PluginKey\" TEXT NOT NULL, \"TableName\" TEXT NOT NULL, \"Adopted\" INTEGER NOT NULL DEFAULT 0, \"SchemaJson\" TEXT NOT NULL DEFAULT '')", ct);
            foreach (var spec in specs)
            {
                var physical = PhysicalName(pluginKey, spec);
                var owner = await ScalarAsync(db, $"SELECT \"PluginKey\" FROM {Q(MetaTable)} WHERE \"PhysicalName\" = $p", ct, ("$p", physical));
                if (owner is string o && !string.Equals(o, pluginKey, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Die Tabelle '{physical}' gehört dem Plugin '{o}'.");

                var existing = await ColumnsOfAsync(db, physical, ct);
                if (existing.Count == 0) await CreateTableAsync(db, physical, spec, ct);
                else await AddMissingColumnsAsync(db, physical, spec, existing, ct);
                await EnsureIndexesAsync(db, physical, spec, ct);

                await ExecAsync(db,
                    $"INSERT INTO {Q(MetaTable)} (\"PhysicalName\",\"PluginKey\",\"TableName\",\"Adopted\",\"SchemaJson\") VALUES ($p,$k,$t,$a,$s) " +
                    "ON CONFLICT(\"PhysicalName\") DO UPDATE SET \"TableName\"=$t, \"Adopted\"=$a, \"SchemaJson\"=$s", ct,
                    ("$p", physical), ("$k", pluginKey), ("$t", spec.Name), ("$a", spec.Adopts is null ? 0 : 1), ("$s", SchemaToJson(spec)));
            }
            if (own is not null) await own.CommitAsync(ct);
        }
        catch
        {
            if (own is not null) await own.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally { own?.Dispose(); _ = conn; }
    }

    private static string SqlType(ColumnType t) => t switch
    {
        ColumnType.Int or ColumnType.Bool => "INTEGER",
        ColumnType.Real => "REAL",
        _ => "TEXT"
    };

    private static string DefaultSql(ColumnSpec c)
    {
        if (c.Default is null) return "";
        return c.Type switch
        {
            ColumnType.Int or ColumnType.Bool => long.TryParse(c.Default, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                ? " DEFAULT " + l.ToString(CultureInfo.InvariantCulture)
                : c.Default is "true" or "false" ? " DEFAULT " + (c.Default == "true" ? "1" : "0")
                : throw new InvalidOperationException($"Column '{c.Name}': the default '{c.Default}' is not a whole number."),
            ColumnType.Real => double.TryParse(c.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? " DEFAULT " + d.ToString("R", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"Column '{c.Name}': the default '{c.Default}' is not a number."),
            _ => " DEFAULT '" + c.Default.Replace("'", "''") + "'"
        };
    }

    private static async Task CreateTableAsync(AppDbContext db, string physical, TableSpec spec, CancellationToken ct)
    {
        var sb = new StringBuilder($"CREATE TABLE {Q(physical)} ({Q(spec.KeyColumn)} INTEGER PRIMARY KEY AUTOINCREMENT");
        foreach (var c in spec.Columns)
            sb.Append(", ").Append(Q(c.Name)).Append(' ').Append(SqlType(c.Type)).Append(c.Required ? " NOT NULL" : "").Append(DefaultSql(c));
        sb.Append(')');
        await ExecAsync(db, sb.ToString(), ct);
    }

    private static async Task AddMissingColumnsAsync(AppDbContext db, string physical, TableSpec spec, HashSet<string> existing, CancellationToken ct)
    {
        foreach (var c in spec.Columns.Where(c => !existing.Contains(c.Name)))
        {
            // SQLite cannot add a NOT NULL column without a default to a table that may hold rows.
            if (c.Required && c.Default is null)
                throw new InvalidOperationException($"Die Spalte '{c.Name}' ist Pflicht, hat aber keinen Standardwert — in eine bestehende Tabelle lässt sie sich so nicht aufnehmen.");
            await ExecAsync(db, $"ALTER TABLE {Q(physical)} ADD COLUMN {Q(c.Name)} {SqlType(c.Type)}{(c.Required ? " NOT NULL" : "")}{DefaultSql(c)}", ct);
        }
    }

    private static async Task EnsureIndexesAsync(AppDbContext db, string physical, TableSpec spec, CancellationToken ct)
    {
        var present = await IndexesOfAsync(db, physical, ct);
        var wanted = spec.Indexes.Select(i => (Columns: i.Columns, i.Unique))
            .Concat(spec.Columns.Where(c => c.Unique).Select(c => (Columns: (IReadOnlyList<string>)new[] { c.Name }, Unique: true)));
        foreach (var (cols, unique) in wanted)
        {
            var key = string.Join(",", cols.Select(c => c.ToLowerInvariant()));
            // An index over exactly these columns already there (the CMS created one earlier) is enough — a second would only cost writes.
            if (present.Any(p => p.Columns == key && (p.Unique || !unique))) continue;
            var name = (unique ? "ux_" : "ix_") + physical + "_" + string.Join("_", cols).ToLowerInvariant();
            await ExecAsync(db, $"CREATE {(unique ? "UNIQUE " : "")}INDEX IF NOT EXISTS {Q(name)} ON {Q(physical)} ({string.Join(",", cols.Select(Q))})", ct);
        }
    }

    private static string SchemaToJson(TableSpec spec) => JsonSerializer.Serialize(new
    {
        spec.Name, spec.Adopts, spec.KeyColumn,
        Columns = spec.Columns.Select(c => new { c.Name, Type = c.Type.ToString(), c.Required, c.Unique, c.Default }),
        Indexes = spec.Indexes.Select(i => new { i.Columns, i.Unique })
    });

    // ---- describe, drop, export, import ---------------------------------------------------------

    /// <summary>One table of a plugin with its row count.</summary>
    public sealed record TableInfo(string Physical, string Name, bool Adopted, long Rows);

    /// <summary>The tables a plugin owns and how many rows they hold (for the "delete plugin" warning and the plugin page).</summary>
    public async Task<IReadOnlyList<TableInfo>> DescribeAsync(AppDbContext db, string pluginKey, CancellationToken ct = default)
    {
        var list = new List<TableInfo>();
        if (!await MetaExistsAsync(db, ct)) return list;
        foreach (var (physical, name, adopted) in await OwnedAsync(db, pluginKey, ct))
        {
            if ((await ColumnsOfAsync(db, physical, ct)).Count == 0) continue;
            var rows = Convert.ToInt64(await ScalarAsync(db, $"SELECT COUNT(*) FROM {Q(physical)}", ct) ?? 0L, CultureInfo.InvariantCulture);
            list.Add(new TableInfo(physical, name, adopted, rows));
        }
        return list;
    }

    /// <summary>
    /// ALL text and JSON of every plugin table, concatenated — for the cleanup's "is this file mentioned anywhere" search. It reads
    /// every plugin's tables whether the plugin is loaded or not: a shop that is switched off for a week must not have its product
    /// images offered for deletion. (Tables a plugin adopted are included; the CMS's own search reads them again, which is harmless.)
    /// </summary>
    public async Task<string> CorpusAsync(AppDbContext db, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        if (!await MetaExistsAsync(db, ct)) return "";
        var owned = new List<(string Physical, string Schema)>();
        await ReadAsync(db, $"SELECT \"PhysicalName\", \"SchemaJson\" FROM {Q(MetaTable)}", ct, r => owned.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1))), Array.Empty<(string, object?)>());
        foreach (var (physical, _) in owned)
        {
            var cols = await ColumnNamesAsync(db, physical, ct);
            if (cols.Count == 0) continue;
            // Every column is read as text: a file name can sit in a text, a JSON or (rarely) any other column.
            await ReadAsync(db, $"SELECT {string.Join(",", cols.Select(c => "CAST(" + Q(c) + " AS TEXT)"))} FROM {Q(physical)}", ct, r =>
            {
                for (var i = 0; i < cols.Count; i++) if (!r.IsDBNull(i)) { sb.Append(r.GetString(i)); sb.Append('\n'); }
            }, Array.Empty<(string, object?)>());
        }
        return sb.ToString();
    }

    /// <summary>Deletes a plugin's tables and their bookkeeping. A table the plugin only adopted belongs to the CMS and stays.</summary>
    public async Task DropAsync(AppDbContext db, string pluginKey, CancellationToken ct = default)
    {
        if (!await MetaExistsAsync(db, ct)) return;
        foreach (var (physical, _, adopted) in await OwnedAsync(db, pluginKey, ct))
        {
            if (!adopted) await ExecAsync(db, $"DROP TABLE IF EXISTS {Q(physical)}", ct);
            await ExecAsync(db, $"DELETE FROM {Q(MetaTable)} WHERE \"PhysicalName\" = $p", ct, ("$p", physical));
        }
    }

    /// <summary>A plugin's tables with schema and rows, as the JSON the backup stores.</summary>
    public async Task<JsonObject> ExportAsync(AppDbContext db, string pluginKey, CancellationToken ct = default)
    {
        var tables = new JsonObject();
        if (!await MetaExistsAsync(db, ct)) return tables;
        foreach (var (physical, name, adopted) in await OwnedAsync(db, pluginKey, ct))
        {
            // A table the plugin only adopted belongs to the CMS and travels in the core backup's own section.
            if (adopted) continue;
            var cols = (await ColumnNamesAsync(db, physical, ct));
            if (cols.Count == 0) continue;
            var schema = await ScalarAsync(db, $"SELECT \"SchemaJson\" FROM {Q(MetaTable)} WHERE \"PhysicalName\" = $p", ct, ("$p", physical)) as string ?? "";
            var rows = new JsonArray();
            await ReadAsync(db, $"SELECT {string.Join(",", cols.Select(Q))} FROM {Q(physical)}", ct, r =>
            {
                var o = new JsonObject();
                for (var i = 0; i < cols.Count; i++)
                {
                    if (r.IsDBNull(i)) { o[cols[i]] = null; continue; }
                    var v = r.GetValue(i);
                    o[cols[i]] = v switch { long l => l, int n => n, double d => d, string s => s, byte[] b => Convert.ToBase64String(b), _ => v.ToString() };
                }
                rows.Add(o);
            }, Array.Empty<(string, object?)>());
            tables[name] = new JsonObject
            {
                ["physical"] = physical, ["adopted"] = adopted,
                ["schema"] = string.IsNullOrEmpty(schema) ? null : JsonNode.Parse(schema),
                ["rows"] = rows
            };
        }
        return tables;
    }

    /// <summary>
    /// Puts a plugin's backed-up tables back: replaces the rows of each table. The destination is NEVER taken from the
    /// backup — it is recomputed from the plugin key and the table's own name, and a table the plugin adopts is only restored
    /// when <paramref name="adoptable"/> allows exactly that name. A backup cannot name <c>Users</c> and overwrite it.
    /// </summary>
    /// <param name="adoptable">The core tables a built-in plugin of this key may restore into (empty for every other plugin).</param>
    public async Task ImportAsync(AppDbContext db, string pluginKey, JsonObject tables, IReadOnlyCollection<string> adoptable, CancellationToken ct = default)
    {
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await OpenAsync(db, ct);
        try
        {
            await ExecAsync(db, $"CREATE TABLE IF NOT EXISTS {Q(MetaTable)} (\"PhysicalName\" TEXT NOT NULL PRIMARY KEY, \"PluginKey\" TEXT NOT NULL, \"TableName\" TEXT NOT NULL, \"Adopted\" INTEGER NOT NULL DEFAULT 0, \"SchemaJson\" TEXT NOT NULL DEFAULT '')", ct);
            foreach (var (name, node) in tables)
            {
                if (node is not JsonObject t || !Regex.IsMatch(name, "^[a-z][a-z0-9_]{0,39}$")) continue;
                var adopted = t["adopted"]?.GetValue<bool>() == true;
                var spec = SpecFromJson(name, t["schema"] as JsonObject);
                string physical;
                if (adopted)
                {
                    var wanted = spec?.Adopts ?? "";
                    if (!adoptable.Contains(wanted, StringComparer.Ordinal)) continue;       // refused: not this plugin's to touch
                    physical = wanted;
                }
                else physical = PhysicalName(pluginKey, spec ?? new TableSpec(name, Array.Empty<ColumnSpec>(), Array.Empty<IndexSpec>()));
                if (spec is null) continue;

                var owner = await ScalarAsync(db, $"SELECT \"PluginKey\" FROM {Q(MetaTable)} WHERE \"PhysicalName\" = $p", ct, ("$p", physical));
                if (owner is string o && !string.Equals(o, pluginKey, StringComparison.Ordinal)) continue;   // someone else's table

                var existing = await ColumnsOfAsync(db, physical, ct);
                if (existing.Count == 0) { await CreateTableAsync(db, physical, spec, ct); existing = await ColumnsOfAsync(db, physical, ct); }
                else await AddMissingColumnsAsync(db, physical, spec, existing, ct);
                await EnsureIndexesAsync(db, physical, spec, ct);

                await ExecAsync(db, $"DELETE FROM {Q(physical)}", ct);
                var knownColumns = (await ColumnNamesAsync(db, physical, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var rowNode in t["rows"] as JsonArray ?? new JsonArray())
                {
                    if (rowNode is not JsonObject row) continue;
                    var cols = row.Where(kv => knownColumns.Contains(kv.Key) && Ident.IsMatch(kv.Key)).ToList();
                    if (cols.Count == 0) continue;
                    var ps = cols.Select((kv, i) => ("$v" + i, (object?)JsonToDb(kv.Value))).ToArray();
                    await ExecAsync(db, $"INSERT INTO {Q(physical)} ({string.Join(",", cols.Select(kv => Q(kv.Key)))}) VALUES ({string.Join(",", ps.Select(p => p.Item1))})", ct, ps);
                }
                await ExecAsync(db,
                    $"INSERT INTO {Q(MetaTable)} (\"PhysicalName\",\"PluginKey\",\"TableName\",\"Adopted\",\"SchemaJson\") VALUES ($p,$k,$t,$a,$s) " +
                    "ON CONFLICT(\"PhysicalName\") DO UPDATE SET \"TableName\"=$t, \"Adopted\"=$a, \"SchemaJson\"=$s", ct,
                    ("$p", physical), ("$k", pluginKey), ("$t", name), ("$a", adopted ? 1 : 0), ("$s", SchemaToJson(spec)));
            }
            if (own is not null) await own.CommitAsync(ct);
        }
        catch
        {
            if (own is not null) await own.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally { own?.Dispose(); }
    }

    private static object? JsonToDb(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? 1L : 0L,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => n.ToJsonString()
    };

    private static TableSpec? SpecFromJson(string name, JsonObject? schema)
    {
        if (schema is null) return null;
        var cols = new List<ColumnSpec>();
        foreach (var c in schema["Columns"] as JsonArray ?? new JsonArray())
        {
            if (c is not JsonObject co) continue;
            var cn = co["Name"]?.GetValue<string>();
            if (cn is null || !Ident.IsMatch(cn) || !Enum.TryParse<ColumnType>(co["Type"]?.GetValue<string>(), out var type)) continue;
            cols.Add(new ColumnSpec(cn, type) { Required = co["Required"]?.GetValue<bool>() == true, Unique = co["Unique"]?.GetValue<bool>() == true, Default = co["Default"]?.GetValue<string>() });
        }
        var idx = new List<IndexSpec>();
        foreach (var i in schema["Indexes"] as JsonArray ?? new JsonArray())
        {
            if (i is not JsonObject io) continue;
            var ic = (io["Columns"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => Ident.IsMatch(x)).ToList() ?? new List<string>();
            if (ic.Count > 0 && ic.All(x => cols.Any(c => string.Equals(c.Name, x, StringComparison.OrdinalIgnoreCase)))) idx.Add(new IndexSpec(ic, io["Unique"]?.GetValue<bool>() == true));
        }
        var adopts = schema["Adopts"]?.GetValue<string>();
        var key = schema["KeyColumn"]?.GetValue<string>();
        return new TableSpec(name, cols, idx)
        {
            Adopts = adopts is not null && Ident.IsMatch(adopts) ? adopts : null,
            KeyColumn = key is not null && Ident.IsMatch(key) ? key : "id"
        };
    }

    // ---- ADO helpers ----------------------------------------------------------------------------

    internal static async Task<DbConnection> OpenAsync(AppDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        return db.Database.GetDbConnection();
    }

    private static DbCommand Command(AppDbContext db, string sql, params (string Name, object? Value)[] ps)
    {
        var conn = db.Database.GetDbConnection();
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var tx = db.Database.CurrentTransaction?.GetDbTransaction();
        if (tx is not null) cmd.Transaction = tx;
        foreach (var (name, value) in ps)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name; p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    internal static async Task<int> ExecAsync(AppDbContext db, string sql, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        await OpenAsync(db, ct);
        await using var cmd = Command(db, sql, ps);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<object?> ScalarAsync(AppDbContext db, string sql, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        await OpenAsync(db, ct);
        await using var cmd = Command(db, sql, ps);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    internal static async Task ReadAsync(AppDbContext db, string sql, CancellationToken ct, Action<DbDataReader> each, (string Name, object? Value)[] ps)
    {
        await OpenAsync(db, ct);
        await using var cmd = Command(db, sql, ps);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) each(r);
    }

    private static async Task<bool> MetaExistsAsync(AppDbContext db, CancellationToken ct) =>
        await ScalarAsync(db, "SELECT 1 FROM sqlite_master WHERE type='table' AND name = $n", ct, ("$n", MetaTable)) is not null;

    private static async Task<List<(string Physical, string Name, bool Adopted)>> OwnedAsync(AppDbContext db, string pluginKey, CancellationToken ct)
    {
        var list = new List<(string, string, bool)>();
        await ReadAsync(db, $"SELECT \"PhysicalName\", \"TableName\", \"Adopted\" FROM {Q(MetaTable)} WHERE \"PluginKey\" = $k ORDER BY \"TableName\"", ct,
            r => list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2) != 0)), new[] { ("$k", (object?)pluginKey) });
        return list;
    }

    internal static async Task<List<string>> ColumnNamesAsync(AppDbContext db, string table, CancellationToken ct)
    {
        var list = new List<string>();
        await ReadAsync(db, $"PRAGMA table_info({Q(table)})", ct, r => list.Add(r.GetString(1)), Array.Empty<(string, object?)>());
        return list;
    }

    private static async Task<HashSet<string>> ColumnsOfAsync(AppDbContext db, string table, CancellationToken ct) =>
        (await ColumnNamesAsync(db, table, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task<List<(string Columns, bool Unique)>> IndexesOfAsync(AppDbContext db, string table, CancellationToken ct)
    {
        var names = new List<(string Name, bool Unique)>();
        await ReadAsync(db, $"PRAGMA index_list({Q(table)})", ct, r => names.Add((r.GetString(1), r.GetInt64(2) != 0)), Array.Empty<(string, object?)>());
        var result = new List<(string, bool)>();
        foreach (var (name, unique) in names)
        {
            var cols = new List<string>();
            await ReadAsync(db, $"PRAGMA index_info({Q(name)})", ct, r => { if (!r.IsDBNull(2)) cols.Add(r.GetString(2).ToLowerInvariant()); }, Array.Empty<(string, object?)>());
            result.Add((string.Join(",", cols), unique));
        }
        return result;
    }
}
