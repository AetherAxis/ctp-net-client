# Ctp.Net.Client

客户端侧 CTP SDK 封装——「去连的一方」：官方 SDK 链路（ctp-net-core wrapper）封装为 `Ctp.Net.ICtpSession` 会话端口 + SQLite 凭据档案仓。与服务端 ctp-net-front（「被连的一方」，线协议服务器）分仓。

## 组成

| 文件 | 说明 |
|---|---|
| `CtpSessionService.cs` | 官方 SDK 会话：断线/闭市/重连状态机 + 30s 安全定时器；同名方法双形态（public 原生 SDK 类型供 AI 工具 / explicit `ICtpSession` DTO 面） |
| `CtpSessionMapper.cs` | DomainMapper——SDK 类型 ↔ `Ctp.Net` DTO 的唯一转换入口（业务核心与协议层的类型隔离） |
| `SqliteCtpConfigStore.cs` | `ICtpConfigStore` 实现（连接档案/凭据持久化，chmod 600） |
| `Persistence/` | SqliteStoreBase / SqliteMigrations（同命名空间吸收） |
| `DependencyInjection/CtpInfrastructureExtensions.cs` | DI 入口 `AddCtpInfrastructure()`（会话 + 配置仓注册） |

## 三层命名空间分离

```
Ctp.Net          契约 DTO    （ctp-net-contracts）
Ctp.Net.Core.*   SDK wrapper （ctp-net-core）
Ctp.Net.Client   实现层      （本仓）
```

## 使用

```csharp
services.AddCtpInfrastructure();   // ICtpSession + ICtpConfigStore 注册
```

会话连接参数（前置地址/凭据）经 `ICtpConfigStore` 档案或调用方注入——本地仿真柜台填 `tcp://127.0.0.1:17801`（交易）/ `tcp://127.0.0.1:17802`（行情），真实前置（SimNow 等）同一链路。

## 构建与测试

```bash
dotnet test ctp-net-client.slnx    # tests/Ctp.Net.Client.Tests：25 例（会话注册 DI / SimFront 地址判据 / SQLite 凭据仓含 0600 权限）——纯逻辑零 native SDK，CI 可全量跑
```

## 来历

2026-10-20 拆自 Aether monorepo `business/Ctp/Acme.Ctp.Infrastructure/`（初名 `Acme.Ctp.Infrastructure`，同日命名统一 `Ctp.Net.Client`）。完整决策记录见工作区根 `MIGRATION.md`。
