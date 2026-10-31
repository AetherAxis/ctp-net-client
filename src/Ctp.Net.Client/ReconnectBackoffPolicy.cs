using System;

namespace Ctp.Net.Client;

/// <summary>
/// 重连退避策略（2026-10-31，线上风暴治理）。
/// <para>
/// <b>为什么需要它</b>：没有退避时，「断开 → 重建实例 → 登录 → 重连即对账 → 又断」是**无节流**的紧循环。
/// 2026-10-31 09:48–10:12 光是 botmm 就打了 336 次登录（每轮 1~4 秒）；每次断连窗口都会吞掉报单回报
/// （看门狗随后把「其实活着」的委托判无效），孤儿活单越积越多 ⇒ 每次查报单都是满额 200 行 ⇒
/// 风暴自我喂食。退避把这种循环钉在「越连不上越慢」上，正常掉线（一天几次）则完全不受影响。
/// </para>
/// <para>
/// 判据是「上一次 Ready 撑了多久」：撑满 <c>stableAfter</c> 才算真恢复，计数清零、本次不等待；
/// 否则按 2^(n-1) 递增，封顶 <c>max</c>。
/// </para>
/// </summary>
internal static class ReconnectBackoffPolicy
{
    internal static readonly TimeSpan Base = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    /// <summary>连接稳定多久才算「真恢复」——低于它，退避计数不清零。</summary>
    internal static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    /// <summary>第 <paramref name="consecutive"/> 次连击前应等待的时长（1 起算）。</summary>
    internal static TimeSpan DelayFor(int consecutive)
    {
        if (consecutive <= 1) return Base;
        // 2^(n-1) 在 n 较大时会溢出 double 的指数范围，先按封顶值截断
        var doublings = Math.Min(consecutive - 1, 16);
        var ms = Base.TotalMilliseconds * Math.Pow(2, doublings);
        return TimeSpan.FromMilliseconds(Math.Min(ms, Max.TotalMilliseconds));
    }

    /// <summary>上一次 Ready 之后过去了 <paramref name="readyFor"/>，是否可清零连击计数。</summary>
    internal static bool IsReset(TimeSpan readyFor) => readyFor >= StableAfter;
}
