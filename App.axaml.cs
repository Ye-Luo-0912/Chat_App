using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Identity;
using Chat_App.Infrastructure.Diagnostics;
using Chat_App.Presentation.ViewModels.Auth;
using Chat_App.Presentation.ViewModels.Chat;
using Chat_App.Presentation.ViewModels.Friends;
using Chat_App.Presentation.ViewModels.Shell;
using Chat_App.Presentation.Views;
using Chat_App.Presentation.Services;
using Chat_App.Services;
using Core.Interfaces;
using Core.Protocol;
using Core.Services;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Networking;
using Chat_App.Infrastructure.Serialization;
using Chat_App.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Chat_App;

/// <summary>
/// 应用程序入口类，负责初始化 DI 容器、日志、数据库和视图注册。
/// </summary>
public partial class App : Application
{
    /// <summary>DI 服务提供者（私有实例字段，避免全局服务定位器反模式）。</summary>
    private IServiceProvider _services = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // ——— 初始化 Serilog 日志框架 ———
        // 日志写入用户应用数据目录（与 DB/device.id 复用同一根目录），避免只读安装目录。
        // File sink 通过 Async 异步写入，避免日志 IO 阻塞 UI 线程。
        // Release 配置降级为 Information，减少生产环境日志量。
        var logDir = Path.Combine(DbPathProvider.GetAppDataDir(), "logs");
#if DEBUG
        var minLevel = Serilog.Events.LogEventLevel.Debug;
#else
        var minLevel = Serilog.Events.LogEventLevel.Information;
#endif
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minLevel)
            .WriteTo.Async(a => a.File(
                Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
            .CreateLogger();

        Log.Information("应用程序启动");

        // ——— 配置依赖注入容器 ———
        var services = new ServiceCollection();

        // ViewModel 注册（桌面应用：单例 VM，避免 ValidateScopes 与根容器冲突）
        services.AddSingleton<LoginViewModel>();
        services.AddSingleton<RegisterViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<MessageViewModel>();
        services.AddSingleton<IChatFriendLoader, ChatFriendLoader>();
        services.AddSingleton<IChatConnectionCoordinator, ChatConnectionCoordinator>();
        services.AddSingleton<IFriendStore, FriendStore>();
        services.AddSingleton<IFriendFetcher>(sp =>
            new FriendFetcherAdapter(sp.GetRequiredService<IFriendshipService>()));
        services.AddSingleton<UserSessionOrchestrator>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<FriendsViewModel>();

        // 加载应用配置
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        var baseUrl = configuration["AuthServer:BaseUrl"] ?? throw new NullReferenceException();

        // 注册基础服务
        AddDbContext(services, configuration)
            .AddTransient<AuthInterceptor>()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<ILocalDeviceIdentity, LocalDeviceIdentity>()
            .AddSingleton<IDatabaseWriteQueue, DatabaseWriteQueue>()
        .AddSingleton<IDatabaseService, DatabaseService>()
            .AddSingleton<IFriendsPageService, FriendsPageService>()
            .AddSingleton<INotificationService, NotificationService>()
            .AddSingleton<TokenInfo>()
            .AddSingleton<ITcpClient, TcpClientExample>()
            .AddSingleton<IChatSessionClient, ChatSessionClient>()
            .AddSingleton<IMessagePacketCodec, MessagePacketCodec>()
            .AddSingleton<IPacketBodySerializer, JsonPacketBodySerializer>()
            .AddSingleton<ICurrentUserState, CurrentUserContext>()
            .AddSingleton<ICurrentUserContext>(sp => sp.GetRequiredService<ICurrentUserState>())
            .AddSingleton<IEventBus, InMemoryEventBus>()
            .AddSingleton<IMessageStore, MessageStore>()
            .AddSingleton<ChatMessageCoordinator>()
            .AddSingleton<OutboxProcessor>()
            .AddSingleton<ISyncCheckpointStore, SyncCheckpointStore>()
            .AddSingleton<ISyncConflictResolver, SyncConflictResolver>()
            .AddSingleton<ISyncEngine, SyncEngine>()
            .AddSingleton<IAttachmentStorageService, AttachmentStorageService>()
            .AddSingleton<IAttachmentDownloadService, AttachmentDownloadService>()
            .AddSingleton<AttachmentRecoveryService>()
            .AddSingleton<DiagnosticsService>();


        services.AddHttpClient<IAuthClientService, AuthClientService>("AuthClient", (sp, client) =>
        {
            client.BaseAddress = new Uri(baseUrl);
            ApplyDeviceHeaders(client, sp.GetRequiredService<ILocalDeviceIdentity>());
        });

        services.AddHttpClient<IFriendshipService, FriendshipApiService>((sp, client) =>
            {
                client.BaseAddress = new Uri(baseUrl);
                ApplyDeviceHeaders(client, sp.GetRequiredService<ILocalDeviceIdentity>());
            })
            .AddHttpMessageHandler<AuthInterceptor>();

        services.AddHttpClient<IAttachmentClientService, AttachmentApiService>((sp, client) =>
            {
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromMinutes(10);
                ApplyDeviceHeaders(client, sp.GetRequiredService<ILocalDeviceIdentity>());
            })
            .AddHttpMessageHandler<AuthInterceptor>();

        services.AddHttpClient<ISessionApiService, SessionApiService>((sp, client) =>
            {
                client.BaseAddress = new Uri(baseUrl);
                ApplyDeviceHeaders(client, sp.GetRequiredService<ILocalDeviceIdentity>());
            })
            .AddHttpMessageHandler<AuthInterceptor>();

        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private static void ApplyDeviceHeaders(HttpClient client, ILocalDeviceIdentity device)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Device-Id", device.DeviceId);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", device.UserAgent);
    }

    /// <summary>
    /// 在 Avalonia 框架初始化完成后执行：确保数据库就绪并创建主窗口。
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        // 确保数据库表已创建，并应用所有待执行的迁移
        // 迁移失败必须终止启动，否则后续会得到大量误导性的"表不存在"错误
        var dbFactory = _services.GetRequiredService<IDbContextFactory<ClientDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            try
            {
                // 在迁移前执行 PRAGMA。
                // journal_mode=WAL 和 synchronous=NORMAL 是持久设置（写入数据库文件头），启动时确认一次；
                // 此后每个连接的 PRAGMA 由 SqlitePragmaInterceptor（EF Core DbConnectionInterceptor）统一执行。
                db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
                db.Database.Migrate();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "数据库迁移失败，应用无法启动。请检查 {DbPath} 或手动清理后重启", db.DbPath);
                throw;
            }
        }

        // 实例化协调器，开始订阅网络事件进行持久化
        _services.GetRequiredService<ChatMessageCoordinator>();

        // 启动 Outbox 排空处理器（事务化 Outbox）
        var outboxProcessor = _services.GetRequiredService<OutboxProcessor>();
        outboxProcessor.Start();

        // 注册诊断指标源并启动周期聚合导出（队列积压 / 写延迟 p95/p99 / 网络重连 / 同步统计）
        var diagnostics = _services.GetRequiredService<DiagnosticsService>();
        diagnostics.AddSource(_services.GetRequiredService<DatabaseWriteQueue>());
        diagnostics.AddSource(outboxProcessor);
        diagnostics.AddSource(_services.GetRequiredService<ChatSessionClient>());
        diagnostics.AddSource(_services.GetRequiredService<ChatConnectionCoordinator>());
        diagnostics.AddSource(_services.GetRequiredService<SyncEngine>());
        diagnostics.AddSource(_services.GetRequiredService<ChatMessageCoordinator>());
        diagnostics.Start();

        // 实例化附件恢复服务以注册鉴权事件订阅：恢复任务在 Authenticated 事件触发，
        // 不再依赖启动固定延迟，未登录会在鉴权成功时自动重试。若当前已鉴权则立即尝试一次。
        var attachmentRecovery = _services.GetRequiredService<AttachmentRecoveryService>();
        var currentUser = _services.GetRequiredService<ICurrentUserContext>();
        if (currentUser.IsAuthenticated && currentUser.UserId is { } ownerUserId)
            _ = attachmentRecovery.RecoverFailedUploadsAsync(ownerUserId);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindowViewModel = _services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel
            };
            _ = mainWindowViewModel.InitializeAsync(CancellationToken.None);

            // 统一应用关闭序列：草稿落盘 → 停止同步/Outbox/TCP → 排空数据库写入队列 → 关闭日志。
            desktop.Exit += (_, _) => ShutdownApplication();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 应用退出前的统一收尾：停止会话服务（SyncEngine/Outbox/TCP/好友快照），
    /// 再落盘草稿，最后排空数据库写入队列并关闭日志。任何一步失败都不阻断退出。
    /// </summary>
    private void ShutdownApplication()
    {
        try
        {
            var orchestrator = _services.GetRequiredService<UserSessionOrchestrator>();
            orchestrator.StopSessionAsync("app_exit").GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "关闭应用：停止会话失败（继续退出）");
        }

        try
        {
            var chatViewModel = _services.GetRequiredService<ChatViewModel>();
            chatViewModel.FlushDraftsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "关闭应用：草稿落盘失败（继续退出）");
        }

        try
        {
            var dbWriteQueue = _services.GetRequiredService<IDatabaseWriteQueue>();
            dbWriteQueue.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "关闭应用：数据库写入队列排空失败（继续退出）");
        }

        Log.Information("应用程序退出，日志已落盘");
        Log.CloseAndFlush();
    }

    /// <summary>
    /// 配置 SQLite 数据库上下文，并确保数据库目录存在。
    /// </summary>
    private static IServiceCollection AddDbContext(IServiceCollection services, IConfiguration configuration)
    {
        // 使用共享连接字符串构造器：仅使用 Microsoft.Data.Sqlite 官方支持的关键字
        // Journal Mode / Synchronous / Busy Timeout 不是连接字符串关键字，
        // 由 SqlitePragmaInterceptor（EF Core DbConnectionInterceptor）在每个连接打开时统一执行
        //（ForeignKeys=true / DefaultTimeout=5 由连接字符串保证，双保险）。
        var connectionString = DbPathProvider.BuildConnectionString();

        // 使用池化工厂：每个仓储操作通过 CreateDbContextAsync 获取独立短生命周期 DbContext，
        // 避免 scoped DbContext 被 singleton 服务捕获共享。
        // SqlitePragmaInterceptor 保证每个物理连接打开时执行 PRAGMA。
        services.AddPooledDbContextFactory<ClientDbContext>(op =>
        {
            op.UseSqlite(connectionString);
            op.AddInterceptors(new SqlitePragmaInterceptor());
        });
        return services;
    }
}
