using System.Collections.Generic;

namespace Core.Interfaces;

/// <summary>
/// 系统音频输出设备枚举器抽象（VOICE-MSG-3）：把 NAudio/WaveOut 的平台相关枚举
/// 隔离在实现层，单元测试注入桩即可覆盖设备选择逻辑，不依赖真实音频设备。
/// </summary>
public interface IAudioOutputDeviceEnumerator
{
    /// <summary>
    /// 枚举系统输出设备（索引 0 = 系统枚举顺序的第一个设备）。
    /// 无音频设备/平台不支持时返回空列表；任何失败都不得抛异常（优雅降级）。
    /// </summary>
    IReadOnlyList<AudioOutputDevice> EnumerateOutputDevices();

    /// <summary>
    /// 当前系统可用输出设备数；用于校验待选 deviceId 是否越界。
    /// 无法确定（平台不支持/驱动异常）返回 null，调用方应跳过越界校验。
    /// </summary>
    int? GetDeviceCount();
}
