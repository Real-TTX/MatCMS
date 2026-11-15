using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MatCMS.Data;

/// <summary>Sets SQLite's own busy timeout on every connection the app opens. Microsoft.Data.Sqlite retries a busy database only
/// on a statement's FIRST step; EF saves with <c>… RETURNING</c>, whose commit happens on a LATER step, so a save that met another
/// writer failed at once with "database is locked" (the shop's start in the tests, a plugin's mail templates and settings under
/// load). With the native timeout SQLite itself waits on every step.</summary>
public sealed class SqliteBusyTimeout : DbConnectionInterceptor
{
    public static readonly SqliteBusyTimeout Instance = new();
    private const string Pragma = "PRAGMA busy_timeout = 30000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragma;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragma;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
