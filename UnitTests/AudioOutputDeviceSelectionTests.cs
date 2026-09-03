using System;
using System.Collections.Generic;
using System.Globalization;
using Chat_App.Infrastructure.Services.Voice;
using Core.Interfaces;
using Xunit;

namespace UnitTests;

/// <summary>
/// 音频输出路由（VOICE-MSG-3）单元测试：设备枚举经 <see cref="IAudioOutputDeviceEnumerator"/>
/// 抽象注入桩，验证 <see cref="PcmAudioPlayer"/> 的设备选择状态机（合法选择/非法回退/越界回退/
/// 系统默认），不依赖真实音频设备。
/// </summary>
public sealed class AudioOutputDeviceSelectionTests
{
    /// <summary>可编程设备枚举桩：模拟真实设备表与"计数不可知"（平台不支持）场景。</summary>
    private sealed class FakeDeviceEnumerator : IAudioOutputDeviceEnumerator
    {
        public List<AudioOutputDevice> Devices { get; set; } =
        [
            new("0", "扬声器 (Realtek)"),
            new("1", "耳机 (USB)")
        ];

        /// <summary>true = 模拟平台不支持/计数不可知（GetDeviceCount 返回 null）。</summary>
        public bool CountUnknown { get; set; }

        public IReadOnlyList<AudioOutputDevice> EnumerateOutputDevices() => Devices;

        public int? GetDeviceCount() => CountUnknown ? null : Devices.Count;
    }

    private static PcmAudioPlayer CreatePlayer(FakeDeviceEnumerator enumerator) => new(enumerator);

    // ---- 枚举 ----

    [Fact]
    public void GetOutputDevices_Returns_Enumerator_List_With_Stable_Ids()
    {
        var player = CreatePlayer(new FakeDeviceEnumerator());

        var devices = player.GetOutputDevices();

        Assert.Equal(2, devices.Count);
        Assert.Equal("0", devices[0].DeviceId);
        Assert.Equal("扬声器 (Realtek)", devices[0].Name);
        Assert.Equal("1", devices[1].DeviceId);
    }

    [Fact]
    public void GetOutputDevices_Empty_List_Is_Tolerated()
    {
        var player = CreatePlayer(new FakeDeviceEnumerator { Devices = [] });

        Assert.Empty(player.GetOutputDevices());
    }

    // ---- 选择状态机 ----

    [Fact]
    public void SelectOutputDevice_Valid_Id_Is_Recorded()
    {
        var player = CreatePlayer(new FakeDeviceEnumerator());

        player.SelectOutputDevice("1");

        Assert.Equal("1", player.SelectedOutputDeviceId);
    }

    [Fact]
    public void SelectOutputDevice_Null_Or_Blank_Resets_To_System_Default()
    {
        var player = CreatePlayer(new FakeDeviceEnumerator());
        player.SelectOutputDevice("1");

        player.SelectOutputDevice(null);
        Assert.Null(player.SelectedOutputDeviceId);

        player.SelectOutputDevice("1");
        player.SelectOutputDevice("   ");
        Assert.Null(player.SelectedOutputDeviceId);
    }

    [Theory]
    [InlineData("abc")]     // 非数字（损坏的持久化值）
    [InlineData("1.5")]     // 非整数
    [InlineData("-3")]      // 负数不是合法设备号（-1 由 null 语义表达）
    [InlineData("2")]       // 越界（计数 2，合法区间 [0,2)）
    [InlineData("999")]     // 明显越界
    public void SelectOutputDevice_Invalid_Or_OutOfRange_Falls_Back_To_System_Default(string deviceId)
    {
        var player = CreatePlayer(new FakeDeviceEnumerator());

        player.SelectOutputDevice(deviceId);

        Assert.Null(player.SelectedOutputDeviceId);
    }

    [Fact]
    public void SelectOutputDevice_ReSelection_Overwrites_Previous()
    {
        var player = CreatePlayer(new FakeDeviceEnumerator());

        player.SelectOutputDevice("0");
        Assert.Equal("0", player.SelectedOutputDeviceId);

        player.SelectOutputDevice("1");
        Assert.Equal("1", player.SelectedOutputDeviceId);

        player.SelectOutputDevice(null);
        Assert.Null(player.SelectedOutputDeviceId);
    }

    [Fact]
    public void SelectOutputDevice_When_Count_Unknown_Accepts_NonNegative_Id()
    {
        // 平台不支持时计数不可知（null）：跳过越界校验，接受合法格式（播放时由设备层兜底）。
        var player = CreatePlayer(new FakeDeviceEnumerator { CountUnknown = true });

        player.SelectOutputDevice("5");

        Assert.Equal("5", player.SelectedOutputDeviceId);
    }

    [Fact]
    public void SelectOutputDevice_With_No_Devices_Falls_Back_To_System_Default()
    {
        var player = CreatePlayer(new FakeDeviceEnumerator { Devices = [] });

        player.SelectOutputDevice("0");

        Assert.Null(player.SelectedOutputDeviceId);
    }

    [Fact]
    public void Selected_Device_Survives_Failed_Play_Attempt()
    {
        // "切换只影响下一次 Play"：Play 因文件缺失失败不应丢失已选设备。
        var player = CreatePlayer(new FakeDeviceEnumerator());
        player.SelectOutputDevice("1");

        Assert.ThrowsAny<Exception>(() => player.Play("k", @"Z:\nonexistent\voice.wav"));

        Assert.Equal("1", player.SelectedOutputDeviceId);
    }

    // ---- 真实枚举器（不依赖设备存在性，只验证绝不抛异常）----

    [Fact]
    public void WaveOutDeviceEnumerator_Never_Throws_On_Any_Platform()
    {
        var enumerator = new WaveOutDeviceEnumerator();

        var devices = enumerator.EnumerateOutputDevices();
        var count = enumerator.GetDeviceCount();

        Assert.NotNull(devices);
        foreach (var device in devices)
        {
            Assert.True(int.TryParse(device.DeviceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n));
            Assert.True(n >= 0);
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
        }
        // 无设备平台 count 可为 0 或 null（平台不支持），但绝不抛出。
        Assert.True(count is null or >= 0);
    }

    [Fact]
    public void PcmAudioPlayer_Default_Constructor_Works_Without_Devices()
    {
        // 生产默认构造路径（真实枚举器）：在无音频设备环境（CI/Linux）同样可用。
        using var player = new PcmAudioPlayer();

        var devices = player.GetOutputDevices();
        Assert.NotNull(devices);
        Assert.Null(player.SelectedOutputDeviceId);
        player.SelectOutputDevice("0");
        // 有真实设备时选中 "0"；无设备时优雅回退默认 —— 两种结果都不抛。
        Assert.True(player.SelectedOutputDeviceId is null or "0");
    }
}
