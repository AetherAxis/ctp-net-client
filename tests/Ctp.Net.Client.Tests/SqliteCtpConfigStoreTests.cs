using Ctp.Net;
using Ctp.Net.Client;
using Microsoft.Data.Sqlite;

namespace Ctp.Net.Client.Tests;

public class SqliteCtpConfigStoreTests
{
    [Fact]
    public async Task 凭据库文件权限为0600_同机其它用户不可读()
    {
        if (OperatingSystem.IsWindows()) return;   // 权限位是 POSIX 语义（Windows 走目录 ACL）
        await using var store = await CreateAsync();
        await store.SaveAsync(NewProfile(userId: "600", front: "sim", name: "600"));

        // README 承诺的 chmod 600 必须由代码兑现——表里是明文口令/授权码
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(store.Path));
    }

    [Fact]
    public async Task GetAll_returns_empty_on_fresh_db()
    {
        await using var store = await CreateAsync();
        Assert.Empty(await store.GetAllAsync());
        Assert.Null(await store.GetAsync());
    }

    [Fact]
    public async Task Save_then_GetAll_round_trips_all_fields()
    {
        await using var store = await CreateAsync();
        var cfg = NewProfile(userId: "12345", front: "sim", name: "12345", isLastUsed: true);

        await store.SaveAsync(cfg);
        var all = await store.GetAllAsync();

        var loaded = Assert.Single(all);
        Assert.Equal(cfg.Id, loaded.Id);
        Assert.Equal(cfg.Name, loaded.Name);
        Assert.Equal(cfg.FrontAddress, loaded.FrontAddress);
        Assert.Equal(cfg.MdFrontAddress, loaded.MdFrontAddress);
        Assert.Equal(cfg.BrokerId, loaded.BrokerId);
        Assert.Equal(cfg.UserId, loaded.UserId);
        Assert.Equal(cfg.Password, loaded.Password);
        Assert.Equal(cfg.AppId, loaded.AppId);
        Assert.Equal(cfg.AuthCode, loaded.AuthCode);
        Assert.Equal(cfg.AutoLogin, loaded.AutoLogin);
        Assert.Equal(cfg.IsLastUsed, loaded.IsLastUsed);
    }

    [Fact]
    public async Task Save_upserts_by_id()
    {
        await using var store = await CreateAsync();
        var id = Guid.NewGuid();
        await store.SaveAsync(NewProfile(userId: "a", front: "sim", name: "old", id: id));
        await store.SaveAsync(NewProfile(userId: "a", front: "sim", name: "new", id: id));

        var all = await store.GetAllAsync();
        var loaded = Assert.Single(all);
        Assert.Equal("new", loaded.Name);
    }

    [Fact]
    public async Task Save_last_used_demotes_others()
    {
        await using var store = await CreateAsync();
        var a = NewProfile(userId: "a", front: "sim", name: "a", isLastUsed: true);
        var b = NewProfile(userId: "b", front: "sim", name: "b", isLastUsed: true);
        await store.SaveAsync(a);
        await store.SaveAsync(b);

        var all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Single(all, p => p.IsLastUsed); // 唯一性不变量
        Assert.DoesNotContain(all, p => p.Id == a.Id && p.IsLastUsed);
        Assert.True((await store.GetAsync())!.Id == b.Id);
    }

    [Fact]
    public async Task Save_non_last_used_keeps_current()
    {
        await using var store = await CreateAsync();
        var a = NewProfile(userId: "a", front: "sim", name: "a", isLastUsed: true);
        await store.SaveAsync(a);
        await store.SaveAsync(NewProfile(userId: "b", front: "sim", name: "b")); // IsLastUsed=false

        var current = await store.GetAsync();
        Assert.NotNull(current);
        Assert.Equal(a.Id, current!.Id);
    }

    [Fact]
    public async Task Delete_removes_profile_and_last_used_goes_null()
    {
        await using var store = await CreateAsync();
        var a = NewProfile(userId: "a", front: "sim", name: "a", isLastUsed: true);
        await store.SaveAsync(a);

        await store.DeleteAsync(a.Id);

        Assert.Empty(await store.GetAllAsync());
        Assert.Null(await store.GetAsync()); // 自动登录随之关闭（有意行为）
    }

    [Fact]
    public async Task Clear_empties_profiles_but_keeps_legacy_table()
    {
        var path = NewTempPath();
        await CreateLegacyDbAsync(path);
        try
        {
            var store = SqliteCtpConfigStore.ForFile(path);
            await store.GetAllAsync(); // 触发迁移种子
            Assert.Single(await store.GetAllAsync());

            await store.ClearAsync();

            Assert.Empty(await store.GetAllAsync());
            Assert.True(await LegacyRowExistsAsync(path)); // 旧 ctp_config 表不再触碰
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Migration_seeds_from_legacy_row()
    {
        var path = NewTempPath();
        await CreateLegacyDbAsync(path);
        try
        {
            var store = SqliteCtpConfigStore.ForFile(path);

            var all = await store.GetAllAsync(); // 触发 EnsureSchema → AddColumn + 种子

            var seeded = Assert.Single(all);
            Assert.Equal("12345", seeded.Name); // Name = UserId
            Assert.True(seeded.IsLastUsed);
            Assert.False(seeded.AutoLogin); // AddColumn 默认 0
            Assert.Equal("tcp://1.2.3.4:9999", seeded.FrontAddress);
            Assert.Equal("tcp://1.2.3.4:9998", seeded.MdFrontAddress);
            Assert.Equal("9999", seeded.BrokerId);
            Assert.Equal("12345", seeded.UserId);
            Assert.Equal("pw", seeded.Password);
            Assert.Equal("simnow_client_test", seeded.AppId);
            Assert.Equal("0000000000000000", seeded.AuthCode);
            Assert.Equal(seeded.Id, (await store.GetAsync())!.Id);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Migration_is_idempotent()
    {
        var path = NewTempPath();
        await CreateLegacyDbAsync(path);
        try
        {
            await SqliteCtpConfigStore.ForFile(path).GetAllAsync();
            // 第二个实例（模拟重启再跑 MigrateAsync）
            await SqliteCtpConfigStore.ForFile(path).GetAllAsync();

            Assert.Single(await SqliteCtpConfigStore.ForFile(path).GetAllAsync());
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Migration_skips_seed_when_profiles_exist()
    {
        await using var store = await CreateAsync();
        await store.SaveAsync(NewProfile(userId: "a", front: "sim", name: "a", isLastUsed: true));

        // 同库新实例（profile 表已非空）→ 不种子（也无法种子：本测试库无 legacy 行）
        await SqliteCtpConfigStore.ForFile(store.Path).GetAllAsync();

        Assert.Single(await store.GetAllAsync());
    }

    private static CtpConnectionConfig NewProfile(
        string userId, string front, string name, Guid? id = null, bool isLastUsed = false) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        FrontAddress = front,
        MdFrontAddress = front == "sim" ? "" : "tcp://md:1234",
        BrokerId = "9999",
        UserId = userId,
        Password = "pw",
        AppId = "app",
        AuthCode = "auth",
        AutoLogin = false,
        IsLastUsed = isLastUsed
    };

    private static async Task<TempStore> CreateAsync()
    {
        var path = NewTempPath();
        var store = SqliteCtpConfigStore.ForFile(path);
        await store.GetAllAsync(); // force schema creation
        return new TempStore(store, path);
    }

    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"ctp-test-{Guid.NewGuid():N}.db");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>按迁移前旧 schema（无 auto_login 列）造一个含单行配置的 ctp.db。</summary>
    private static async Task CreateLegacyDbAsync(string path)
    {
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        await using var create = conn.CreateCommand();
        create.CommandText =
            "CREATE TABLE ctp_config (" +
            "id INTEGER PRIMARY KEY CHECK (id = 1), " +
            "front_address TEXT NOT NULL, md_front_address TEXT NOT NULL, " +
            "broker_id TEXT NOT NULL, user_id TEXT NOT NULL, password TEXT NOT NULL, " +
            "app_id TEXT NOT NULL, auth_code TEXT NOT NULL)";
        await create.ExecuteNonQueryAsync();
        await using var insert = conn.CreateCommand();
        insert.CommandText =
            "INSERT INTO ctp_config (id, front_address, md_front_address, broker_id, user_id, password, app_id, auth_code) " +
            "VALUES (1, 'tcp://1.2.3.4:9999', 'tcp://1.2.3.4:9998', '9999', '12345', 'pw', 'simnow_client_test', '0000000000000000')";
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<bool> LegacyRowExistsAsync(string path)
    {
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ctp_config WHERE id = 1";
        var result = await cmd.ExecuteScalarAsync();
        return result is long n && n > 0;
    }

    /// <summary>临时文件 store 包装：暴露 Path 供同库第二实例测试；Dispose 删库文件。</summary>
    private sealed class TempStore : IAsyncDisposable
    {
        public SqliteCtpConfigStore Store { get; }
        public string Path { get; }
        public TempStore(SqliteCtpConfigStore store, string path) { Store = store; Path = path; }

        public static implicit operator SqliteCtpConfigStore(TempStore t) => t.Store;
        public Task<CtpConnectionConfig?> GetAsync(CancellationToken ct = default) => Store.GetAsync(ct);
        public Task<List<CtpConnectionConfig>> GetAllAsync(CancellationToken ct = default) => Store.GetAllAsync(ct);
        public Task SaveAsync(CtpConnectionConfig config, CancellationToken ct = default) => Store.SaveAsync(config, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Store.DeleteAsync(id, ct);
        public Task ClearAsync(CancellationToken ct = default) => Store.ClearAsync(ct);

        public ValueTask DisposeAsync() { try { File.Delete(Path); } catch { } return ValueTask.CompletedTask; }
    }
}
