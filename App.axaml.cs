using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Identity;
using Chat_App.Presentation.ViewModels.Auth;
using Chat_App.Presentation.ViewModels.Chat;
using Chat_App.Presentation.ViewModels.Friends;
using Chat_App.Presentation.ViewModels.Shell;
using Chat_App.Presentation.Views;
using Chat_App.Services;
using Core.Interfaces;
using Core.Protocol;
using Core.Services;
using Infrastructure.Models.Context;
using Infrastructure.Networking;
using Infrastructure.Serialization;
using Infrastructure.Services;
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
    /// <summary>
    /// 全局服务提供者，用于获取依赖注入的服务实例。
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // ——— 初始化 Serilog 日志框架 ———
        // 日志写入用户应用数据目录（与 DB/device.id 复用同一根目录），避免只读安装目录。
        // File sink 通过 Async 异步写入，避免日志 IO 阻塞 UI 线程。
        // Release 配置降级为 Information，减少生产环境日志量。
        var logDir = Path.Combine(Infrastructure.Persistence.DbPathProvider.GetAppDataDir(), "logs");
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

        // ViewModel 注册（桌面应用：单例 VM，避免 ValidateScopes 与根容器冲突 P0-1）
        services.AddSingleton<LoginViewModel>();
        services.AddSingleton<RegisterViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<MessageViewModel>();
        services.AddSingleton<IChatFriendLoader, ChatFriendLoader>();
        services.AddSingleton<IChatConnectionCoordinator, ChatConnectionCoordinator>();
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
            .AddSingleton<IAttachmentStorageService, AttachmentStorageService>()
            .AddSingleton<AttachmentRecoveryService>();


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

        Services = services.BuildServiceProvider(new ServiceProviderOptions
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
        // P0-2: 迁移失败必须终止启动，否则后续会得到大量误导性的"表不存在"错误
        var dbFactory = Services.GetRequiredService<IDbContextFactory<ClientDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            try
            {
                // P0-2: 在迁移前执行 PRAGMA。
                // journal_mode=WAL 和 synchronous=NORMAL 是持久设置（写入数据库文件头），一次设置即可。
                // foreign_keys=ON 是每连接设置，由连接字符串 Foreign Keys=true 保证（Microsoft.Data.Sqlite 官方支持）。
                // busy_timeout 由连接字符串 DefaultTimeout=5 对应。
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
        Services.GetRequiredService<ChatMessageCoordinator>();

        // 启动 Outbox 排空处理器（P0-4 事务化 Outbox）
        var outboxProcessor = Services.GetRequiredService<OutboxProcessor>();
        outboxProcessor.Start();

        // 实例化附件恢复服务以注册鉴权事件订阅（九1）：恢复任务在 Authenticated 事件触发，
        // 不再依赖启动固定延迟，未登录会在鉴权成功时自动重试。若当前已鉴权则立即尝试一次。
        var attachmentRecovery = Services.GetRequiredService<AttachmentRecoveryService>();
        _ = attachmentRecovery.RecoverFailedUploadsAsync();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 配置 SQLite 数据库上下文，并确保数据库目录存在。
    /// </summary>
    private static IServiceCollection AddDbContext(IServiceCollection services, IConfiguration configuration)
    {
        // 使用共享连接字符串构造器（P0-2）：仅使用 Microsoft.Data.Sqlite 官方支持的关键字
        // Journal Mode / Synchronous / Busy Timeout 不是连接字符串关键字，
        // 由 ClientDbContext.OnConfiguring 通过 PRAGMA 执行。
        var connectionString = DbPathProvider.BuildConnectionString();

        // 使用池化工厂：每个仓储操作通过 CreateDbContextAsync 获取独立短生命周期 DbContext，
        // 避免 scoped DbContext 被 singleton 服务捕获共享（P0-4）。
        services.AddPooledDbContextFactory<ClientDbContext>(op =>
        {
            op.UseSqlite(connectionString);
        });
        return services;
    }
}
