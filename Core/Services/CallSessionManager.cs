using Core.Interfaces;
using Core.Models;
using System.Collections.Concurrent;
using System.Globalization;
using TcpCallKind = ChatApp.Shared.Protocol.Tcp.TcpCallKind;
using TcpCallSignals = ChatApp.Shared.Protocol.Tcp.TcpCallConstants;

namespace Core.Services;

/// <summary>
/// 客户端通话会话管理器（CALL-E2E-2；群组多方 GROUP-CALL-1）。
/// <para>
/// 编排通话会话：主叫发起 / 被叫应答/拒绝 / 取消 / 挂断 / 重连，来电分派到被叫会话，
/// invite/ringing 超时收尾，多设备竞争时以服务端确认的终态收敛（authoritative）。
/// 本地命令乐观迁移 + 服务端响应权威覆盖；对端信令按 signal id 幂等去重。
/// SDP 经 <see cref="ICallMediaSession"/> 媒体面抽象在信令平面中传递。
/// </para>
/// <para>
/// 群组（Mesh ≤4 人）为本类加性扩展：会话 + 成员字典模型，每成员一条独立对端媒体协商
/// （<see cref="PeerMediaFactory"/> 每 PeerConnection 一个实例）；主叫为 hub 逐成员 offer/answer，
/// 成员 accept 即加入、participant-left 拆除该成员媒体、发起者 end 终结全会话。
/// 1:1 Direct 路径零改动。
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
    // 群组（Mesh）：对端成员媒体与配套状态，键 "callId|memberUserId"（每成员一个实例）。
    private readonly ConcurrentDictionary<string, ICallMediaSession> _peerMedia = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _peerStarted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _peerAppliedRemote = new(StringComparer.Ordinal);
    // 群组 grant 缓存（发起者持有）：供后续成员命令（accept/end 等）原样携带。
    private readonly ConcurrentDictionary<string, CallGrantDto> _grants = new(StringComparer.Ordinal);
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

    /// <summary>群组对端媒体工厂：每成员一条 PeerConnection（参数 callId + memberUserId）；
    /// 未设置时回退按通话的 <see cref="MediaFactory"/> 逐成员调用。</summary>
    public Func<string, long, ICallMediaSession?>? PeerMediaFactory { get; set; }

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

    /// <inheritdoc />
    public async Task<CallSession> StartGroupCallAsync(
        string callId,
        CallGrantDto grant,
        string? sdpOffer = null,
        CancellationToken ct = default)
    {
        EnsureUsable();
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(grant);
        if (!string.Equals(grant.CallId, callId, StringComparison.Ordinal))
            throw new ArgumentException("grant 与通话 Id 不匹配。", nameof(grant));
        if (grant.CallKind != TcpCallKind.Group)
            throw new ArgumentException("发起群组通话需要 CallKind=Group 的 grant。", nameof(grant));

        var me = _currentUser.RequireUserId();
        if (grant.CallerUserId != me)
            throw new InvalidOperationException("仅群组 grant 发起者可发起群组通话。");
        var others = (grant.Participants ?? Array.Empty<long>())
            .Where(id => id > 0 && id != me)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (others.Length == 0)
            throw new ArgumentException("群组 grant 缺少其他参与者。", nameof(grant));

        // 群组会话：成员集合 = grant 签发名单（含主叫，升序，见 group-call-sfu-design §4.1）；
        // 被叫侧仅知 [本端, 发起者]，其余成员随加入信令逐个出现（wire 不向被叫透出名单）。
        var roster = grant.Participants is { Count: > 0 } ? grant.Participants : new long[] { me };
        var session = new CallSession(callId, CallRole.Caller, grant.CallerUserId, roster);
        if (!_sessions.TryAdd(callId, session))
            throw new InvalidOperationException("通话已存在。");
        _grants[callId] = grant;

        // 本地乐观迁移（Idle → Ringing）一次；随后逐成员 invite 走无迁移的成员命令路径
        // （无状态中继按名单扇出，房间状态由客户端按成员/revision 自洽）。
        if (!session.TryApplyLocalCommand(CallCommandTypeDto.Invite))
        {
            _grants.TryRemove(callId, out _);
            _sessions.TryRemove(callId, out _);
            throw new InvalidOperationException("不允许的本地迁移：Invite（当前状态 Ringing/其他）。");
        }

        // Mesh 编排：每成员独立 offer（每 PeerConnection 一个实例）→ 逐成员 invite 上行。
        var failures = new List<Exception>();
        foreach (var member in others)
        {
            try
            {
                var media = CreatePeerMedia(session, member);
                var offer = sdpOffer ?? media?.CreateOffer();
                await SendGroupMemberCommandAsync(
                    session, CallCommandTypeDto.Invite, member, offer, grant, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 单成员邀请失败不拖垮整场：拆其媒体，继续邀请其余成员。
                failures.Add(ex);
                DestroyPeerMedia(callId, member);
            }
        }

        if (failures.Count == others.Length)
        {
            // 全部邀请失败：fail-closed，本端唯一终态收尾后向上抛。
            session.ForceEnd(CallEndReasonDto.HungUp);
            CompleteSession(session);
            throw new AggregateException("群组通话邀请全部上行失败。", failures);
        }

        StartTimeout(session, InviteTimeout, CallCommandTypeDto.Cancel, CallEndReasonDto.TimedOut);
        return session;
    }

    /// <summary>
    /// 群组成员命令上行（无状态中继路径）：不做本地状态迁移（房间状态由成员/revision 自洽），
    /// grant 原样携带；服务端响应推进权威 revision（回显状态与本地一致，不产生迁移）。
    /// <para>
    /// Invite 命令携带目标成员 Id（0.5.8 加性，GROUP-CALL-SDP-1）：无状态中继按名单广播、
    /// 被叫按目标过滤——只有目标成员应用该 invite 的 offer/建会话，其余成员对当前会话无操作。
    /// </para>
    /// </summary>
    private async Task<CallCommandResponseDto> SendGroupMemberCommandAsync(
        CallSession session,
        CallCommandTypeDto type,
        long memberUserId,
        string? sdp,
        CallGrantDto grant,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(sdp))
            session.SetLocalSdp(sdp);
        var request = new CallCommandRequestDto
        {
            CommandId = session.NextCommandId(_currentUser.RequireUserId()),
            CallId = session.CallId,
            Type = type,
            ActorUserId = _currentUser.RequireUserId(),
            Revision = session.NextRevision(),
            Grant = grant,
            Sdp = sdp,
            // 逐成员 invite：目标成员 Id 随命令透传（中继转扇出信号，被叫按目标过滤）。
            ParticipantUserId = type == CallCommandTypeDto.Invite ? memberUserId : null,
            ClientOccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var response = await _client.SendCallCommandAsync(request, ct).ConfigureAwait(false);
        session.ApplyServerState(response);
        return response;
    }

    /// <summary>
    /// 群组成员中途加入（GROUP-CALL-MIDJOIN-1）：发起者以<b>携带原 callId 重签</b>的群组 grant
    /// （名单含新成员）向该成员发起逐成员 invite——同一通话持续、既有会话不中断。
    /// <para>
    /// 前置：本端为发起者、grant 为 CallKind=Group 且名单含被邀成员（即调用方已携带原 callId
    /// 向 Server 重签）。效果：缓存换新 grant（后续成员命令携带新名单）、本地成员集合先行收敛、
    /// 新成员走既有逐成员 invite/媒体协商链。invite 上行失败时拆除该成员媒体并回滚本地集合。
    /// </para>
    /// </summary>
    public async Task InviteMemberAsync(
        string callId,
        long memberUserId,
        CallGrantDto grant,
        string? sdpOffer = null,
        CancellationToken ct = default)
    {
        EnsureUsable();
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memberUserId);
        ArgumentNullException.ThrowIfNull(grant);
        if (!string.Equals(grant.CallId, callId, StringComparison.Ordinal))
            throw new ArgumentException("grant 与通话 Id 不匹配。", nameof(grant));
        if (grant.CallKind != TcpCallKind.Group)
            throw new ArgumentException("群组中期加人需要 CallKind=Group 的 grant。", nameof(grant));

        var session = RequireSession(callId);
        if (!session.IsGroup || session.IsTerminal)
            throw new InvalidOperationException("仅进行中的群组通话支持中期加人。");
        var me = _currentUser.RequireUserId();
        if (me != session.InitiatorUserId || grant.CallerUserId != me)
            throw new InvalidOperationException("仅群组发起者可中期加人。");
        if (memberUserId == me || session.Participants.Contains(memberUserId))
            throw new InvalidOperationException("该成员已在通话中。");
        if (grant.Participants?.Contains(memberUserId) != true)
            throw new ArgumentException(
                "grant 名单缺少被邀成员（须携带原 callId 重签并更新参与者名单）。", nameof(grant));

        // 重签批次：缓存换新 grant（后续成员命令按新名单扇出），本地成员集合先行收敛。
        _grants[callId] = grant;
        if (!session.TryAddParticipant(memberUserId))
            throw new InvalidOperationException("成员加入被拒绝（名单已满）。");

        try
        {
            var media = EnsurePeerMedia(session, memberUserId);
            var offer = sdpOffer ?? media?.CreateOffer();
            await SendGroupMemberCommandAsync(
                session, CallCommandTypeDto.Invite, memberUserId, offer, grant, ct).ConfigureAwait(false);
        }
        catch
        {
            // fail-closed：邀请上行失败拆其媒体并回滚本地集合（成员集合随信令最终自洽）。
            DestroyPeerMedia(callId, memberUserId);
            session.TryRemoveParticipant(memberUserId);
            throw;
        }
    }

    public async Task AcceptAsync(string callId, string? sdpAnswer = null, CancellationToken ct = default)
    {
        EnsureUsable();
        var session = RequireSession(callId);
        // 群组：被叫仅与发起者协商（Mesh hub 星型），媒体实例按成员键取用
        // （晋升前的 invite 路径落在通话级实例，此处回退兼容）；1:1 沿用通话级实例。
        var media = session.IsGroup
            ? (GetPeerMedia(callId, session.PeerUserId) ?? GetMedia(callId))
            : GetMedia(callId);
        // 生成 answer 前必须先应用对端 offer（WebRTC 协商顺序）。仅应用一次，避免与 StartMedia 重复。
        if (media is not null && !string.IsNullOrWhiteSpace(session.RemoteSdp) && _appliedRemote.TryAdd(callId, true))
            media.SetRemoteDescription(session.RemoteSdp);
        var answer = sdpAnswer ?? media?.CreateAnswer();
        CancelTimeout(callId);
        await SendCommandAsync(session, CallCommandTypeDto.Accept, sdp: answer, ct: ct).ConfigureAwait(false);
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
        // 群组：取首个成员实例做 ICE restart offer（每成员实例共用同一条重连命令广播）。
        var restartOffer = sdp
            ?? GetMedia(callId)?.RestartIce()
            ?? _peerMedia.FirstOrDefault(kv => IsKeyForCall(kv.Key, callId)).Value?.RestartIce();
        await SendCommandAsync(session, CallCommandTypeDto.Reconnect, sdp: restartOffer, ct: ct).ConfigureAwait(false);
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
        foreach (var media in _peerMedia.Values)
            media.Dispose();
        _peerMedia.Clear();
        _peerStarted.Clear();
        _peerAppliedRemote.Clear();
        _grants.Clear();
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
            CommandId = session.NextCommandId(_currentUser.RequireUserId()),
            CallId = session.CallId,
            Type = type,
            ActorUserId = _currentUser.RequireUserId(),
            Revision = session.NextRevision(),
            // 群组会话：无状态中继要求每条命令携带 grant；发起者缓存随命令自动附加。
            Grant = grant ?? (session.IsGroup ? FindGrant(session.CallId) : null),
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

        // 群组逐成员 invite 的目标过滤（GROUP-CALL-SDP-1）：无状态中继把发给某成员的 invite
        // 广播到全部成员，非目标成员不得建会话/应用 offer（否则 RemoteSdp 被最后送达者覆盖，
        // 媒体面应用的是别人的 offer）。participant-joined 等带事件的信令不受此过滤影响；
        // 目标缺省（null，0.5.7 广播形态）= 既有语义。
        if (signal.Kind == CallCommandTypeDto.Invite
            && string.IsNullOrWhiteSpace(signal.Event)
            && signal.ParticipantUserId is { } inviteTarget
            && _currentUser.TryGetUserId(out var selfUserId)
            && inviteTarget != selfUserId)
        {
            return; // 发给其他成员的 invite：对当前会话无操作（仅忽略）。
        }

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
                // 群组 invite 随信令下发 grant（0.5.8，GROUP-CALL-GAP-1）：据此建立群组会话
                // （成员集合 = 签发名单）并缓存 grant——被叫 accept/end 原样携带回中继。
                PromoteFromGroupInvite(signal, session);
                session.ApplyRemoteSignal(signal); // 记录对端 SDP（状态已由构造置 Ringing）。
                IncomingCall?.Invoke(this, session);
                StartRingingTimeout(session);
                return;
            }

            // 已存在会话：仅当信号幂等键未处理过且引发可见变化时才上报。
            PromoteFromGroupInvite(signal, session!);
            var changed = session!.ApplyRemoteSignal(signal);
            if (changed)
                OnSessionChanged(session);
            else
                StartRingingTimeout(session);
            return;
        }

        if (_sessions.TryGetValue(signal.CallId, out var existing))
        {
            // 群组证据：1:1 形态的被叫会话收到非主叫来源的成员信令（无状态中继 Mesh 扇出），
            // 晋升为群组会话（初始成员 [本端, 发起者]），随后信令按群组语义应用。
            if (!existing.IsGroup
                && existing.Role == CallRole.Callee
                && _currentUser.TryGetUserId(out var selfId)
                && IsGroupMembershipEvidence(existing, signal, selfId))
            {
                existing.TryPromoteToGroup(existing.PeerUserId, new[] { selfId, existing.PeerUserId });
            }

            var changed = existing.ApplyRemoteSignal(signal);
            if (existing.IsGroup)
                HandleGroupPeerSignal(existing, signal);
            if (changed)
                OnSessionChanged(existing);
        }
    }

    /// <summary>
    /// 群组 invite 随信令下发的 grant 处理（0.5.8 加性，GROUP-CALL-GAP-1）：被叫侧建立群组会话
    /// （成员集合 = grant 签发名单）并缓存 grant，使 accept/end 能原样携带授权回无状态中继。
    /// 仅在信令携带群组 grant、来源为 grant 发起者且 callId 匹配时生效；Direct/既有形态零改动。
    /// </summary>
    private void PromoteFromGroupInvite(CallSignalDto signal, CallSession session)
    {
        var grant = signal.Grant;
        if (grant is null
            || grant.CallKind != TcpCallKind.Group
            || grant.CallerUserId != signal.FromUserId
            || !string.Equals(grant.CallId, signal.CallId, StringComparison.Ordinal))
        {
            return;
        }

        // 已群组（重复 invite 重传）仅刷新 grant 缓存；未晋升则按签发名单建立群组会话。
        if (!session.IsGroup)
        {
            session.TryPromoteToGroup(grant.CallerUserId, grant.Participants ?? new[] { signal.FromUserId });
        }
        _grants[session.CallId] = grant;
    }

    /// <summary>该信令是否构成"群组存在"的证据：来自第三成员的 join 事件或非 invite 类成员信令。</summary>
    private static bool IsGroupMembershipEvidence(CallSession session, CallSignalDto signal, long selfId)
    {
        var from = signal.FromUserId;
        if (from <= 0 || from == selfId || from == session.PeerUserId)
            return false;
        if (string.Equals(
                signal.Event,
                TcpCallSignals.SignalEventParticipantJoined,
                StringComparison.Ordinal))
            return true;
        return signal.Kind != CallCommandTypeDto.Invite;
    }

    /// <summary>
    /// 群组会话的每成员媒体编排（Mesh 阶段一，主叫为 hub）：
    /// <list type="bullet">
    /// <item>成员 accept：其 answer 应用到该成员的媒体实例并启动（逐成员 offer/answer 交换收口）；</item>
    /// <item>participant-joined（发起者侧）：为该成员建立新对端协商——逐成员 invite（新 grant 批次语义）；</item>
    /// <item>participant-left / 成员 reject：拆除该成员媒体与状态（成员集合变更已由会话模型完成）；</item>
    /// <item>发起者离开：会话已终态，全部成员媒体由 <see cref="CompleteSession"/> 统一收尾。</item>
    /// </list>
    /// </summary>
    private void HandleGroupPeerSignal(CallSession session, CallSignalDto signal)
    {
        var callId = session.CallId;
        var from = signal.FromUserId;
        if (from <= 0 || from == _currentUser.UserId)
            return;

        if (string.Equals(signal.Event, TcpCallSignals.SignalEventParticipantJoined, StringComparison.Ordinal))
        {
            var joined = signal.ParticipantUserId ?? from;
            if (joined > 0 && joined != _currentUser.UserId)
                EnsureMemberNegotiationAsync(session, joined);
            return;
        }

        switch (signal.Kind)
        {
            case CallCommandTypeDto.Accept:
            {
                // 逐成员 answer 收口（主叫 hub 专属）：answer 应用到该成员实例（幂等一次）并启动。
                // 被叫侧仅记录成员加入（会话模型已增减成员）；被叫与发起者的协商由本端 AcceptAsync 收口。
                if (session.Role != CallRole.Caller)
                    break;
                var media = EnsurePeerMedia(session, from);
                var key = PeerKey(callId, from);
                if (media is not null
                    && !string.IsNullOrWhiteSpace(signal.Sdp)
                    && _peerAppliedRemote.TryAdd(key, true))
                {
                    media.SetRemoteDescription(signal.Sdp);
                }
                StartPeerMedia(key, media);
                break;
            }
            case CallCommandTypeDto.Reject:
                // 成员拒绝 = 成员离开：拆其媒体（会话模型已移除成员并触发事件）。
                DestroyPeerMedia(callId, from);
                break;
            case CallCommandTypeDto.End:
            {
                var left = signal.ParticipantUserId ?? from;
                if (left == session.InitiatorUserId)
                    return; // 发起者离开：会话已终态，CompleteSession 统一拆除全部成员媒体。
                DestroyPeerMedia(callId, left);
                break;
            }
            default:
                break;
        }
    }

    /// <summary>
    /// 发起者侧的成员中途加入编排（participant-joined 事件路径）：为该成员建立对端媒体实例
    /// 并发起逐成员 invite（offer 由该成员实例生成；invite 自动携带目标成员 Id，被叫按目标
    /// 过滤——GROUP-CALL-SDP-1）。grant 以当前缓存携带；同 CallId 重签批次见
    /// <see cref="InviteMemberAsync"/>。上行失败仅拆除该成员媒体，不影响在场成员。
    /// </summary>
    private async void EnsureMemberNegotiationAsync(CallSession session, long memberUserId)
    {
        try
        {
            if (!_grants.TryGetValue(session.CallId, out var grant))
                return;
            var media = EnsurePeerMedia(session, memberUserId);
            var offer = media?.CreateOffer();
            await SendGroupMemberCommandAsync(
                session, CallCommandTypeDto.Invite, memberUserId, offer, grant, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            DestroyPeerMedia(session.CallId, memberUserId);
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

    private void StartMedia(CallSession session)
    {
        var callId = session.CallId;
        if (session.IsGroup)
        {
            // 群组：逐成员启动（仅已应用对端 offer/answer 的实例；StartPeerMedia 幂等）。
            foreach (var key in _peerAppliedRemote.Keys)
            {
                if (!IsKeyForCall(key, callId))
                    continue;
                StartPeerMedia(key, GetPeerMediaByKey(key));
            }
            // 晋升前的被叫实例仍落在通话级键位：一并启动（幂等）。
            if (_appliedRemote.ContainsKey(callId)
                && _media.TryGetValue(callId, out var legacyMedia)
                && _startedMedia.TryAdd(callId, true))
            {
                legacyMedia.Start();
            }
            return;
        }
        if (_startedMedia.ContainsKey(callId))
            return;
        var media = GetMedia(callId);
        if (media is null)
            return;
        if (!string.IsNullOrWhiteSpace(session.RemoteSdp) && !_appliedRemote.ContainsKey(callId))
            media.SetRemoteDescription(session.RemoteSdp);
        media.Start();
        _startedMedia[callId] = true;
    }

    // ── 群组每成员媒体（Mesh：每 PeerConnection 一个实例，键 "callId|memberUserId"） ──

    private static string PeerKey(string callId, long memberUserId)
        => string.Create(CultureInfo.InvariantCulture, $"{callId}|{memberUserId}");

    private static bool IsKeyForCall(string key, string callId)
        => key.StartsWith(callId, StringComparison.Ordinal)
            && key.Length > callId.Length
            && key[callId.Length] == '|';

    private ICallMediaSession? CreatePeerMedia(CallSession session, long memberUserId)
    {
        var key = PeerKey(session.CallId, memberUserId);
        if (_peerMedia.TryGetValue(key, out var existingMedia))
            return existingMedia;
        // 未设置对端工厂时回退按通话工厂逐成员调用（Mesh：每 PeerConnection 仍各一个实例）。
        var callMediaFactory = MediaFactory;
        var fallback = session.IsGroup && callMediaFactory is not null
            ? new Func<string, long, ICallMediaSession?>((cid, _) => callMediaFactory(cid))
            : null;
        var media = (PeerMediaFactory ?? fallback)?.Invoke(session.CallId, memberUserId);
        if (media is not null)
            _peerMedia[key] = media;
        return media;
    }

    private ICallMediaSession? EnsurePeerMedia(CallSession session, long memberUserId)
        => CreatePeerMedia(session, memberUserId);

    private ICallMediaSession? GetPeerMedia(string callId, long memberUserId)
        => _peerMedia.TryGetValue(PeerKey(callId, memberUserId), out var media) ? media : null;

    private ICallMediaSession? GetPeerMediaByKey(string key)
        => _peerMedia.TryGetValue(key, out var media) ? media : null;

    private void StartPeerMedia(string key, ICallMediaSession? media)
    {
        if (media is null || !_peerStarted.TryAdd(key, true))
            return;
        media.Start();
    }

    /// <summary>拆除单个成员的媒体与配套状态（成员离开语义：仅拆自身，不终结全会话）。</summary>
    private void DestroyPeerMedia(string callId, long memberUserId)
    {
        var key = PeerKey(callId, memberUserId);
        if (_peerMedia.TryRemove(key, out var media))
        {
            try
            {
                media.Stop();
            }
            finally
            {
                media.Dispose();
            }
        }
        _peerStarted.TryRemove(key, out _);
        _peerAppliedRemote.TryRemove(key, out _);
    }

    /// <summary>拆除某通话的全部成员媒体（会话终态收尾）。</summary>
    private void DestroyAllPeerMedia(string callId)
    {
        foreach (var key in _peerMedia.Keys)
        {
            if (!IsKeyForCall(key, callId))
                continue;
            if (_peerMedia.TryRemove(key, out var media))
            {
                try
                {
                    media.Stop();
                }
                finally
                {
                    media.Dispose();
                }
            }
            _peerStarted.TryRemove(key, out _);
            _peerAppliedRemote.TryRemove(key, out _);
        }
    }

    private CallGrantDto? FindGrant(string callId)
        => _grants.TryGetValue(callId, out var grant) ? grant : null;

    private void CompleteSession(CallSession session)
    {
        CancelTimeout(session.CallId);
        CallEnded?.Invoke(this, session);
        _sessions.TryRemove(session.CallId, out _);
        _startedMedia.TryRemove(session.CallId, out _);
        _appliedRemote.TryRemove(session.CallId, out _);
        _grants.TryRemove(session.CallId, out _);
        DestroyAllPeerMedia(session.CallId);
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
