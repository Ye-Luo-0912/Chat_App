namespace Core.Models;

/// <summary>通话本地方向角色：主叫（发起方）或被叫（接听方）。</summary>
public enum CallRole
{
    Caller,
    Callee,
}

/// <summary>
/// 客户端单通话状态机（CALL-E2E-2）。
/// <para>
/// 覆盖 invite/ringing/accept/reject/cancel/end/timeout/reconnect 与多设备竞争：
/// 本地命令乐观迁移后由服务端响应权威覆盖；对端信令按 signal id 幂等去重；
/// 终态唯一——任何非 Ended 状态经合法迁移最终收敛到 Ended，一旦进入 Ended 不再迁出。
/// 纯状态机，不触网；迁移判定不依赖网络，最终态由服务端确认（authoritative）决定。
/// </para>
/// </summary>
public sealed class CallSession
{
    private const int MaxSeenSignalIds = 64;
    private readonly object _gate = new();
    private readonly HashSet<string> _seenSignalIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenSignalOrder = new();
    private long _commandSeq;
    private long _localRevision;
    private long _serverRevision;

    public CallSession(
        string callId,
        CallRole role,
        long peerUserId,
        CallStateDto initialState = CallStateDto.Idle,
        long initialRevision = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        if (peerUserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(peerUserId));
        CallId = callId;
        Role = role;
        PeerUserId = peerUserId;
        State = initialState;
        _serverRevision = Math.Max(0, initialRevision);
    }

    /// <summary>通话 Id（信令平面唯一键）。</summary>
    public string CallId { get; }

    /// <summary>本地方向角色。</summary>
    public CallRole Role { get; }

    /// <summary>对端用户 Id（主叫视角为被叫，被叫视角为主叫）。</summary>
    public long PeerUserId { get; }

    /// <summary>当前状态（乐观本地视图，由服务端响应权威覆盖）。</summary>
    public CallStateDto State { get; private set; }

    /// <summary>终态原因（仅审计与展示，不参与迁移判定）。</summary>
    public CallEndReasonDto EndReason { get; private set; } = CallEndReasonDto.None;

    /// <summary>服务端确认过的状态机 revision（authoritative，仅由命令响应更新）。</summary>
    public long Revision => _serverRevision;

    /// <summary>是否收到过服务端状态确认。</summary>
    public bool ServerConfirmed { get; private set; }

    /// <summary>是否终态（Ended）。</summary>
    public bool IsTerminal => State == CallStateDto.Ended;

    /// <summary>本端最近一次发出的 SDP（offer/answer/reconnect）。</summary>
    public string? LocalSdp { get; private set; }

    /// <summary>对端最近一次送达的 SDP（offer 或 answer）。</summary>
    public string? RemoteSdp { get; private set; }

    /// <summary>下一条本地命令幂等键（同一 call 内唯一、单调递增）。</summary>
    public string NextCommandId()
    {
        lock (_gate)
        {
            _commandSeq++;
            return $"{CallId}:c{_commandSeq}";
        }
    }

    /// <summary>下一条本地命令 revision（单调递增，供服务端乱序/过期判定）。</summary>
    public long NextRevision()
    {
        lock (_gate)
        {
            _localRevision++;
            return _localRevision;
        }
    }

    /// <summary>
    /// 本地乐观迁移判定与执行。返回 false 表示当前状态不允许该命令（fail-closed，不上行）。
    /// 终态后一律拒绝；Reconnect 仅在 Active 允许；Invite 仅主叫从 Idle 发起。
    /// </summary>
    public bool TryApplyLocalCommand(CallCommandTypeDto type)
    {
        lock (_gate)
        {
            if (IsTerminal)
                return false;
            switch (type)
            {
                case CallCommandTypeDto.Invite:
                    if (State != CallStateDto.Idle)
                        return false;
                    State = CallStateDto.Ringing;
                    return true;
                case CallCommandTypeDto.Ringing:
                    return Role == CallRole.Callee && State == CallStateDto.Ringing;
                case CallCommandTypeDto.Accept:
                    if (Role != CallRole.Callee || State != CallStateDto.Ringing)
                        return false;
                    State = CallStateDto.Active;
                    return true;
                case CallCommandTypeDto.Reject:
                    if (Role != CallRole.Callee || State != CallStateDto.Ringing)
                        return false;
                    TransitionToEndedUnsafe(CallEndReasonDto.Rejected);
                    return true;
                case CallCommandTypeDto.Cancel:
                    if (Role != CallRole.Caller || State != CallStateDto.Ringing)
                        return false;
                    TransitionToEndedUnsafe(CallEndReasonDto.Cancelled);
                    return true;
                case CallCommandTypeDto.End:
                    if (State is not (CallStateDto.Ringing or CallStateDto.Active))
                        return false;
                    TransitionToEndedUnsafe(CallEndReasonDto.HungUp);
                    return true;
                case CallCommandTypeDto.Reconnect:
                    return State == CallStateDto.Active;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// 应用服务端确认的状态（authoritative）。覆盖本地乐观视图；终态不可逆，
    /// 已终态后不再改写状态或原因。返回是否发生可见状态变化（含首次进入 Ended）。
    /// </summary>
    public bool ApplyServerState(CallCommandResponseDto response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            ServerConfirmed = true;
            _serverRevision = Math.Max(_serverRevision, response.Revision);
            if (IsTerminal)
                return false;
            if (response.State == CallStateDto.Ended)
            {
                TransitionToEndedUnsafe(
                    response.EndReason == CallEndReasonDto.None ? CallEndReasonDto.HungUp : response.EndReason);
                return true;
            }
            if (response.State != State)
            {
                State = response.State;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 应用对端信令（S2C push）。按 signal id 幂等去重（重复/乱序信令忽略）；
    /// 终态后忽略；接受/拒绝/取消/挂断类信令驱动本端终态。返回是否发生可见状态变化。
    /// </summary>
    public bool ApplyRemoteSignal(CallSignalDto signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (string.IsNullOrWhiteSpace(signal.SignalId))
            return false;
        lock (_gate)
        {
            if (!_seenSignalIds.Add(signal.SignalId))
                return false;
            EnqueueSignalId(signal.SignalId);
            if (IsTerminal)
                return false;

            if (!string.IsNullOrWhiteSpace(signal.Sdp))
                RemoteSdp = signal.Sdp;

            switch (signal.Kind)
            {
                case CallCommandTypeDto.Accept:
                    if (Role == CallRole.Caller && State == CallStateDto.Ringing)
                    {
                        State = CallStateDto.Active;
                        return true;
                    }
                    break;
                case CallCommandTypeDto.Reject:
                    if (Role == CallRole.Caller && State == CallStateDto.Ringing)
                    {
                        TransitionToEndedUnsafe(CallEndReasonDto.Rejected);
                        return true;
                    }
                    break;
                case CallCommandTypeDto.Cancel:
                    if (Role == CallRole.Callee && State == CallStateDto.Ringing)
                    {
                        TransitionToEndedUnsafe(CallEndReasonDto.Cancelled);
                        return true;
                    }
                    break;
                case CallCommandTypeDto.End:
                    if (State is CallStateDto.Ringing or CallStateDto.Active)
                    {
                        TransitionToEndedUnsafe(CallEndReasonDto.HungUp);
                        return true;
                    }
                    break;
                case CallCommandTypeDto.Invite:
                    if (State == CallStateDto.Idle)
                    {
                        State = CallStateDto.Ringing;
                        return true;
                    }
                    break;
                default:
                    break;
            }
            return false;
        }
    }

    /// <summary>记录本端发出的 SDP（offer/answer/reconnect）。</summary>
    public void SetLocalSdp(string? sdp)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(sdp))
                LocalSdp = sdp;
        }
    }

    /// <summary>本地超时/终端路径强制收尾（终态唯一：已终态则忽略）。</summary>
    public void ForceEnd(CallEndReasonDto reason)
    {
        lock (_gate)
        {
            if (IsTerminal)
                return;
            TransitionToEndedUnsafe(reason);
        }
    }

    /// <summary>覆盖终态展示原因（仅本地展示/审计，不参与迁移；仅在终态时生效）。</summary>
    public void OverrideEndReason(CallEndReasonDto reason)
    {
        lock (_gate)
        {
            if (IsTerminal && reason != CallEndReasonDto.None)
                EndReason = reason;
        }
    }

    private void TransitionToEndedUnsafe(CallEndReasonDto reason)
    {
        State = CallStateDto.Ended;
        EndReason = reason;
    }

    private void EnqueueSignalId(string signalId)
    {
        _seenSignalOrder.Enqueue(signalId);
        while (_seenSignalOrder.Count > MaxSeenSignalIds)
            _seenSignalIds.Remove(_seenSignalOrder.Dequeue());
    }
}
