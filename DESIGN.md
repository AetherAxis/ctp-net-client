# Ctp.Net.Client 设计说明

> 本文件写「怎么设计 / 为什么这么设计 / 还能往哪升级」。工作区边界与迁移决策以根 `MIGRATION.md` 为准。

## 1. 定位与边界

本仓是客户端侧 SDK 封装（「去连的一方」）：把官方 CTP SDK 收敛为 `Ctp.Net.ICtpSession` 端口，并提供 SQLite 档案仓；对端 ctp-net-front 是线协议服务器。

- 范围：会话生命周期、交易/查询/行情原语、DTO 映射、凭据持久化、DI；不含策略与 UI。
- 分层：契约 ctp-net-contracts，SDK ctp-net-core，本仓实现；SDK 类型不出程序集。

## 2. 项目组成

| 文件 | 职责 | 关键类型 | 出向依赖 |
|---|---|---|---|
| `CtpSessionService.cs` | 生命周期；断线/闭市/重连状态机；30s 定时器；同名双形态 | `CtpSessionService` | core、contracts |
| `CtpSessionMapper.cs` | SDK ↔ DTO 唯一转换入口；哨兵归零 | `internal static CtpSessionMapper` | core、contracts |
| `SqliteCtpConfigStore.cs` | 多档案凭据仓；last-used 唯一性；旧表迁移 | `SqliteCtpConfigStore` | Sqlite、contracts、Persistence |
| `Persistence/` | 公共吸收件：连接/WAL/busy_timeout/惰性建表；幂等加列 | `SqliteStoreBase`、`SqliteMigrations` | Microsoft.Data.Sqlite |
| `DependencyInjection/` | DI 入口 | `AddCtpInfrastructure()` | contracts、MS.DI.Abstractions |
| `tests/.../Ctp.Net.Client.Tests` | 24 例：DI 1、SimFront 13、SQLite 10 | xUnit | 本仓、contracts、xunit 栈 |

## 3. 关键设计决策

### 3.1 SDK 类型隔离 + 单一映射点
- **决策**：`Ctp.Net.Core` 类型只在本程序集；跨边界只用 `Ctp.Net` DTO；转换集中 `CtpSessionMapper`，枚举按镜像 byte 强转，`Nv()` 归零 DBL_MAX/负数哨兵。
- **理由**：P/Invoke 类型不外溢；字段变更只改一处；哨兵不归零会显示巨值并算错涨跌幅。
- **代价**：新字段需补映射；Mapper 无测试；合约查询内联。

### 3.2 ICtpSession 端口 + 同名双形态
- **决策**：公开方法保留原生形态（如 `LoginAsync(CtpConnectRequest)→LoginResult`）供 AI 工具使用；`ICtpSession` 用 explicit 实现提供 DTO 形态（`CtpLoginRequest→CtpLoginResult`），SDK 异常译为 `Ctp.Net.CtpException`/`CtpBusinessException`。
- **理由**：一份状态服务两个消费面；explicit 避开同名冲突；异常不泄漏。
- **代价**：约 9 对方法手工同步；explicit 方法在具体类型不可见；无一致性测试。

### 3.3 断线 / 闭市 / 重连状态机 + 30s 安全定时器
- **决策**：断线置 `_socketAlive=false`，按 `ShouldAutoReconnect()` 切 `Reconnecting`/`Closed`；仿真前置恒重连，真实前置按交易日历。`FrontConnected` 且曾登录则 `Task.Run(ReconnectAsync)`；30s `Timer` 兜底；`SemaphoreSlim.WaitAsync(0)` 防并发；行情腿独立状态 + 重入守卫，失败 5s 退避。
- **理由**：重连后会话失效须重登重订阅；SDK 可能放弃重连；native 回调内 Dispose 触发 C++ 纯虚崩溃；MD 重登会自触发风暴。
- **会话门禁（2026-10-29 补）**：在上面这套「可变标志」之上加一层显式状态：`State`（`Disconnected / Connecting / Ready`）
  与单调自增的 `Epoch`（`CtpSessionState.cs`）。所有对外写入（报单/撤单/查询）先过 `RequireReady()`，
  非 Ready 直接抛 `CtpSessionNotReadyException`（带当时的 `State` 与 `Epoch`），**不再把帧写进一个注定被丢的连接**。
  `Epoch` 让调用方能判断「重发时的会话与失败时是不是同一条」。
- **代价**：十余个可变标志仍靠约定（状态机是叠加层，不是替换层）；状态迁移本身仍无行为测试（§6 #2）；无重连上限。

### 3.4 凭据 SQLite：多档案 + 幂等迁移
- **决策**：`ctp_profile` 多行表，`is_last_used` 唯一性由 `SaveAsync` 事务内降级其它行；旧 `ctp_config` 保留为种子/回滚安全；`MigrateAsync` 幂等补列并在表空时种一行；WAL + `busy_timeout=5000`。
- **理由**：多前置/多账户与启动自动登录；从 monorepo 单行配置平滑升级；重复初始化安全。
- **代价**：密码明文落库；README 称 chmod 600 但未实现；SQL 为 SQLite 方言。

### 3.5 DI 组合根
- **决策**：`AddCtpInfrastructure(dbPath)` 注册 `CtpSessionService` 单例，`ICtpSession` 映射同一实例；`ICtpConfigStore` 单例 `ForFile`；路径 参数→`CTP_DB_PATH`→`~/.aether/ctp.db`。
- **理由**：有状态单会话，UI 与 AI 工具须共享；测试用 `Assert.Same` 钉住。
- **代价**：无法多账户并行；路径注册时固化。

## 4. 运行与验证

`dotnet test ctp-net-client.slnx`：25 例（DI 1 + SimFront Theory 13 + SQLite 11），纯逻辑、不触发 native 登录；会话状态机目前**没有直接用例**（§6 #2）。CI 在 ubuntu-latest/.NET 10.0.x 并排 checkout 三仓，注入 GitHub Packages 凭据后 Release build + test。本次未跑测试。

## 5. 升级记录（2026-10-24）

`git diff`：依赖版本改动仅在 `Directory.Packages.props`（HEAD `ea92499`）。

| 项 | 变更前 | 变更后 | 依据 / 验证 |
|---|---|---|---|
| Microsoft.Data.Sqlite | 10.0.0 | 10.0.13 | — |
| MS.Extensions.DependencyInjection.Abstractions | 10.0.0 | 10.0.13 | — |
| MS.Extensions.DependencyInjection | 10.0.0 | 10.0.13 | 仅测试项目 |
| Microsoft.NET.Test.Sdk | 17.14.1 | 18.11.0 | 测试栈 |
| xunit.runner.visualstudio | 3.1.4 | 4.0.1 | xunit 保持 2.9.3 |
| coverlet.collector | 6.0.4 | 10.1.0 | CI 未收集覆盖率 |

## 6. 待升级项

| # | 优先级 | 项目 | 现状证据 | 建议 | 代价 / 风险 |
|---|---|---|---|---|---|
| 1 | P1 | 凭据文件权限 | `README.md:11` 称 chmod 600；`src/` 无 `UnixFileMode`，密码明文 | 库文件设 0600 或用钥匙串 | 跨平台分支与测试 |
| 2 | P1 | 状态机无行为测试 | 服务 588 行；24 例只覆盖 DI、地址、SQLite | 抽出状态迁移纯函数补矩阵 | 需重构；native 难单测 |
| 3 | P1 | `_cachedSymbols` 并发读写 | 写在锁内（L226-248），读 L441/L513 无锁 | 锁内快照 `ToArray()` 再传入 | 低；消除并发异常 |
| 4 | P2 | Dispose/在飞重连竞态 | `_reconnectLock.Dispose()`（L586）后，在飞任务仍 `Release`（L527） | 加 `_disposed`/CTS 检查 | 低-中；勿吞异常 |
| 5 | P2 | 直接写 stderr | L566 `Console.Error.WriteLine`，无 `ILogger` | 注入可选 `ILogger` | 低；新增构造依赖 |
| 6 | P2 | 注释与实现漂移 | L446-447 称「计数达 220 重连」，无计数逻辑 | 实现计数或删注释 | 低；实现需验 E2 |
| 7 | P3 | 跨仓相对 ProjectReference | csproj 用 `..\..\..\ctp-net-*`；CI 需 checkout 两仓 | 切 `PackageReference` | 中；需发版联动 |

## 7. 本轮升级落地（2026-10-24）

- **凭据库 0600**（第 1 项）：`SqliteStoreBase.RestrictDatabaseFilePermissions()` 在 schema 初始化后把 DB 文件设为 `UserRead|UserWrite`（POSIX；Windows 走用户目录 ACL）。SQLite 用 DB 文件 mode 创建 `-wal`/`-shm`，收 DB 即覆盖三件套。README 承诺的 chmod 600 现在由代码兑现，不再靠部署方记得。
- **`_cachedSymbols` 并发**（第 3 项）：`OnMdFrontConnected` 与 `ReconnectAsync` 都在锁内取 `ToArray()` 快照再交给后台任务，不再把可变 `List` 递出锁外。
- **Dispose/在飞重连竞态**（第 4 项）：不再 `_reconnectLock.Dispose()`（SemaphoreSlim 无等待句柄时无托管资源，而 Dispose 与在飞 `Release` 是竞态，异常落在后台任务上无人接）；改由 `_disposed`（volatile）守卫——`ReconnectAsync`/`ReconnectMdAsync` 入口、退避后、`finally` 的 `Release` 前。`Dispose` 语义不变。
- **stderr → 可替换出口**（第 5 项）：`Console.Error` 换成实例 `LogSink`（默认仍写 stderr，保持可见性）。**偏离 §6 建议的 ILogger**：本仓是 native 封装库、日志只有几行，加 `Microsoft.Extensions.Logging.Abstractions` 还要给 `nuget.config` 的 source mapping 加条目；一个 sink 足够，且与 `CtpLeanGateway.LogSink` 同款。
- **注释漂移**（第 6 项）：`OnSafetyTimer` 上方「计数达 220 即主动重连」的说法没有实现——改成明确标注「未实现 + 为什么不在本层臆造代理计数」。
- 新增测试：凭据库文件权限为 0600。

**未做（有意）**：第 2 项状态机行为测试需要把 588 行的 native 服务抽出纯函数矩阵（重构面大，须与 E2 验证一起做）；第 7 项切 PackageReference 需先发包并版本对齐。

## 8. 本轮升级落地（2026-10-29）

| 项 | 落点 | 说明 |
|---|---|---|
| 会话状态机 + Ready 门禁 | 新增 `src/Ctp.Net.Client/CtpSessionState.cs`；`CtpSessionService.State / Epoch / IsReady / RequireReady()` | 断线即离开 Ready；报单/撤单/查资金/查持仓/查报单/查合约六个调用点都带上操作名，异常里能看出是哪一步被拒 |
| 重连失败不漏连接 | `ReconnectAsync` 的 catch 分支摘事件 + `Dispose` 新建的 trader 再抛 | 旧版把这条从未挂到 `_trader` 的实例直接丢掉，它会继续在后台自动重连/重登 ⇒ 每 30s 的重试都留一条幽灵连接 |
| 未 Ready 时不静默丢帧 | 同上（门禁） | 对应 `session-reauth-after-reconnect.md` §5 的 P0-3 |

仍缺（与本轮无关、已被文档钉住）：`FrontConnected` 仍以 `_socketAlive` 为闸（§6 之外，见 `doc/session-lifecycle.md` §11）、
会话日志未恢复、240 断连未挪安全点。

## 9. 本轮升级落地（2026-10-30）：状态机行为测试（§6 #2）

**问题**：#2 一直卡在「测状态迁移就得构造 `CtpSessionService`，而它构造即加载 `ctp_native`」——
于是「断线窗口里状态落在哪」这个**直接决定要不要发单**的判断，一条用例都没有。

**做法**：把迁移抽成纯函数 `src/Ctp.Net.Client/CtpSessionStateMachine.cs`
（`CtpSessionEvent` 三事件 + `Next/AllowsWrite/StartsNewEpoch`），并把服务里**全部 4 处** `_state` 赋值
换成走 `Next`（`EnterReady` / `LeaveReady` / `Dispose` / `ReconnectAsync`），`RequireReady` 的判据换成
`AllowsWrite`。测试覆盖的就是线上那份逻辑，不是平行实现。

新增 `tests/Ctp.Net.Client.Tests/CtpSessionStateMachineTests.cs`（20 例）：12 行迁移矩阵 +
三条不变量（只有登录成功能进 Ready / 连接丢了绝不停在 Ready / 只有 Ready 可写）+ 幂等与反复断线重登 +
纪元语义 + 未知事件抛异常。客户端测试总数 24 → 45，全绿。

**一处刻意的行为变更**：`Disconnected`（从未登录过）收到 `SocketLost` 现在**留在 Disconnected**，
不再因为「会自动重连」而宣称 Connecting。理由：枚举注释里 Connecting 的定义是「**曾登录过**，
正在重连 + 重登」，启动时前置还没起来的抖动不构成可恢复的会话。两种状态都禁止写操作，对交易零影响。

**仍未做**：#2 的另一半「native 壳内部的行为测试」依然不可能（没有接缝）——但那部分由
`ctp-net-front` 的 loopback E2E 用真 SDK 覆盖；`FrontConnected` 闸门（§11）与 240 安全点仍按原计划列在 front 仓。

## 10. 本轮升级落地（2026-10-30）：#3 FrontConnected 门控 + #13 OrderRef 本地高水位

### #3 `FrontConnected` 不再以 `_socketAlive` 为闸

**问题（2026-10-29 事故）**：`OnTraderFrontConnected` 的门控是 `_hadLoggedIn && !_socketAlive`，
它成立的前提是「**先**收到 `FrontDisconnected`」。SDK 在某些时序下（服务端主动 Close）只回调
`FrontConnected` ⇒ `_socketAlive` 仍为真 ⇒ 重连不触发 ⇒ 新连接从不发认证帧 ⇒ 之后的报单全被
柜台当垃圾帧丢弃。当时没敢动，是因为「没有客户端侧证据，改错方向就是重连风暴」。

**做法**：门控事实换成「**这条 trader 实例自己登过没有**」（`_traderLoggedIn`，volatile）：
只要它登过又收到 `FrontConnected`，就一定是掉线重连，必须重建 + 重登。
重连风暴不需要靠门控防——`ReconnectAsync` 内部 `_reconnectLock.WaitAsync(0)` 单飞；
新实例在它自己 `LoginAsync` 期间收到的 `FrontConnected` 因为 `_traderLoggedIn=false` 不会自触发。
顺带把「状态漂移」写成一条日志（`FrontConnected` 时 `_socketAlive` 仍为真）——这正是当初缺的证据，
以后再出现同类事件，bot 侧日志里能直接看到。

### #13 OrderRef 本地高水位

**问题**：`OrderRef` 只由柜台登录应答的 `MaxOrderRef` 决定。柜台会话一换（重启 / 换前置 / 换交易日）
计数归零 ⇒ 新会话从 1 发号 ⇒ 与前置/交易所里仍挂着的旧单撞号 ⇒ 撤错单。

**做法（分两层，各管一件事）**：

| 层 | 落点 | 说明 |
|---|---|---|
| core | `CtpTrader.OrderRefFloor` / `OrderRefHighWater` | 登录时起点 = `max(柜台 MaxOrderRef, OrderRefFloor)`；每发一个号就抬高 `OrderRefHighWater`（**先记账再返回**，发出去的号永不回卷）。core 不碰磁盘。 |
| client | `IOrderRefHighWaterStore` + `SqliteOrderRefHighWaterStore`（表 `ctp_order_ref`，键 `(broker,user)`） | 登录/重登成功即把持久化水位顶成 `OrderRefFloor`；下单后异步记账（不 await，审计性写入不加下单延迟）。`ON CONFLICT ... max()` 保证**只升不降**。 |

未注册 store 时（老宿主）行为与旧版完全一致——新能力是 opt-in，不是隐式改变下单路径。
新增测试 7 例（`OrderRefHighWaterStoreTests`）：读空、写读、只升不降、非正数忽略、账户隔离、跨进程重开、并发取最大。
客户端测试总数 45 → 52。
