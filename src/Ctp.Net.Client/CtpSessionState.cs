namespace Ctp.Net.Client;

/// <summary>
/// CTP 会话状态（2026-10-29 引入，对齐 <c>ctp-net-client/doc/session-lifecycle.md</c> 的状态机设计）：
/// <code>
/// Disconnected ──LoginAsync──► Ready ──FrontDisconnected──► Connecting ──重登成功──► Ready
///        ▲                        │                              │
///        └──────Logout/Dispose────┴──────────闭市/放弃重连───────┴──► Disconnected
/// </code>
/// <para>
/// <b>为什么要有它</b>：旧实现只有一个 <c>_socketAlive</c> 布尔量，且下单/撤单路径从不检查它——
/// 断线窗口里（SDK 还没重连、或重连上但还没重登）发出去的报单**没有任何人告诉你它去哪了**：
/// native 层要么静默排队到一条已死的 socket，要么 rc 失败被当成业务异常。这就是 2026-10-29
/// 复盘里「新连接不重新认证 ⇒ 117 单被当垃圾帧丢弃」的同族缺口。现在写入类操作在非 <see cref="Ready"/>
/// 时直接抛 <see cref="CtpSessionNotReadyException"/>——**显式失败好过静默丢失**。
/// </para>
/// </summary>
public enum CtpSessionState
{
    /// <summary>没有活连接（未登录 / 已登出 / 闭市主动放弃重连）。</summary>
    Disconnected,

    /// <summary>曾登录过，当前正在（或等待）重连 + 重登。<b>此状态不可下单</b>。</summary>
    Connecting,

    /// <summary>已认证 + 已登录 + 结算已确认——本状态才允许发单/撤单/查询。</summary>
    Ready,
}

/// <summary>
/// 会话未就绪时拒绝操作（2026-10-29）：<b>不静默丢弃</b>。
/// <para>
/// 出现在断线窗口（<see cref="CtpSessionState.Connecting"/>）或未登录（<see cref="CtpSessionState.Disconnected"/>）
/// 时调用下单/撤单/查询。调用方应把它当作「可重试的暂时性失败」——重连完成后（<see cref="CtpSessionService.State"/>
/// 回到 <see cref="CtpSessionState.Ready"/>）再重发。<see cref="CtpSessionService.Epoch"/> 可用来判断
/// 「重发时是不是已经换了一条会话」。
/// </para>
/// </summary>
public sealed class CtpSessionNotReadyException : InvalidOperationException
{
    /// <summary>抛异常那一刻的会话状态。</summary>
    public CtpSessionState State { get; }

    /// <summary>抛异常那一刻的会话纪元（见 <see cref="CtpSessionService.Epoch"/>）。</summary>
    public long Epoch { get; }

    public CtpSessionNotReadyException(CtpSessionState state, long epoch, string operation)
        : base($"CTP 会话未就绪（当前 {state}，纪元 {epoch}）——「{operation}」未发送。" +
               "断线重连窗口内不做静默排队，请在连接恢复后重试。")
    {
        State = state;
        Epoch = epoch;
    }
}
