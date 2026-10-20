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
    private CtpConnectRequest? _cachedTraderCreds;          // 重登用缓存凭据
    private MdCreds? _cachedMdCreds;                          // 行情重订阅缓存
    private readonly List<string> _cachedSymbols = new();    // 重订阅缓存合约
    private volatile bool _simFront;                          // 目标是否自建仿真前置（断线后跳过时段判断，见 ShouldAutoReconnect）
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private Timer? _safetyTimer;
    private volatile bool _reconnecting;
    private bool _disposed;

    private sealed record MdCreds(string Front, string BrokerId, string UserId, string Password);

    /// <summary>socket 存活 && 对象未释放才算已连接（修复"前置关闭后 IsConnected 仍 true"的假状态）。</summary>
    public bool IsConnected => _trader != null && !_trader.IsDisposed && _socketAlive;

    public bool IsMdSubscribed => _md != null && !_md.IsDisposed;

    /// <summary>登录结果（Ctp.Net.Core 原生类型，供同程序集 AI 工具使用）。</summary>
    public LoginResult? LoginResult => _loginResult;

    /// <summary>登录结果（领域 DTO，<see cref="ICtpSession"/> 显式实现，避免与原生属性同名冲突）。</summary>
    CtpLoginResult? ICtpSession.LoginResult => _loginResult is null ? null : CtpSessionMapper.ToDto(_loginResult);

    public string? BrokerId => _brokerId;

    public string? UserId => _userId;

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
        SetStatus(CtpConnectionStatus.Disconnected);
    }

    internal CtpTrader GetTrader()
    {
        lock (_lock)
        {
            return _trader ?? throw new InvalidOperationException("CTP 未登录");
        }
    }

    // —— 原生查询/交易方法（供同程序集 AI 工具使用）——

    public async Task<Account> QueryTradingAccountAsync(string brokerId, string accountId)
    {
        var trader = GetTrader();
        return await trader.QueryTradingAccountAsync(brokerId, accountId);
    }

    public async Task<IReadOnlyList<Position>> QueryInvestorPositionAsync(string brokerId, string investorId, string? instrumentId = null)
    {
        var trader = GetTrader();
        return await trader.QueryInvestorPositionAsync(brokerId, investorId, instrumentId);
    }

    public async Task<OrderInsertResult> OrderInsertAsync(OrderRequest request)
    {
        var trader = GetTrader();
        return await trader.OrderInsertAsync(request);
    }

    public async Task OrderActionAsync(string brokerId, string investorId, int frontId, int sessionId, string orderRef, string exchangeId)
    {
        var trader = GetTrader();
        await trader.OrderActionAsync(brokerId, investorId, frontId, sessionId, orderRef, exchangeId);
    }

    public async Task<IReadOnlyList<Order>> QueryOrderAsync(string brokerId, string investorId, string? instrumentId = null)
    {
        var trader = GetTrader();
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
        var trader = GetTrader();
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
            orderRef.FrontId, orderRef.SessionId, orderRef.OrderRef, orderRef.ExchangeId);
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
        var creds = _cachedMdCreds;
        var symbols = _cachedSymbols;
        if (creds == null || symbols.Count == 0) return;
        _ = Task.Run(() => ReconnectMdAsync(creds, symbols));
    }

    /// <summary>安全定时器：30s 巡检。盘中失活 → 强制重建重登（兜底 SDK 放弃重连）；闭市 → 维持 Closed。</summary>
    // E2 形态边界规避阈值（2026-10-15）：SDK 的查询族链路计数第 256 个起换编码形态，本地前置应答
    // 无法满足客户端校验——计数达 220（36 余量）即主动重连交易腿（计数清零）。行情腿保留不动 ⇒
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
        if (!await _reconnectLock.WaitAsync(0)) return; // 已有重连在进行，跳过
        _reconnecting = true;
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

            var result = await trader.LoginAsync(creds);

            lock (_lock)
            {
                _trader = trader;
                _loginResult = result;
                _socketAlive = true;
            }
            SetStatus(CtpConnectionStatus.Connected);

            // 重新订阅行情（若有缓存）
            var md = _cachedMdCreds;
            if (md != null && _cachedSymbols.Count > 0)
                _ = ReconnectMdAsync(md, _cachedSymbols);
        }
        catch
        {
            // 重登失败：保持失活。盘中定时器继续重试；闭市切 Closed。
            _socketAlive = false;
            SetStatus(ShouldAutoReconnect()
                ? CtpConnectionStatus.Reconnecting
                : CtpConnectionStatus.Closed);
        }
        finally
        {
            _reconnecting = false;
            _reconnectLock.Release();
        }
    }

    /// <summary>重建行情前置 + 重新登录 + 重新订阅。行情失败不影响交易状态。</summary>
    private async Task ReconnectMdAsync(MdCreds creds, IReadOnlyList<string> symbols)
    {
        // 重入守卫（2026-10-19 md 重连风暴根因）：LoginAsync 期间新连接的 FrontConnected 同样满足
        // OnMdFrontConnected 的 `!_mdSocketAlive` 条件 ⇒ 立即重入本方法 ⇒ 开头 Dispose 刚登录的连接
        // ⇒ 前置侧表现为「md 登录成功后立即断开」的每秒数十次风暴（F0 回收断交易腿触发首轮后自持）。
        if (_mdReconnecting) return;
        _mdReconnecting = true;
        try
        {
            lock (_lock)
            {
                if (_md != null)
                {
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
            Console.Error.WriteLine($"[CtpSession] MD 重连/重订阅失败（5s 后重试）: {ex.Message}");
            await Task.Delay(TimeSpan.FromSeconds(5));
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSafetyTimer();
        Logout();
        _reconnectLock.Dispose();
    }
}
