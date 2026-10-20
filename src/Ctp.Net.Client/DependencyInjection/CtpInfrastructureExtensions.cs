using System.IO;
using Ctp.Net;
using Ctp.Net.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Ctp.Net.Client.DependencyInjection;

/// <summary>
/// DI entry point for the CTP infrastructure layer. Registers the CTP session
/// (the connection to the CTP broker) + the SQLite config store for credentials.
/// <para>
/// <b>一律官方 SDK 链路</b>（2026-10-01 起）：<see cref="ICtpSession"/> 直连 <see cref="CtpSessionService"/>——
/// 本地仿真柜台填前置 <c>tcp://127.0.0.1:17801</c>（交易）/ <c>tcp://127.0.0.1:17802</c>（行情，host 进程
/// <c>dotnet run --project aether-apps/Aether.Exchange.Host</c>）；真实前置（SimNow 等）同一链路。
/// 旧 <c>sim</c> 关键字 JSON-RPC 直连已退役（2026-10-04 死代码清理：<c>RoutingCtpSession</c> 删除；
/// <c>RemoteCtpSession</c> 迁至 <c>Aether.Exchange.Rpc</c> 归属仿真侧——客户端上下文自此零引用服务端项目）。
/// </para>
/// </summary>
public static class CtpInfrastructureExtensions
{
    /// <param name="sqliteDbPath">SQLite file for the ctp_config table. Falls back to <c>CTP_DB_PATH</c>, then <c>~/.aether/ctp.db</c>.</param>
    public static IServiceCollection AddCtpInfrastructure(
        this IServiceCollection services,
        string? sqliteDbPath = null)
    {
        var dbPath = sqliteDbPath
            ?? Environment.GetEnvironmentVariable("CTP_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "ctp.db");

        // 2026-10-01 用户定调：桌面端砍掉 sim 关键字直连（JSON-RPC），一律走前置端口——
        // ICtpSession 直连官方 SDK 链路（本地前置 tcp://127.0.0.1:17801/17802 或真实 SimNow 同链路）。
        // 2026-10-04：RoutingCtpSession（死代码）删除；RemoteCtpSession 迁至 Aether.Exchange.Rpc（E2E 用）。
        services.AddSingleton<CtpSessionService>();
        services.AddSingleton<ICtpSession>(sp => sp.GetRequiredService<CtpSessionService>());

        services.AddSingleton<ICtpConfigStore>(_ => SqliteCtpConfigStore.ForFile(dbPath));
        return services;
    }
}
