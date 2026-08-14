using System;
using Chat_App.Infrastructure.Services.Voice;
using Xunit;

namespace UnitTests;

/// <summary>
/// RealMic 采集源的平台边界测试（VOICE-MSG-2）。
/// 真实采集依赖 Windows 音频设备，不在此处开关设备；仅验证平台门控与构造参数。
/// </summary>
public sealed class MicrophoneSampleSourceTests
{
    [Fact]
    public void Constructor_ThrowsPlatformNotSupported_OnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows 上构造应成功（尚未打开设备）。
            using var source = new MicrophoneSampleSource(sampleRateHz: 16_000, channels: 1);
            Assert.Equal(16_000, source.SampleRateHz);
            Assert.Equal(1, source.Channels);
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(
            () => new MicrophoneSampleSource(sampleRateHz: 16_000, channels: 1));
    }

    [Fact]
    public void Constructor_RejectsInvalidFormat()
    {
        if (!OperatingSystem.IsWindows())
            return; // 非 Windows 下构造函数先抛平台异常，参数校验语义不适用。

        Assert.Throws<ArgumentOutOfRangeException>(() => new MicrophoneSampleSource(sampleRateHz: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MicrophoneSampleSource(sampleRateHz: 16_000, channels: 0));
    }
}