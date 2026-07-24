using Core.Contracts.Sessions;

namespace Core.Interfaces;

public interface ISessionApiService
{
    Task<IReadOnlyList<SessionDeviceDto>> ListSessionsAsync(CancellationToken ct = default);
    Task RevokeSessionAsync(string deviceId, CancellationToken ct = default);
    Task<int> RevokeOtherSessionsAsync(CancellationToken ct = default);
}
