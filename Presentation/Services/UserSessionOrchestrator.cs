using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Chat_App.Infrastructure.Identity;
using Chat_App.Infrastructure.Services;
using Chat_App.Presentation.ViewModels.Chat;
using Chat_App.Services;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Presentation.Services;

/// <summary>
/// 统一会话生命周期编排器：
/// 登录/自动登录成功 → StartSessionAsync 统一启动 TCP、SyncEngine、OutboxProcessor、
/// AttachmentRecovery、FriendSync、Notifications；
/// 退出登录/会话失效/应用退出 → StopSessionAsync 统一停止，而不是只 Reset ChatViewModel。
/// 页面导航（Home/Chat/Friends）不再承担会话启动职责。
/// </summary>
public sealed class UserSessionOrchestrator : IDisposable
{
    private readonly IChatConnectionCoordinator _connectionCoordinator;
    private readonly IChatSessionClient _chatSession;
    private readonly ISyncEngine _syncEngine;
    private readonly OutboxProcessor _outboxProcessor;
    private readonly AttachmentRecoveryService _attachmentRecovery;
    private readonly IFriendStore _friendStore;
    private readonly ICurrentUserState _currentUserState;
    private readonly TokenInfo _tokenInfo;
    private readonly INotificationService _notificationService;

    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private bool _sessionRunning;
    private bool _eventHandlersRegistered;
    private bool _disposed;

    /// <summary>在途会话后台任务计数（好友同步/附件恢复）：StopSessionAsync 等待其退出。</summary>
    private long _inFlightTasks;
    private readonly object _taskSignal = new();

    /// <summary>会话启动完成（TCP 已连接+已鉴权）或停止完成时触发，供 UI 导航。</summary>
    public event EventHandler? SessionStarted;
    public event EventHandler<string>? SessionStopped;

    public UserSessionOrchestrator(
        IChatConnectionCoordinator connectionCoordinator,
        IChatSessionClient chatSession,
        ISyncEngine syncEngine,
        OutboxProcessor outboxProcessor,
        AttachmentRecoveryService attachmentRecovery,
        IFriendStore friendStore,
        ICurrentUserState currentUserState,
        TokenInfo tokenInfo,
        INotificationService notificationService)
    {
        _connectionCoordinator = connectionCoordinator;
        _chatSession = chatSession;
        _syncEngine = syncEngine;
        _outboxProcessor = outboxProcessor;
        _attachmentRecovery = attachmentRecovery;
        _friendStore = friendStore;
        _currentUserState = currentUserState;
        _tokenInfo = tokenInfo;
        _notificationService = notificationService;
    }

    public bool IsSessionRunning
    {
        get { lock (_sessionGate) return _sessionRunning; }
    }

    private void RegisterEventHandlers()
    {
        if (_eventHandlersRegistered)
            return;
        _connectionCoordinator.RegisterEventHandlers();
        _chatSession.Authenticated += OnAuthenticated;
        // 令牌刷新失败/过期 → 统一停止会话并通知 UI 回到登录页。
        _tokenInfo.SessionExpired += OnSessionExpired;
        _eventHandlersRegistered = true;
    }

    private void UnregisterEventHandlers()
    {
        if (!_eventHandlersRegistered)
            return;
        _chatSession.Authenticated -= OnAuthenticated;
        _tokenInfo.SessionExpired -= OnSessionExpired;
        _eventHandlersRegistered = false;
    }

    /// <summary>
    /// 登录/自动登录成功后启动完整会话：TCP 连接 + 鉴权。
    /// 鉴权成功（Authenticated 事件）后统一启动 SyncEngine / 好友同步 / 附件恢复 / 通知。
    /// 幂等：会话已运行时无操作；先 Stop 再 Start 保证干净会话。
    /// </summary>
    public async Task StartSessionAsync(CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessionRunning)
                return;
            if (_disposed)
                throw new ObjectDisposedException(nameof(UserSessionOrchestrator));

            RegisterEventHandlers();
            _sessionRunning = true;

            // Outbox 在鉴权成功前处于等待状态，随会话启动重新开始排空。
            _outboxProcessor.Start();

            Log.Information("会话编排器：开始连接服务器");
            try
            {
                await _connectionCoordinator.ConnectAsync(ct).ConfigureAwait(false);
                // ConnectAsync 内部：首次失败进入自动重连（不抛出），
                // 鉴权成功由 OnAuthenticated 启动同步/恢复；鉴权失败由协调器停止重连并提示。
            }
            catch
            {
                // 连接被取消（如自动登录被手动登录取消）或鉴权失败：回滚会话标记，
                // 允许后续 StartSessionAsync 重新发起。
                _sessionRunning = false;
                _outboxProcessor.Stop();
                throw;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// 统一停止会话：停止 SyncEngine（严格等待退出）/ OutboxProcessor，等待在途
    /// 好友同步与附件恢复收尾，断开 TCP，并通知 UI。
    /// </summary>
    public async Task StopSessionAsync(string? reason = null, CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_sessionRunning)
                return;

            _sessionRunning = false;
            await _syncEngine.StopAsync().ConfigureAwait(false);
            _outboxProcessor.Stop();
            // 等待在途好友同步/附件恢复退出（不等待则可能在新会话/退出后污染数据）
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref _inFlightTasks) > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20, ct).ConfigureAwait(false);
            _friendStore.Reset();
            await _connectionCoordinator.StopAsync().ConfigureAwait(false);

            Log.Information("会话编排器：会话已停止 ({Reason})", reason ?? "user_stop");
            SessionStopped?.Invoke(this, reason ?? "user_stop");
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>鉴权成功：启动同步引擎、好友增量同步、附件恢复与通知（会话级能力统一挂载点）。</summary>
    private void OnAuthenticated(object? sender, long userId)
    {
        Log.Information("会话编排器：鉴权成功 OwnerUserId={Id}，启动会话服务", userId);

        // 严格重启：等待旧同步任务退出后再启动，避免旧任务竞争/污染新会话。
        var session = _chatSession.CurrentSession;
        _ = RestartSyncSafeAsync(session);

        // 好友增量同步：与通讯录页共享 FriendStore，同步完成后两处 UI 统一刷新。
        StartSessionTask(() => _friendStore.SyncFromServerAsync());

        // 附件恢复：重连重新鉴权也会触发（AttachmentRecoveryService 自身已防重入）。
        if (_currentUserState.UserId is { } ownerUserId)
            StartSessionTask(() => _attachmentRecovery.RecoverFailedUploadsAsync(ownerUserId));

        Dispatcher.UIThread.Post(() => SessionStarted?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>启动并跟踪会话后台任务（停止时等待其收尾）。</summary>
    private void StartSessionTask(Func<Task> taskFactory)
    {
        Interlocked.Increment(ref _inFlightTasks);
        _ = Task.Run(async () =>
        {
            try
            {
                await taskFactory().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "会话后台任务异常");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightTasks);
            }
        });
    }

    /// <summary>
    /// 重启同步并处理 Partial 结果：预算截断（大账户会话/历史超限）时延迟继续同步，
    /// 直到完整完成或会话停止——绝不静默接受不完整同步。
    /// </summary>
    private async Task RestartSyncSafeAsync(Core.Models.SessionStamp session)
    {
        try
        {
            var first = true;
            while (_sessionRunning)
            {
                if (!first)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (!_sessionRunning)
                        return;
                }
                first = false;

                var tcs = new TaskCompletionSource<Core.Interfaces.SyncCompletedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                void Handler(object? s, Core.Interfaces.SyncCompletedEventArgs e)
                {
                    if (e.Session == session)
                        tcs.TrySetResult(e);
                }
                _syncEngine.Completed += Handler;
                try
                {
                    await _syncEngine.RestartAsync(session).ConfigureAwait(false);
                    var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                    if (!result.Succeeded)
                        return; // 同步失败：由重连/下次鉴权再次触发
                    if (result.Outcome != Core.Interfaces.SyncOutcome.PartialLimitReached)
                        return; // 完整完成
                    Log.Information("同步预算截断（Partial），延迟继续同步");
                }
                finally
                {
                    _syncEngine.Completed -= Handler;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "同步重启失败（会话可能已停止）");
        }
    }

    /// <summary>令牌刷新失败/过期：统一停止会话（TCP/同步/Outbox），UI 应回到登录页。</summary>
    private void OnSessionExpired(object? sender, EventArgs e)
    {
        Log.Warning("会话编排器：令牌失效，统一停止会话");
        Dispatcher.UIThread.Post(() =>
        {
            _notificationService.ShowError("登录状态已过期，请重新登录", "会话失效");
            _ = StopSessionAsync("session_expired");
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        UnregisterEventHandlers();
        _sessionGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
