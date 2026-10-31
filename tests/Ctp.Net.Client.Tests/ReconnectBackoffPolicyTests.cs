using Ctp.Net.Client;

namespace Ctp.Net.Client.Tests;

/// <summary>
/// 重连退避曲线（2026-10-31 线上风暴的钉子）。
/// <para>
/// 事故：2026-10-31 09:48–10:12，botmm 在前置上打了 <b>336 次登录</b>（每轮 1~4 秒）。每一轮都是
/// 「断开 → 新建 trader → 登录 → 重连即对账（200 行查报单）→ 又断」，而每次断连窗口还会吞掉报单回报
/// （看门狗随后把其实活着的委托判无效 ⇒ 孤儿活单堆积）。这条曲线就是"风暴不可能再无节流地发生"的裁判：
/// 前几次退避很短（正常掉线无感），越连不上越慢，封顶 30s。
/// </para>
/// </summary>
public class ReconnectBackoffPolicyTests
{
    [Theory]
    [InlineData(1, 500)]
    [InlineData(2, 1000)]
    [InlineData(3, 2000)]
    [InlineData(4, 4000)]
    [InlineData(5, 8000)]
    [InlineData(6, 16000)]
    [InlineData(7, 30000)]     // 32000 → 封顶 30s
    [InlineData(50, 30000)]    // 不溢出、不越界
    public void 连击次数映射到等待时长(int consecutive, int expectedMs)
        => Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), ReconnectBackoffPolicy.DelayFor(consecutive));

    [Fact]
    public void 非法连击次数退回基础退避而不是抛()
    {
        Assert.Equal(ReconnectBackoffPolicy.Base, ReconnectBackoffPolicy.DelayFor(0));
        Assert.Equal(ReconnectBackoffPolicy.Base, ReconnectBackoffPolicy.DelayFor(-3));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(59, false)]
    [InlineData(60, true)]      // 撑满 60s 才算真恢复
    [InlineData(3600, true)]
    public void 只有连接稳定够久才清零连击(int readySeconds, bool expected)
        => Assert.Equal(expected, ReconnectBackoffPolicy.IsReset(TimeSpan.FromSeconds(readySeconds)));

    [Fact]
    public void 一次普通掉线的等待不超过半秒()
    {
        // 现场纪律：正常掉线（上一次 Ready 撑过 60s）不该让客户端"等"——首轮退避恒为 500ms。
        Assert.Equal(TimeSpan.FromMilliseconds(500), ReconnectBackoffPolicy.DelayFor(1));
    }
}
