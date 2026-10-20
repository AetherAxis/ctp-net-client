using Ctp.Net;
using Microsoft.Data.Sqlite;

namespace Ctp.Net.Client;

/// <summary>
/// SQLite-backed <see cref="ICtpConfigStore"/>. Multi-row table <c>ctp_profile</c> holds the
/// saved CTP connection profiles（连接即自动建档，键 = 前置地址 + 用户号；登录窗标签切换）.
/// <c>is_last_used</c> 全表唯一（<see cref="SaveAsync"/> 事务内维护），驱动启动自动登录。
/// Legacy single-row table <c>ctp_config</c> 原样保留（迁移种子源 + 回滚安全，新代码不再读它）——
/// 首次启动幂等迁移成一个 last-used profile。WAL + busy_timeout set.
/// </summary>
public sealed class SqliteCtpConfigStore : SqliteStoreBase, ICtpConfigStore
{
    private const string LegacyTableName = "ctp_config";
    private const string TableName = "ctp_profile";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + LegacyTableName + " (" +
        "id INTEGER PRIMARY KEY CHECK (id = 1), " +
        "front_address TEXT NOT NULL, md_front_address TEXT NOT NULL, " +
        "broker_id TEXT NOT NULL, user_id TEXT NOT NULL, password TEXT NOT NULL, " +
        "app_id TEXT NOT NULL, auth_code TEXT NOT NULL, auto_login INTEGER NOT NULL DEFAULT 0); " +
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id TEXT NOT NULL PRIMARY KEY, " +
        "name TEXT NOT NULL, " +
        "front_address TEXT NOT NULL, md_front_address TEXT NOT NULL, " +
        "broker_id TEXT NOT NULL, user_id TEXT NOT NULL, password TEXT NOT NULL, " +
        "app_id TEXT NOT NULL, auth_code TEXT NOT NULL, " +
        "auto_login INTEGER NOT NULL DEFAULT 0, is_last_used INTEGER NOT NULL DEFAULT 0)";

    private static readonly string SelectColumns =
        "id, name, front_address, md_front_address, broker_id, user_id, password, app_id, auth_code, auto_login, is_last_used";

    public SqliteCtpConfigStore(string connectionString) : base(connectionString) { }

    public static SqliteCtpConfigStore ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    /// <inheritdoc/>
    public async Task<CtpConnectionConfig?> GetAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // LIMIT 1 防御脏数据（唯一性由 SaveAsync 维护）
        cmd.CommandText = $"SELECT {SelectColumns} FROM {TableName} WHERE is_last_used = 1 LIMIT 1";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadOne(reader);
    }

    /// <inheritdoc/>
    public async Task<List<CtpConnectionConfig>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM {TableName} ORDER BY is_last_used DESC, name COLLATE NOCASE";
        return await ReadAllAsync(cmd, ct);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(CtpConnectionConfig config, CancellationToken ct = default)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            if (config.IsLastUsed)
            {
                // 唯一性不变量：清掉其它行的 last-used 旗标（IsLastUsed=false 的保存不降级现任）
                await using var demote = conn.CreateCommand();
                demote.Transaction = tx;
                demote.CommandText = $"UPDATE {TableName} SET is_last_used = 0 WHERE is_last_used = 1 AND id != @id";
                demote.Parameters.AddWithValue("@id", config.Id.ToString());
                await demote.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"INSERT OR REPLACE INTO {TableName} ({SelectColumns}) " +
                "VALUES (@id, @n, @f, @m, @b, @u, @p, @a, @c, @al, @l)";
            cmd.Parameters.AddWithValue("@id", config.Id.ToString());
            cmd.Parameters.AddWithValue("@n", config.Name);
            cmd.Parameters.AddWithValue("@f", config.FrontAddress);
            cmd.Parameters.AddWithValue("@m", config.MdFrontAddress);
            cmd.Parameters.AddWithValue("@b", config.BrokerId);
            cmd.Parameters.AddWithValue("@u", config.UserId);
            cmd.Parameters.AddWithValue("@p", config.Password);
            cmd.Parameters.AddWithValue("@a", config.AppId);
            cmd.Parameters.AddWithValue("@c", config.AuthCode);
            cmd.Parameters.AddWithValue("@al", config.AutoLogin ? 1 : 0);
            cmd.Parameters.AddWithValue("@l", config.IsLastUsed ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 迁移：①旧 ctp_config 无 auto_login 列时补列（幂等，须先跑——种子要读该列）；
    /// ②一次性种子：ctp_profile 为空且旧单行配置存在 → 迁移成一个 last-used profile
    /// （幂等：profile 表非空即跳过）。
    /// </summary>
    protected override async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        await SqliteMigrations.AddColumnAsync(connection, LegacyTableName, "auto_login",
            "INTEGER NOT NULL DEFAULT 0", ct);

        if (await CountAsync(connection, TableName, ct) > 0) return;

        await using var legacy = connection.CreateCommand();
        legacy.CommandText =
            $"SELECT front_address, md_front_address, broker_id, user_id, password, app_id, auth_code, auto_login " +
            $"FROM {LegacyTableName} WHERE id = 1";
        await using var reader = await legacy.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return; // 全新 db：旧表存在但无行 → 不种子

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            $"INSERT INTO {TableName} ({SelectColumns}) VALUES (@id, @n, @f, @m, @b, @u, @p, @a, @c, @al, 1)";
        insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("@n", reader.GetString(3)); // Name = UserId；重名时连接建档会补区分后缀
        insert.Parameters.AddWithValue("@f", reader.GetString(0));
        insert.Parameters.AddWithValue("@m", reader.GetString(1));
        insert.Parameters.AddWithValue("@b", reader.GetString(2));
        insert.Parameters.AddWithValue("@u", reader.GetString(3));
        insert.Parameters.AddWithValue("@p", reader.GetString(4));
        insert.Parameters.AddWithValue("@a", reader.GetString(5));
        insert.Parameters.AddWithValue("@c", reader.GetString(6));
        insert.Parameters.AddWithValue("@al", reader.GetInt32(7));
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long n ? n : 0;
    }

    private static CtpConnectionConfig ReadOne(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        FrontAddress = reader.GetString(2),
        MdFrontAddress = reader.GetString(3),
        BrokerId = reader.GetString(4),
        UserId = reader.GetString(5),
        Password = reader.GetString(6),
        AppId = reader.GetString(7),
        AuthCode = reader.GetString(8),
        AutoLogin = reader.GetInt32(9) != 0,
        IsLastUsed = reader.GetInt32(10) != 0
    };

    private static async Task<List<CtpConnectionConfig>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<CtpConnectionConfig>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadOne(reader));
        return list;
    }
}
