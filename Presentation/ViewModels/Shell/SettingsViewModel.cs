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

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ISessionApiService _sessions;
    private readonly INotificationService _notifications;
    private readonly ICurrentUserContext _currentUser;
    private readonly ISettingsService _settings;
    private readonly IAccessibilityService _accessibility;

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

    public string CurrentUserDisplay =>
        _currentUser.UserName is { Length: > 0 } name
            ? $"{name} (#{_currentUser.UserId})"
            : (_currentUser.UserId?.ToString() ?? "未登录");

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RevokeOthersCommand { get; }
    public AsyncRelayCommand<SessionDeviceListItem> RevokeDeviceCommand { get; }

    public SettingsViewModel(
        ISessionApiService sessions,
        INotificationService notifications,
        ICurrentUserContext currentUser,
        ISettingsService settings,
        IAccessibilityService accessibility)
    {
        _sessions = sessions;
        _notifications = notifications;
        _currentUser = currentUser;
        _settings = settings;
        _accessibility = accessibility;

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        RevokeOthersCommand = new AsyncRelayCommand(
            RevokeOthersAsync,
            () => !IsLoading,
            ex => _notifications.ShowError($"撤销其他设备失败: {ex.Message}"));
        RevokeDeviceCommand = new AsyncRelayCommand<SessionDeviceListItem>(
            RevokeDeviceAsync,
            d => d is not null && !d.IsCurrent && !IsLoading,
            ex => _notifications.ShowError($"撤销设备失败: {ex.Message}"));
    }

    public async Task InitAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(true);
        await LoadSettingsAsync(ct).ConfigureAwait(true);
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
