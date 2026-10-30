using Ctp.Net.Client;

namespace Ctp.Net.Client.Tests;

/// <summary>
/// OrderRef 本地高水位存储（`DESIGN.md` §6 #13）。
/// <para>
/// 唯一真正要钉住的性质是**只升不降**：水位被写回去就等于「这台机器可能已经把号发到了那里」，
/// 降低它会让重启后的会话重发已经用过的 OrderRef——而前置/交易所侧还挂着那些单，撞号即撤错单。
/// </para>
/// </summary>
public class OrderRefHighWaterStoreTests
{
    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), $"aether-ctp-orderref-test-{Guid.NewGuid():N}", "ctp.db");

    private static SqliteOrderRefHighWaterStore NewStore(string path) => SqliteOrderRefHighWaterStore.ForFile(path);

    [Fact]
    public async Task 没有记录时返回0()
    {
        var store = NewStore(TempDbPath());
        Assert.Equal(0, await store.GetAsync("9999", "u1"));
    }

    [Fact]
    public async Task 写入后能读回()
    {
        var store = NewStore(TempDbPath());
        await store.SaveAsync("9999", "u1", 4242);

        Assert.Equal(4242, await store.GetAsync("9999", "u1"));
    }

    /// <summary>核心性质：写一个更小的值不能把水位拉回去。</summary>
    [Fact]
    public async Task 只升不降()
    {
        var store = NewStore(TempDbPath());
        await store.SaveAsync("9999", "u1", 500);
        await store.SaveAsync("9999", "u1", 100);

        Assert.Equal(500, await store.GetAsync("9999", "u1"));
    }

    /// <summary>0 / 负数不写垃圾行——「没发过单」不该覆盖已有水位。</summary>
    [Fact]
    public async Task 非正数被忽略()
    {
        var store = NewStore(TempDbPath());
        await store.SaveAsync("9999", "u1", 700);
        await store.SaveAsync("9999", "u1", 0);
        await store.SaveAsync("9999", "u1", -5);

        Assert.Equal(700, await store.GetAsync("9999", "u1"));
    }

    /// <summary>键是 (broker, user)：不同投资者的水位互不影响（OrderRef 的唯一性范围就是投资者账号）。</summary>
    [Fact]
    public async Task 账户之间互不影响()
    {
        var store = NewStore(TempDbPath());
        await store.SaveAsync("9999", "u1", 100);
        await store.SaveAsync("9999", "u2", 900);
        await store.SaveAsync("8888", "u1", 50);

        Assert.Equal(100, await store.GetAsync("9999", "u1"));
        Assert.Equal(900, await store.GetAsync("9999", "u2"));
        Assert.Equal(50, await store.GetAsync("8888", "u1"));
    }

    /// <summary>同一文件重开（模拟客户端重启）后水位仍在——这是整个机制的意义所在。</summary>
    [Fact]
    public async Task 跨进程重开保持()
    {
        var path = TempDbPath();
        await NewStore(path).SaveAsync("9999", "u1", 123456);

        // 新实例 = 新连接 = 模拟重启
        Assert.Equal(123456, await NewStore(path).GetAsync("9999", "u1"));
    }

    /// <summary>并发抬高同一个键：最终值必须是最大那个（ON CONFLICT max 语义）。</summary>
    [Fact]
    public async Task 并发写入取最大值()
    {
        var store = NewStore(TempDbPath());
        var values = new[] { 10, 900, 300, 1200, 40, 77 };
        await Task.WhenAll(values.Select(v => store.SaveAsync("9999", "u1", v)));

        Assert.Equal(1200, await store.GetAsync("9999", "u1"));
    }
}
