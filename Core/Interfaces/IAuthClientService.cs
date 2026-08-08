using ChatApp.Contracts.Http.Auth;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

/// <summary>
/// HTTP 认证服务：负责登录、登出、令牌刷新、注册验证码与注册流程。
/// 返回的 <see cref="LoginResponse"/> 包含访问令牌与刷新令牌，供 TCP 鉴权与后续 HTTP 请求使用。
/// </summary>
public interface IAuthClientService
{
    /// <summary>使用用户名/密码登录，返回访问令牌与刷新令牌。</summary>
    Task<LoginResponse> LoginAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>登出当前会话，服务端吊销令牌。</summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>使用刷新令牌换取新的访问令牌；userId 用于关联本地持久化的刷新令牌。</summary>
    Task<RefreshTokenResponse> RefreshTokenAsync(
        string refreshToken,
        long userId,
        string? deviceCredential,
        CancellationToken cancellationToken = default);

    /// <summary>向指定邮箱发送注册验证码；成功返回 true。</summary>
    Task<bool> SendRegisterCodeAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>使用邮箱、验证码、密码完成注册，返回注册结果（含 userId 与初始令牌）。</summary>
    Task<RegisterResponse> RegisterAsync(string email, string code, string password, CancellationToken cancellationToken = default);
}
