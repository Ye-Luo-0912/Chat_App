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

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

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
    /// </summary>
    public void SetToken(Token token)
    {
        _token = token;
    }

    public Token? Token => _token;

    public async Task<bool> RefreshTokensAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var stored = await _databaseService.GetTokenAsync().ConfigureAwait(false);
            if (stored is null || stored.RefreshTokenExpires <= DateTime.UtcNow)
            {
                await ClearTokensAsync();
                return false;
            }

            var result = await _loginService.RefreshTokenAsync(stored.RefreshToken, stored.UserId, ct)
                .ConfigureAwait(false);

            if (!result.IsSuccess
                || string.IsNullOrWhiteSpace(result.AccessToken)
                || string.IsNullOrWhiteSpace(result.RefreshToken))
            {
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
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task ClearLocalSessionAsync(CancellationToken ct = default)
    {
        Log.Warning("正在清除本地 Token 信息");
        _token = null;
        _currentUserState.Clear();
        await _databaseService.DeleteTokenAsync().ConfigureAwait(false);
    }

    private Task ClearTokensAsync() => ClearLocalSessionAsync();
}

