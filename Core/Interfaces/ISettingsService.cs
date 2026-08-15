using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Settings;

namespace Core.Interfaces;

/// <summary>
/// 客户端设备与安全设置服务：按账户（owner）读取/写入类型化设置。
/// 未显式设置的项回退默认值；写入使用键值存储持久化到本地 SQLite。
/// </summary>
public interface ISettingsService
{
    /// <summary>读取指定账户的设置（含默认值合并）。</summary>
    Task<ClientSettings> GetAsync(long ownerUserId, CancellationToken ct = default);

    /// <summary>整体覆盖写入指定账户的设置（null 值项回退默认）。</summary>
    Task SetAsync(long ownerUserId, ClientSettings settings, CancellationToken ct = default);

    /// <summary>在事务内读取并变更指定账户的设置后整体写回。</summary>
    Task UpdateAsync(long ownerUserId, Action<ClientSettings> mutate, CancellationToken ct = default);
}