using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Services;
using Core.Interfaces;
using Core.Models.DTO;
using Serilog;

namespace Chat_App.Presentation.ViewModels.Chat;

public sealed class ChatConnectionCoordinator : IChatConnectionCoordinator, IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly IDatabaseService _databaseService;
    private readonly IChatSessionClient _chatSessionClient;
    private readonly ICurrentUserState _currentUserState;
    private readonly INotificationService _notificationService;

    private readonly object _gate = new();
    private bool _eventHandlersRegistered;
    private bool _autoReconnectEnabled;
    private bool _reconnectLoopRunning;
    private bool _intentionalDisconnect;
    private int _attempt;
    private string? _pendingSessionId;
    private ulong? _pendingDeviceIdHash;
    private CancellationTokenSource? _lifecycleCts;
    private CancellationTokenSource? _heartbeatCts;
    private ChatConnectionStatus _status = ChatConnectionStatus.Disconnected;

    public ChatConnectionStatus Status
    {
        get
        {
            lock (_gate) return _status;
        }
        private set
        {
            lock (_gate)
            {
                if (_status == value)
                    return;
                _status = value;
            }

            StatusChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<ChatConnectionStatus>? StatusChanged;

    public ChatConnectionCoordinator(
        IDatabaseService databaseService,
        IChatSessionClient chatSessionClient,
        ICurrentUserState currentUserState,
        INotificationService notificationService)
    {
        _databaseService = databaseService;
        _chatSessionClient = chatSessionClient;
        _currentUserState = currentUserState;
        _notificationService = notificationService;
    }

    public void RegisterEventHandlers()
    {
        if (_eventHandlersRegistered)
            return;

        _chatSessionClient.Authenticated += OnAuthenticated;
        _chatSessionClient.AuthenticationFailed += OnAuthenticationFailed;
        _chatSessionClient.ProtocolError += OnProtocolError;
        _chatSessionClient.ConnectionClosed += OnConnectionClosed;
        _chatSessionClient.Connected += OnConnected;
        _eventHandlersRegistered = true;
    }

    public void UnregisterEventHandlers()
    {
        if (!_eventHandlersRegistered)
            return;

        _chatSessionClient.Authenticated -= OnAuthenticated;
        _chatSessionClient.AuthenticationFailed -= OnAuthenticationFailed;
        _chatSessionClient.ProtocolError -= OnProtocolError;
        _chatSessionClient.ConnectionClosed -= OnConnectionClosed;
        _chatSessionClient.Connected -= OnConnected;
        _eventHandlersRegistered = false;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _intentionalDisconnect = false;
        _autoReconnectEnabled = true;
        _attempt = 0;
        ReplaceLifecycleCts();
        try
        {
            await ConnectOnceAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (_autoReconnectEnabled)
        {
            Log.Warning(ex, "首次连接失败，进入重连");
            Status = ChatConnectionStatus.Reconnecting;
            ScheduleReconnect();
        }
    }

    public async Task StopAsync()
    {
        _intentionalDisconnect = true;
        _autoReconnectEnabled = false;
        StopHeartbeat();
        CancelLifecycle();
        await _chatSessionClient.DisconnectAsync("user_stop").ConfigureAwait(false);
        Status = ChatConnectionStatus.Disconnected;
    }

    public void Dispose()
    {
        _ = StopAsync();
        UnregisterEventHandlers();
        CancelLifecycle();
        StopHeartbeat();
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        Status = _attempt == 0 ? ChatConnectionStatus.Connecting : ChatConnectionStatus.Reconnecting;

        var serverInfoTask = _databaseService.GetServerInfoAsync();
        var authTokenTask = _databaseService.GetTokenAsync();
        await Task.WhenAll(serverInfoTask, authTokenTask).ConfigureAwait(false);

        var serverInfo = await serverInfoTask.ConfigureAwait(false);
        var authToken = await authTokenTask.ConfigureAwait(false);

        if (serverInfo is null || authToken is null || string.IsNullOrWhiteSpace(authToken.AccessToken))
        {
            Log.Warning("未找到服务器信息或 Token 丢失，跳过 TCP 连接");
            Status = ChatConnectionStatus.Disconnected;
            _autoReconnectEnabled = false;
            return;
        }

        if (!_currentUserState.TryGetUserId(out var userId))
        {
            Log.Warning("当前用户 ID 不可用，跳过 TCP 连接");
            Status = ChatConnectionStatus.Disconnected;
            _autoReconnectEnabled = false;
            return;
        }

        var deviceIdHash = authToken.DeviceIdHash.HasValue
            ? unchecked((ulong?)authToken.DeviceIdHash.Value)
            : null;

        Log.Information(
            "正在连接 TCP 服务器: {Server}:{Port} (attempt={Attempt})...",
            serverInfo.ServerIpAddress,
            serverInfo.ServerPort,
            _attempt + 1);

        await _chatSessionClient.ConnectAsync(serverInfo, ct).ConfigureAwait(false);

        Status = ChatConnectionStatus.Authenticating;
        _pendingSessionId = authToken.SessionId;
        _pendingDeviceIdHash = deviceIdHash;
        await _chatSessionClient.AuthenticateAsync(
                authToken.AccessToken,
                userId,
                authToken.SessionId,
                deviceIdHash,
                ct)
            .ConfigureAwait(false);
    }

    private void OnAuthenticated(object? sender, long userId)
    {
        _attempt = 0;
        Status = ChatConnectionStatus.Connected;
        _currentUserState.SetCurrentUser(userId, _currentUserState.UserName);
        _currentUserState.SetSession(_pendingSessionId, _pendingDeviceIdHash);
        Log.Information("TCP 鉴权成功 OwnerUserId={Id}", userId);
        StartHeartbeat();
        Dispatcher.UIThread.Post(() =>
            _notificationService.ShowSuccess("服务器连接成功，聊天功能已就绪"));
    }

    private void OnAuthenticationFailed(object? sender, string errorMessage)
    {
        Log.Error("TCP 鉴权失败: {Message}", errorMessage);
        StopHeartbeat();
        Status = ChatConnectionStatus.Disconnected;
        Dispatcher.UIThread.Post(() =>
            _notificationService.ShowError($"服务器鉴权失败: {errorMessage}"));

        // 鉴权失败通常需要重新登录，停止自动重连。
        _autoReconnectEnabled = false;
    }

    /// <summary>
    /// 普通协议错误（非致命）：仅提示用户，不影响心跳与重连。
    /// 致命错误已由 AuthenticationFailed 流程处理。
    /// </summary>
    private void OnProtocolError(object? sender, ProtocolErrorDto error)
    {
        Log.Warning("协议错误 Code={Code} Command={Command} Message={Message}",
            error.ErrorCode, error.Command, error.ErrorMessage);
        Dispatcher.UIThread.Post(() =>
            _notificationService.ShowError($"服务器错误: {error.ErrorMessage ?? error.ErrorCode ?? "未知错误"}"));
    }

    private void OnConnectionClosed(object? sender, string reason)
    {
        Log.Warning("TCP 连接断开: {Reason}", reason);
        StopHeartbeat();

        if (_intentionalDisconnect)
        {
            Status = ChatConnectionStatus.Disconnected;
            return;
        }

        Status = ChatConnectionStatus.Reconnecting;
        Dispatcher.UIThread.Post(() =>
            _notificationService.ShowWarning($"连接已断开，正在重连… ({reason})"));

        ScheduleReconnect();
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        Log.Information("TCP 套接字已连接，等待鉴权…");
    }

    private void ScheduleReconnect()
    {
        if (!_autoReconnectEnabled)
            return;

        lock (_gate)
        {
            if (_reconnectLoopRunning)
                return;
            _reconnectLoopRunning = true;
        }

        var ct = _lifecycleCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                while (_autoReconnectEnabled && !ct.IsCancellationRequested)
                {
                    _attempt++;
                    var delaySeconds = Math.Min(
                        MaxBackoff.TotalSeconds,
                        Math.Pow(2, Math.Min(_attempt - 1, 5)));
                    var jitterMs = Random.Shared.Next(0, 400);
                    var delay = TimeSpan.FromSeconds(delaySeconds) + TimeSpan.FromMilliseconds(jitterMs);
                    Log.Information("将在 {Delay}ms 后重连 (attempt={Attempt})", delay.TotalMilliseconds, _attempt);

                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        await ConnectOnceAsync(ct).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "重连失败 attempt={Attempt}", _attempt);
                        Status = ChatConnectionStatus.Reconnecting;
                    }
                }
            }
            finally
            {
                lock (_gate) _reconnectLoopRunning = false;
            }
        }, ct);
    }

    private void StartHeartbeat()
    {
        StopHeartbeat();
        var cts = new CancellationTokenSource();
        _heartbeatCts = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HeartbeatInterval, cts.Token).ConfigureAwait(false);
                    if (_chatSessionClient.IsConnected && _chatSessionClient.IsAuthenticated)
                        await _chatSessionClient.SendHeartbeatAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "心跳发送失败");
                }
            }
        }, cts.Token);
    }

    private void StopHeartbeat()
    {
        try
        {
            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
        }
        catch
        {
            // ignore
        }

        _heartbeatCts = null;
    }

    private void ReplaceLifecycleCts()
    {
        CancelLifecycle();
        _lifecycleCts = new CancellationTokenSource();
    }

    private void CancelLifecycle()
    {
        try
        {
            _lifecycleCts?.Cancel();
            _lifecycleCts?.Dispose();
        }
        catch
        {
            // ignore
        }

        _lifecycleCts = null;
    }
}
