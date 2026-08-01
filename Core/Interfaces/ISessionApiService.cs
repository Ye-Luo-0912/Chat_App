using Core.Contracts.Sessions;

namespace Core.Interfaces;

/// <summary>
/// HTTP 会话管理服务：查询当前用户的多端登录会话，并支持按设备撤销。
/// 用于"登录设备管理"界面。
/// </summary>
public interface ISessionApiService
{
    /// <summary>列出当前用户所有活跃会话（含设备名称、最后活跃时间等）。</summary>
    Task<IReadOnlyList<SessionDeviceDto>> ListSessionsAsync(CancellationToken ct = default);

    /// <summary>撤销指定设备对应的会话，使其令牌失效。</summary>
    /// <param name="deviceId">目标设备指纹（<see cref="ILocalDeviceIdentity.DeviceId"/>）。</param>
    Task RevokeSessionAsync(string deviceId, CancellationToken ct = default);

    /// <summary>撤销除当前设备外的所有会话，返回已撤销的会话数。</summary>
    Task<int> RevokeOtherSessionsAsync(CancellationToken ct = default);
}
