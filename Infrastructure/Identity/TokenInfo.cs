using Chat_App.Infrastructure.Persistence;
using Core.Contracts.Auth;
using Core.Interfaces;
using Chat_App.Infrastructure.Models;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Infrastructure.Identity;

/// <summary>
/// 管理内存中的 AccessToken 快照与当前用户状态。
/// 服务端已迁移到不透明令牌（Redis 存储），不再使用 JWT，因此移除了所有 JWT 解析逻辑。
/// </summary>
public class TokenInfo
{
    private Token? _token;

    private readonly IDatabaseService _databaseService;
    private readonly IAuthClientService _loginService;
    private readonly ICurrentUserState _currentUserState;

    /// <summary>
    /// 在途刷新任务：严格 single-flight，并发 401 共享同一轮刷新。
    /// 任务在锁内创建（创建即启动），竞争失败的调用方绝不会发起第二次刷新；
    /// 共享刷新使用 session 生命周期令牌，各调用方只能取消自己的等待。
    /// </summary>
    private readonly object _refreshGate = new();
    private Task<bool>? _refreshTask;

    /// <summary>会话生命周期令牌：登录期间有效；退出登录/应用关闭时取消并替换。</summary>
    private CancellationTokenSource _sessionLifetimeCts = new();

    /// <summary>
    /// 会话失效事件：刷新令牌已过期/无效、或刷新失败时触发。
    /// 订阅方（UserSessionOrchestrator）应停止 TCP/同步/Outbox 并回到登录页，而不是仅清空 Token。
    /// </summary>
    public event EventHandler? SessionExpired;

    public TokenInfo(IDatabaseService databaseService, IAuthClientService loginService, ICurrentUserState currentUserState)
    {
        _databaseService = databaseService;
        _loginService = loginService;
        _currentUserState = currentUserState;
    }

    /// <summary>
    /// 异步初始化：从数据库加载 AccessToken，并从本地用户表还原当前用户状态。
    /// </summary>
    public async Task InitAsync(CancellationToken ct = default)
    {
        _currentUserState.Clear();

        var authToken = await _databaseService.GetTokenAsync().ConfigureAwait(false);
        if (authToken is null || string.IsNullOrWhiteSpace(authToken.AccessToken))
            return;

        _token = new Token
        {
            TokenValue = authToken.AccessToken,
            TokenExpires = authToken.AccessTokenExpires
        };

        // 从本地用户表还原用户状态，无需 JWT 解析
        var localUser = await _databaseService.GetUserAsync(authToken.UserId).ConfigureAwait(false);
        if (localUser is not null)
        {
            _currentUserState.SetCurrentUser(authToken.UserId, localUser.Username);
        }
        else if (authToken.UserId > 0)
        {
            // 用户表不存在（极少情况），仅设置 ID
            _currentUserState.SetCurrentUser(authToken.UserId, null);
        }
    }

    /// <summary>
    /// 登录成功后由外部直接设置内存中的 Token（LoginResult 中的数据已经完整）。
    /// 同时开启新的会话生命周期令牌（旧令牌已被 ClearLocalSessionAsync 取消）。
    /// </summary>
    public void SetToken(Token token)
    {
        _token = token;
        lock (_refreshGate)
        {
            _sessionLifetimeCts = new CancellationTokenSource();
        }
    }

    public Token? Token => _token;

    /// <summary>
    /// 严格 single-flight 刷新：并发调用共享同一轮刷新任务。
    /// 任务在锁内创建：仅当无在途任务时才启动新的 RefreshTokensCoreAsync，
    /// 竞争失败者（并发 401）不会各自再次发起 HTTP 刷新（CAS 前启动的假合并已修复）。
    /// 共享任务使用 session 生命周期令牌：任一调用方的取消只中断自己的等待，
    /// 不取消全局刷新（首个调用方取消不再连带取消所有等待者）。
    /// </summary>
    public Task<bool> RefreshTokensAsync(CancellationToken callerToken = default)
    {
        Task<bool> shared;
        lock (_refreshGate)
        {
            shared = _refreshTask ??= RefreshTokensCoreAsync(_sessionLifetimeCts.Token);
        }
        return AwaitSharedAndClearAsync(shared, callerToken);
    }

    private async Task<bool> AwaitSharedAndClearAsync(Task<bool> shared, CancellationToken callerToken)
    {
        try
        {
            return await shared.WaitAsync(callerToken).ConfigureAwait(false);
        }
        finally
        {
            // 仅清空仍指向本轮刷新任务的引用，避免误清后续刷新。
            lock (_refreshGate)
            {
                if (ReferenceEquals(_refreshTask, shared))
                    _refreshTask = null;
            }
        }
    }

    private async Task<bool> RefreshTokensCoreAsync(CancellationToken ct)
    {
        try
        {
            var stored = await _databaseService.GetTokenAsync().ConfigureAwait(false);
            if (stored is null || stored.RefreshTokenExpires <= DateTime.UtcNow)
            {
                Log.Warning("RefreshToken 缺失或已过期，会话失效");
                await ClearTokensAsync();
                return false;
            }

            var result = await _loginService.RefreshTokenAsync(stored.RefreshToken, stored.UserId, ct)
                .ConfigureAwait(false);

            if (!result.IsSuccess
                || string.IsNullOrWhiteSpace(result.AccessToken)
                || string.IsNullOrWhiteSpace(result.RefreshToken))
            {
                Log.Warning("刷新令牌响应无效，会话失效");
                await ClearTokensAsync();
                return false;
            }

            var newToken = new AuthToken
            {
                AccessToken = result.AccessToken,
                AccessTokenExpires = result.AccessTokenExpiresAtUtc,
                RefreshToken = result.RefreshToken,
                RefreshTokenExpires = result.RefreshTokenExpiresAtUtc,
                UserId = stored.UserId,
                // SessionId / DeviceIdHash 不随刷新改变，保留原值
                SessionId = stored.SessionId,
                DeviceIdHash = stored.DeviceIdHash,
            };

            await _databaseService.UpdateTokenAsync(newToken).ConfigureAwait(false);

            _token = new Token
            {
                TokenValue = newToken.AccessToken,
                TokenExpires = newToken.AccessTokenExpires
            };

            // 用户信息不随 token 刷新变化，无需重新加载
            if (!_currentUserState.IsAuthenticated && stored.UserId > 0)
            {
                var localUser = await _databaseService.GetUserAsync(stored.UserId).ConfigureAwait(false);
                _currentUserState.SetCurrentUser(stored.UserId, localUser?.Username);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Token 刷新操作被取消");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Token 刷新过程中发生异常");
            await ClearTokensAsync();
            return false;
        }
    }

    public async Task ClearLocalSessionAsync(CancellationToken ct = default)
    {
        Log.Warning("正在清除本地 Token 信息");
        // 会话结束：取消并替换 session 生命周期令牌（在途刷新任务随之失败退出；
        // 替换保证后续访问不会触及已 Dispose 的 CTS）。
        lock (_refreshGate)
        {
            try { _sessionLifetimeCts.Cancel(); } catch (ObjectDisposedException) { }
            _sessionLifetimeCts.Dispose();
            _sessionLifetimeCts = new CancellationTokenSource();
        }
        _token = null;
        _currentUserState.Clear();
        await _databaseService.DeleteTokenAsync().ConfigureAwait(false);
    }

    private Task ClearTokensAsync()
    {
        SessionExpired?.Invoke(this, EventArgs.Empty);
        return ClearLocalSessionAsync();
    }
}

