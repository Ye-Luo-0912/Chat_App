using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Core.Interfaces;
using NAudio.Wave;

namespace Chat_App.Infrastructure.Services.Voice;

/// <summary>
/// WaveOut 输出设备枚举实现（VOICE-MSG-3）。
/// NAudio 2.2.1 在 net6+ 目标上移除了 WaveOut.DeviceCount/GetCapabilities（随 WinForms
/// 依赖被裁剪），此处直接 P/Invoke winmm 的 waveOutGetNumDevs / waveOutGetDevCaps，
/// 复用 NAudio 公开的 <see cref="WaveOutCapabilities"/> 结构做封送。
/// WaveOut 为 Windows winmm 设备：无音频设备/非 Windows 平台（如 CI/Linux）上
/// DllNotFound/调用失败一律降级为空列表/计数不可知，绝不抛出。
/// 注意：WaveOut 设备没有跨重启稳定的字符串 Id，只能以枚举序号标识
/// （DeviceId = DeviceNumber 十进制字符串，与 <see cref="PcmAudioPlayer"/> 选择语义一致）。
/// </summary>
public sealed class WaveOutDeviceEnumerator : IAudioOutputDeviceEnumerator
{
    public IReadOnlyList<AudioOutputDevice> EnumerateOutputDevices()
    {
        try
        {
            var count = waveOutGetNumDevs();
            if (count <= 0)
                return [];

            var devices = new List<AudioOutputDevice>(count);
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var caps = default(WaveOutCapabilities);
                    var result = waveOutGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf<WaveOutCapabilities>());
                    if (result != 0) // MMSYSERR_NOERROR
                        continue;
                    var name = caps.ProductName?.TrimEnd('\0');
                    devices.Add(new AudioOutputDevice(
                        i.ToString(CultureInfo.InvariantCulture),
                        string.IsNullOrWhiteSpace(name) ? $"输出设备 {i}" : name!));
                }
                catch
                {
                    // 单个设备查询失败：跳过该设备，不影响其余枚举。
                }
            }
            return devices;
        }
        catch
        {
            // 无 winmm（非 Windows，如 CI/Linux）：优雅降级为空列表。
            return [];
        }
    }

    public int? GetDeviceCount()
    {
        try
        {
            return waveOutGetNumDevs();
        }
        catch
        {
            // 计数不可得：调用方应跳过 deviceId 越界校验。
            return null;
        }
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int waveOutGetDevCaps(IntPtr uDeviceID, ref WaveOutCapabilities lpCaps, int uSize);
}
