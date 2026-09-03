using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Presentation.ViewModels.Shell;
using Chat_App.Services;
using ChatApp.Contracts.Http.Sessions;
using Core.Accessibility;
using Core.Interfaces;
using Core.Settings;
using Xunit;

// 测试桩声明全部接口事件但从不触发属预期（CS0067）：仅实现接口成员以满足编译。
#pragma warning disable CS0067

namespace UnitTests;

/// <summary>
/// 设置页语音体验（VOICE-MSG-3）单元测试：音频输出设备列表/选择/持久化、
/// 持久化偏好在设置加载时回放、语音缓存占用展示与"清除语音缓存"命令。
/// 全部依赖以最小桩注入，不触达 NAudio 与磁盘。
/// </summary>
public sealed class SettingsViewModelVoiceTests
{
    private const long UserId = 42;

    // ── 音频输出设备 ─────────────────────────────────────────

    [Fact]
    public void AudioOutputDevices_Listed_With_System_Default_First()
    {
        var player = new StubAudioPlayer();
        player.Devices.Add(new AudioOutputDevice("0", "扬声器"));
        player.Devices.Add(new AudioOutputDevice("1", "耳机"));

        var vm = CreateVm(player: player);

        Assert.Equal(3, vm.AudioOutputDevices.Count);
        Assert.Null(vm.AudioOutputDevices[0].DeviceId);
        Assert.Equal("系统默认", vm.AudioOutputDevices[0].DisplayName);
        Assert.Equal("0", vm.AudioOutputDevices[1].DeviceId);
        Assert.Equal("1", vm.AudioOutputDevices[2].DeviceId);
        // 初始选中系统默认。
        Assert.Null(vm.SelectedAudioOutputDevice.DeviceId);
        Assert.Null(player.SelectedOutputDeviceId);
    }

    [Fact]
    public async Task Selecting_Device_Applies_To_Player_And_Persists()
    {
        var player = new StubAudioPlayer();
        player.Devices.Add(new AudioOutputDevice("1", "耳机"));
        var settings = new StubSettingsService();
        var vm = CreateVm(player: player, settings: settings, user: new StubUserContext { UserId = UserId });

        vm.SelectedAudioOutputDevice = new AudioOutputDeviceOption("1", "耳机");

        // 即刻作用于播放器（下一次 Play 生效）并最终持久化为用户偏好。
        Assert.Equal("1", player.SelectedOutputDeviceId);
        await WaitForAsync(() => settings.Current.AudioOutputDeviceId == "1");
        Assert.Equal("1", settings.Current.AudioOutputDeviceId);
    }

    [Fact]
    public async Task Selecting_Back_To_System_Default_Persists_Null()
    {
        var player = new StubAudioPlayer();
        player.Devices.Add(new AudioOutputDevice("1", "耳机"));
        var settings = new StubSettingsService();
        var vm = CreateVm(player: player, settings: settings, user: new StubUserContext { UserId = UserId });

        vm.SelectedAudioOutputDevice = new AudioOutputDeviceOption("1", "耳机");
        await WaitForAsync(() => settings.Current.AudioOutputDeviceId == "1");

        vm.SelectedAudioOutputDevice = vm.AudioOutputDevices[0]; // 系统默认
        Assert.Null(player.SelectedOutputDeviceId);
        await WaitForAsync(() => settings.Current.AudioOutputDeviceId is null);
        Assert.Null(settings.Current.AudioOutputDeviceId);
    }

    [Fact]
    public void SelectDevice_Command_With_Null_Parameter_Restores_System_Default()
    {
        var player = new StubAudioPlayer();
        player.Devices.Add(new AudioOutputDevice("1", "耳机"));
        var vm = CreateVm(player: player, user: new StubUserContext { UserId = UserId });

        vm.SelectAudioOutputDeviceCommand.Execute(new AudioOutputDeviceOption("1", "耳机"));
        Assert.Equal("1", vm.SelectedAudioOutputDevice.DeviceId);

        vm.SelectAudioOutputDeviceCommand.Execute(null);
        Assert.Null(vm.SelectedAudioOutputDevice.DeviceId);
        Assert.Null(player.SelectedOutputDeviceId);
    }

    [Fact]
    public async Task LoadSettings_Applies_Persisted_Device()
    {
        var player = new StubAudioPlayer();
        player.Devices.Add(new AudioOutputDevice("1", "耳机"));
        var settings = new StubSettingsService();
        settings.Current.AudioOutputDeviceId = "1";
        var vm = CreateVm(player: player, settings: settings, user: new StubUserContext { UserId = UserId });

        await vm.InitAsync();

        Assert.Equal("1", vm.SelectedAudioOutputDevice.DeviceId);
        Assert.Equal("1", player.SelectedOutputDeviceId);
    }

    [Fact]
    public async Task LoadSettings_Persisted_Device_No_Longer_Present_Falls_Back_To_Default()
    {
        // 持久化值 "9" 不在当前设备列表（拔出/序号漂移）：回退系统默认且不把无效值喂给播放器。
        var player = new StubAudioPlayer();
        player.Devices.Add(new AudioOutputDevice("1", "耳机"));
        var settings = new StubSettingsService();
        settings.Current.AudioOutputDeviceId = "9";
        var vm = CreateVm(player: player, settings: settings, user: new StubUserContext { UserId = UserId });

        await vm.InitAsync();

        Assert.Null(vm.SelectedAudioOutputDevice.DeviceId);
        Assert.Null(player.SelectedOutputDeviceId);
    }

    [Fact]
    public void Enumeration_Failure_Degrades_To_System_Default_Only()
    {
        var player = new StubAudioPlayer { EnumerationThrows = true };
        var vm = CreateVm(player: player, user: new StubUserContext { UserId = UserId });

        var option = Assert.Single(vm.AudioOutputDevices);
        Assert.Null(option.DeviceId);
        Assert.Equal("系统默认", option.DisplayName);
    }

    // ── 语音缓存占用与清理 ───────────────────────────────────

    [Fact]
    public async Task Init_Shows_Cache_Usage_Against_Limit()
    {
        var storage = new StubStorage { UsedBytes = 300L * 1024 * 1024, LimitBytes = 512L * 1024 * 1024 };
        var vm = CreateVm(storage: storage, user: new StubUserContext { UserId = UserId });

        await vm.InitAsync();

        Assert.Contains("300 MB", vm.VoiceCacheUsageDisplay);
        Assert.Contains("512 MB", vm.VoiceCacheUsageDisplay);
    }

    [Fact]
    public void Cache_Usage_Without_Login_Shows_Login_Hint()
    {
        var storage = new StubStorage { UsedBytes = 100, LimitBytes = 512 };
        var vm = CreateVm(storage: storage, user: new StubUserContext { UserId = null });

        vm.RefreshVoiceCacheUsage();

        Assert.Contains("登录", vm.VoiceCacheUsageDisplay);
        Assert.Equal(0, storage.SizeQueries);
    }

    [Fact]
    public async Task ClearVoiceCache_Calls_Storage_Reports_Freed_And_Refreshes_Usage()
    {
        var storage = new StubStorage { UsedBytes = 400L * 1024 * 1024, LimitBytes = 512L * 1024 * 1024 };
        storage.FreedOnClear = 300L * 1024 * 1024;
        var notifications = new StubNotifications();
        var vm = CreateVm(
            storage: storage, notifications: notifications, user: new StubUserContext { UserId = UserId });
        await vm.InitAsync();

        vm.ClearVoiceCacheCommand.Execute(null);
        await WaitForAsync(() => notifications.Successes.Count > 0);

        Assert.Equal(1, storage.ClearCalls);
        Assert.Contains("300 MB", Assert.Single(notifications.Successes));
        // 清理后占用刷新（桩模拟：400MB - 释放 300MB = 剩 100MB）。
        Assert.Contains("100 MB", vm.VoiceCacheUsageDisplay);
    }

    [Fact]
    public async Task ClearVoiceCache_Without_Login_Shows_Error_And_Does_Not_Clear()
    {
        var storage = new StubStorage();
        var notifications = new StubNotifications();
        var vm = CreateVm(storage: storage, notifications: notifications, user: new StubUserContext { UserId = null });

        vm.ClearVoiceCacheCommand.Execute(null);
        await WaitForAsync(() => notifications.Errors.Count > 0);

        Assert.Equal(0, storage.ClearCalls);
        Assert.Contains("登录", Assert.Single(notifications.Errors));
    }

    // ── 构造与等待辅助 ───────────────────────────────────────

    private static SettingsViewModel CreateVm(
        IAudioPlayer? player = null,
        IAttachmentStorageService? storage = null,
        ISettingsService? settings = null,
        ICurrentUserContext? user = null,
        StubNotifications? notifications = null)
    {
        return new SettingsViewModel(
            new StubSessionApi(),
            notifications ?? new StubNotifications(),
            user ?? new StubUserContext { UserId = UserId },
            settings ?? new StubSettingsService(),
            new StubAccessibility(),
            player ?? new StubAudioPlayer(),
            storage ?? new StubStorage());
    }

    /// <summary>PersistSettingsAsync 为 fire-and-forget：轮询等待异步持久化可见（上限 2s）。</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "等待异步操作超时");
    }

    // ── 最小测试桩 ──────────────────────────────────────────

    internal sealed class StubNotifications : INotificationService
    {
        public List<string> Errors { get; } = [];
        public List<string> Successes { get; } = [];
        public void ShowError(string message, string title = "错误") => Errors.Add(message);
        public void ShowWarning(string message, string title = "警告") { }
        public void ShowInfo(string message, string title = "提示") { }
        public void ShowSuccess(string message, string title = "成功") => Successes.Add(message);
    }

    internal sealed class StubAudioPlayer : IAudioPlayer
    {
        public List<AudioOutputDevice> Devices { get; } = [];
        public string? SelectedOutputDeviceId { get; private set; }
        public bool EnumerationThrows { get; set; }
        public bool IsPlaying => false;
        public string? CurrentKey => null;
        public event Action<AudioPlaybackProgress>? Progress;
        public event Action? Stopped;
        public void Play(string key, string wavPath) { }
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }

        public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
        {
            if (EnumerationThrows)
                throw new PlatformNotSupportedException("无音频后端");
            return Devices;
        }

        public void SelectOutputDevice(string? deviceId) => SelectedOutputDeviceId = deviceId;
        public void Dispose() { }
    }

    internal sealed class StubSettingsService : ISettingsService
    {
        /// <summary>内存中的当前设置（Get 返回副本语义对测试足够）。</summary>
        public ClientSettings Current { get; set; } = new();
        public List<ClientSettings> Saved { get; } = [];

        public Task<ClientSettings> GetAsync(long ownerUserId, CancellationToken ct = default)
            => Task.FromResult(new ClientSettings
            {
                AudioOutputDeviceId = Current.AudioOutputDeviceId,
                NotificationPreviewEnabled = Current.NotificationPreviewEnabled
            });

        public Task SetAsync(long ownerUserId, ClientSettings settings, CancellationToken ct = default)
        {
            Current = new ClientSettings { AudioOutputDeviceId = settings.AudioOutputDeviceId };
            Saved.Add(settings);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(long ownerUserId, Action<ClientSettings> mutate, CancellationToken ct = default)
        {
            var copy = new ClientSettings { AudioOutputDeviceId = Current.AudioOutputDeviceId };
            mutate(copy);
            return SetAsync(ownerUserId, copy, ct);
        }
    }

    internal sealed class StubUserContext : ICurrentUserContext
    {
        public long Generation { get; set; } = 1;
        public long? UserId { get; set; }
        public string? UserName => UserId is { } id ? $"user-{id}" : null;
        public bool IsAuthenticated => UserId is > 0;
        public bool HasUserId => UserId is > 0;
        public Core.Models.UserSessionSnapshot Snapshot => new(UserId ?? 0, Generation, UserName, null, null);
        public long RequireUserId() => UserId ?? throw new InvalidOperationException("未登录");
        public bool TryGetUserId(out long id)
        {
            id = UserId ?? 0;
            return UserId is > 0;
        }
    }

    internal sealed class StubSessionApi : ISessionApiService
    {
        public Task<IReadOnlyList<SessionDevice>> ListSessionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionDevice>>([]);

        public Task RevokeSessionAsync(string deviceId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<int> RevokeOtherSessionsAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    internal sealed class StubAccessibility : IAccessibilityService
    {
        public AccessibilityOptions Current { get; private set; } = new();
        public event EventHandler<AccessibilityOptions>? OptionsChanged;
        public void Apply(ClientSettings settings) { }
    }

    internal sealed class StubStorage : IAttachmentStorageService
    {
        public long UsedBytes { get; set; }
        public long LimitBytes { get; set; } = 512L * 1024 * 1024;
        public long FreedOnClear { get; set; }
        public int ClearCalls { get; private set; }
        public int SizeQueries { get; private set; }

        public long MaxCacheBytes => LimitBytes;

        public long GetDownloadsCacheSizeBytes(long ownerUserId)
        {
            SizeQueries++;
            return UsedBytes;
        }

        public long ClearDownloadsCache(long ownerUserId)
        {
            ClearCalls++;
            var freed = FreedOnClear;
            UsedBytes -= freed; // 模拟清理后占用下降（供用量刷新断言）。
            return freed;
        }

        public string GetAttachmentsRoot(long ownerUserId) => throw new NotSupportedException();
        public string GetUploadingDir(long ownerUserId) => throw new NotSupportedException();
        public string GetDownloadsDir(long ownerUserId) => throw new NotSupportedException();
        public string GetThumbnailsDir(long ownerUserId) => throw new NotSupportedException();
        public string CopyToUploading(long ownerUserId, string sourceFilePath, string fileName) => throw new NotSupportedException();
        public Task<string> WriteToUploadingAsync(long ownerUserId, System.IO.Stream content, string fileName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string relativePath, string sha256)> WriteToUploadingWithHashAsync(long ownerUserId, System.IO.Stream content, string fileName, CancellationToken ct = default) => throw new NotSupportedException();
        public string ResolvePath(long ownerUserId, string relativePath) => throw new NotSupportedException();
        public System.IO.Stream OpenUploadingRead(long ownerUserId, string relativePath) => throw new NotSupportedException();
        public void DeleteUploadingFile(long ownerUserId, string relativePath) { }
        public string MoveToDownloads(long ownerUserId, string uploadingRelativePath, string attachmentId, string fileName) => throw new NotSupportedException();
        public string? GetDownloadCachePath(long ownerUserId, string attachmentId, string fileName) => null;
        public string? GetPartialDownloadPath(long ownerUserId, string attachmentId, string fileName) => null;
        public Task<string> WriteToDownloadsAsync(long ownerUserId, string attachmentId, string fileName, System.IO.Stream content, CancellationToken ct = default, string? expectedSha256 = null, bool append = false) => throw new NotSupportedException();
        public long? GetAvailableDiskSpace() => null;
    }
}
