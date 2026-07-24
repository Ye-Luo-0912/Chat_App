using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Services;
using Chat_App.Shared.Commands;
using Chat_App.Shared.Mvvm;
using Core.Contracts.Sessions;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Presentation.ViewModels.Shell;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ISessionApiService _sessions;
    private readonly INotificationService _notifications;
    private readonly ICurrentUserContext _currentUser;

    public ObservableCollection<SessionDeviceDto> Devices { get; } = [];

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

    public string CurrentUserDisplay =>
        _currentUser.UserName is { Length: > 0 } name
            ? $"{name} (#{_currentUser.UserId})"
            : (_currentUser.UserId?.ToString() ?? "未登录");

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RevokeOthersCommand { get; }
    public AsyncRelayCommand<SessionDeviceDto> RevokeDeviceCommand { get; }

    public SettingsViewModel(
        ISessionApiService sessions,
        INotificationService notifications,
        ICurrentUserContext currentUser)
    {
        _sessions = sessions;
        _notifications = notifications;
        _currentUser = currentUser;

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        RevokeOthersCommand = new AsyncRelayCommand(
            RevokeOthersAsync,
            () => !IsLoading,
            ex => _notifications.ShowError($"撤销其他设备失败: {ex.Message}"));
        RevokeDeviceCommand = new AsyncRelayCommand<SessionDeviceDto>(
            RevokeDeviceAsync,
            d => d is not null && !d.IsCurrent && !IsLoading,
            ex => _notifications.ShowError($"撤销设备失败: {ex.Message}"));
    }

    public async Task InitAsync(CancellationToken ct = default) =>
        await LoadAsync(ct).ConfigureAwait(true);

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
                Devices.Add(item);
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

    private async Task RevokeDeviceAsync(SessionDeviceDto? device, CancellationToken ct)
    {
        if (device is null || device.IsCurrent)
            return;

        await _sessions.RevokeSessionAsync(device.DeviceId, ct).ConfigureAwait(true);
        _notifications.ShowSuccess($"已下线: {device.Title}");
        await LoadAsync(ct).ConfigureAwait(true);
    }
}
