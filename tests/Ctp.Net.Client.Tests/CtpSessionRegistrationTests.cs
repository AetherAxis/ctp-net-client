using Ctp.Net;
using Ctp.Net.Client;
using Ctp.Net.Client.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Ctp.Net.Client.Tests;

/// <summary>
/// CTP 会话 DI 组合形状（2026-10-01 前置端口收口 + 2026-10-04 契约分家后的形态）：
/// <see cref="ICtpSession"/> 直连官方 SDK 链路（<see cref="CtpSessionService"/>）——
/// 本地仿真柜台经前置 tcp://127.0.0.1:17801/17802 接入，真实前置（SimNow）同链路。
/// 旧 sim 关键字 JSON-RPC 路由（RoutingCtpSession）已删——本测试钉住「不再回归」。
/// （2026-10-04 自 tests/Acme.Ctp.Simulator.Tests/RoutingSessionTests 迁入——那测的是客户端组合根，不是柜台。）
/// </summary>
public class CtpSessionRegistrationTests
{
    /// <summary>临时目录下的 ctp.db 路径——**绝不**让测试碰 <c>~/.aether/ctp.db</c>（真实用户的配置库）。</summary>
    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), $"aether-ctp-reg-test-{Guid.NewGuid():N}", "ctp.db");

    [Fact]
    public void ICtpSession直连官方SDK链路()
    {
        using var provider = new ServiceCollection().AddCtpInfrastructure(TempDbPath()).BuildServiceProvider();

        var session = provider.GetRequiredService<ICtpSession>();
        var real = Assert.IsType<CtpSessionService>(session);
        Assert.Same(real, provider.GetRequiredService<CtpSessionService>());   // 同一单例
        Assert.False(session.IsConnected);
        Assert.Null(real.LoginResult);
    }
}
