using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Services;
using ChatApp.Shared.Protocol.Tcp;
using Core.Diagnostics;
using Core.Interfaces;
using Core.Models.DTO;
using Serilog;

namespace Chat_App.Presentation.ViewModels.Chat;

public sealed class ChatConnectionCoordinator : IChatConnectionCoordinator, IDisposable, IMetricsSource
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

    // ── 诊断指标 ──
    private long _connectAttempts;
    private long _connectFailures;
    private long _reconnectCycles;
    private long _sessionGeneration;
    private readonly LatencyHistogram _connectLatency = new();

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
        var resumeTokenTask = _databaseService.GetResumeTokenAsync();
        await Task.WhenAll(serverInfoTask, authTokenTask, resumeTokenTask).ConfigureAwait(false);

        var serverInfo = await serverInfoTask.ConfigureAwait(false);
        var authToken = await authTokenTask.ConfigureAwait(false);
        var resumeToken = await resumeTokenTask.ConfigureAwait(false);

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

        // 本次连接是否携带了 ResumeToken（用于异常路径决定是否清理本地 token）。
        var resumeAttempted = resumeToken is not null;

        Interlocked.Increment(ref _connectAttempts);
        var sw = Stopwatch.StartNew();
        try
        {
            Log.Information(
                "正在连接 TCP 服务器: {Server}:{Port} (attempt={Attempt}, resume={Resume})...",
                serverInfo.ServerIpAddress,
                serverInfo.ServerPort,
                _attempt + 1,
                resumeAttempted);

            // 会话凭据先行暂存：Resume 成功时 Authenticated 事件在 ConnectAsync 内同步触发，
            // OnAuthenticated 依赖这两个字段（与完整认证路径同一约定）。
            _pendingSessionId = authToken.SessionId;
            _pendingDeviceIdHash = deviceIdHash;

            // 本地有未过期 token 时走 Resume 优先；无 token（或被 AdvertiseSessionResume 关闭）
            // 时 ConnectAsync 退化为普通握手，随后完整认证。
            await _chatSessionClient.ConnectAsync(serverInfo, ct, resumeToken).ConfigureAwait(false);

            if (resumeAttempted)
            {
                if (_chatSessionClient.IsAuthenticated)
                {
                    // Resume 成功：Authenticated 事件链已建立状态（Connected/心跳/会话戳），
                    // 跳过 AuthenticateAsync，只做收尾（持久化轮换 token + 指标）。
                    await FinalizeResumeAsync(userId, authToken.SessionId, sw.Elapsed).ConfigureAwait(false);
                    return;
                }

                // 未恢复（网关忽略 token 或明确拒绝）：默认清除本地 token，回退完整认证；
                // DependencyUnavailable 除外——网关语义为依赖暂不可用，保留 token 退避后重试。
                var failureKind = _chatSessionClient.LastResumeResult?.FailureKind;
                if (failureKind != ResumeFailureKind.DependencyUnavailable)
                    await _databaseService.ClearResumeTokenAsync().ConfigureAwait(false);
                Log.Information("Resume 未成功（kind={Kind}），回退完整认证",
                    failureKind?.ToString() ?? "NotAttempted");
            }

            Status = ChatConnectionStatus.Authenticating;
            await _chatSessionClient.AuthenticateAsync(
                    authToken.AccessToken,
                    userId,
                    authToken.SessionId,
                    deviceIdHash,
                    ct)
                .ConfigureAwait(false);

            // 认证成功：持久化网关颁发的 ResumeToken（可能轮换）。网关未颁发说明 Resume
            // 未启用或不可用，清除残留 token，避免下次重连携带注定失败的旧值。
            var issuedResumeToken = _chatSessionClient.LastIssuedResumeToken;
            if (string.IsNullOrWhiteSpace(issuedResumeToken))
                await _databaseService.ClearResumeTokenAsync().ConfigureAwait(false);
            else
                await _databaseService.SaveResumeTokenAsync(issuedResumeToken).ConfigureAwait(false);

            // 会话代数：每次成功鉴权 +1（跨连接误关闭诊断：新代会话的迟到异常必须忽略）
            Interlocked.Increment(ref _sessionGeneration);
            _connectLatency.Add(sw.Elapsed);
        }
        catch (Exception ex)
        {
            // Resume 尝试失败且连接不可用（无法在本连接回退认证）：仅明确拒绝
            // （token 无效/账号冻结）或网关应答超时时清理本地 token；
            // 单纯断线（IOException 等）保留 token，供下次重连在 TTL 内重试 Resume。
            if (resumeAttempted
                && (ex is TimeoutException
                    || _chatSessionClient.LastResumeResult is { Success: false, FailureKind: ResumeFailureKind.InvalidToken or ResumeFailureKind.UserFrozen }))
            {
                await _databaseService.ClearResumeTokenAsync().ConfigureAwait(false);
            }

            Interlocked.Increment(ref _connectFailures);
            throw;
        }
    }

    /// <summary>
    /// Resume 成功收尾：持久化网关轮换的新 token（单次使用语义，旧 token 已失效），
    /// 并以服务端返回的会话 Id 校正会话戳（OnAuthenticated 已用本地暂存值先行建立）。
    /// </summary>
    private async Task FinalizeResumeAsync(long userId, string? localSessionId, TimeSpan elapsed)
    {
        var result = _chatSessionClient.LastResumeResult;
        if (result is not null)
        {
            if (!string.IsNullOrWhiteSpace(result.ResumeToken))
                await _databaseService.SaveResumeTokenAsync(result.ResumeToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.SessionId) && result.SessionId != localSessionId)
            {
                _pendingSessionId = result.SessionId;
                _currentUserState.SetAuthenticatedSession(
                    userId, _currentUserState.UserName, _pendingSessionId, _pendingDeviceIdHash,
                    _chatSessionClient.ConnectionGeneration);
            }
        }

        // 会话代数：恢复出的会话同样推进连接代际计数（语义与完整鉴权一致）。
        Interlocked.Increment(ref _sessionGeneration);
        _connectLatency.Add(elapsed);
    }

    private void OnAuthenticated(object? sender, long userId)
    {
        _attempt = 0;
        Status = ChatConnectionStatus.Connected;
        RegisterPushTokenInBackground(userId);
        // 原子设置完整鉴权会话：同一账户重连不递增账户代际，仅记录连接代际。
        _currentUserState.SetAuthenticatedSession(
            userId, _currentUserState.UserName, _pendingSessionId, _pendingDeviceIdHash,
            _chatSessionClient.ConnectionGeneration);
        Log.Information("TCP 鉴权成功 OwnerUserId={Id}", userId);
        StartHeartbeat();
        Dispatcher.UIThread.Post(() =>
            _notificationService.ShowSuccess("服务器连接成功，聊天功能已就绪"));
    }

    /// <summary>
    /// 鉴权成功后自动注册推送令牌（fire-and-forget）。
    /// 使用设备派生的确定性 token（WebPush 平台），服务端据此路由离线推送。
    /// 失败不影响连接——推送为增值能力，下次鉴权自动重试。
    /// </summary>
    private void RegisterPushTokenInBackground(long userId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // 设备派生确定性 token：SHA256(machineName + userId) 前 64 字符。
                var machine = Environment.MachineName;
                var raw = $"{machine}:{userId}";
                var bytes = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(raw));
                var token = Convert.ToHexString(bytes).ToLowerInvariant();

                var request = new RegisterPushTokenRequestDto
                {
                    Platform = PushPlatformDto.WebPush,
                    Token = token
                };
                await _chatSessionClient.RegisterPushTokenAsync(request).ConfigureAwait(false);
                Log.Information("Push token 已注册 UserId={UserId}", userId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Push token 注册失败（不影响连接）UserId={UserId}", userId);
            }
        });
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
        // 关键：必须关闭 TCP/TLS 连接与收发循环，否则 Socket 与接收任务泄漏打开。
        _ = _chatSessionClient.DisconnectAsync("authentication_failed");
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
            Interlocked.Increment(ref _reconnectCycles);
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

    public string Name => "network_coordinator";

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        ["connect_attempts"] = Volatile.Read(ref _connectAttempts),
        ["connect_failures"] = Volatile.Read(ref _connectFailures),
        ["reconnect_cycles"] = Volatile.Read(ref _reconnectCycles),
        ["session_generation"] = Volatile.Read(ref _sessionGeneration),
        ["current_backoff_ms"] = (long)(Math.Min(
            MaxBackoff.TotalMilliseconds,
            Math.Pow(2, Math.Min(_attempt - 1, 5)) * 1000))
    };

    public IReadOnlyDictionary<string, HistogramSnapshot> Histograms =>
        new Dictionary<string, HistogramSnapshot> { ["connect_latency_ms"] = _connectLatency.Snapshot() };
}
