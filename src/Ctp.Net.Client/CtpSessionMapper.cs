using System.Collections.Generic;
using System.Linq;
using Ctp.Net;
using CtpTraderAccount = Ctp.Net.Core.Trader.Account;
using CtpTraderPosition = Ctp.Net.Core.Trader.Position;
using CtpTraderOrder = Ctp.Net.Core.Trader.Order;
using CtpTraderTrade = Ctp.Net.Core.Trader.Trade;
using CtpTraderOrderRequest = Ctp.Net.Core.Trader.OrderRequest;
using CtpTraderLoginResult = Ctp.Net.Core.Trader.LoginResult;
using CtpTraderOrderInsertResult = Ctp.Net.Core.Trader.OrderInsertResult;
using CtpMarketTick = Ctp.Net.Core.MarketData.TickData;

namespace Ctp.Net.Client;

/// <summary>
/// Ctp.Net.Core SDK 模型 ↔ Domain.Abstractions.Ctp 领域 DTO 映射（双向）。
/// 隔离点：Infrastructure.Ctp 之外不接触 SDK 类型。
/// 枚举 byte 值与 SDK 镜像，直接强转。
/// </summary>
internal static class CtpSessionMapper
{
    // —— SDK → 领域 DTO ——

    public static CtpLoginResult ToDto(CtpTraderLoginResult r) =>
        new(r.TradingDay, r.LoginTime, r.FrontID, r.SessionID, r.MaxOrderRef);

    public static CtpAccount ToDto(CtpTraderAccount a) => new()
    {
        AccountId = a.AccountId,
        Balance = a.Balance, Available = a.Available, WithdrawQuota = a.WithdrawQuota,
        FrozenMargin = a.FrozenMargin, FrozenCash = a.FrozenCash, Commission = a.Commission,
        CurrMargin = a.CurrMargin, PositionProfit = a.PositionProfit, CloseProfit = a.CloseProfit,
    };

    public static CtpPosition ToDto(CtpTraderPosition p) => new()
    {
        InstrumentId = p.InstrumentId, ExchangeId = p.ExchangeId,
        PosiDirection = (CtpPosiDirection)p.PosiDirection,
        HedgeFlag = (CtpHedgeFlag)p.HedgeFlag,
        PositionDate = (CtpPositionDate)p.PositionDate,
        YdPosition = p.YdPosition, TotalPosition = p.TotalPosition, TodayPosition = p.TodayPosition,
        PositionCost = p.PositionCost,
        UseMargin = p.UseMargin, PositionProfit = p.PositionProfit, CloseProfit = p.CloseProfit,
    };

    public static CtpOrder ToDto(CtpTraderOrder o) => new()
    {
        InstrumentId = o.InstrumentId, ExchangeId = o.ExchangeId, OrderRef = o.OrderRef,
        FrontId = o.FrontID, SessionId = o.SessionID, OrderSysId = o.OrderSysID,
        Direction = (CtpDirection)o.Direction, OffsetFlag = (CtpOffsetFlag)o.OffsetFlag,
        PriceType = (CtpOrderPriceType)o.PriceType, LimitPrice = o.LimitPrice,
        VolumeTotalOriginal = o.VolumeTotalOriginal, VolumeTraded = o.VolumeTraded,
        VolumeTotal = o.VolumeTotal, OrderStatusRaw = o.OrderStatusRaw,
        InsertTime = o.InsertTime, UpdateTime = o.UpdateTime, StatusMsg = o.StatusMsg,
    };

    public static CtpTrade ToDto(CtpTraderTrade t) => new()
    {
        InstrumentId = t.InstrumentId, OrderRef = t.OrderRef,
        TradeId = t.TradeId, OrderSysId = t.OrderSysID,
        Direction = (CtpDirection)t.Direction, OffsetFlag = (CtpOffsetFlag)t.OffsetFlag,
        Price = t.Price, Volume = t.Volume, TradeTime = t.TradeTime,
    };

    public static CtpTick ToDto(CtpMarketTick t) => new()
    {
        InstrumentId = t.InstrumentId, ExchangeId = t.ExchangeId,
        TradingDay = t.TradingDay, ActionDay = t.ActionDay,
        UpdateTime = t.UpdateTime, UpdateMillisec = t.UpdateMillisec,
        LastPrice = Nv(t.LastPrice), Volume = Nv(t.Volume),
        OpenInterest = Nv(t.OpenInterest), Turnover = Nv(t.Turnover),
        BidPrice = Nv(t.BidPrice), BidVolume = Nv(t.BidVolume),
        AskPrice = Nv(t.AskPrice), AskVolume = Nv(t.AskVolume),
        PreSettlement = Nv(t.PreSettlement), PreClose = Nv(t.PreClose),
        OpenPrice = Nv(t.OpenPrice), Highest = Nv(t.Highest), Lowest = Nv(t.Lowest),
    };

    /// <summary>CTP 无值哨兵归零：价格用 DBL_MAX(1.79e308)、int 用 0xFFFFFFFF(→-1) 表示无数据，
    /// 不归零会让显示爆成长串、并把涨跌幅算成 -100%。这里统一归零。</summary>
    private static double Nv(double v) => (double.IsNaN(v) || double.IsInfinity(v) || v > 1e15) ? 0 : v;
    private static int Nv(int v) => v < 0 ? 0 : v;
    private static long Nv(long v) => v < 0 ? 0 : v;

    public static CtpOrderInsertResult ToDto(CtpTraderOrderInsertResult r) =>
        new(r.OrderRef, r.RequestId);

    public static IReadOnlyList<CtpPosition> ToDtoList(IReadOnlyList<CtpTraderPosition> list) =>
        list.Select(ToDto).ToList();

    public static IReadOnlyList<CtpOrder> ToDtoList(IReadOnlyList<CtpTraderOrder> list) =>
        list.Select(ToDto).ToList();

    public static IReadOnlyDictionary<string, CtpTick> ToDtoDict(IReadOnlyDictionary<string, CtpMarketTick> dict) =>
        dict.ToDictionary(kv => kv.Key, kv => ToDto(kv.Value));

    // —— 领域 DTO → SDK（下单/登录入参）——

    public static CtpTraderOrderRequest ToSdk(CtpOrderRequest r) => new()
    {
        BrokerId = r.BrokerId,
        InvestorId = r.InvestorId,
        InstrumentId = r.InstrumentId,
        Direction = (global::Ctp.Net.Core.Trader.Direction)r.Direction,
        OffsetFlag = (global::Ctp.Net.Core.Trader.OffsetFlag)r.OffsetFlag,
        PriceType = (global::Ctp.Net.Core.Trader.OrderPriceType)r.PriceType,
        LimitPrice = r.LimitPrice,
        Volume = r.Volume,
        TimeCondition = (global::Ctp.Net.Core.Trader.TimeCondition)r.TimeCondition,
    };
}
