using Chat_App.Infrastructure.Persistence;
using Chat_App.Presentation.ViewModels.Shell;
using Chat_App.Services;
using Chat_App.Shared.Commands;
using Chat_App.Shared.Mvvm;
using Core.Contracts.Auth;
using Core.Interfaces;
using Core.Models;
using Chat_App.Infrastructure.Identity;
using Chat_App.Infrastructure.Models;
using Serilog;
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
    private CancellationTokenSource? _cts;

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
        INotificationService notificationService)
    {
        _authService = loginService;
        _dbService = dbService;
        _tokenInfo = tokenInfo;
        _currentUserContext = currentUserContext;
        _homeViewModel = homeViewModel;
        _notificationService = notificationService;

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
    /// </summary>
    public async Task InitAsync()
    {
        try
        {
            await _tokenInfo.InitAsync();
            var res = _tokenInfo.Token;

            if (res is not null && !string.IsNullOrWhiteSpace(res.TokenValue) && res.TokenExpires > DateTime.UtcNow)
            {
                // Token 仍然有效，直接导航到主页
                NavigateToHome();
                Log.Information("自动登录成功，Token 仍然有效，导航到主页");
                return;
            }

            // 尝试使用 RefreshToken 刷新访问令牌
            if (await _tokenInfo.RefreshTokensAsync())
            {
                Log.Information("自动登录成功，使用 RefreshToken 刷新了访问令牌，导航到主页");
                NavigateToHome();
            }
        }
        catch (Exception ex)
        {
            // 自动登录失败（如服务器不可达），停留在登录页
            ErrorMessage = $"自动登录失败: {ex.Message}";
            _notificationService.ShowError(ErrorMessage, "自动登录失败");
        }
    }

    private void NavigateToHome()
    {
        _homeViewModel.Init();
        NavigateTo?.Invoke(_homeViewModel);
        Log.Information("导航到首页成功");
    }

    /// <summary>执行手动登录操作。</summary>
    private async Task OnLoginAsync(CancellationToken cancellationToken)
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
    private async Task CompleteLoginAsync(LoginResult loginResult)
    {
        if (string.IsNullOrWhiteSpace(loginResult.AccessToken) || string.IsNullOrWhiteSpace(loginResult.RefreshToken))
            throw new InvalidOperationException("登录响应缺少令牌信息");

        if (loginResult.UserId is null)
            throw new InvalidOperationException("登录响应缺少用户 ID 信息");

        var userId = loginResult.UserId.Value;

        // 立即同步内存状态，无需等待 DB 写入完成
        _tokenInfo.SetToken(new Token
        {
            TokenValue = loginResult.AccessToken,
            TokenExpires = loginResult.AccessTokenExpiresAtUtc,
        });
        _currentUserContext.SetCurrentUser(userId, loginResult.UserName);

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
            Status = loginResult.Status,
            PreviousLoginDate = loginResult.PreviousLoginDate,
            LastLoginTime = loginResult.LoginAt.UtcDateTime,
        };

        // 全量并行持久化（Token + 用户画像）
        var saveTokenTask = _dbService.SaveTokenAsync(token);
        var saveUserTask  = _dbService.SaveUserAsync(user);

        if (loginResult.Server is { } server)
        {
            var endpoint = new ServerEndpoint
            {
                ServerPort = server.Port,
                ServerIpAddress = server.Host,
                ServerName = server.Name,
            };
            await Task.WhenAll(saveTokenTask, saveUserTask, _dbService.SaveServerInfoAsync(endpoint))
                .ConfigureAwait(false);
        }
        else
        {
            await Task.WhenAll(saveTokenTask, saveUserTask).ConfigureAwait(false);
        }

        Password = string.Empty;
        NavigateToHome();
    }
}