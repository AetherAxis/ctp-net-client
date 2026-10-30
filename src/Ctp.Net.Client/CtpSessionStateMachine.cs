namespace Ctp.Net.Client;

/// <summary>
/// 推动会话状态变化的事件（见 <see cref="CtpSessionStateMachine"/>）。
/// <para>
/// 刻意只有三个：<b>能让状态变的只有「登录成功」「连接丢了」「会话被关掉」</b>这三类事实。
/// 「开始重连」「定时器到点」「行情连上了」都不是状态迁移——它们是前提或副作用，
/// 混进来会让状态机变成一锅粥（旧实现的 <c>_socketAlive</c> 布尔量就是这样被三处并发写坏的）。
/// </para>
/// </summary>
public enum CtpSessionEvent
{
    /// <summary>认证 + 登录 + 结算确认全部完成。唯一能进入 <see cref="CtpSessionState.Ready"/> 的事件。</summary>
    LoginSucceeded,

    /// <summary>交易 socket 失活（前置断开 / 心跳超时）。落点取决于是否还会自动重连。</summary>
    SocketLost,

    /// <summary>主动关闭会话（<c>Logout</c> / <c>Dispose</c> / 闭市放弃重连）。</summary>
    SessionClosed,
}

/// <summary>
/// 会话状态机（<see cref="CtpSessionState"/>）的**纯函数**迁移表。
/// <para>
/// 抽出来的唯一目的是可测：<see cref="CtpSessionService"/> 是原生句柄的托管壳，构造它就要加载
/// <c>ctp_native</c>，所以「断线窗口里状态到底落在哪」这类判断原先**一条用例都没有**
/// （`DESIGN.md` §6 #2）。现在服务里所有写 <c>_state</c> 的地方都经过 <see cref="Next"/>，
/// 测试覆盖的就是线上真正走的那份逻辑，不是平行实现。
/// </para>
/// <para>
/// 合法迁移（完整矩阵见 <c>CtpSessionStateMachineTests</c>）：
/// <code>
/// Disconnected --LoginSucceeded--> Ready
/// Connecting   --LoginSucceeded--> Ready
/// Ready        --LoginSucceeded--> Ready     （重登：状态不变，纪元 +1）
/// Ready        --SocketLost(auto=true)--> Connecting
/// Ready   |Connecting --SocketLost(auto=false)--> Disconnected
/// Disconnected --SocketLost(任意)--> Disconnected   （从未登录过：没有可恢复的会话，不做「重连中」的宣称）
/// *            --SessionClosed--> Disconnected
/// </code>
/// </para>
/// </summary>
public static class CtpSessionStateMachine
{
    /// <summary>按事件算出下一个状态。<paramref name="autoReconnect"/> 只影响 <see cref="CtpSessionEvent.SocketLost"/>。</summary>
    /// <exception cref="ArgumentOutOfRangeException">未定义的事件值。</exception>
    public static CtpSessionState Next(CtpSessionState current, CtpSessionEvent evt, bool autoReconnect) => evt switch
    {
        CtpSessionEvent.LoginSucceeded => CtpSessionState.Ready,
        // Connecting 的语义是「**曾登录过**，正在重连 + 重登」。从未登录过的会话遇到 socket 抖动
        // （启动时前置还没起来，SDK 会反复回调 FrontDisconnected）应停在 Disconnected——
        // 否则 State 会对外宣称「在重连」，而实际上压根没有可恢复的会话。
        CtpSessionEvent.SocketLost when !autoReconnect => CtpSessionState.Disconnected,
        CtpSessionEvent.SocketLost when current == CtpSessionState.Disconnected => CtpSessionState.Disconnected,
        CtpSessionEvent.SocketLost => CtpSessionState.Connecting,
        CtpSessionEvent.SessionClosed => CtpSessionState.Disconnected,
        _ => throw new ArgumentOutOfRangeException(nameof(evt), evt, "未定义的会话事件"),
    };

    /// <summary>
    /// 该状态是否允许下单/撤单/查询。<b>唯一判据就是 Ready</b>——不要在各调用点另写
    /// <c>IsConnected &amp;&amp; ...</c> 之类的组合条件，那正是 2026-10-29「117 单被当垃圾帧丢弃」的成因。
    /// </summary>
    public static bool AllowsWrite(CtpSessionState state) => state == CtpSessionState.Ready;

    /// <summary>
    /// 该事件是否开启新纪元。每次**登录成功**都算一条新会话（纪元 +1），即使之前已经是 Ready——
    /// 上层用它区分「重发时是不是同一条会话」，所以重复的登录成功也必须让纪元前进，不能幂等。
    /// </summary>
    public static bool StartsNewEpoch(CtpSessionEvent evt) => evt == CtpSessionEvent.LoginSucceeded;
}
