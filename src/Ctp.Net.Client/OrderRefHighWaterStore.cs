using Microsoft.Data.Sqlite;

namespace Ctp.Net.Client;

/// <summary>
/// 下单引用（OrderRef）的**本地高水位**存储（`DESIGN.md` §6 #13）。
/// <para>
/// 要解决的问题：OrderRef 原先只由柜台登录应答的 <c>MaxOrderRef</c> 决定。柜台会话一换
/// （客户端重启 / 换前置 / 换交易日）它的计数就归零，于是新会话从 1 重新发号——而「按
/// (账户, OrderRef) 定位」的前置与交易所侧可能还挂着上一会话的单，撞号就是**撤错单**。
/// </para>
/// <para>
/// 契约：<see cref="GetAsync"/> 返回该账户上次见到的最大号（没有则 0）；<see cref="SaveAsync"/>
/// 只升不降——即使调用方传了个更小的值也不会把水位拉回去（<c>max()</c> 语义）。
/// 键是 <c>(brokerId, userId)</c>：OrderRef 的唯一性范围就是投资者账号。
/// </para>
/// </summary>
public interface IOrderRefHighWaterStore
{
    /// <summary>读该账户的高水位；没有记录返回 0。</summary>
    Task<int> GetAsync(string brokerId, string userId, CancellationToken ct = default);

    /// <summary>抬高该账户的高水位（只升不降）。</summary>
    Task SaveAsync(string brokerId, string userId, int highWater, CancellationToken ct = default);
}

/// <summary>SQLite 实现：单表 <c>ctp_order_ref</c>，主键 <c>(broker_id, user_id)</c>。</summary>
public sealed class SqliteOrderRefHighWaterStore : SqliteStoreBase, IOrderRefHighWaterStore
{
    private const string TableName = "ctp_order_ref";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "broker_id TEXT NOT NULL, " +
        "user_id TEXT NOT NULL, " +
        "high_water INTEGER NOT NULL, " +
        "updated_at TEXT NOT NULL, " +
        "PRIMARY KEY (broker_id, user_id))";

    public SqliteOrderRefHighWaterStore(string connectionString) : base(connectionString) { }

    public static SqliteOrderRefHighWaterStore ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    /// <inheritdoc/>
    public async Task<int> GetAsync(string brokerId, string userId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT high_water FROM {TableName} WHERE broker_id = @b AND user_id = @u";
        cmd.Parameters.AddWithValue("@b", brokerId);
        cmd.Parameters.AddWithValue("@u", userId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long n ? (int)Math.Min(n, int.MaxValue) : 0;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(string brokerId, string userId, int highWater, CancellationToken ct = default)
    {
        if (highWater <= 0) return;   // 没发过号：不写垃圾行
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // ON CONFLICT ... max()：只升不降。并发写（两个会话同一账号）也不会把水位写回去。
        cmd.CommandText =
            $"INSERT INTO {TableName} (broker_id, user_id, high_water, updated_at) VALUES (@b, @u, @h, @t) " +
            "ON CONFLICT(broker_id, user_id) DO UPDATE SET " +
            "high_water = max(high_water, excluded.high_water), updated_at = excluded.updated_at";
        cmd.Parameters.AddWithValue("@b", brokerId);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@h", highWater);
        cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
