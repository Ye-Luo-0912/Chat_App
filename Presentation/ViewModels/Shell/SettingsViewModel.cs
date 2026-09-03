using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Services;
using Chat_App.Shared.Commands;
using Chat_App.Shared.Mvvm;
using Core.Accessibility;
using Core.Interfaces;
using Core.Settings;
using Serilog;

namespace Chat_App.Presentation.ViewModels.Shell;

/// <summary>
/// 输出设备下拉项：DeviceId = null 表示"系统默认"（与 <see cref="IAudioPlayer.SelectOutputDevice"/>
/// 的 null 语义一致），DisplayName 为 UI 展示名。
/// </summary>
public sealed record AudioOutputDeviceOption(string? DeviceId, string DisplayName);

public sealed class SettingsViewModel : ViewModelBase
{
    /// <summary>"系统默认"下拉项（始终排在首位，设备枚举失败时也仅显示它）。</summary>
    private static readonly AudioOutputDeviceOption SystemDefaultOutput = new(null, "系统默认");

    private readonly ISessionApiService _sessions;
    private readonly INotificationService _notifications;
    private readonly ICurrentUserContext _currentUser;
    private readonly ISettingsService _settings;
    private readonly IAccessibilityService _accessibility;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IAttachmentStorageService _attachmentStorage;

    public ObservableCollection<SessionDeviceListItem> Devices { get; } = [];

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // ---- 安全设置（设备本地）----
    private bool _notificationPreviewEnabled = true;
    public bool NotificationPreviewEnabled
    {
        get => _notificationPreviewEnabled;
        set { if (SetProperty(ref _notificationPreviewEnabled, value)) _ = PersistSettingsAsync(); }
    }

    private bool _autoDownloadAttachments = true;
    public bool AutoDownloadAttachments
    {
        get => _autoDownloadAttachments;
        set { if (SetProperty(ref _autoDownloadAttachments, value)) _ = PersistSettingsAsync(); }
    }

    private bool _autoLockOnIdle;
    public bool AutoLockOnIdle
    {
        get => _autoLockOnIdle;
        set { if (SetProperty(ref _autoLockOnIdle, value)) _ = PersistSettingsAsync(); }
    }

    private int _autoLockIdleMinutes = ClientSettings.DefaultAutoLockIdleMinutes;
    public int AutoLockIdleMinutes
    {
        get => _autoLockIdleMinutes;
        set { if (SetProperty(ref _autoLockIdleMinutes, value)) _ = PersistSettingsAsync(); }
    }

    // ---- 无障碍体验（设备本地）----
    private AccessibilityFontSize _fontSize = AccessibilityFontSize.Standard;
    public AccessibilityFontSize FontSize
    {
        get => _fontSize;
        set { if (SetProperty(ref _fontSize, value)) _ = PersistSettingsAsync(); }
    }

    /// <summary>字体档位下拉的可显示项（标准/大/特大）。</summary>
    public IReadOnlyList<string> FontSizeOptionsDisplay { get; } =
        Enum.GetValues<AccessibilityFontSize>().Select(s => s.ToDisplayName()).ToArray();

    /// <summary>字体档位下拉：以档位数值作为选中索引。</summary>
    public int SelectedFontSizeIndex
    {
        get => (int)_fontSize;
        set
        {
            var coerced = AccessibilityFontSizeExtensions.Coerce(value);
            if (coerced == _fontSize)
                return;
            _fontSize = coerced;
            OnPropertyChanged(nameof(FontSize));
            OnPropertyChanged(nameof(SelectedFontSizeIndex));
            OnPropertyChanged(nameof(FontScalePreview));
            OnPropertyChanged(nameof(FontSizeDisplay));
            _ = PersistSettingsAsync();
        }
    }

    private bool _reduceMotion;
    public bool ReduceMotion
    {
        get => _reduceMotion;
        set { if (SetProperty(ref _reduceMotion, value)) _ = PersistSettingsAsync(); }
    }

    private bool _highContrast;
    public bool HighContrast
    {
        get => _highContrast;
        set { if (SetProperty(ref _highContrast, value)) _ = PersistSettingsAsync(); }
    }

    /// <summary>当前字体档位对应的缩放倍率（预览用）。</summary>
    public double FontScalePreview => _fontSize.ToScale();

    /// <summary>当前字体档位显示名（预览用）。</summary>
    public string FontSizeDisplay => _fontSize.ToDisplayName();

    // ---- 语音播放（VOICE-MSG-3）：输出设备路由（耳机 ⇄ 扬声器）----
    // 切换语义：SelectOutputDevice 只影响下一次 Play，正在播放的语音不热切换。

    private IReadOnlyList<AudioOutputDeviceOption> _audioOutputDevices = [SystemDefaultOutput];
    public IReadOnlyList<AudioOutputDeviceOption> AudioOutputDevices
    {
        get => _audioOutputDevices;
        private set => SetProperty(ref _audioOutputDevices, value);
    }

    private AudioOutputDeviceOption _selectedAudioOutputDevice = SystemDefaultOutput;
    public AudioOutputDeviceOption SelectedAudioOutputDevice
    {
        get => _selectedAudioOutputDevice;
        set
        {
            var option = value ?? SystemDefaultOutput;
            if (!SetProperty(ref _selectedAudioOutputDevice, option))
                return;
            // 即刻作用于播放器（下一次 Play 生效）并持久化为用户偏好。
            _audioPlayer.SelectOutputDevice(option.DeviceId);
            _ = PersistSettingsAsync();
        }
    }

    // ---- 语音缓存（VOICE-MSG-3）：占用展示与手动清理 ----

    private string _voiceCacheUsageDisplay = "—";
    public string VoiceCacheUsageDisplay
    {
        get => _voiceCacheUsageDisplay;
        private set => SetProperty(ref _voiceCacheUsageDisplay, value);
    }

    public string CurrentUserDisplay =>
        _currentUser.UserName is { Length: > 0 } name
            ? $"{name} (#{_currentUser.UserId})"
            : (_currentUser.UserId?.ToString() ?? "未登录");

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RevokeOthersCommand { get; }
    public AsyncRelayCommand<SessionDeviceListItem> RevokeDeviceCommand { get; }
    public AsyncRelayCommand<AudioOutputDeviceOption> SelectAudioOutputDeviceCommand { get; }
    public AsyncRelayCommand ClearVoiceCacheCommand { get; }

    public SettingsViewModel(
        ISessionApiService sessions,
        INotificationService notifications,
        ICurrentUserContext currentUser,
        ISettingsService settings,
        IAccessibilityService accessibility,
        IAudioPlayer audioPlayer,
        IAttachmentStorageService attachmentStorage)
    {
        _sessions = sessions;
        _notifications = notifications;
        _currentUser = currentUser;
        _settings = settings;
        _accessibility = accessibility;
        _audioPlayer = audioPlayer;
        _attachmentStorage = attachmentStorage;

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        RevokeOthersCommand = new AsyncRelayCommand(
            RevokeOthersAsync,
            () => !IsLoading,
            ex => _notifications.ShowError($"撤销其他设备失败: {ex.Message}"));
        RevokeDeviceCommand = new AsyncRelayCommand<SessionDeviceListItem>(
            RevokeDeviceAsync,
            d => d is not null && !d.IsCurrent && !IsLoading,
            ex => _notifications.ShowError($"撤销设备失败: {ex.Message}"));
        SelectAudioOutputDeviceCommand = new AsyncRelayCommand<AudioOutputDeviceOption>(
            o =>
            {
                SelectedAudioOutputDevice = o ?? SystemDefaultOutput;
                return Task.CompletedTask;
            });
        ClearVoiceCacheCommand = new AsyncRelayCommand(
            ClearVoiceCacheAsync,
            () => !IsLoading,
            ex =>
            {
                Log.Warning(ex, "清除语音缓存失败");
                _notifications.ShowError($"清除语音缓存失败: {ex.Message}");
            });

        LoadAudioOutputDevices();
    }

    public async Task InitAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(true);
        await LoadSettingsAsync(ct).ConfigureAwait(true);
        LoadAudioOutputDevices();
        RefreshVoiceCacheUsage();
    }

    private async Task LoadSettingsAsync(CancellationToken ct)
    {
        try
        {
            if (_currentUser.UserId is not { } userId)
                return;
            var s = await _settings.GetAsync(userId, ct).ConfigureAwait(true);
            _notificationPreviewEnabled = s.NotificationPreviewEnabled;
            _autoDownloadAttachments = s.AutoDownloadAttachments;
            _autoLockOnIdle = s.AutoLockOnIdle;
            _autoLockIdleMinutes = s.AutoLockIdleMinutes;
            _fontSize = s.FontSize;
            _reduceMotion = s.ReduceMotion;
            _highContrast = s.HighContrast;
            ApplyAudioOutputPreference(s.AudioOutputDeviceId);
            OnPropertyChanged(nameof(NotificationPreviewEnabled));
            OnPropertyChanged(nameof(AutoDownloadAttachments));
            OnPropertyChanged(nameof(AutoLockOnIdle));
            OnPropertyChanged(nameof(AutoLockIdleMinutes));
            OnPropertyChanged(nameof(FontSize));
            OnPropertyChanged(nameof(SelectedFontSizeIndex));
            OnPropertyChanged(nameof(ReduceMotion));
            OnPropertyChanged(nameof(HighContrast));
            OnPropertyChanged(nameof(FontScalePreview));
            OnPropertyChanged(nameof(FontSizeDisplay));
            ApplyAccessibilityToService();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载安全设置失败");
        }
    }

    private async Task PersistSettingsAsync()
    {
        try
        {
            if (_currentUser.UserId is not { } userId)
                return;
            await _settings.UpdateAsync(userId, s =>
            {
                s.NotificationPreviewEnabled = _notificationPreviewEnabled;
                s.AutoDownloadAttachments = _autoDownloadAttachments;
                s.AutoLockOnIdle = _autoLockOnIdle;
                s.AutoLockIdleMinutes = _autoLockIdleMinutes;
                s.FontSize = _fontSize;
                s.ReduceMotion = _reduceMotion;
                s.HighContrast = _highContrast;
                s.AudioOutputDeviceId = _selectedAudioOutputDevice.DeviceId;
            }).ConfigureAwait(false);
            ApplyAccessibilityToService();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存安全设置失败");
            _notifications.ShowError("保存安全设置失败");
        }
    }

    /// <summary>把当前无障碍设置解析为渲染选项并广播给 UI 消费。</summary>
    private void ApplyAccessibilityToService()
    {
        _accessibility.Apply(new ClientSettings
        {
            FontSize = _fontSize,
            ReduceMotion = _reduceMotion,
            HighContrast = _highContrast
        });
    }

    // ---- 语音播放输出设备（VOICE-MSG-3）----

    /// <summary>
    /// 枚举播放器可用输出设备并重建下拉项（"系统默认"恒为第一项）。
    /// 无设备/平台不支持时仅剩系统默认项；尽量保持当前选中项不漂移。
    /// </summary>
    private void LoadAudioOutputDevices()
    {
        var options = new List<AudioOutputDeviceOption> { SystemDefaultOutput };
        try
        {
            foreach (var device in _audioPlayer.GetOutputDevices())
            {
                var name = string.IsNullOrWhiteSpace(device.Name) ? $"输出设备 {device.DeviceId}" : device.Name;
                options.Add(new AudioOutputDeviceOption(device.DeviceId, name));
            }
        }
        catch (Exception ex)
        {
            // 枚举失败（无音频后端等）：降级为仅系统默认，不阻断设置页。
            Log.Debug(ex, "枚举音频输出设备失败");
        }

        var selectedId = _selectedAudioOutputDevice.DeviceId;
        AudioOutputDevices = options;
        _selectedAudioOutputDevice = options.FirstOrDefault(o => o.DeviceId == selectedId) ?? SystemDefaultOutput;
        OnPropertyChanged(nameof(SelectedAudioOutputDevice));
    }

    /// <summary>
    /// 应用持久化的输出设备偏好：设备仍在列表中才应用（拔出/序号漂移时回退系统默认），
    /// 并同步作用于播放器（下一次 Play 生效）。
    /// </summary>
    private void ApplyAudioOutputPreference(string? persistedDeviceId)
    {
        var option = string.IsNullOrWhiteSpace(persistedDeviceId)
            ? SystemDefaultOutput
            : AudioOutputDevices.FirstOrDefault(o => o.DeviceId == persistedDeviceId) ?? SystemDefaultOutput;
        if (!ReferenceEquals(option, _selectedAudioOutputDevice))
        {
            _selectedAudioOutputDevice = option;
            OnPropertyChanged(nameof(SelectedAudioOutputDevice));
        }
        _audioPlayer.SelectOutputDevice(option.DeviceId);
    }

    // ---- 语音缓存维护（VOICE-MSG-3）----

    /// <summary>刷新缓存占用展示（打开设置页时调用，无后台轮询）。</summary>
    public void RefreshVoiceCacheUsage()
    {
        try
        {
            if (_currentUser.TryGetUserId(out var owner))
            {
                var used = _attachmentStorage.GetDownloadsCacheSizeBytes(owner);
                VoiceCacheUsageDisplay = $"已用 {FormatBytes(used)} / 上限 {FormatBytes(_attachmentStorage.MaxCacheBytes)}";
            }
            else
            {
                VoiceCacheUsageDisplay = "登录后可用";
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "统计语音缓存占用失败");
            VoiceCacheUsageDisplay = "—";
        }
    }

    private async Task ClearVoiceCacheAsync(CancellationToken ct)
    {
        if (!_currentUser.TryGetUserId(out var owner))
        {
            _notifications.ShowError("登录后才能清除语音缓存");
            return;
        }
        // 目录 IO 放到线程池，避免大缓存目录扫描卡 UI（打开设置页时的用量统计同理但量小，同步执行）。
        var freed = await Task.Run(() => _attachmentStorage.ClearDownloadsCache(owner), ct).ConfigureAwait(true);
        RefreshVoiceCacheUsage();
        _notifications.ShowSuccess(freed > 0 ? $"已清除语音缓存，释放 {FormatBytes(freed)}" : "语音缓存已是空的");
    }

    private static string FormatBytes(long bytes)
    {
        const long Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        return bytes switch
        {
            >= Gb => $"{bytes / (double)Gb:0.##} GB",
            >= Mb => $"{bytes / (double)Mb:0.#} MB",
            >= Kb => $"{bytes / (double)Kb:0.#} KB",
            _ => $"{bytes} B"
        };
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        StatusText = "加载中…";
        RefreshCommand.RaiseCanExecuteChanged();
        RevokeOthersCommand.RaiseCanExecuteChanged();
        try
        {
            var items = await _sessions.ListSessionsAsync(ct).ConfigureAwait(true);
            Devices.Clear();
            foreach (var item in items)
                Devices.Add(new SessionDeviceListItem(item));
            StatusText = Devices.Count == 0 ? "暂无登录设备" : $"共 {Devices.Count} 台设备";
            OnPropertyChanged(nameof(CurrentUserDisplay));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载设备列表失败");
            StatusText = "加载失败";
            _notifications.ShowError($"加载设备列表失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            RefreshCommand.RaiseCanExecuteChanged();
            RevokeOthersCommand.RaiseCanExecuteChanged();
            RevokeDeviceCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RevokeOthersAsync(CancellationToken ct)
    {
        var count = await _sessions.RevokeOtherSessionsAsync(ct).ConfigureAwait(true);
        _notifications.ShowSuccess(count > 0 ? $"已撤销 {count} 台其他设备" : "没有其他设备需要撤销");
        await LoadAsync(ct).ConfigureAwait(true);
    }

    private async Task RevokeDeviceAsync(SessionDeviceListItem? device, CancellationToken ct)
    {
        if (device is null || device.IsCurrent)
            return;

        await _sessions.RevokeSessionAsync(device.DeviceId, ct).ConfigureAwait(true);
        _notifications.ShowSuccess($"已下线: {device.Title}");
        await LoadAsync(ct).ConfigureAwait(true);
    }
}
