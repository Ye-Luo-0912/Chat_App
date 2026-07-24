using Core.Contracts.Auth;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface IAuthClientService
{
    Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    Task<LoginResult> RefreshTokenAsync(string refreshToken, long userId, CancellationToken cancellationToken = default);
    Task<bool> SendRegisterCodeAsync(string email, CancellationToken cancellationToken = default);
    Task<RegisterResponse> RegisterAsync(string email, string code, string password, CancellationToken cancellationToken = default);
}
