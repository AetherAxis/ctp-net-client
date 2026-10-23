using Ctp.Net.Client;

namespace Ctp.Net.Client.Tests;

/// <summary>
/// <see cref="CtpSessionService.IsSimFrontAddress"/> 判据矩阵（镜像 <c>CtpConnectorViewModel.IsSimFront</c>——
/// 两处判据同步维护）。它决定断线后的重连决策：<b>自建仿真前置 = 24 小时沙盒，断线后永远重连、不做
/// 「闭市等待」</b>（服务器交易所从不因时段拒单——<c>SimOptions.RejectOrdersOutsideTradingTime</c>
/// 默认关；客户端若周末/深夜断线躺到下个交易时段会打断 bot 挂机，2026-10-17 用户定调）。
/// </summary>
public class CtpSessionSimFrontTests
{
    [Theory]
    [InlineData("tcp://127.0.0.1:17801", true)]          // 本地前置
    [InlineData("tcp://localhost:17801", true)]           // localhost 容忍
    [InlineData("tcp://117.72.186.128:17801", true)]      // 远程服务器部署形态（统一按端口判）
    [InlineData("tcp://127.0.0.1:17802", false)]          // 行情腿端口不算
    [InlineData("sim", true)]                             // sim 哨兵
    [InlineData("SIM", true)]                             // 不分大小写
    [InlineData("tcp://127.0.0.1:17800", true)]           // 旧 JSON-RPC 形态
    [InlineData("tcp://180.168.146.187:10130", false)]    // SimNow 真前置
    [InlineData("tcp://127.0.0.1:10130", false)]          // 端口不匹配（本地部署的真前置也不算）
    [InlineData("tcp://localhost:9999", false)]           // localhost 但端口不符
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("  tcp://127.0.0.1:17801  ", true)]       // 尾空格容忍（全字段 Trim 同款纪律）
    public void 判据矩阵_端口与哨兵(string? front, bool expected)
        => Assert.Equal(expected, CtpSessionService.IsSimFrontAddress(front));
}
