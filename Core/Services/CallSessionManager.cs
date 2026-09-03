using Core.Interfaces;
using Core.Models;
using System.Collections.Concurrent;

namespace Core.Services;

/// <summary>
/// 客户端通话会话管理器（CALL-E2E-2）。
/// <para>
/// 编排通话会话：主叫发起 / 被叫应答/拒绝 / 取消 / 挂断 / 重连，来电分派到被叫会话，
/// invite/ringing 超时收尾，多设备竞争时以服务端确认的终态收敛（authoritative）。
/// 本地命令乐观迁移 + 服务端响应权威覆盖；对端信令按 signal id 幂等去重。
/// SDP 经 <see cref="ICallMediaSession"/> 媒体面抽象在信令平面中传递。
/// </para>
/// </summary>
public sealed class CallSessionManager : ICallSessionManager
{
    private readonly IChatSessionClient _client;
    private readonly ICurrentUserContext _currentUser;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ConcurrentDictionary<string, CallSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ICallMediaSession> _media = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _startedMedia = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _appliedRemote = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _timeouts = new(StringComparer.Ordinal);
    private bool _disposed;

    public CallSessionManager(IChatSessionClient client, ICurrentUserContext currentUser)
        : this(client, currentUser, delayAsync: null)
    {
    }

    public CallSessionManager(
        IChatSessionClient client,
        ICurrentUserContext currentUser,
        Func<TimeSpan, CancellationToken, Task>? delayAsync)
    {
        _client = client;
        _currentUser = currentUser;
        _delay = delayAsync ?? Task.Delay;
        _client.CallSignalReceived += OnCallSignal;
    }

    /// <summary>主叫等待被叫应答超时（默认 30s），超时后取消并标记 TimedOut。</summary>
    public TimeSpan InviteTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>被叫振铃超时（默认 45s），超时后挂断并标记 Missed。</summary>
    public TimeSpan RingingTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>媒体面工厂：每个通话创建对应媒体会话；缺省为 null（仅控制面，不建媒体）。</summary>
    public Func<string, ICallMediaSession?>? MediaFactory { get; set; }

    public event EventHandler<CallSession>? IncomingCall;
    public event EventHandler<CallSession>? CallStateChanged;
    public event EventHandler<CallSession>? CallEnded;

    public IReadOnlyCollection<CallSession> ActiveCalls
        => _sessions.Values.Where(s => !s.IsTerminal).ToArray();

    public CallSession? GetCall(string callId)
        => _sessions.TryGetValue(callId, out var session) ? session : null;

    public async Task<CallSession> StartCallAsync(
        long calleeUserId,
        string? sdpOffer = null,
        CallGrantDto? grant = null,
        CancellationToken ct = default)
    {
        EnsureUsable();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(calleeUserId);

        var callId = Guid.NewGuid().ToString("N");
        var session = new CallSession(callId, CallRole.Caller, calleeUserId);
        if (!_sessions.TryAdd(callId, session))
            throw new InvalidOperationException("通话已存在。");
        CreateMedia(session);

        var offer = sdpOffer ?? GetMedia(callId)?.CreateOffer();
        try
        {
            await SendCommandAsync(session, CallCommandTypeDto.Invite, sdp: offer, grant: grant, ct);
            StartTimeout(session, InviteTimeout, CallCommandTypeDto.Cancel, CallEndReasonDto.TimedOut);
            return session;
        }
        catch
        {
            // fail-closed：邀请上行失败视为通话失败，本端唯一终态收尾后向上抛。
            session.ForceEnd(CallEndReasonDto.HungUp);
            CompleteSession(session);
            throw;
        }
    }

    public async Task AcceptAsync(string callId, string? sdpAnswer = null, CancellationToken ct = default)
    {
        EnsureUsable();
        var session = RequireSession(callId);
        var media = GetMedia(callId);
        // 生成 answer 前必须先应用对端 offer（WebRTC 协商顺序）。仅应用一次，避免与 StartMedia 重复。
        if (media is not null && !string.IsNullOrWhiteSpace(session.RemoteSdp) && _appliedRemote.TryAdd(callId, true))
            media.SetRemoteDescription(session.RemoteSdp);
        var answer = sdpAnswer ?? media?.CreateAnswer();
        CancelTimeout(callId);
        await SendCommandAsync(session, CallCommandTypeDto.Accept, sdp: answer, ct: ct);
    }

    public Task RejectAsync(string callId, CancellationToken ct = default)
    {
        EnsureUsable();
        var session = RequireSession(callId);
        CancelTimeout(callId);
        return SendCommandAsync(session, CallCommandTypeDto.Reject, ct: ct);
    }

    public Task CancelAsync(string callId, CancellationToken ct = default)
    {
        EnsureUsable();
        var session = RequireSession(callId);
        CancelTimeout(callId);
        return SendCommandAsync(session, CallCommandTypeDto.Cancel, ct: ct);
    }

    public async Task EndAsync(string callId, CancellationToken ct = default)
    {
        EnsureUsable();
        var session = RequireSession(callId);
        CancelTimeout(callId);
        await SendCommandAsync(session, CallCommandTypeDto.End, ct: ct);
    }

    public async Task ReconnectAsync(string callId, string? sdp = null, CancellationToken ct = default)
    {
        EnsureUsable();
        var session = RequireSession(callId);
        var offer = sdp ?? GetMedia(callId)?.RestartIce();
        await SendCommandAsync(session, CallCommandTypeDto.Reconnect, sdp: offer, ct: ct);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _client.CallSignalReceived -= OnCallSignal;
        foreach (var cts in _timeouts.Values)
            cts.Cancel();
        _timeouts.Clear();
        foreach (var media in _media.Values)
            media.Dispose();
        _media.Clear();
        _sessions.Clear();
        _startedMedia.Clear();
        _appliedRemote.Clear();
    }

    // ── 内部 ──

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_client.SupportsCallSignaling)
            throw new NotSupportedException("服务器未协商通话信令能力（CallSignaling）。");
    }

    private CallSession RequireSession(string callId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        if (!_sessions.TryGetValue(callId, out var session))
            throw new KeyNotFoundException($"通话不存在或已结束：{callId}");
        return session;
    }

    /// <summary>
    /// 上行一条通话信令命令：本地乐观迁移 → 发送 → 服务端响应权威覆盖 → 通知/收尾。
    /// 发送失败时本地乐观迁移仍成立（fail-closed），终态命令路径在此收尾后向上抛。
    /// </summary>
    private async Task<CallCommandResponseDto> SendCommandAsync(
        CallSession session,
        CallCommandTypeDto type,
        string? sdp = null,
        CallGrantDto? grant = null,
        CancellationToken ct = default)
    {
        var before = session.State;
        if (!session.TryApplyLocalCommand(type))
            throw new InvalidOperationException($"不允许的本地迁移：{type}（当前状态 {session.State}）。");
        if (!string.IsNullOrWhiteSpace(sdp))
            session.SetLocalSdp(sdp);

        var request = new CallCommandRequestDto
        {
            CommandId = session.NextCommandId(),
            CallId = session.CallId,
            Type = type,
            ActorUserId = _currentUser.RequireUserId(),
            Revision = session.NextRevision(),
            Grant = grant,
            Sdp = sdp,
            ClientOccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        CallCommandResponseDto response;
        try
        {
            response = await _client.SendCallCommandAsync(request, ct);
        }
        catch
        {
            if (session.IsTerminal)
                OnSessionChanged(session);
            throw;
        }

        var serverChanged = session.ApplyServerState(response);
        if (serverChanged || session.State != before || response.Replayed)
            OnSessionChanged(session);
        return response;
    }

    private void OnCallSignal(object? sender, CallSignalDto signal)
    {
        if (signal is null || string.IsNullOrWhiteSpace(signal.CallId))
            return;

        if (signal.Kind == CallCommandTypeDto.Invite
            && _currentUser.TryGetUserId(out var myId)
            && signal.FromUserId != myId)
        {
            // 来电：以对端为 peer 创建被叫会话（同 call id 已存在则复用，信号按 signal id 幂等去重）。
            // 仅在真正新建会话时上报 IncomingCall，重复/乱序 invite（网络重传、多设备竞争）不再重复提示。
            var existed = _sessions.TryGetValue(signal.CallId, out var session);
            if (!existed)
            {
                session = new CallSession(signal.CallId, CallRole.Callee, signal.FromUserId,
                    CallStateDto.Ringing, signal.Revision);
                if (!_sessions.TryAdd(signal.CallId, session))
                    return; // 并发竞争已由其他路径建立，忽略本次重复邀请。
                CreateMedia(session);
                session.ApplyRemoteSignal(signal); // 记录对端 SDP（状态已由构造置 Ringing）。
                IncomingCall?.Invoke(this, session);
                StartRingingTimeout(session);
                return;
            }

            // 已存在会话：仅当信号幂等键未处理过且引发可见变化时才上报。
            var changed = session!.ApplyRemoteSignal(signal);
            if (changed)
                OnSessionChanged(session);
            else
                StartRingingTimeout(session);
            return;
        }

        if (_sessions.TryGetValue(signal.CallId, out var existing))
        {
            var changed = existing.ApplyRemoteSignal(signal);
            if (changed)
                OnSessionChanged(existing);
        }
    }

    private void StartRingingTimeout(CallSession session)
    {
        // 被叫振铃超时在来电建立后启动；已存在超时则不重复启动。
        if (_timeouts.ContainsKey(session.CallId))
            return;
        StartTimeout(session, RingingTimeout, CallCommandTypeDto.End, CallEndReasonDto.Missed);
    }

    private void StartTimeout(
        CallSession session,
        TimeSpan timeout,
        CallCommandTypeDto terminalCommand,
        CallEndReasonDto localReason)
    {
        var cts = new CancellationTokenSource();
        if (!_timeouts.TryAdd(session.CallId, cts))
        {
            cts.Dispose();
            return;
        }
        _ = RunTimeoutLoopAsync(session, timeout, terminalCommand, localReason, cts.Token);
    }

    private async Task RunTimeoutLoopAsync(
        CallSession session,
        TimeSpan timeout,
        CallCommandTypeDto terminalCommand,
        CallEndReasonDto localReason,
        CancellationToken token)
    {
        try
        {
            await _delay(timeout, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 已终态或已接通（Active）则不再收尾。
        if (session.IsTerminal || session.State != CallStateDto.Ringing)
            return;

        try
        {
            var response = await SendCommandAsync(session, terminalCommand, ct: CancellationToken.None);
            if (session.IsTerminal)
                session.OverrideEndReason(localReason);
        }
        catch
        {
            // 尽力而为：服务端清理失败不影响本端唯一终态。
            session.ForceEnd(localReason);
            OnSessionChanged(session);
        }
    }

    /// <summary>会话可见变化：接通时启动媒体面，进入终态时收尾（事件 + 清理）。</summary>
    private void OnSessionChanged(CallSession session)
    {
        if (session.State == CallStateDto.Active)
            StartMedia(session);
        CallStateChanged?.Invoke(this, session);
        if (session.IsTerminal)
            CompleteSession(session);
    }

    private void StartMedia(CallSession session)
    {
        if (_startedMedia.ContainsKey(session.CallId))
            return;
        var media = GetMedia(session.CallId);
        if (media is null)
            return;
        if (!string.IsNullOrWhiteSpace(session.RemoteSdp) && !_appliedRemote.ContainsKey(session.CallId))
            media.SetRemoteDescription(session.RemoteSdp);
        media.Start();
        _startedMedia[session.CallId] = true;
    }

    private void CreateMedia(CallSession session)
    {
        if (MediaFactory is null)
            return;
        var media = MediaFactory(session.CallId);
        if (media is not null)
            _media[session.CallId] = media;
    }

    private ICallMediaSession? GetMedia(string callId)
        => _media.TryGetValue(callId, out var media) ? media : null;

    private void CompleteSession(CallSession session)
    {
        CancelTimeout(session.CallId);
        CallEnded?.Invoke(this, session);
        _sessions.TryRemove(session.CallId, out _);
        _startedMedia.TryRemove(session.CallId, out _);
        _appliedRemote.TryRemove(session.CallId, out _);
        if (_media.TryRemove(session.CallId, out var media))
        {
            media.Stop();
            media.Dispose();
        }
    }

    private void CancelTimeout(string callId)
    {
        if (_timeouts.TryRemove(callId, out var cts))
            cts.Cancel();
    }
}
