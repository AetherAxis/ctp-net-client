# 断线重连后未重新认证 ⇒ 报单被前置当垃圾帧丢弃（2026-10-29 事故）

> 状态：**主体已修（2026-10-29），与会话门禁相关的一半见 §7**。重连路径现在会重建 trader 并**重放认证 + 登录**，且未 Ready 的写入被显式拒绝；
> 仍缺的是「触发条件不依赖 `Disconnected` 先到」和 240 断连的安全点化。事故当时的临时处置是让两个 bot 改走**直连交易所的旁路客户端**（见 `../../aether-ctp-hosts/doc/direct-bot-bypass.md`），旁路尚未退役。
> 归属：本仓 `CtpSessionService`（会话状态机）+ `ctp-net-core/CtpTrader`（认证原语）；诱因在 `ctp-net-front`（F0 序号 240 强制断连）。
> 同批困难点汇总见 `../../aether-ctp-hosts/doc/two-bots-live-market.md`；行情投递契约见 `../../ctp-net-front/doc/md-tick-delivery.md`。
> **怎么修、以及整条 CTP 线接下来该做什么**：`session-lifecycle.md`（会话语义、状态机 + epoch、在途对账、240 断连的消除路径、待实测清单）。

## 1. 现象

服务器上两个 bot（做市商 + 噪声）进程都活着、CPU 也在跑，但仿真市场整体停摆：

- 桌面端连远程前置（`117.72.186.128:17802`）能登录、能订阅 `ax001`，但**45 秒收不到一条 tick**；
- 前置侧统计 `TICKS_60s=0 ORDERS_60s=0`，最后一条 tick 距今 85 秒；
- 前置日志里持续出现 `非预期帧（等认证）: body前缀=E101E240E4...`——`E2 40` 是报单 op。

对用户的表现就是「行情死了」，但它既不是行情源故障，也不是前置挂了。

## 2. 现场证据（前置日志，`C:\Aether\ctpsim-data\ctpsim-front.log`）

13:54:30–13:59:10 窗口内的统计：

| 指标 | 数值 |
|---|---|
| `认证: broker=9999 user=botmm` / `登录` / `交易所登录成功` | 各 2 次（两个 bot 各一次，**登录是成功的**） |
| `F0 推送序号达 240 上限——主动断开客户端` | 12 次，全部发生在 13:54:45 一秒内 |
| 会话启动（重连后的新连接） | 13:54:45（`127.0.0.1:23073`）、13:54:58（`127.0.0.1:23081`） |
| `非预期帧（等认证）`（被丢弃的报单） | **117 张** |

时间线：

```
13:54:45  认证: broker=9999 user=botmm reqId=1          ← 登录成功
13:54:45  登录: broker=9999 user=botmm reqId=2
13:54:45  交易所登录成功: 交易日=20261029 MaxOrderRef=36472
13:54:45  F0 推送序号达 240 上限——主动断开客户端 × 12
13:54:58  会话启动: 127.0.0.1:23081                      ← SDK 自动重连的新连接
13:55:00  非预期帧（等认证）: body前缀=E101E240E401...     ← 新连接上直接来了报单
   ...    持续 117 张，全部被丢
13:57:38  最后一条 tick（此后无成交 → 无 tick）
```

后续链条：做市商阶梯挂不上去 → 存量挂单被噪声单吃完 → 没有成交 → 交易所不产生 tick → 前置没东西可推 → 桌面端一片死水。

## 3. 机制

### 3.1 前置为什么会主动断连 —— **该机制已于 2026-10-30 删除**

（以下为 2026-10-29 事故当时的机制，保留用于回溯）前置认为 **F0 序号 SDK 只接受 1~251**（2026-10-19 一次 E2E 结论），因此累计到 240 条推送就主动 `Close`，靠 SDK 自动重连把序号归零。活跃行情下 240 条几秒就攒够 —— 13:54:45 那一秒内连断 12 次。

**2026-10-30 推翻**：probe1 单会话连推 **900 帧**、客户端零丢失 ⇒ 251 不是天花板；把回收开回去反而当场丢 4 笔报单回报 + 27 次撤单失败。回收代码（`_rtnRecycleDue` / `TryRecycleAtSafePoint` / 251 硬闸）**已整体删除并上线**（front=`2e4867b`），现在 ≥240 只打一行日志、≥65000 才断。

⇒ 本文后面「断连会丢撤单/丢回报」的推演**仍然成立**（网络断连照样走到这些路径），但**不要再把周期性断连归因于前置回收**——那个源头已经没有了。

### 3.2 认证状态是 per-connection 的

CTP 的 `ReqAuthenticate`/`ReqUserLogin` 语义绑定在**当前 TCP 连接**上。前置断开后 SDK 自动重连开出的是**一条新连接**，认证状态随之清空——旧连接登录过不等于新连接已认证。

### 3.3 客户端漏了「重连后重新认证」

`CtpSessionService` 的重登入口是本仓 `src/Ctp.Net.Client/CtpSessionService.cs:427`：

```csharp
private void OnTraderFrontConnected(object? sender, EventArgs e)
{
    if (_hadLoggedIn && !_socketAlive)      // ← 门控建立在「先收到 Disconnected」之上
        _ = Task.Run(() => ReconnectAsync());
}
```

只有当 `FrontDisconnected` 先把 `_socketAlive` 置假（`:417-419`），随后的 `FrontConnected` 才会触发 `ReconnectAsync()`（重建 `CtpTrader` → 认证 → 登录）。

本次事故里新连接**从未发送认证帧**（日志中 13:54:58 之后没有任何 `认证: ...` 行），说明 `ReconnectAsync` 没有执行，最可能是 `_socketAlive` 仍为真 —— 即服务端主动 `Close` 之后 SDK 只回调了 `FrontConnected`（或回调顺序/线程使得 `!​_socketAlive` 不成立）。

30 秒兜底定时器同样救不了（`:469`）：

```csharp
if (!_hadLoggedIn || _socketAlive || _reconnecting) return;   // _socketAlive 为真 ⇒ 直接返回
```

于是系统进入「看起来一切正常、实际报单全丢」的静默失败：状态没变 Reconnecting、UI 没有告警、策略照常下单。

### 3.4 待确认项（阻塞精确定位）

上面的 3.3 有两种可能：**(a)** SDK 对服务端 `Close` 不触发 `OnFrontDisconnected`；**(b)** 触发了但 `_socketAlive` 被别处（如 `:173` 的登录成功路径）重新置真。要区分必须看**客户端侧**证据，而当前 bot 的 stdout 被重定向到 `NUL`（见 3.5），没有会话日志。

### 3.5 为什么这次只能倒推

2026-10-29 早些时候为治「日志撑爆磁盘」（单 bot 8.6 GB/天）把 bot 日志改成 `>NUL`，于是客户端侧零证据，全部结论靠前置日志反推。

## 4. 影响面

- 任何长跑的 bot / 客户端都会在 F0 序号攒满 240 条后被前置断连一次；只要重连没有重新认证，**其后所有报单静默丢失**，且状态显示仍然是「已连接」。
- 表现与「策略没信号」「交易所没成交」混淆，排查成本极高（本次从怀疑客户端 → 怀疑交易所 → 定位到认证状态，耗时约 1 小时）。
- 历史上服务器双 bot 的多次「莫名停摆」很可能同源。

## 5. 修复方案

### P0（本仓，必做）

1. **`OnTraderFrontConnected` 不依赖 `_socketAlive`**：只要 `_hadLoggedIn` 为真且不是**首次**连接，就触发 `ReconnectAsync()`。用独立的「本 trader 实例是否已完成首登」标志替代 socket 活跃位做门控。
2. **`CtpTrader` 提供显式状态**：`IsAuthenticated`（收到 `OnRspAuthenticate` 成功且未被断开事件清掉），供上层做门禁。
3. **未 Ready 时拒绝报单**：会话未认证/未登录时，报单路径直接抛 `CtpSessionNotReadyException`，**不要**把帧写进一个注定被丢的连接。当前行为是静默丢，等于黑洞。
4. **状态要可见**：重连期间 `CtpConnectionStatus` 必须真的切到 `Reconnecting`；`_socketAlive` 与真实连接状态不一致时报警（例如 `FrontConnected` 时发现 `_socketAlive==true` 说明状态漂移，记一条明确日志）。

### P1（`ctp-net-front`，可选）

240 断连无法取消，但可以把它挪到**安全点**：等到「无在途报单回报 + 出站队列空 + 距上次业务帧有短静默」再 `Close`，减少断在报单中间的概率。当前实现是攒满即断（`:306`），会与客户的报单突发正面撞车。

### P2（`aether-ctp-hosts`，必做）

恢复**极简会话日志**：只记 `连接 / 认证 / 登录 / 断开(reason) / 重连 / 会话重建`，按天轮转 + 单文件上限（例如 8 MB × 7）。不记逐笔、不记查询，成本约几十 KB/天，但足以定位本类问题。

## 6. 复现与验证

1. 正常跑两个 bot，等前置日志出现 `F0 推送序号达 240 上限`（活跃时几秒一次）。
2. 观察随后是否出现 `认证: broker=... user=<bot>`：正常应有；若缺失且紧接着出现 `非预期帧（等认证）`，即命中本缺陷。
3. 统计丢单量：

```powershell
$log = 'C:\Aether\ctpsim-data\ctpsim-front.log'
$t = Get-Content $log -Tail 250000 -Encoding UTF8
($t | Select-String -SimpleMatch '认证: broker').Count
($t | Select-String -SimpleMatch '240 上限').Count
($t | Select-String -SimpleMatch '等认证').Count     # 注意：日志按 UTF-8 读，用 -Encoding UTF8
```

> 注意：该日志是 **UTF-8**，`Get-Content` 默认按 ANSI/GBK 解码会让中文匹配全部落空（本次排查踩过一次）。

4. 修复验收：同场景下 `等认证` 计数应为 0，且每次 `240 上限` 之后 1~2 秒内必然出现对应的 `认证/登录` 行。

## 7. 2026-10-29 更新：实际改了什么、还剩什么

### 已落地

| 项 | 落点 | 效果 |
|---|---|---|
| **重连重放认证 + 登录** | `CtpSessionService.ReconnectAsync` 摘事件 → Dispose 旧 `CtpTrader` → 新建 → `LoginAsync`；而 `CtpTrader.LoginAsync` 内部顺序是「等连接 → `Authenticate` → `Login` → 结算确认」（`ctp-net-core/src/Ctp.Net.Core/Trader/CtpTrader.cs:177-196`） | §3.3 的缺陷本身不再成立：只要 `ReconnectAsync` 被触发，新连接**一定**重新认证 |
| **重连失败不漏连接** | 同方法内新增 catch：摘事件 + `Dispose` 新建的 trader 再抛（旧版把这条从未挂到 `_trader` 的实例直接丢掉，它会继续在后台自动重连/自动重登） | 每 30s 一次的重试不再累积幽灵连接 |
| **会话状态机 + Ready 门禁** | 新文件 `Ctp.Net.Client/CtpSessionState.cs`（`Disconnected / Connecting / Ready` + `CtpSessionNotReadyException`）；`CtpSessionService.State / Epoch / IsReady`；`GetTrader()` 改为 `RequireReady()` —— 报单、撤单、查询在非 Ready 时**直接抛异常** | §5 的 P0-3（静默黑洞）关闭：断线窗口里发单会得到 `CtpSessionNotReadyException`，调用方知道「等一下重发」 |
| **纪元** | `Epoch` 每次进入 Ready 自增 | 上层可判断「重发时的会话与失败时是不是同一条」 |
| **状态可见** | `State` 与 `CtpConnectionStatus` 同步切（断线 → `Connecting`，重登成功 → `Connected`） | §5 的 P0-4 的一半 |

### 仍缺（都是本文件 §5 里未做的项）

1. **触发条件仍依赖回调顺序**（P0-1）：`OnTraderFrontConnected` 还是 `if (_hadLoggedIn && !_socketAlive)`。§3.4 的 (a)/(b) 两种可能至今**没有客户端证据**能区分——要动它必须先补会话日志（P2），否则改错方向会引入重连风暴。
2. **`CtpTrader.IsAuthenticated` 未提供**（P0-2）：认证状态仍只存在于 SDK 内部；本层的 Ready 是「登录 + 结算确认都成功」的等价物，够门禁用，但拿不到「认证已过、登录未过」这一中间态。
3. **240 断连未挪安全点**（P1）：仍是攒满即断。
4. **会话日志未恢复**（P2）：bot 的 stdout 依旧重定向到 `NUL`。

> 验收口径不变：`等认证` 计数为 0，且每次 `240 上限` 后 1~2 秒内必有 `认证/登录` 行。区别是现在「没有 Ready 就发单」这件事在客户端侧就有明确异常，不再需要靠前置日志反推。
