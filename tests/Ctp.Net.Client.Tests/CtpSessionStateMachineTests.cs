using Ctp.Net.Client;

namespace Ctp.Net.Client.Tests;

/// <summary>
/// 会话状态机迁移矩阵（`DESIGN.md` §6 #2）。
/// <para>
/// 这一项原先一直做不了，原因是「状态判断散在 588 行的原生壳里，测它就得加载 ctp_native」。
/// 2026-10-29 引入 <see cref="CtpSessionState"/>、2026-10-30 把迁移抽成纯函数
/// <see cref="CtpSessionStateMachine"/> 之后，服务里所有 <c>_state</c> 赋值都走
/// <see cref="CtpSessionStateMachine.Next"/>——**这里覆盖的就是线上跑的那份逻辑**。
/// </para>
/// <para>
/// 重点不是「实现和表一致」（那只是自证），而是这三条不变量：
/// ① 只有登录成功能进 Ready；② 连接丢了绝不会落回 Ready；③ Ready 是唯一的可写状态。
/// 断线窗口里误发报单（2026-10-29 复盘：117 单被当垃圾帧丢弃）就是②③被破坏的后果。
/// </para>
/// </summary>
public class CtpSessionStateMachineTests
{
    public static TheoryData<CtpSessionState, CtpSessionEvent, bool, CtpSessionState> Matrix => new()
    {
        // —— 未登录 ——
        { CtpSessionState.Disconnected, CtpSessionEvent.LoginSucceeded, false, CtpSessionState.Ready },
        { CtpSessionState.Disconnected, CtpSessionEvent.SocketLost,     true,  CtpSessionState.Disconnected },
        { CtpSessionState.Disconnected, CtpSessionEvent.SocketLost,     false, CtpSessionState.Disconnected },
        { CtpSessionState.Disconnected, CtpSessionEvent.SessionClosed,  true,  CtpSessionState.Disconnected },

        // —— 重连窗口（曾登录过，正在重连/重登）——
        { CtpSessionState.Connecting,   CtpSessionEvent.LoginSucceeded, false, CtpSessionState.Ready },
        { CtpSessionState.Connecting,   CtpSessionEvent.SocketLost,     true,  CtpSessionState.Connecting },
        { CtpSessionState.Connecting,   CtpSessionEvent.SocketLost,     false, CtpSessionState.Disconnected },
        { CtpSessionState.Connecting,   CtpSessionEvent.SessionClosed,  true,  CtpSessionState.Disconnected },

        // —— 已就绪 ——
        { CtpSessionState.Ready,        CtpSessionEvent.LoginSucceeded, false, CtpSessionState.Ready },
        { CtpSessionState.Ready,        CtpSessionEvent.SocketLost,     true,  CtpSessionState.Connecting },
        { CtpSessionState.Ready,        CtpSessionEvent.SocketLost,     false, CtpSessionState.Disconnected },
        { CtpSessionState.Ready,        CtpSessionEvent.SessionClosed,  false, CtpSessionState.Disconnected },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void 迁移表(CtpSessionState from, CtpSessionEvent evt, bool autoReconnect, CtpSessionState expected)
        => Assert.Equal(expected, CtpSessionStateMachine.Next(from, evt, autoReconnect));

    /// <summary>不变量①：Ready 只能由「登录成功」产出——断线、关闭都不能把状态拉回 Ready。</summary>
    [Fact]
    public void 只有登录成功能进入Ready()
    {
        foreach (var from in Enum.GetValues<CtpSessionState>())
        foreach (var evt in Enum.GetValues<CtpSessionEvent>())
        foreach (var auto in new[] { true, false })
        {
            var next = CtpSessionStateMachine.Next(from, evt, auto);
            if (next == CtpSessionState.Ready)
                Assert.Equal(CtpSessionEvent.LoginSucceeded, evt);
        }
    }

    /// <summary>
    /// 不变量②：**只要**收到 SocketLost（不论当前是什么状态、不论是否自动重连），
    /// 结果一定不是 Ready。这是「断线窗口不放行写操作」的根。
    /// </summary>
    [Fact]
    public void 连接丢了绝不会停在Ready()
    {
        foreach (var from in Enum.GetValues<CtpSessionState>())
        foreach (var auto in new[] { true, false })
        {
            Assert.NotEqual(CtpSessionState.Ready,
                CtpSessionStateMachine.Next(from, CtpSessionEvent.SocketLost, auto));
        }
    }

    /// <summary>不变量③：可写状态有且只有 Ready。</summary>
    [Fact]
    public void 只有Ready允许写操作()
    {
        Assert.True(CtpSessionStateMachine.AllowsWrite(CtpSessionState.Ready));
        Assert.False(CtpSessionStateMachine.AllowsWrite(CtpSessionState.Connecting));
        Assert.False(CtpSessionStateMachine.AllowsWrite(CtpSessionState.Disconnected));
    }

    /// <summary>未登录时收到 SocketLost 是幂等的——SDK 会在连不上前置时反复回调，不能把状态搅乱。</summary>
    [Fact]
    public void 未登录时的断线事件幂等()
    {
        var s = CtpSessionState.Disconnected;
        for (var i = 0; i < 5; i++)
            s = CtpSessionStateMachine.Next(s, CtpSessionEvent.SocketLost, autoReconnect: true);
        Assert.Equal(CtpSessionState.Disconnected, s);
    }

    /// <summary>Ready 后连续断开/重登多次：状态在 Ready ⇄ Connecting 之间往返，不会越陷越深。</summary>
    [Fact]
    public void 反复断线与重登状态不累积()
    {
        var s = CtpSessionState.Disconnected;
        for (var round = 0; round < 3; round++)
        {
            s = CtpSessionStateMachine.Next(s, CtpSessionEvent.LoginSucceeded, false);
            Assert.Equal(CtpSessionState.Ready, s);
            Assert.True(CtpSessionStateMachine.AllowsWrite(s));

            s = CtpSessionStateMachine.Next(s, CtpSessionEvent.SocketLost, autoReconnect: true);
            Assert.Equal(CtpSessionState.Connecting, s);
            Assert.False(CtpSessionStateMachine.AllowsWrite(s));   // 断线窗口绝不能写
        }
    }

    /// <summary>只有登录成功开启新纪元——纪元是上层判断「是不是同一条会话」的唯一依据，不能被别的事件推着走。</summary>
    [Fact]
    public void 只有登录成功开启新纪元()
    {
        Assert.True(CtpSessionStateMachine.StartsNewEpoch(CtpSessionEvent.LoginSucceeded));
        Assert.False(CtpSessionStateMachine.StartsNewEpoch(CtpSessionEvent.SocketLost));
        Assert.False(CtpSessionStateMachine.StartsNewEpoch(CtpSessionEvent.SessionClosed));
    }

    /// <summary>
    /// 重复的登录成功（重连后重登、或迟到的重复回调）**也要**推进纪元：
    /// 幂等会让上层误判「还是那条会话」，把旧纪元里失败的请求当同一条重发。
    /// </summary>
    [Fact]
    public void 重复登录成功也算新纪元()
    {
        Assert.Equal(CtpSessionState.Ready,
            CtpSessionStateMachine.Next(CtpSessionState.Ready, CtpSessionEvent.LoginSucceeded, false));
        Assert.True(CtpSessionStateMachine.StartsNewEpoch(CtpSessionEvent.LoginSucceeded));
    }

    /// <summary>未定义的事件值必须显式抛，而不是落进某个默认分支静默给出一个状态。</summary>
    [Fact]
    public void 未知事件抛异常()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => CtpSessionStateMachine.Next(CtpSessionState.Ready, (CtpSessionEvent)99, true));
}
