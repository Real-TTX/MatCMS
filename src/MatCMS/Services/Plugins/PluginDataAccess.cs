using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Plugin;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Plugins;

/// <summary>A plugin's tables at run time (<see cref="IPluginData"/>): one per request, over that request's database context.</summary>
internal sealed class PluginDataAccess : IPluginData
{
    private readonly AppDbContext _db;
    private readonly string _key;
    private readonly IReadOnlyList<TableSpec> _specs;

    public PluginDataAccess(AppDbContext db, string pluginKey, IReadOnlyList<TableSpec> specs)
    {
        _db = db; _key = pluginKey; _specs = specs;
    }

    public IDataTable Table(string name)
    {
        var spec = _specs.FirstOrDefault(s => s.Name == name)
                   ?? throw new InvalidOperationException($"Das Plugin '{_key}' hat keine Tabelle '{name}' deklariert.");
        return new PluginTable(_db, PluginDataService.PhysicalName(_key, spec), spec);
    }

    public async Task<T> InTransactionAsync<T>(Func<IPluginData, Task<T>> work, CancellationToken ct = default)
    {
        // Already inside one (a nested call): everything shares the connection's transaction, so just run.
        if (_db.Database.CurrentTransaction is not null) return await work(this);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await work(this);
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private sealed class PluginTable : IDataTable
    {
        private readonly AppDbContext _db;
        private readonly string _physical;
        private readonly TableSpec _spec;
        private readonly Dictionary<string, ColumnSpec> _columns;

        public PluginTable(AppDbContext db, string physical, TableSpec spec)
        {
            _db = db; _physical = physical; _spec = spec;
            _columns = spec.Columns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
        }

        private string Key => "\"" + _spec.KeyColumn + "\"";
        private string Table => "\"" + _physical + "\"";
        private string SelectList => Key + string.Concat(_spec.Columns.Select(c => ", \"" + c.Name + "\""));

        private ColumnSpec Column(string name) =>
            _columns.TryGetValue(name, out var c) ? c : throw new ArgumentException($"Die Tabelle '{_spec.Name}' hat keine Spalte '{name}'.", nameof(name));

        // ---- values -----------------------------------------------------------------------------

        private object? ToDb(ColumnSpec c, object? v)
        {
            if (v is null) return null;
            try
            {
                switch (c.Type)
                {
                    case ColumnType.Text: return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
                    case ColumnType.Int: return v is bool b ? (b ? 1L : 0L) : Convert.ToInt64(v, CultureInfo.InvariantCulture);
                    case ColumnType.Real: return Convert.ToDouble(v, CultureInfo.InvariantCulture);
                    case ColumnType.Bool: return v is bool bb ? (bb ? 1L : 0L) : Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0 ? 1L : 0L;
                    case ColumnType.DateTime:
                        return v switch
                        {
                            DateTime d => AsUtc(d).ToString(PluginDataService.TimeFormat, CultureInfo.InvariantCulture),
                            DateTimeOffset o => o.UtcDateTime.ToString(PluginDataService.TimeFormat, CultureInfo.InvariantCulture),
                            _ => throw new ArgumentException("not a date")
                        };
                    default: return v is JsonNode n ? n.ToJsonString() : v as string ?? throw new ArgumentException("not JSON");
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException or OverflowException)
            {
                throw new ArgumentException($"Der Wert für '{_spec.Name}.{c.Name}' passt nicht zum Typ {c.Type}.", c.Name, ex);
            }
        }

        private static DateTime AsUtc(DateTime d) => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

        private object? FromDb(ColumnSpec c, object? v)
        {
            if (v is null or DBNull) return null;
            return c.Type switch
            {
                ColumnType.Int => Convert.ToInt64(v, CultureInfo.InvariantCulture),
                ColumnType.Real => Convert.ToDouble(v, CultureInfo.InvariantCulture),
                ColumnType.Bool => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0,
                ColumnType.DateTime => DateTime.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null,
                _ => Convert.ToString(v, CultureInfo.InvariantCulture)
            };
        }

        private DataRow Materialise(System.Data.Common.DbDataReader r)
        {
            var id = r.GetInt64(0);
            var values = new List<KeyValuePair<string, object?>>();
            for (var i = 0; i < _spec.Columns.Count; i++)
                values.Add(new(_spec.Columns[i].Name, FromDb(_spec.Columns[i], r.GetValue(i + 1))));
            return new DataRow(id, values);
        }

        // ---- where ------------------------------------------------------------------------------

        private string Where(DataQuery? q, List<(string, object?)> ps)
        {
            if (q is null) return "";
            var parts = new List<string>();
            foreach (var f in q.Filters) parts.Add(Condition(f, ps));
            foreach (var group in q.AnyOf)
                if (group.Count > 0) parts.Add("(" + string.Join(" OR ", group.Select(f => Condition(f, ps))) + ")");
            return parts.Count == 0 ? "" : " WHERE " + string.Join(" AND ", parts);
        }

        private string Condition(DataFilter f, List<(string, object?)> ps)
        {
            // The key column can be filtered too (Eq/In on the id); every other column must be declared.
            ColumnSpec c;
            string col;
            if (string.Equals(f.Column, _spec.KeyColumn, StringComparison.OrdinalIgnoreCase)) { c = new ColumnSpec(_spec.KeyColumn, ColumnType.Int); col = Key; }
            else { c = Column(f.Column); col = "\"" + c.Name + "\""; }
            string P(object? v) { var n = "$f" + ps.Count; ps.Add((n, v)); return n; }
            return f.Op switch
            {
                DataOp.Eq => f.Value is null ? col + " IS NULL" : col + " = " + P(ToDb(c, f.Value)),
                DataOp.Ne => f.Value is null ? col + " IS NOT NULL" : "(" + col + " IS NULL OR " + col + " <> " + P(ToDb(c, f.Value)) + ")",
                DataOp.Lt => col + " < " + P(ToDb(c, f.Value)),
                DataOp.Le => col + " <= " + P(ToDb(c, f.Value)),
                DataOp.Gt => col + " > " + P(ToDb(c, f.Value)),
                DataOp.Ge => col + " >= " + P(ToDb(c, f.Value)),
                DataOp.Contains => col + " LIKE " + P("%" + EscapeLike(Convert.ToString(f.Value, CultureInfo.InvariantCulture) ?? "") + "%") + " ESCAPE '\\'",
                DataOp.StartsWith => col + " LIKE " + P(EscapeLike(Convert.ToString(f.Value, CultureInfo.InvariantCulture) ?? "") + "%") + " ESCAPE '\\'",
                DataOp.IsNull => col + " IS NULL",
                DataOp.NotNull => col + " IS NOT NULL",
                DataOp.In => InList(col, c, f.Value, P),
                _ => throw new ArgumentOutOfRangeException(nameof(f))
            };
        }

        private string InList(string col, ColumnSpec c, object? value, Func<object?, string> p)
        {
            if (value is string || value is not System.Collections.IEnumerable items) throw new ArgumentException("DataOp.In needs a collection.");
            var names = new List<string>();
            foreach (var item in items) names.Add(p(ToDb(c, item)));
            return names.Count == 0 ? "0" : col + " IN (" + string.Join(",", names) + ")";
        }

        private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        private string OrderAndPage(DataQuery? q)
        {
            if (q is null) return "";
            var sb = new StringBuilder();
            if (q.Order.Count > 0)
                sb.Append(" ORDER BY ").Append(string.Join(", ", q.Order.Select(o =>
                    (string.Equals(o.Column, _spec.KeyColumn, StringComparison.OrdinalIgnoreCase) ? Key : "\"" + Column(o.Column).Name + "\"") + (o.Descending ? " DESC" : " ASC"))));
            if (q.Take > 0) sb.Append(" LIMIT ").Append(q.Take).Append(" OFFSET ").Append(q.Skip);
            else if (q.Skip > 0) sb.Append(" LIMIT -1 OFFSET ").Append(q.Skip);
            return sb.ToString();
        }

        // ---- operations -------------------------------------------------------------------------

        public async Task<long> InsertAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
        {
            var cols = values.Where(kv => !string.Equals(kv.Key, _spec.KeyColumn, StringComparison.OrdinalIgnoreCase)).Select(kv => (Spec: Column(kv.Key), kv.Value)).ToList();
            var ps = cols.Select((c, i) => ("$v" + i, ToDb(c.Spec, c.Value))).ToArray();
            var sql = cols.Count == 0
                ? $"INSERT INTO {Table} DEFAULT VALUES"
                : $"INSERT INTO {Table} ({string.Join(",", cols.Select(c => "\"" + c.Spec.Name + "\""))}) VALUES ({string.Join(",", ps.Select(p => p.Item1))})";
            await PluginDataService.ExecAsync(_db, sql, ct, ps.Select(p => (p.Item1, (object?)p.Item2)).ToArray());
            return Convert.ToInt64(await PluginDataService.ScalarAsync(_db, "SELECT last_insert_rowid()", ct), CultureInfo.InvariantCulture);
        }

        public async Task<DataRow?> GetAsync(long id, CancellationToken ct = default) =>
            await FindAsync(DataQuery.Everything.Where(_spec.KeyColumn, DataOp.Eq, id), ct);

        public async Task<DataRow?> FindAsync(DataQuery query, CancellationToken ct = default) =>
            (await QueryAsync((query ?? DataQuery.Everything).Page(query?.Skip ?? 0, 1), ct)).FirstOrDefault();

        public async Task<IReadOnlyList<DataRow>> QueryAsync(DataQuery query, CancellationToken ct = default)
        {
            var ps = new List<(string, object?)>();
            var sql = $"SELECT {SelectList} FROM {Table}{Where(query, ps)}{OrderAndPage(query)}";
            var rows = new List<DataRow>();
            await PluginDataService.ReadAsync(_db, sql, ct, r => rows.Add(Materialise(r)), ps.ToArray());
            return rows;
        }

        public async Task<long> CountAsync(DataQuery? query = null, CancellationToken ct = default)
        {
            var ps = new List<(string, object?)>();
            var v = await PluginDataService.ScalarAsync(_db, $"SELECT COUNT(*) FROM {Table}{Where(query, ps)}", ct, ps.ToArray());
            return Convert.ToInt64(v ?? 0L, CultureInfo.InvariantCulture);
        }

        public Task<int> UpdateAsync(long id, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default) =>
            UpdateWhereAsync(DataQuery.Everything.Where(_spec.KeyColumn, DataOp.Eq, id), values, ct);

        public async Task<int> UpdateWhereAsync(DataQuery where, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
        {
            var sets = values.Where(kv => !string.Equals(kv.Key, _spec.KeyColumn, StringComparison.OrdinalIgnoreCase)).Select(kv => (Spec: Column(kv.Key), kv.Value)).ToList();
            if (sets.Count == 0) return 0;
            var ps = new List<(string, object?)>();
            var assignments = sets.Select((s, i) => { var n = "$s" + i; ps.Add((n, ToDb(s.Spec, s.Value))); return "\"" + s.Spec.Name + "\" = " + n; });
            var sql = $"UPDATE {Table} SET {string.Join(", ", assignments)}{Where(where, ps)}";
            return await PluginDataService.ExecAsync(_db, sql, ct, ps.ToArray());
        }

        public Task<int> DeleteAsync(long id, CancellationToken ct = default) =>
            DeleteWhereAsync(DataQuery.Everything.Where(_spec.KeyColumn, DataOp.Eq, id), ct);

        public async Task<int> DeleteWhereAsync(DataQuery where, CancellationToken ct = default)
        {
            var ps = new List<(string, object?)>();
            return await PluginDataService.ExecAsync(_db, $"DELETE FROM {Table}{Where(where, ps)}", ct, ps.ToArray());
        }
    }
}
