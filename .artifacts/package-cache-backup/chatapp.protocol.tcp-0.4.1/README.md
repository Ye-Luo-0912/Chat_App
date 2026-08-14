# ChatApp.Shared

`ChatApp.Shared` 是 ChatApp 各应用之间共享的跨进程契约仓库。它只承载可版本化的 wire、JSON、缓存 schema 与稳定基础类型，不承载 Redis、NATS、Entity Framework Core、ASP.NET Core、UI 或业务实现。

## 可发布包

| 包 | 版本 | 职责 | 内部依赖 |
| --- | --- | --- | --- |
| `ChatApp.Auth.Contracts` | `0.3.0` | AccessToken Redis 键、缓存值 schema、账户状态与 JSON metadata | 无 |
| `ChatApp.Contracts.Http` | `0.3.0` | Auth、好友、附件、会话 HTTP wire DTO 与 JSON metadata | 无 |
| `ChatApp.Protocol.Tcp` | `0.3.0` | TCP 帧常量、命令号、能力位、错误码与握手/恢复 DTO | 无 |
| `ChatApp.Protocol.Tcp.Json` | `0.3.0` | TCP DTO 的 source-generated JSON metadata 与统一序列化策略 | `ChatApp.Protocol.Tcp` |

这些包均为 .NET 10、BCL-only 包，并作为本轮完整契约抽取统一发布 `0.3.0`。

当前没有 `ChatApp.Shared.Primitives` 项目：尚无两个以上语义稳定的消费者，暂不建立 speculative primitives 包。未来满足共享门槛时再以真实类型、消费者矩阵和兼容性测试共同引入，不保留空 marker。

## Realtime 包谱系

`ChatApp.Realtime.Contracts` 已在现有 Realtime 工程中形成 2.x 包谱系。本仓库不创建同名 0.x 包，也暂不发布新的 `ChatApp.Realtime.Client.Abstractions`。当前 NATS 客户端集成由 `ChatApp.Realtime.Integration 3.0.0` 提供；EF Outbox 模型与映射已独立为 `ChatApp.Realtime.Outbox.EntityFrameworkCore 1.0.0`，仅由需要 EF 的宿主引用。后续提取实时契约时，必须延续各自 PackageId 与版本历史，不能从本仓另起重名包。

## 兼容性承诺

本次迁移保持的是跨进程契约兼容性：

- TCP Magic、帧头布局、命令号、协议版本和枚举数值；
- JSON 字段名、大小写、null 行为和数值表示；
- Auth 刷新凭据、过期时间，好友分页/envelope，以及附件上传 header；
- AccessToken Redis 键和值 schema。

类型迁移到 `ChatApp.Shared.*` 命名空间是有意的源码 breaking migration，并不承诺旧程序集或旧 namespace 的二进制/源码 ABI。所有直接引用这些类型的源码消费者必须在同一迁移批次更新；如需错峰发布，应由消费者临时提供 alias/facade，同时仍以本包的 wire contract 为唯一事实源。

## 依赖边界

- `src` 下所有项目必须保持 BCL-only：不得添加外部 `PackageReference`、`FrameworkReference` 或手工程序集 `Reference`。
- 共享项目之间只在真实类型使用发生时添加 `ProjectReference`，不得为未来可能的需要预先耦合。
- Redis/NATS/EF/ASP.NET/日志等实现放在独立 adapter 包或原业务仓库中，不进入 contracts。
- TCP DTO、HTTP DTO、NATS payload 分开维护；字段相似不代表它们是同一 wire contract。
- 每个项目拥有独立的 `PackageId`、`AssemblyName` 与 `RootNamespace`。
- 最大帧长、超时、限流等端点策略不进入固定帧布局常量。

更详细的判定和迁移规则见 [docs/BOUNDARIES.md](docs/BOUNDARIES.md)，首批完成状态与后续顺序见
[docs/MIGRATION-ROADMAP.md](docs/MIGRATION-ROADMAP.md)。

## 本地验证与打包

```powershell
dotnet restore .\ChatApp.Shared.slnx
dotnet build .\ChatApp.Shared.slnx -c Release --no-restore
dotnet test .\ChatApp.Shared.slnx -c Release --no-build
dotnet pack .\ChatApp.Shared.slnx -c Release --no-build -o .\artifacts\packages
```

架构测试会检查源项目依赖、唯一项目身份、目标框架和编译后程序集引用。`artifacts/` 是本地生成目录且不会提交；正式推送包源应由后续 CI 发布流程完成。
