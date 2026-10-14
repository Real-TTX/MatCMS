using System.Text.Json.Nodes;

namespace MatCMS.Plugin;

// Plugin data, layer 1 — the part both kinds of plugin share. A plugin DECLARES its tables while it configures
// (no database access then) and the host creates and updates them when the plugin is loaded; at run time it
// reads and writes rows through ctx.Data. Backup, restore, the cleanup and "delete plugin" know these tables.
//
// A native plugin may additionally bring its own EF context for what this layer does not express (joins,
// reports); that is the one named difference between the kinds and not part of this file.

/// <summary>The column types the host knows how to create, read and compare.</summary>
public enum ColumnType
{
    /// <summary>Text.</summary>
    Text,
    /// <summary>A 64-bit whole number. Money belongs here, in the smallest unit (cents): there is no exact decimal in SQLite.</summary>
    Int,
    /// <summary>A floating-point number. Never for money.</summary>
    Real,
    /// <summary>True or false.</summary>
    Bool,
    /// <summary>A point in time, kept in UTC; sorts and compares correctly.</summary>
    DateTime,
    /// <summary>A JSON document, kept as text.</summary>
    Json
}

/// <summary>One declared column.</summary>
/// <param name="Name">The column name: lower-case letters, digits and underscores.</param>
/// <param name="Type">Its type.</param>
public sealed record ColumnSpec(string Name, ColumnType Type)
{
    /// <summary>A value is required (<c>NOT NULL</c>). Adding a required column to an existing table needs a <see cref="Default"/>.</summary>
    public bool Required { get; init; }
    /// <summary>No two rows share a value.</summary>
    public bool Unique { get; init; }
    /// <summary>The value of a row that does not set the column, written as the literal it is (<c>0</c>, <c>draft</c>).</summary>
    public string? Default { get; init; }
}

/// <summary>One declared index.</summary>
/// <param name="Columns">The columns, in order.</param>
/// <param name="Unique">No two rows share the combination.</param>
public sealed record IndexSpec(IReadOnlyList<string> Columns, bool Unique);

/// <summary>A declared table.</summary>
/// <param name="Name">The plugin's own name for it (<c>products</c>).</param>
/// <param name="Columns">The columns besides the implicit <c>id</c> key.</param>
/// <param name="Indexes">The indexes.</param>
public sealed record TableSpec(string Name, IReadOnlyList<ColumnSpec> Columns, IReadOnlyList<IndexSpec> Indexes)
{
    /// <summary>The name of an existing table the plugin takes over instead of creating one (built-in plugins only —
    /// it is how the blog keeps its <c>Posts</c> table). Null for an ordinary plugin table.</summary>
    public string? Adopts { get; init; }
    /// <summary>The key column of an adopted table (<c>Id</c> for tables the CMS created earlier); <c>id</c> otherwise.</summary>
    public string KeyColumn { get; init; } = "id";
}

/// <summary>Declares the columns of one table. Every column is nullable and without a default unless it says otherwise.</summary>
public interface ITableBuilder
{
    /// <summary>A text column.</summary>
    ITableBuilder Text(string name, bool required = false, bool unique = false, string? def = null);
    /// <summary>A whole-number column (also the right type for money in cents).</summary>
    ITableBuilder Int(string name, bool required = false, string? def = null);
    /// <summary>A floating-point column.</summary>
    ITableBuilder Real(string name, bool required = false, string? def = null);
    /// <summary>A true/false column.</summary>
    ITableBuilder Bool(string name, bool required = false, string? def = null);
    /// <summary>A UTC date-and-time column.</summary>
    ITableBuilder Timestamp(string name, bool required = false);
    /// <summary>A JSON column.</summary>
    ITableBuilder Json(string name, bool required = false, string? def = null);
    /// <summary>An index over one or more columns.</summary>
    ITableBuilder Index(params string[] columns);
    /// <summary>A unique index over one or more columns.</summary>
    ITableBuilder UniqueIndex(params string[] columns);
    /// <summary>Takes over an existing table. Built-in plugins only; an ordinary plugin gets an error when it tries.</summary>
    ITableBuilder Adopt(string existingTable, string keyColumn = "Id");
}

/// <summary>
/// Declares a plugin's tables. The host reconciles the declaration with the database whenever the plugin loads:
/// a missing table is created, a missing column or index is added. It <b>never</b> drops, renames or retypes anything —
/// a change like that is a data migration the plugin does itself in <see cref="ILifecycleBuilder.OnUpdate(PluginUpdateHandler)"/>.
/// </summary>
public interface IDataBuilder
{
    /// <summary>Declares a table. Its name: lower-case letters, digits and underscores, at most 40 characters.</summary>
    void Table(string name, Action<ITableBuilder> define);
}

/// <summary>A comparison of a query.</summary>
public enum DataOp
{
    /// <summary>Equal.</summary>
    Eq,
    /// <summary>Not equal.</summary>
    Ne,
    /// <summary>Less than.</summary>
    Lt,
    /// <summary>Less than or equal.</summary>
    Le,
    /// <summary>Greater than.</summary>
    Gt,
    /// <summary>Greater than or equal.</summary>
    Ge,
    /// <summary>The text contains the value (case-insensitive for ASCII).</summary>
    Contains,
    /// <summary>The text starts with the value.</summary>
    StartsWith,
    /// <summary>The value is in the given collection.</summary>
    In,
    /// <summary>The column is empty (NULL).</summary>
    IsNull,
    /// <summary>The column has a value.</summary>
    NotNull
}

/// <summary>One condition of a query.</summary>
/// <param name="Column">The column name.</param>
/// <param name="Op">How to compare.</param>
/// <param name="Value">The value: text, a number, a bool, a <see cref="DateTime"/>, a <see cref="JsonNode"/>, or for <see cref="DataOp.In"/> a collection of those.</param>
public sealed record DataFilter(string Column, DataOp Op, object? Value = null);

/// <summary>
/// A query: conditions joined with AND (plus groups of which ONE must hold), an order and a window. Immutable —
/// every method returns a new query, so a base query can be kept and extended. All values travel as parameters, and a
/// column name that the table does not declare is refused; nothing a visitor types can become SQL.
/// </summary>
public sealed record DataQuery
{
    /// <summary>The conditions that all must hold.</summary>
    public IReadOnlyList<DataFilter> Filters { get; init; } = Array.Empty<DataFilter>();
    /// <summary>Groups of conditions of which at least one must hold (an OR), each group in addition to <see cref="Filters"/>.</summary>
    public IReadOnlyList<IReadOnlyList<DataFilter>> AnyOf { get; init; } = Array.Empty<IReadOnlyList<DataFilter>>();
    /// <summary>The sort order, most important first.</summary>
    public IReadOnlyList<(string Column, bool Descending)> Order { get; init; } = Array.Empty<(string, bool)>();
    /// <summary>Rows to skip.</summary>
    public int Skip { get; init; }
    /// <summary>Rows to return at most; 0 = all.</summary>
    public int Take { get; init; }

    /// <summary>A query without conditions: every row.</summary>
    public static DataQuery Everything => new();

    /// <summary>Adds a condition.</summary>
    public DataQuery Where(string column, DataOp op, object? value = null) => this with { Filters = Filters.Append(new DataFilter(column, op, value)).ToList() };
    /// <summary>Adds an equality condition.</summary>
    public DataQuery Eq(string column, object? value) => Where(column, DataOp.Eq, value);
    /// <summary>Adds a group of which at least one condition must hold.</summary>
    public DataQuery Either(params DataFilter[] anyOf) => this with { AnyOf = AnyOf.Append(anyOf.ToList()).ToList() };
    /// <summary>Adds a sort key.</summary>
    public DataQuery OrderBy(string column, bool descending = false) => this with { Order = Order.Append((column, descending)).ToList() };
    /// <summary>Sets the window.</summary>
    public DataQuery Page(int skip, int take) => this with { Skip = Math.Max(0, skip), Take = Math.Max(0, take) };
}

/// <summary>One row, read case-insensitively by column name. The accessors return the type's empty value for a NULL;
/// use the indexer to tell NULL apart.</summary>
public sealed class DataRow : IReadOnlyDictionary<string, object?>
{
    private readonly Dictionary<string, object?> _values;

    /// <summary>Creates a row from column values. The host builds these; a test can too.</summary>
    public DataRow(long id, IEnumerable<KeyValuePair<string, object?>> values)
    {
        Id = id;
        _values = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The row's key.</summary>
    public long Id { get; }
    /// <summary>A text column, or "".</summary>
    public string Text(string column) => _values.TryGetValue(column, out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "";
    /// <summary>A whole-number column, or 0.</summary>
    public long Int(string column) => _values.TryGetValue(column, out var v) && v is not null ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) : 0;
    /// <summary>A floating-point column, or 0.</summary>
    public double Real(string column) => _values.TryGetValue(column, out var v) && v is not null ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : 0;
    /// <summary>A true/false column, or false.</summary>
    public bool Bool(string column) => _values.TryGetValue(column, out var v) && v is not null && Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) != 0;
    /// <summary>A date-and-time column in UTC, or null.</summary>
    public DateTime? Timestamp(string column) => _values.TryGetValue(column, out var v) && v is DateTime d ? d : null;
    /// <summary>A JSON column, or null.</summary>
    public JsonNode? Json(string column)
    {
        var t = Text(column);
        if (t.Length == 0) return null;
        try { return JsonNode.Parse(t); } catch (System.Text.Json.JsonException) { return null; }
    }
    /// <summary>True when the column is NULL or does not exist.</summary>
    public bool IsNull(string column) => !_values.TryGetValue(column, out var v) || v is null;

    /// <inheritdoc />
    public object? this[string key] => _values[key];
    /// <inheritdoc />
    public IEnumerable<string> Keys => _values.Keys;
    /// <inheritdoc />
    public IEnumerable<object?> Values => _values.Values;
    /// <inheritdoc />
    public int Count => _values.Count;
    /// <inheritdoc />
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    /// <inheritdoc />
    public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);
    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One of the plugin's tables at run time.</summary>
public interface IDataTable
{
    /// <summary>Inserts a row and returns its key. Columns not mentioned get their default.</summary>
    Task<long> InsertAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct = default);
    /// <summary>The row with this key, or null.</summary>
    Task<DataRow?> GetAsync(long id, CancellationToken ct = default);
    /// <summary>The first row matching the query, or null.</summary>
    Task<DataRow?> FindAsync(DataQuery query, CancellationToken ct = default);
    /// <summary>The rows matching the query.</summary>
    Task<IReadOnlyList<DataRow>> QueryAsync(DataQuery query, CancellationToken ct = default);
    /// <summary>The number of rows matching the query (all rows when null).</summary>
    Task<long> CountAsync(DataQuery? query = null, CancellationToken ct = default);
    /// <summary>Changes the given columns of one row. Returns 0 when there is no such row.</summary>
    Task<int> UpdateAsync(long id, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default);
    /// <summary>Changes the given columns of every row matching the query. Returns the number changed.</summary>
    Task<int> UpdateWhereAsync(DataQuery where, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default);
    /// <summary>Deletes one row. Returns 0 when there is no such row.</summary>
    Task<int> DeleteAsync(long id, CancellationToken ct = default);
    /// <summary>Deletes every row matching the query. Returns the number deleted.</summary>
    Task<int> DeleteWhereAsync(DataQuery where, CancellationToken ct = default);
}

/// <summary>A plugin's data at run time.</summary>
public interface IPluginData
{
    /// <summary>One of the tables the plugin declared. An undeclared name throws.</summary>
    IDataTable Table(string name);

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction: everything it writes is kept if it returns, and undone if it throws.
    /// The <see cref="IPluginData"/> it receives is the one to use inside; the outer one does not take part.
    /// </summary>
    Task<T> InTransactionAsync<T>(Func<IPluginData, Task<T>> work, CancellationToken ct = default);
}

/// <summary>Convenience overloads for data.</summary>
public static class PluginDataExtensions
{
    /// <summary>A transaction without a result.</summary>
    public static Task InTransactionAsync(this IPluginData data, Func<IPluginData, Task> work, CancellationToken ct = default) =>
        data.InTransactionAsync<bool>(async d => { await work(d); return true; }, ct);

    /// <summary>The first row whose <paramref name="column"/> equals <paramref name="value"/>, or null.</summary>
    public static Task<DataRow?> FindByAsync(this IDataTable table, string column, object? value, CancellationToken ct = default) =>
        table.FindAsync(DataQuery.Everything.Eq(column, value), ct);
}
