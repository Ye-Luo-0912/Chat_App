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
using Core.Services.Voice;
using Chat_App.Infrastructure.Services.Voice;
using Chat_App.Infrastructure.Services.Call;
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
        services.AddSingleton<DiagnosticsViewModel>();
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
            .AddSingleton<ISettingsService, SettingsService>()
            .AddSingleton<IAccessibilityService, AccessibilityService>()
            .AddSingleton<IAttachmentStorageService, AttachmentStorageService>()
            .AddSingleton<IAttachmentDownloadService, AttachmentDownloadService>()
            .AddSingleton<IAttachmentThumbnailService, AttachmentThumbnailService>()
            .AddSingleton<IThumbnailImageCodec, SkiaThumbnailImageCodec>()
            .AddSingleton<IAttachmentDownloadService, AttachmentDownloadService>()
            .AddSingleton<AttachmentRecoveryService>()
            .AddSingleton<DiagnosticsService>()
            .AddSingleton<IAudioPlayer, PcmAudioPlayer>()
            // CALL-E2E-2：通话会话管理器（控制面状态机编排 + 媒体面抽象注入点）。
            // 依赖 IChatSessionClient（wire 信令）与 ICurrentUserContext（当前用户）。
            // MediaFactory 接入 SIPSorcery/WebRTC 媒体面；创建失败时回退 null（仅控制面），
            // 保证无音频设备的平台仍可进行信令联调。
            .AddSingleton<ICallSessionManager>(sp => new CallSessionManager(
                sp.GetRequiredService<IChatSessionClient>(),
                sp.GetRequiredService<ICurrentUserContext>())
            {
                MediaFactory = callId => CreateCallMedia(callId)
            })
            // VOICE-MSG-2：录音源注入（Windows 真实麦克风，其他平台回退正弦波）。
            .AddSingleton<IVoiceRecorder>(_ =>
            {
                // 单次录音最长时长默认 60s；可用配置 Voice:MaxDurationSeconds 覆盖（秒），
                // 便于真机验证录音超时自动收尾路径。覆盖 0/负值时回退默认值。
                var maxDurationSeconds = configuration.GetValue<double?>("Voice:MaxDurationSeconds");
                var maxDuration = maxDurationSeconds is > 0
                    ? TimeSpan.FromSeconds(maxDurationSeconds.Value)
                    : TimeSpan.FromSeconds(60);

                // VOICE-MSG-2：Windows 用真实麦克风采集；其他平台回退到确定性正弦波源，
                // 保证 UI/上传/发送链路跨平台可用。真实采集源由 MicrophoneSampleSource 提供。
                IWaveSampleSource source;
                try
                {
                    source = new MicrophoneSampleSource(sampleRateHz: 16_000, channels: 1);
                }
                catch (PlatformNotSupportedException)
                {
                    source = new SineToneSampleSource(
                        sampleRateHz: 16_000,
                        channels: 1,
                        maxDuration: maxDuration);
                }
                return new VoiceRecorderService(source, maxDuration: maxDuration);
            });


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
    /// 为每个通话创建 SIPSorcery/WebRTC 媒体面（CALL-E2E-2）：麦克风上行 + NAudio 播放下行。
    /// 任一步骤失败（如无音频设备）时回退 null，通话退化为仅控制面信令。
    /// </summary>
    private static ICallMediaSession? CreateCallMedia(string callId)
    {
        try
        {
            var microphone = new MicrophoneSampleSource(sampleRateHz: 16_000, channels: 1);
            var sink = new WaveOutCallAudioSink();
            return new SipsorceryCallMediaSession(callId, sink, microphone);
        }
        catch (PlatformNotSupportedException)
        {
            return null; // 非 Windows / 无采集设备：仅控制面。
        }
        catch (Exception)
        {
            return null; // 媒体面初始化失败：fail-soft，不阻断信令。
        }
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

        // 生产配置校验：服务器端点/认证/TLS 首启前置检查（不合规则仅告警，登录页可覆盖）
        ValidateProductionConfiguration();

        // 注册诊断指标源并启动周期聚合导出（队列积压 / 写延迟 p95/p99 / 网络重连 / 同步统计）
        var diagnostics = _services.GetRequiredService<DiagnosticsService>();
        diagnostics.AddSource(_services.GetRequiredService<DatabaseWriteQueue>());
        diagnostics.AddSource(outboxProcessor);
        diagnostics.AddSource(_services.GetRequiredService<ChatSessionClient>());
        diagnostics.AddSource(_services.GetRequiredService<ChatConnectionCoordinator>());
        diagnostics.AddSource(_services.GetRequiredService<SyncEngine>());
        diagnostics.AddSource(_services.GetRequiredService<ChatMessageCoordinator>());
        diagnostics.AddSource(_services.GetRequiredService<MessageStore>());
        diagnostics.AddSource((IMetricsSource)_services.GetRequiredService<IAttachmentThumbnailService>());
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

        // DI 容器完整释放（S2）：单例组件的 IDisposable/IAsyncDisposable 统一收尾
        //（诊断定时器、恢复服务、连接协调器、数据库工厂池等），防止句柄/定时器泄漏。
        try
        {
            if (_services is IAsyncDisposable asyncDisposable)
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            else if (_services is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "关闭应用：DI 容器释放失败（继续退出）");
        }

        Log.Information("应用程序退出，日志已落盘");
        Log.CloseAndFlush();
    }

    /// <summary>
    /// 生产配置验证（S2）：服务器端点存在且为合法 https URI、TLS 保持开启。
    /// 不合规仅告警不阻断（登录页/设置页可覆盖端点），保证误配可即时发现。
    /// </summary>
    private void ValidateProductionConfiguration()
    {
        try
        {
            var configuration = _services.GetRequiredService<IConfiguration>();
            var authBaseUrl = configuration["AuthServer:BaseUrl"];
            if (string.IsNullOrWhiteSpace(authBaseUrl)
                || !Uri.TryCreate(authBaseUrl, UriKind.Absolute, out var authUri)
                || (authUri.Scheme != Uri.UriSchemeHttps && !authUri.IsLoopback))
            {
                Log.Warning(
                    "生产配置校验：AuthServer:BaseUrl 缺失或非 HTTPS（当前 '{AuthBaseUrl}'），请确认服务器证书与地址，登录页可手动覆盖",
                    authBaseUrl);
            }

            if (bool.TryParse(configuration["Tcp:UseTls"], out var useTls) && !useTls)
            {
                Log.Warning("生产配置校验：Tcp:UseTls 被显式关闭——明文传输，仅限内网调试环境");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "生产配置校验失败（不阻断启动）");
        }
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
