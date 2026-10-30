using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ctp.Net;
using Ctp.Net.Core.MarketData;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Client;

/// <summary>
/// CTP 会话服务：封装 CtpTrader/CtpMarketData 生命周期，实现 <see cref="ICtpSession"/> 领域契约。
/// 同时保留 Ctp.Net.Core 原生类型的 public 方法供本程序集内的 AI 工具（Tools/*）直接使用——
/// 工具返回原生类型给 LLM；Application/UI 层只通过 <see cref="ICtpSession"/> 拿领域 DTO。
/// </summary>
/// <remarks>
/// <b>断线/闭市处理</b>：CTP 前置只在交易时段开放，收盘会断连。本服务订阅 SDK 的
/// <c>FrontDisconnected</c>/<c>FrontConnected</c>：断线时置 socket 失活并按 <see cref="CtpTradingCalendar"/>
/// 判断是"盘中重连(Reconnecting)"还是"闭市等待(Closed)"；SDK 内置自动重连成功后用缓存的凭据自动重新登录 +
/// 重新订阅行情（CTP 重连后会话失效，必须重登）。另有 30s 安全定时器在开盘后强制重建重登（兜底 SDK 放弃重连的情况）。
/// </remarks>
public class CtpSessionService : ICtpSession, IDisposable
{
    private CtpTrader? _trader;
    private CtpMarketData? _md;
    private LoginResult? _loginResult;
    private string? _brokerId;
    private string? _userId;
    private readonly Dictionary<string, TickData> _latestTicks = new();
    private readonly object _lock = new();

    // —— 断线/重连状态 ——
    private volatile bool _socketAlive;          // 交易 socket 是否存活（断线后 false，重连重登成功 true）
    private volatile bool _hadLoggedIn;          // 是否曾成功登录过（区分首次连接 vs 重连）
    private volatile bool _mdSocketAlive;        // 行情 socket 是否存活
    private volatile bool _mdHadSubscribed;      // 是否曾成功订阅过行情（区分首次连接 vs 重连）
    private volatile bool _mdReconnecting;       // MD 重连进行中（重入守卫——见 ReconnectMdAsync 注释）
    private CtpConnectionStatus _status = CtpConnectionStatus.Disconnected;

    /// <summary>会话状态机（2026-10-29——见 <see cref="CtpSessionState"/>）。写入类操作只在 Ready 时放行。</summary>
    private volatile CtpSessionState _state = CtpSessionState.Disconnected;

    /// <summary>会话纪元：每次「Ready」自增。上层用它判断「重发时的会话与失败时是不是同一条」。</summary>
    private long _epoch;
    private CtpConnectRequest? _cachedTraderCreds;          // 重登用缓存凭据
    private MdCreds? _cachedMdCreds;                          // 行情重订阅缓存
    private readonly List<string> _cachedSymbols = new();    // 重订阅缓存合约
    private volatile bool _simFront;                          // 目标是否自建仿真前置（断线后跳过时段判断，见 ShouldAutoReconnect）
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private Timer? _safetyTimer;
    private volatile bool _reconnecting;
    private volatile bool _disposed;

    private sealed record MdCreds(string Front, string BrokerId, string UserId, string Password);

    /// <summary>socket 存活 && 对象未释放才算已连接（修复"前置关闭后 IsConnected 仍 true"的假状态）。</summary>
    public bool IsConnected => _trader != null && !_trader.IsDisposed && _socketAlive;

    /// <summary>当前会话状态（状态机见 <see cref="CtpSessionState"/>）。</summary>
    public CtpSessionState State => _state;

    /// <summary>会话纪元：每次进入 Ready 自增（0 = 从未就绪）。</summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>是否已就绪（认证 + 登录 + 结算确认都完成）。写入类操作的前置条件。</summary>
    public bool IsReady => _state == CtpSessionState.Ready;

    /// <summary>
    /// 取 trader 并要求会话 Ready——<b>写入/查询的唯一入口</b>（2026-10-29）。
    /// 旧版直接返回 <c>_trader</c>：断线窗口里那个 trader 的 socket 已经死了，native 调用要么
    /// 静默排队（报单蒸发）、要么抛一个语义含混的 rc 错——两种都没有「等一下再发」的信息。
    /// </summary>
    private CtpTrader RequireReady(string operation)
    {
        lock (_lock)
        {
            if (_state != CtpSessionState.Ready || _trader is null || _trader.IsDisposed)
                throw new CtpSessionNotReadyException(_state, Epoch, operation);
            return _trader;
        }
    }

    /// <summary>进入 Ready（唯一写入口：登录成功 / 重登成功）。</summary>
    private void EnterReady()
    {
        Interlocked.Increment(ref _epoch);
        _state = CtpSessionState.Ready;
    }

    /// <summary>
    /// socket 失活后的状态落点：盘中 ⇒ <see cref="CtpSessionState.Connecting"/>（等重连重登），
    /// 闭市/放弃重连 ⇒ <see cref="CtpSessionState.Disconnected"/>。
    /// </summary>
    private void LeaveReady()
    {
        _state = ShouldAutoReconnect() ? CtpSessionState.Connecting : CtpSessionState.Disconnected;
    }

    public bool IsMdSubscribed => _md != null && !_md.IsDisposed;

    /// <summary>登录结果（Ctp.Net.Core 原生类型，供同程序集 AI 工具使用）。</summary>
    public LoginResult? LoginResult => _loginResult;

    /// <summary>登录结果（领域 DTO，<see cref="ICtpSession"/> 显式实现，避免与原生属性同名冲突）。</summary>
    CtpLoginResult? ICtpSession.LoginResult => _loginResult is null ? null : CtpSessionMapper.ToDto(_loginResult);

    public string? BrokerId => _brokerId;

    public string? UserId => _userId;

    /// <summary>诊断日志出口（默认 stderr——保持原有可见性；宿主可替换成自己的 logger/日志窗）。
    /// 不引 <c>Microsoft.Extensions.Logging.Abstractions</c>：本仓是 native 封装库，日志只有这几行，
    /// 一个 sink 足够，也免掉给 <c>nuget.config</c> 的 source mapping 加一条（同 CtpLeanGateway.LogSink 做法）。</summary>
    public Action<string> LogSink { get; set; } = m => Console.Error.WriteLine(m);

    // —— 事件：领域 DTO 类型（Application 层订阅；本程序集 AI 工具不订阅事件）——
    public event EventHandler<CtpOrder>? OrderReceived;
    public event EventHandler<CtpTrade>? TradeReceived;
    public event EventHandler<CtpTick>? TickReceived;
    public event EventHandler<CtpConnectionStatus>? ConnectionStateChanged;

    /// <summary>登录（Ctp.Net.Core 原生请求，供同程序集 AI 工具 CtpLoginTool 使用）。</summary>
    public async Task<LoginResult> LoginAsync(CtpConnectRequest request)
    {
        lock (_lock)
        {
            if (IsConnected)
                throw new InvalidOperationException("已有活跃连接，请先登出");
        }

        var flowPath = Path.Combine(Path.GetTempPath(), "ctp_flow", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(flowPath);

        var trader = new CtpTrader(flowPath);
        trader.OrderReceived += OnOrderReceived;
        trader.TradeReceived += OnTradeReceived;
        trader.FrontConnected += OnTraderFrontConnected;
        trader.FrontDisconnected += OnTraderFrontDisconnected;

        LoginResult result;
        try
        {
            result = await trader.LoginAsync(request);
        }
        catch
        {
            // 登录失败即弃 native 会话：SDK 失败后仍按前置地址后台自动重连+自动重登
            // （_hadLoggedIn=false 只挡本服务的重登，挡不住 SDK 自身）——不 Dispose 会留下
            // 永久重连的孤儿连接（连接泄漏 + 柜台日志刷屏 + 幽灵重登与后续手动登录互踩）。
            // 先摘事件再释放（镜像 ReconnectAsync 的释放序）。
            try { trader.FrontConnected -= OnTraderFrontConnected; } catch { }
            try { trader.FrontDisconnected -= OnTraderFrontDisconnected; } catch { }
            try { trader.Dispose(); } catch { }
            throw;
        }

        lock (_lock)
        {
            _trader = trader;
            _loginResult = result;
            _brokerId = request.BrokerId;
            _userId = request.UserId;
            _cachedTraderCreds = request;
            _simFront = IsSimFrontAddress(request.FrontAddress);
            _socketAlive = true;
            _hadLoggedIn = true;
        }

        EnterReady();
        StartSafetyTimer();
        SetStatus(CtpConnectionStatus.Connected);
        return result;
    }

    /// <summary>登录（领域 DTO，<see cref="ICtpSession"/> 实现）。</summary>
    async Task<CtpLoginResult> ICtpSession.LoginAsync(CtpLoginRequest request, CancellationToken ct)
    {
        var sdk = new CtpConnectRequest
        {
            FrontAddress = request.FrontAddress,
            BrokerId = request.BrokerId,
            UserId = request.UserId,
            Password = request.Password,
            AppId = request.AppId,
            AuthCode = request.AuthCode,
        };

        try
        {
            var result = await LoginAsync(sdk);
            return CtpSessionMapper.ToDto(result);
        }
        catch (global::Ctp.Net.Core.Trader.CtpBusinessException ex)   // SDK 业务异常 → 领域异常（不泄漏 SDK 类型）
        {
            throw new global::Ctp.Net.CtpBusinessException(ex.ErrorId, ex.ErrorMsg);
        }
        catch (global::Ctp.Net.Core.Trader.CtpException ex)           // SDK 通用异常 → 领域异常
        {
            throw new global::Ctp.Net.CtpException(ex.Message);
        }
    }

    public void Logout()
    {
        StopSafetyTimer();
        lock (_lock)
        {
            _md?.Dispose();
            _md = null;
            _trader?.Dispose();
            _trader = null;
            _loginResult = null;
            _brokerId = null;
            _userId = null;
            _latestTicks.Clear();
            _cachedTraderCreds = null;
            _cachedMdCreds = null;
            _cachedSymbols.Clear();
            _socketAlive = false;
            _hadLoggedIn = false;
            _mdSocketAlive = false;
            _mdHadSubscribed = false;
        }
        _state = CtpSessionState.Disconnected;
        SetStatus(CtpConnectionStatus.Disconnected);
    }

    /// <summary>
    /// 取 trader，<b>会话未就绪即抛 <see cref="CtpSessionNotReadyException"/></b>（2026-10-29）。
    /// 旧版在「已登录但 socket 已死」时照样把 trader 交出去——native 调用落进死连接，
    /// 报单静默蒸发（复盘里 117 单被当垃圾帧丢弃的同族缺口）。现在失败是显式的、可重试的。
    /// </summary>
    internal CtpTrader GetTrader(string operation = "交易/查询") => RequireReady(operation);

    // —— 原生查询/交易方法（供同程序集 AI 工具使用）——

    public async Task<Account> QueryTradingAccountAsync(string brokerId, string accountId)
    {
        var trader = GetTrader("查询资金");   // 未就绪即抛——不把查询排到死连接上
        return await trader.QueryTradingAccountAsync(brokerId, accountId);
    }

    public async Task<IReadOnlyList<Position>> QueryInvestorPositionAsync(string brokerId, string investorId, string? instrumentId = null)
    {
        var trader = GetTrader("查询持仓");
        return await trader.QueryInvestorPositionAsync(brokerId, investorId, instrumentId);
    }

    public async Task<OrderInsertResult> OrderInsertAsync(OrderRequest request)
    {
        var trader = GetTrader("报单");   // 断线窗口内直接拒绝：不排队、不静默丢弃
        return await trader.OrderInsertAsync(request);
    }

    public async Task OrderActionAsync(string brokerId, string investorId, int frontId, int sessionId, string orderRef, string exchangeId, string? instrumentId = null)
    {
        var trader = GetTrader("撤单");
        await trader.OrderActionAsync(brokerId, investorId, frontId, sessionId, orderRef, exchangeId, instrumentId);
    }

    public async Task<IReadOnlyList<Order>> QueryOrderAsync(string brokerId, string investorId, string? instrumentId = null)
    {
        var trader = GetTrader("查询报单");
        return await trader.QueryOrderAsync(brokerId, investorId, instrumentId);
    }

    public async Task SubscribeMarketDataAsync(string mdFrontAddress, string brokerId, string userId, string password, IReadOnlyList<string> symbols)
    {
        List<string> toSubscribe;
        bool needRebuild;
        lock (_lock)
        {
            var creds = new MdCreds(mdFrontAddress, brokerId, userId, password);
            // 已连接且同一账户：增量订阅新 symbols（不重建连接，避免覆盖已有订阅——如期货页全量 + 实盘 ag2610 累加）
            if (_md != null && _mdSocketAlive && _cachedMdCreds == creds)
            {
                toSubscribe = symbols.Where(s => !_cachedSymbols.Contains(s)).ToList();
                _cachedSymbols.AddRange(toSubscribe);
                needRebuild = false;
            }
            else
            {
                // 未连接或账户变：重建连接 + 订阅
                if (_md != null)
                {
                    _md.TickReceived -= OnTickReceived;
                    _md.FrontConnected -= OnMdFrontConnected;
                    _md.FrontDisconnected -= OnMdFrontDisconnected;
                    _md.Dispose();
                }

                var flow = Path.Combine(Path.GetTempPath(), "ctp_md_flow", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(flow);
                _md = new CtpMarketData(flow);
                _md.TickReceived += OnTickReceived;
                _md.FrontConnected += OnMdFrontConnected;
                _md.FrontDisconnected += OnMdFrontDisconnected;
                _cachedMdCreds = creds;
                _cachedSymbols.Clear();
                _cachedSymbols.AddRange(symbols);
                toSubscribe = symbols.ToList();
                needRebuild = true;
            }
        }

        if (needRebuild)
        {
            await _md!.LoginAsync(mdFrontAddress, brokerId, userId, password);
            await _md.SubscribeMarketDataAsync(toSubscribe);
            _mdSocketAlive = true;
            _mdHadSubscribed = true;   // 首次订阅完成——之后 FrontConnected 才视作重连，触发重订阅
        }
        else if (toSubscribe.Count > 0)
        {
            // 增量订阅（连接已建立，只订阅新 symbols，不覆盖已有订阅）
            await _md!.SubscribeMarketDataAsync(toSubscribe);
        }
        // else: 所有 symbols 已订阅，无操作
    }

    public void UnsubscribeMarketData()
    {
        lock (_lock)
        {
            if (_md != null)
            {
                _md.TickReceived -= OnTickReceived;
                _md.FrontConnected -= OnMdFrontConnected;
                _md.FrontDisconnected -= OnMdFrontDisconnected;
                _md.Dispose();
            }
            _md = null;
            _cachedMdCreds = null;
            _cachedSymbols.Clear();
            _mdSocketAlive = false;
            _mdHadSubscribed = false;
            _latestTicks.Clear();
        }
    }

    public TickData? GetLatestTick(string instrumentId)
    {
        lock (_lock)
        {
            _latestTicks.TryGetValue(instrumentId, out var tick);
            return tick;
        }
    }

    public IReadOnlyDictionary<string, TickData> GetAllLatestTicks()
    {
        lock (_lock)
        {
            return new Dictionary<string, TickData>(_latestTicks);
        }
    }

    // —— ICtpSession 领域 DTO 实现（Application 层调用面）——

    async Task<CtpAccount> ICtpSession.QueryAccountAsync(CancellationToken ct)
    {
        var a = await QueryTradingAccountAsync(_brokerId ?? "", _userId ?? "");
        return CtpSessionMapper.ToDto(a);
    }

    async Task<IReadOnlyList<CtpPosition>> ICtpSession.QueryPositionAsync(string? instrumentId, CancellationToken ct)
    {
        var list = await QueryInvestorPositionAsync(_brokerId ?? "", _userId ?? "", instrumentId);
        return CtpSessionMapper.ToDtoList(list);
    }

    async Task<IReadOnlyList<CtpOrder>> ICtpSession.QueryOrderAsync(string? instrumentId, CancellationToken ct)
    {
        var list = await QueryOrderAsync(_brokerId ?? "", _userId ?? "", instrumentId);
        return CtpSessionMapper.ToDtoList(list);
    }

    /// <summary>查合约（wrapper 2026-10-02 已包装 ReqQryInstrument）。本地前置返回上市表（tag 小应答）；
    /// 真实前置返回全市场清单（~1.6 万行，LZ4 批量格式——SDK 自己解）。板子只在 sim 分支调用。</summary>
    async Task<IReadOnlyList<CtpInstrument>> ICtpSession.QueryInstrumentsAsync(CancellationToken ct)
    {
        var trader = GetTrader("查询合约");
        var items = await trader.QueryInstrumentsAsync(ct: ct);
        return items.Select(i => new CtpInstrument
        {
            InstrumentId = i.InstrumentId,
            ExchangeId = i.ExchangeId,
            VolumeMultiple = i.VolumeMultiple,
            PriceTick = i.PriceTick,
        }).ToList();
    }

    async Task<CtpOrderInsertResult> ICtpSession.OrderInsertAsync(CtpOrderRequest request, CancellationToken ct)
    {
        var r = await OrderInsertAsync(CtpSessionMapper.ToSdk(request));
        return CtpSessionMapper.ToDto(r);
    }

    async Task ICtpSession.OrderActionAsync(CtpOrderRef orderRef, CancellationToken ct)
    {
        await OrderActionAsync(orderRef.BrokerId, orderRef.InvestorId,
            orderRef.FrontId, orderRef.SessionId, orderRef.OrderRef, orderRef.ExchangeId, orderRef.InstrumentId);
    }

    async Task ICtpSession.SubscribeMarketDataAsync(CtpMarketDataConnect connect, IReadOnlyList<string> symbols, CancellationToken ct)
    {
        await SubscribeMarketDataAsync(connect.MdFrontAddress, connect.BrokerId, connect.UserId, connect.Password, symbols);
    }

    CtpTick? ICtpSession.GetLatestTick(string instrumentId)
    {
        var t = GetLatestTick(instrumentId);
        return t is null ? null : CtpSessionMapper.ToDto(t);
    }

    IReadOnlyDictionary<string, CtpTick> ICtpSession.GetAllLatestTicks()
        => CtpSessionMapper.ToDtoDict(GetAllLatestTicks());

    // —— 内部事件桥接：SDK 原生事件 → 领域 DTO 事件 + tick 缓存 ——

    private void OnOrderReceived(object? sender, Order order)
    {
        OrderReceived?.Invoke(this, CtpSessionMapper.ToDto(order));
    }

    private void OnTradeReceived(object? sender, Trade trade)
    {
        TradeReceived?.Invoke(this, CtpSessionMapper.ToDto(trade));
    }

    private void OnTickReceived(object? sender, TickData tick)
    {
        lock (_lock)
        {
            _latestTicks[tick.InstrumentId] = tick;
        }
        TickReceived?.Invoke(this, CtpSessionMapper.ToDto(tick));
    }

    // —— 断线 / 重连 / 闭市 ——

    /// <summary>断线后是否走「盘中重连」路径。真实前置维持交易时段判断（闭市躺平等开盘）；
    /// <b>自建仿真前置（24 小时沙盒）恒 true</b>——服务器交易所从不因时段拒单
    /// （<c>SimOptions.RejectOrdersOutsideTradingTime</c> 默认关），客户端若在周末/深夜断线后
    /// 切 Closed 躺到下个交易时段，会把 bot 挂机打断（2026-10-17 用户定调：自己的前置要 24 小时可交易）。</summary>
    private bool ShouldAutoReconnect() => _simFront || CtpTradingCalendar.IsTradingTime(DateTime.Now);

    /// <summary>前置地址是否指向自建仿真柜台（镜像 <c>CtpConnectorViewModel.IsSimFront</c> 的纯文本判定——
    /// Infrastructure 不引 apps 层；判据同步维护）：sim 哨兵 / 任意 host 的 :17801（本地与远程服务器部署形态
    /// 统一按端口判）/ 127.0.0.1:17800（旧 JSON-RPC 形态）；容忍 tcp:// 前缀与 localhost。</summary>
    internal static bool IsSimFrontAddress(string? frontAddress)
    {
        if (string.IsNullOrWhiteSpace(frontAddress)) return false;
        var a = frontAddress.Trim();
        if (string.Equals(a, "sim", StringComparison.OrdinalIgnoreCase)) return true;
        var bare = a.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ? a["tcp://".Length..] : a;
        bare = bare.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase);
        return bare.EndsWith(":17801", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bare, "127.0.0.1:17800", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>交易前置断线（收盘/网络）。置 socket 失活，按交易时段切 Reconnecting/Closed。</summary>
    private void OnTraderFrontDisconnected(object? sender, int reason)
    {
        _socketAlive = false;
        LeaveReady();   // 断线即刻关闸：此刻到重登完成之间不允许发单（旧版这段窗口是黑洞）
        SetStatus(ShouldAutoReconnect()
            ? CtpConnectionStatus.Reconnecting
            : CtpConnectionStatus.Closed);
    }

    /// <summary>交易前置（重）连上。若曾登录且 socket 失活 → 自动重新登录（CTP 重连后会话失效）。
    /// marshal 到 ThreadPool——不在 native 回调线程里 Dispose trader（否则触发 C++ 纯虚调用崩溃）。</summary>
    private void OnTraderFrontConnected(object? sender, EventArgs e)
    {
        if (_hadLoggedIn && !_socketAlive)
            _ = Task.Run(() => ReconnectAsync());
    }

    /// <summary>行情前置断线：标记失活（行情不影响交易状态机，仅清 tick 缓存避免脏数据）。</summary>
    private void OnMdFrontDisconnected(object? sender, int reason)
    {
        _mdSocketAlive = false;
        lock (_lock) _latestTicks.Clear();
    }

    /// <summary>行情前置（重）连上：若曾订阅且 socket 失活 → 重新登录 + 重新订阅缓存合约。
    /// 首次连接（_mdHadSubscribed=false）必须跳过——否则在 native 回调里 Dispose 正在回调的 _md → 纯虚崩溃。
    /// marshal 到 ThreadPool 同上。</summary>
    private void OnMdFrontConnected(object? sender, EventArgs e)
    {
        if (!_mdHadSubscribed || _mdSocketAlive) return;
        // 快照：_cachedSymbols 会被 SubscribeMarketDataAsync/UnsubscribeMarketData 在锁内改（AddRange/Clear），
        // 直接把 List 交给后台任务 = 边遍历边被改（InvalidOperationException/读到撕裂序列）。
        MdCreds? creds;
        string[] symbols;
        lock (_lock)
        {
            creds = _cachedMdCreds;
            symbols = _cachedSymbols.ToArray();
        }
        if (creds == null || symbols.Length == 0) return;
        _ = Task.Run(() => ReconnectMdAsync(creds, symbols));
    }

    /// <summary>安全定时器：30s 巡检。盘中失活 → 强制重建重登（兜底 SDK 放弃重连）；闭市 → 维持 Closed。</summary>
    /// <remarks>
    /// <b>未实现（原注释曾称已实现，属注释漂移——2026-10-24 校正）</b>：E2 形态边界规避阈值
    /// （2026-10-15 观察：SDK 查询族链路计数第 256 个起换编码形态，本地前置应答无法满足客户端校验 ⇒
    /// 设想「计数达 220 即主动重连交易腿」）。本服务没有该计数逻辑：计数发生在 SDK 内部链路，
    /// 且前置适配层的查询也走同一链路，本类数不到真实数字——**不在本层臆造一个代理计数**（会掩盖而非修复）。
    /// 真要落地须在 E2 前置侧验证后，以 SDK/前置的确定语义实现。
    /// </remarks>
    private void OnSafetyTimer(object? state)
    {
        if (!_hadLoggedIn || _socketAlive || _reconnecting) return;
        if (!ShouldAutoReconnect())
        {
            SetStatus(CtpConnectionStatus.Closed);
            return;
        }
        _ = ReconnectAsync();
    }

    private void StartSafetyTimer() => _safetyTimer ??= new Timer(OnSafetyTimer, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    private void StopSafetyTimer()
    {
        var t = _safetyTimer;
        _safetyTimer = null;
        t?.Dispose();
    }

    /// <summary>
    /// 重建交易前置 + 重新登录（重连兜底）。失败时保持失活，定时器/SDK 自动重连会继续尝试。
    /// SemaphoreSlim 防并发重连。
    /// </summary>
    private async Task ReconnectAsync()
    {
        if (_disposed) return;   // 已释放：不再起新连接（Dispose 后到达的 FrontConnected 回调）
        if (!await _reconnectLock.WaitAsync(0)) return; // 已有重连在进行，跳过
        _reconnecting = true;
        _state = CtpSessionState.Connecting;
        SetStatus(CtpConnectionStatus.Reconnecting);
        try
        {
            var creds = _cachedTraderCreds;
            if (creds == null) return;

            // 旧连接已死，释放重建
            lock (_lock)
            {
                if (_trader != null)
                {
                    // 事件订阅必须显式摘除：CtpTrader/CtpMarketData 是进程外句柄的托管壳，Dispose 只释放原生
                    // 句柄，挂在上面的托管委托仍指向本会话——旧实例一旦还有回调进来就会重复投递到同一个下游
                    // （2026-10-29 双 bot 事故复核：MD 重建路径只摘了 FrontConnected/Disconnected）。
                    try { _trader.OrderReceived -= OnOrderReceived; } catch { }
                    try { _trader.TradeReceived -= OnTradeReceived; } catch { }
                    try { _trader.FrontConnected -= OnTraderFrontConnected; } catch { }
                    try { _trader.FrontDisconnected -= OnTraderFrontDisconnected; } catch { }
                    _trader.Dispose();
                    _trader = null;
                }
            }

            var flowPath = Path.Combine(Path.GetTempPath(), "ctp_flow", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(flowPath);
            var trader = new CtpTrader(flowPath);
            trader.OrderReceived += OnOrderReceived;
            trader.TradeReceived += OnTradeReceived;
            trader.FrontConnected += OnTraderFrontConnected;
            trader.FrontDisconnected += OnTraderFrontDisconnected;

            LoginResult result;
            try
            {
                result = await trader.LoginAsync(creds);
            }
            catch
            {
                // 重建失败必须把这条**新** trader 收掉（2026-10-29 修）：旧版 catch 只置 _socketAlive=false，
                // 既不 Dispose 也不摘事件，而它从未赋给 _trader —— Logout/Dispose 永远够不到它。
                // 结果是每次重连失败都漏一条仍在后台自动重连 + 自动重登的幽灵连接（与首次登录路径
                // 专门写的清理正好相反，那段注释描述的也是这个失败模式）。
                try { trader.OrderReceived -= OnOrderReceived; } catch { }
                try { trader.TradeReceived -= OnTradeReceived; } catch { }
                try { trader.FrontConnected -= OnTraderFrontConnected; } catch { }
                try { trader.FrontDisconnected -= OnTraderFrontDisconnected; } catch { }
                try { trader.Dispose(); } catch { }
                throw;
            }

            lock (_lock)
            {
                _trader = trader;
                _loginResult = result;
                _socketAlive = true;
            }
            EnterReady();
            SetStatus(CtpConnectionStatus.Connected);

            // 重新订阅行情（若有缓存）——同样取快照（_cachedSymbols 在锁内会被改）
            MdCreds? md;
            string[] symbols;
            lock (_lock)
            {
                md = _cachedMdCreds;
                symbols = _cachedSymbols.ToArray();
            }
            if (md != null && symbols.Length > 0)
                _ = ReconnectMdAsync(md, symbols);
        }
        catch
        {
            // 重登失败：保持失活。盘中定时器继续重试；闭市切 Closed。
            _socketAlive = false;
            LeaveReady();
            SetStatus(ShouldAutoReconnect()
                ? CtpConnectionStatus.Reconnecting
                : CtpConnectionStatus.Closed);
        }
        finally
        {
            _reconnecting = false;
            // 不 Release 已释放的信号量（Dispose 与在飞重连的竞态——旧代码在 in-flight 的 Release 之前
            // 就 Dispose，Release 抛 ObjectDisposedException 落在后台任务上无人接）。见 Dispose。
            if (!_disposed) _reconnectLock.Release();
        }
    }

    /// <summary>重建行情前置 + 重新登录 + 重新订阅。行情失败不影响交易状态。</summary>
    private async Task ReconnectMdAsync(MdCreds creds, IReadOnlyList<string> symbols)
    {
        // 重入守卫（2026-10-19 md 重连风暴根因）：LoginAsync 期间新连接的 FrontConnected 同样满足
        // OnMdFrontConnected 的 `!_mdSocketAlive` 条件 ⇒ 立即重入本方法 ⇒ 开头 Dispose 刚登录的连接
        // ⇒ 前置侧表现为「md 登录成功后立即断开」的每秒数十次风暴（F0 回收断交易腿触发首轮后自持）。
        if (_disposed || _mdReconnecting) return;
        _mdReconnecting = true;
        try
        {
            lock (_lock)
            {
                if (_md != null)
                {
                    try { _md.TickReceived -= OnTickReceived; } catch { }
                    try { _md.FrontConnected -= OnMdFrontConnected; } catch { }
                    try { _md.FrontDisconnected -= OnMdFrontDisconnected; } catch { }
                    _md.Dispose();
                    _md = null;
                }
                var flow = Path.Combine(Path.GetTempPath(), "ctp_md_flow", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(flow);
                _md = new CtpMarketData(flow);
                _md.TickReceived += OnTickReceived;
                _md.FrontConnected += OnMdFrontConnected;
                _md.FrontDisconnected += OnMdFrontDisconnected;
            }

            await _md!.LoginAsync(creds.Front, creds.BrokerId, creds.UserId, creds.Password);
            await _md.SubscribeMarketDataAsync(symbols);
            _mdSocketAlive = true;
        }
        catch (Exception ex)
        {
            // 订阅失败：退避重试（受控递归，替代事件风暴）。旧版吞异常——md 静默死且第一推动力不可见。
            _mdSocketAlive = false;
            Log($"[CtpSession] MD 重连/重订阅失败（5s 后重试）: {ex.Message}");
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (_disposed) return;   // 退避期间宿主已释放——不再排重试（否则释放后仍在起连接）
            _ = Task.Run(() => ReconnectMdAsync(creds, symbols));
        }
        finally { _mdReconnecting = false; }
    }

    private void SetStatus(CtpConnectionStatus newStatus)
    {
        if (_status == newStatus) return;
        _status = newStatus;
        ConnectionStateChanged?.Invoke(this, newStatus);
    }

    private void Log(string message)
    {
        try { LogSink(message); } catch { /* 宿主的日志出口炸了不能反过来炸会话 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSafetyTimer();
        Logout();
        // 不 Dispose _reconnectLock：SemaphoreSlim 无等待句柄时没有非托管资源，而 Dispose 与在飞
        // ReconnectAsync 的 Release 是竞态——释放后 Release/WaitAsync 抛 ObjectDisposedException，
        // 且那行在后台任务的 finally 里，异常无人接（会变成 UnobservedTaskException）。
        // 释放语义由 _disposed 守卫（ReconnectAsync/ReconnectMdAsync 入口 + 退避后）承担。
    }
}
