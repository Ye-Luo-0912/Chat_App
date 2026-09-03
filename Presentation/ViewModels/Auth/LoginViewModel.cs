using Chat_App.Infrastructure.Persistence;
using Chat_App.Presentation.Services;
using Chat_App.Presentation.ViewModels.Shell;
using Chat_App.Services;
using Chat_App.Shared.Commands;
using Chat_App.Shared.Mvvm;
using Core.Contracts.Auth;
using ChatApp.Contracts.Http.Auth;
using Core.Interfaces;
using Core.Models;
using Chat_App.Infrastructure.Identity;
using Chat_App.Infrastructure.Models;
using Serilog;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Presentation.ViewModels.Auth;

/// <summary>
/// 登录页面 ViewModel，处理用户登录逻辑和自动登录（Token 刷新）。
/// 使用手写的 AsyncRelayCommand 替代 ReactiveCommand。
/// </summary>
public class LoginViewModel : ViewModelBase
{
    private readonly HomeViewModel _homeViewModel;
    private readonly IDatabaseService _dbService;
    private readonly IAuthClientService _authService;
    private readonly INotificationService _notificationService;
    private readonly TokenInfo _tokenInfo;
    private readonly ICurrentUserState _currentUserContext;
    private readonly UserSessionOrchestrator _sessionOrchestrator;
    private readonly bool _useTls;
    private CancellationTokenSource? _cts;

    /// <summary>单一登录互斥锁：自动登录与手动登录串行执行，杜绝 Token/页面导航竞争。</summary>
    private readonly SemaphoreSlim _loginGate = new(1, 1);

    /// <summary>自动登录的取消令牌：手动登录开始时取消在途自动登录。</summary>
    private CancellationTokenSource? _autoLoginCts;

    /// <summary>
    /// 由 MainWindowViewModel 注入的导航回调，调用时传入目标 ViewModel。
    /// </summary>
    public Action<object>? NavigateTo { get; set; }

    private string _username = string.Empty;
    /// <summary>用户名绑定属性。</summary>
    public string Username
    {
        get => _username;
        set
        {
            if (SetProperty(ref _username, value))
                LoginCommand?.RaiseCanExecuteChanged();
        }
    }

    private string _password = string.Empty;
    /// <summary>密码绑定属性。</summary>
    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
                LoginCommand?.RaiseCanExecuteChanged();
        }
    }

    private string _errorMessage = string.Empty;
    /// <summary>页面上显示的错误提示信息。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>登录命令，绑定到"登录"按钮。</summary>
    public AsyncRelayCommand LoginCommand { get; }

    /// <summary>跳转到注册页命令，绑定到"注册账号"文字。</summary>
    public RelayCommand GoToRegisterCommand { get; }

    #region 构造函数

    public LoginViewModel(
        IAuthClientService loginService,
        IDatabaseService dbService,
        TokenInfo tokenInfo,
        ICurrentUserState currentUserContext,
        HomeViewModel homeViewModel,
        INotificationService notificationService,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        UserSessionOrchestrator sessionOrchestrator)
    {
        _authService = loginService;
        _dbService = dbService;
        _tokenInfo = tokenInfo;
        _currentUserContext = currentUserContext;
        _homeViewModel = homeViewModel;
        _notificationService = notificationService;
        _sessionOrchestrator = sessionOrchestrator;
        _useTls = configuration.GetValue<bool>("Tcp:UseTls", true);
        LoginCommand = new AsyncRelayCommand(OnLoginAsync, CanLogin);
        GoToRegisterCommand = new RelayCommand(() => NavigateTo?.Invoke("register"));
    }

    private bool CanLogin()
    {
        return !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
    }

    #endregion

    /// <summary>
    /// 初始化登录页：检查已有 Token 是否有效，有效则自动跳转主页。
    /// 自动登录受互斥锁保护，且可被手动登录取消。
    /// </summary>
    public async Task InitAsync(CancellationToken ct = default)
    {
        await _loginGate.WaitAsync(ct);
        try
        {
            var autoCts = new CancellationTokenSource();
            _autoLoginCts = autoCts;
            try
            {
                await _tokenInfo.InitAsync(autoCts.Token);
                var res = _tokenInfo.Token;

                if (res is not null && !string.IsNullOrWhiteSpace(res.TokenValue) && res.TokenExpires > DateTime.UtcNow)
                {
                    // Token 仍然有效，直接导航到主页
                    await NavigateToHomeAsync(autoCts.Token);
                    Log.Information("自动登录成功，Token 仍然有效，导航到主页");
                    return;
                }

                // 尝试使用 RefreshToken 刷新访问令牌
                if (await _tokenInfo.RefreshTokensAsync(autoCts.Token))
                {
                    Log.Information("自动登录成功，使用 RefreshToken 刷新了访问令牌，导航到主页");
                    await NavigateToHomeAsync(autoCts.Token);
                }
            }
            catch (OperationCanceledException) when (autoCts.IsCancellationRequested)
            {
                Log.Information("自动登录已被手动登录取消");
            }
            catch (Exception ex)
            {
                // 自动登录失败（如服务器不可达），停留在登录页
                ErrorMessage = $"自动登录失败: {ex.Message}";
                _notificationService.ShowError(ErrorMessage, "自动登录失败");
            }
            finally
            {
                if (ReferenceEquals(_autoLoginCts, autoCts))
                    _autoLoginCts = null;
                autoCts.Dispose();
            }
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private async Task NavigateToHomeAsync(CancellationToken ct)
    {
        _homeViewModel.Init();
        // 会话编排器启动：TCP + 同步 + Outbox + 附件恢复 + 好友同步 + 通知。
        await _sessionOrchestrator.StartSessionAsync(ct);
        NavigateTo?.Invoke(_homeViewModel);
        Log.Information("导航到首页成功");
    }

    /// <summary>执行手动登录操作。</summary>
    private async Task OnLoginAsync(CancellationToken cancellationToken)
    {
        // 手动登录开始时取消在途自动登录。
        _autoLoginCts?.Cancel();

        // 单一登录互斥：等待自动登录结束后再执行手动登录。
        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            await OnLoginCoreAsync(cancellationToken);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private async Task OnLoginCoreAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var combinedToken = _cts.Token;

        try
        {
            ErrorMessage = string.Empty;
            Log.Information("用户 {Username} 尝试登录", Username);

            var loginResult = await _authService.LoginAsync(Username, Password, combinedToken);

            if (loginResult.IsSuccess)
            {
                Log.Information("用户 {Username} 登录成功", Username);
                await CompleteLoginAsync(loginResult);
            }
            else
            {
                ErrorMessage = loginResult.ErrorMessage ?? string.Empty;
                if (string.IsNullOrEmpty(ErrorMessage))
                {
                    ErrorMessage = loginResult.LoginCheckStatus switch
                    {
                        LoginCheckStatus.InvalidCredentials => "用户名或密码错误。",
                        LoginCheckStatus.LockedOut => "账户已被锁定，请稍后再试。",
                        LoginCheckStatus.NotAllowed => "账户已被禁用，请联系管理员。",
                        LoginCheckStatus.RequiresTwoFactor => "需要两步验证。",
                        _ => "登录失败。"
                    };
                }
                Log.Warning("用户 {Username} 登录失败: {Error}", Username, ErrorMessage);
                _notificationService.ShowError(ErrorMessage, "登录失败");
            }
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "登录已取消。";
        }
        catch (Exception e)
        {
            Log.Error(e, "用户 {Username} 登录过程异常", Username);
            ErrorMessage = $"登录失败: {e.Message}";
            _notificationService.ShowError($"登录失败: {e.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>登录成功后的处理逻辑：全量保存服务端返回数据，并导航到主页。</summary>
    private async Task CompleteLoginAsync(LoginResponse loginResult)
    {
        if (string.IsNullOrWhiteSpace(loginResult.AccessToken) || string.IsNullOrWhiteSpace(loginResult.RefreshToken))
            throw new InvalidOperationException("登录响应缺少令牌信息");

        if (loginResult.UserId is null)
            throw new InvalidOperationException("登录响应缺少用户 ID 信息");

        var userId = loginResult.UserId.Value;

        Log.Information("用户 {UserId} 登录成功，正在持久化数据", userId);

        // DeviceIdHash：ulong → long（二进制位模式相同），避免 SQLite EF 类型问题
        var deviceIdHashDb = loginResult.DeviceIdHash.HasValue
            ? unchecked((long?)loginResult.DeviceIdHash.Value)
            : (long?)null;

        var token = new AuthToken
        {
            UserId = userId,
            AccessToken = loginResult.AccessToken!,
            AccessTokenExpires = loginResult.AccessTokenExpiresAtUtc,
            RefreshToken = loginResult.RefreshToken!,
            RefreshTokenExpires = loginResult.RefreshTokenExpiresAtUtc,
            SessionId = loginResult.SessionId,
            DeviceIdHash = deviceIdHashDb,
            DeviceCredential = loginResult.DeviceCredential,
        };

        var user = new LocalUser
        {
            UserId = userId,
            Username = loginResult.UserName ?? string.Empty,
            AvatarUrl = loginResult.AvatarUrl,
            Email = loginResult.Email,
            Signature = loginResult.Signature,
            Gender = loginResult.Gender,
            Region = loginResult.Region,
            Status = (UserStatus)(byte)loginResult.Status,
            PreviousLoginDate = loginResult.PreviousLoginDate,
            LastLoginTime = loginResult.LoginAt.UtcDateTime,
        };

        ServerEndpoint? endpoint = null;
        if (loginResult.Server is { } server)
        {
            if (ServerEndpointImport.TryMapFromWire(server, _useTls, out var mapped, out var violation))
            {
                endpoint = mapped;
            }
            else
            {
                // fail-closed：未知枚举/非法组合不得解释成任何"安全默认"，也不中断登录；
                // 端点按旧形状缺省处理（UseTls 沿用既有默认），违规细节进日志。
                Log.Warning(
                    "登录响应端点元数据未通过校验 Violation={Violation} Host={Host} Port={Port}",
                    violation, server.Host, server.Port);
                endpoint = new ServerEndpoint
                {
                    ServerPort = server.Port,
                    ServerIpAddress = server.Host,
                    ServerName = server.Name,
                    // TCP 传输默认启用 TLS（appsettings Tcp:UseTls）；明文端口需显式关闭。
                    UseTls = _useTls
                };
            }
        }

        // 原子持久化：Token + 用户画像 + 服务器端点单事务提交；
        // 任一失败整体回滚，不会出现内存状态与数据库状态分叉。
        await _dbService.PersistLoginSessionAsync(token, user, endpoint);

        // 内存状态与会话启动在持久化成功后再进行。
        _tokenInfo.SetToken(new Token
        {
            TokenValue = loginResult.AccessToken,
            TokenExpires = loginResult.AccessTokenExpiresAtUtc,
        }, loginResult.DeviceCredential);
        _currentUserContext.SetCurrentUser(userId, loginResult.UserName);

        Password = string.Empty;
        await NavigateToHomeAsync(CancellationToken.None);
    }
}
