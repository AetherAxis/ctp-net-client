using System.IO;
using Microsoft.Data.Sqlite;

namespace Ctp.Net.Client;

/// <summary>
/// Shared base for SQLite-backed stores. Collapses the copy-pasted boilerplate
/// (connection-string validation, <c>ForFile</c> factory, <c>busy_timeout</c> +
/// WAL PRAGMAs, idempotent lazy schema init, data-source path extraction) that
/// was duplicated across every business Infrastructure repo and the platform
/// <c>.Sqlite</c> stores.
/// <para>
/// <b>Swap-aware, not swap-ready.</b> Connection-string construction + PRAGMA
/// setup live here, so a future runtime swap rewrites this one class rather than
/// 15 scattered files. Per-store raw SQL (CREATE TABLE / INSERT OR REPLACE) is
/// still SQLite dialect and would need per-store rewriting on a real swap.
/// </para>
/// </summary>
public abstract class SqliteStoreBase
{
    protected readonly string ConnectionString;
    private int _initialized;
    /// <summary>串行化 schema 初始化：首个调用者做建表+迁移，并发调用者在此阻塞等其完成后再放行
    /// （而非早返回 → 撞上「表还没建好」的查询失败）。async/sync 两路共用一把锁——async 全程
    /// ConfigureAwait(false) 无 UI 线程亲和，sync 路阻塞量等同原实现（建表本就同步），不死锁。</summary>
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    protected SqliteStoreBase(string connectionString)
        => ConnectionString = !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new ArgumentException("Connection string is required.", nameof(connectionString));

    /// <summary>Builds a <c>Data Source=</c> connection string for a file path (the swap seam).</summary>
    public static string BuildConnectionString(string dbPath) => $"Data Source={dbPath}";

    /// <summary>When true, <see cref="OpenAsync"/>/<see cref="Open"/> set <c>PRAGMA foreign_keys=ON</c>
    /// on every connection. Default false; stores with FK-cascade tables (e.g. conversation→message) override to true.</summary>
    protected virtual bool ForeignKeysEnabled => false;

    /// <summary>Opens a connection with <c>busy_timeout=5000</c> (and foreign_keys when enabled).
    /// Uses <c>ConfigureAwait(false)</c> — library best practice; some stores (e.g. Llm) are consumed
    /// sync-over-async on the UI thread and would deadlock without it.</summary>
    protected async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var c = new SqliteConnection(ConnectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var p = c.CreateCommand();
        p.CommandText = ForeignKeysEnabled
            ? "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;"
            : "PRAGMA busy_timeout=5000;";
        await p.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>Synchronous open — for ctor-time init only (see <see cref="EnsureSchema"/>).</summary>
    protected SqliteConnection Open()
    {
        var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = ForeignKeysEnabled
            ? "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;"
            : "PRAGMA busy_timeout=5000;";
        p.ExecuteNonQuery();
        return c;
    }

    /// <summary>Idempotent lazy schema init: creates the parent directory, enables WAL, runs
    /// <paramref name="createSql"/>, then <see cref="MigrateAsync"/>. Runs once. 并发调用者在
    /// <see cref="_schemaLock"/> 上等首个调用者完成后放行（双检：拿到锁后若已初始化则直接返回）。
    /// 失败时 <see cref="_initialized"/> 仍为 0（仅在成功后置位），锁在 finally 释放 → 下次调用重试。</summary>
    protected async Task EnsureSchemaAsync(string createSql, CancellationToken ct = default)
    {
        await _schemaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized != 0) return;   // 双检：并发调用者拿到锁时首个已完成
            EnsureDirectory();
            await using var c = await OpenAsync(ct).ConfigureAwait(false);
            RestrictDatabaseFilePermissions();   // 文件此刻才存在（SQLite 懒创建）
            await using (var wal = c.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var cmd = c.CreateCommand();
            cmd.CommandText = createSql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await MigrateAsync(c, ct).ConfigureAwait(false);
            _initialized = 1;   // 仅建表+迁移全成功后标记完成
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    /// <summary>Synchronous idempotent schema init for stores that must init in their ctor
    /// (e.g. Automation — avoids async-over-sync deadlock on the UI thread; see #3a)。与 async 路共用
    /// <see cref="_schemaLock"/>：若后台正跑 async 初始化，ctor 线程在此短暂阻塞等其完成（建表本就同步阻塞，无新增阻塞量）。</summary>
    protected void EnsureSchema(string createSql)
    {
        _schemaLock.Wait();
        try
        {
            if (_initialized != 0) return;
            EnsureDirectory();
            using var c = Open();
            RestrictDatabaseFilePermissions();   // 文件此刻才存在（SQLite 懒创建）
            using (var wal = c.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                wal.ExecuteNonQuery();
            }
            using var cmd = c.CreateCommand();
            cmd.CommandText = createSql;
            cmd.ExecuteNonQuery();
            Migrate(c);
            _initialized = 1;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    /// <summary>Override to run idempotent column migrations after schema creation (async path).</summary>
    protected virtual Task MigrateAsync(SqliteConnection connection, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Override to run idempotent column migrations after schema creation (sync path).</summary>
    protected virtual void Migrate(SqliteConnection connection) { }

    /// <summary>
    /// 凭据文件收权限到 0600（POSIX）。本类承载的表里有多处明文口令/授权码（如 <c>ctp_profile.password</c>），
    /// 默认 umask 022 建出来的文件同机任何用户可读——<c>README</c> 承诺的「chmod 600」必须由代码兑现，
    /// 不能指望部署方记得手工做。SQLite 用 DB 文件自身的 mode 创建 <c>-wal</c>/<c>-shm</c>，所以收 DB 文件即可。
    /// Windows 走用户目录 ACL，不做处理。
    /// </summary>
    protected void RestrictDatabaseFilePermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var dbPath = ExtractDataSourcePath(ConnectionString);
        if (string.IsNullOrEmpty(dbPath)) return;
        try
        {
            if (File.Exists(dbPath))
                File.SetUnixFileMode(dbPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException) { /* 只读介质/并发占用：权限收不上不是致命错，别让存取失败 */ }
        catch (UnauthorizedAccessException) { }
    }

    private void EnsureDirectory()
    {
        var dbPath = ExtractDataSourcePath(ConnectionString);
        if (string.IsNullOrEmpty(dbPath)) return;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>Extracts the file path from a <c>Data Source=</c>/<c>Filename=</c> segment;
    /// returns null for <c>:memory:</c> or in-memory/uri connection strings.</summary>
    protected static string? ExtractDataSourcePath(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = part[..eq].Trim();
            if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) || key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
            {
                var val = part[(eq + 1)..].Trim();
                return val.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ? null : val;
            }
        }
        return null;
    }
}
