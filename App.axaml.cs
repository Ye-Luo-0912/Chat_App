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
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("应用程序启动");

        // ——— 配置依赖注入容器 ———
        var services = new ServiceCollection();

        // ViewModel 注册
        services.AddScoped<LoginViewModel>();
        services.AddScoped<RegisterViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<MessageViewModel>();
        services.AddSingleton<IChatFriendLoader, ChatFriendLoader>();
        services.AddSingleton<IChatConnectionCoordinator, ChatConnectionCoordinator>();
        services.AddScoped<MainWindowViewModel>()
            .AddSingleton<HomeViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<FriendsViewModel>();

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
        var dbFactory = Services.GetRequiredService<IDbContextFactory<ClientDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            try
            {
                db.Database.Migrate();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "数据库迁移失败，可能需要手动删除 Data/ChatApp.db");
            }
        }

        // 实例化协调器，开始订阅网络事件进行持久化
        Services.GetRequiredService<Core.Services.ChatMessageCoordinator>();

        // Recover failed attachment uploads (fire-and-forget, delayed for auth)
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, CancellationToken.None);
                await Services.GetRequiredService<AttachmentRecoveryService>().RecoverFailedUploadsAsync();
            }
            catch
            {
                // Ignore recovery failures
            }
        });

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
        // 数据库存储在用户数据目录
        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatApp",
            "Data");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "ChatApp.db");
        var connectionString = $"Data Source={dbPath};Cache=Shared;Journal Mode=WAL;Synchronous=NORMAL;Busy Timeout=5000;Foreign Keys=ON;";

        
        // 使用池化工厂：每个仓储操作通过 CreateDbContextAsync 获取独立短生命周期 DbContext，
        // 避免 scoped DbContext 被 singleton 服务捕获共享（P0-4）。
        services.AddPooledDbContextFactory<ClientDbContext>(op =>
        {
            op.UseSqlite(connectionString);
        });
        return services;
    }
}
