using Microsoft.Data.Sqlite;

namespace Ctp.Net.Client;

/// <summary>
/// Idempotent SQLite schema-migration helpers. <c>ALTER TABLE ADD COLUMN</c> throws when the
/// column already exists; these swallow that <see cref="SqliteException"/> so migrations are
/// safe to run on every init (replaces the per-store <c>MigrateAddColumn</c>/<c>EnsureColumn</c> copies).
/// </summary>
public static class SqliteMigrations
{
    /// <summary>Adds a column if absent; silently no-ops when it already exists.</summary>
    public static async Task AddColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // 列已存在——忽略（幂等迁移）。
        }
    }

    /// <summary>Synchronous overload — for ctor-time init (see <see cref="SqliteStoreBase.EnsureSchema"/>).</summary>
    public static void AddColumn(SqliteConnection connection, string table, string column, string definition)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 列已存在——忽略（幂等迁移）。
        }
    }
}
