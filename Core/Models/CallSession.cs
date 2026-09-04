using ChatApp.Shared.Protocol.Tcp;
using TcpCallSignalEvents = ChatApp.Shared.Protocol.Tcp.TcpCallConstants;

namespace Core.Models;

/// <summary>通话本地方向角色：主叫（发起方）或被叫（接听方）。</summary>
public enum CallRole
{
    Caller,
    Callee,
}

/// <summary>
/// 客户端单通话状态机（CALL-E2E-2；群组多方 GROUP-CALL-1）。
/// <para>
/// 覆盖 invite/ringing/accept/reject/cancel/end/timeout/reconnect 与多设备竞争：
/// 本地命令乐观迁移后由服务端响应权威覆盖；对端信令按 signal id 幂等去重；
/// 终态唯一——任何非 Ended 状态经合法迁移最终收敛到 Ended，一旦进入 Ended 不再迁出。
/// 纯状态机，不触网；迁移判定不依赖网络，最终态由服务端确认（authoritative）决定。
/// </para>
/// <para>
/// 群组（Mesh ≤4 人）语义为本文件加性扩展（<see cref="IsGroup"/> 守卫，1:1 行为零改动）：
/// 会话持有当前成员集合 <see cref="Participants"/>（随 participant-joined/left 增减，含本端）；
/// 无状态中继的成员信令（accept/reject/End→participant-left）驱动成员增减——成员离开仅拆自身，
/// 发起者离开（End）终结全会话；unknown 事件词容忍跳过（前向兼容）。
/// </para>
/// </summary>
public sealed class CallSession
{
    private const int MaxSeenSignalIds = 64;
    private readonly object _gate = new();
    private readonly HashSet<string> _seenSignalIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenSignalOrder = new();
    private readonly List<long> _participants = new();
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

    /// <summary>
    /// 群组（Mesh）通话会话构造（GROUP-CALL-1）。成员集合 = grant 签发名单的可见子集：
    /// 主叫侧初始 <see cref="Participants"/> 通常仅含发起者，成员随 accept/participant-joined 逐个加入。
    /// </summary>
    /// <param name="callId">通话 Id。</param>
    /// <param name="role">本地方向角色。</param>
    /// <param name="initiatorUserId">群组发起者（主叫）用户 Id。</param>
    /// <param name="participants">初始成员名单（含发起者；去重升序存储，≤4 人）。</param>
    /// <param name="initialState">初始状态。</param>
    /// <param name="initialRevision">初始服务端 revision。</param>
    public CallSession(
        string callId,
        CallRole role,
        long initiatorUserId,
        IReadOnlyList<long> participants,
        CallStateDto initialState = CallStateDto.Idle,
        long initialRevision = 0)
        : this(callId, role, ResolveGroupPeerUserId(initiatorUserId, participants), initialState, initialRevision)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (initiatorUserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(initiatorUserId));

        InitiatorUserId = initiatorUserId;
        IsGroup = true;
        foreach (var id in participants)
            AddParticipantUnsafe(id);
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

    // ── 群组（Mesh 阶段一）多方语义（GROUP-CALL-1；1:1 会话以下均为缺省值） ──

    /// <summary>是否群组（Mesh）通话。1:1 通话恒为 false（既有语义零改动）。</summary>
    public bool IsGroup { get; private set; }

    /// <summary>群组发起者（主叫）用户 Id。1:1 会话为 0。</summary>
    public long InitiatorUserId { get; private set; }

    /// <summary>
    /// 当前成员集合（升序、含本端、随 participant-joined/left 增减）。
    /// 1:1 会话为空集合。被叫侧初始为 [本端, 发起者]，其余成员随加入信令逐个出现。
    /// </summary>
    public IReadOnlyList<long> Participants
    {
        get
        {
            lock (_gate)
                return _participants.ToArray();
        }
    }

    /// <summary>当前成员数（群组 UI 呈现 "N 人通话"；1:1 为 0）。</summary>
    public int ParticipantCount
    {
        get
        {
            lock (_gate)
                return _participants.Count;
        }
    }

    /// <summary>成员加入（participant-joined / 成员 accept 证据）后触发。</summary>
    public event EventHandler<CallParticipantEventArgs>? ParticipantJoined;

    /// <summary>成员离开（participant-left / 成员 reject 证据）后触发；发起者离开走会话终态，不触发本事件。</summary>
    public event EventHandler<CallParticipantEventArgs>? ParticipantLeft;

    /// <summary>本端最近一次发出的 SDP（offer/answer/reconnect）。</summary>
    public string? LocalSdp { get; private set; }

    /// <summary>对端最近一次送达的 SDP（offer 或 answer）。</summary>
    public string? RemoteSdp { get; private set; }

    /// <summary>
    /// 下一条本地命令幂等键（同一 call 内唯一、单调递增）。
    /// 以本端角色作前缀，保证主叫/被叫两端的命令 id 不互相碰撞——否则被叫首条 Accept 的
    /// <c>{CallId}:c1</c> 会与主叫 Invite 的 <c>{CallId}:c1</c> 相同，被 Realtime 误判为幂等重放
    /// 而返回当前状态（Ringing）。角色在 1:1 通话内唯一标识参与方。
    /// </summary>
    public string NextCommandId()
    {
        lock (_gate)
        {
            _commandSeq++;
            var roleTag = Role == CallRole.Caller ? "A" : "B";
            return $"{CallId}:{roleTag}:c{_commandSeq}";
        }
    }

    /// <summary>
    /// 下一条本地命令 revision（单调递增，供服务端乱序/过期判定）。
    /// 以当前已确认的服务端权威 revision 为底（max(本地, 服务端) + 1），避免主叫/被叫各自的
    /// 本地计数器从同一起点起步而撞上服务端全局 revision——否则被叫首条 Accept、以及主叫在对方
    /// Accept/Reject 之后发出的命令会被服务端判为 RevisionStale（command.Revision &lt;= snapshot.Revision）。
    /// </summary>
    public long NextRevision()
    {
        lock (_gate)
        {
            var next = Math.Max(_localRevision, _serverRevision) + 1;
            _localRevision = next;
            return next;
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
    /// <para>
    /// 群组会话（<see cref="IsGroup"/>）另按成员语义处理：accept/reject 证据增减成员，
    /// participant-joined/left 事件驱动成员集合增减（发起者离开终结全会话），unknown 事件容忍跳过。
    /// </para>
    /// </summary>
    public bool ApplyRemoteSignal(CallSignalDto signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (string.IsNullOrWhiteSpace(signal.SignalId))
            return false;
        List<KeyValuePair<long, bool>>? pendingEvents = null;
        bool changed;
        lock (_gate)
        {
            if (!_seenSignalIds.Add(signal.SignalId))
                return false;
            EnqueueSignalId(signal.SignalId);
            if (IsTerminal)
                return false;

            // 对端信令携带服务端权威 revision：本端据此推进基准，
            // 使后续命令 revision 严格大于服务端全局 revision（见 NextRevision）。
            _serverRevision = Math.Max(_serverRevision, signal.Revision);

            if (!string.IsNullOrWhiteSpace(signal.Sdp))
                RemoteSdp = signal.Sdp;

            changed = IsGroup
                ? ApplyGroupSignalUnsafe(signal, ref pendingEvents)
                : ApplyDirectSignalUnsafe(signal);
        }
        RaiseParticipantEvents(pendingEvents);
        return changed;
    }

    /// <summary>1:1 既有信令迁移（原逻辑，群组路径见 <see cref="ApplyGroupSignalUnsafe"/>）。</summary>
    private bool ApplyDirectSignalUnsafe(CallSignalDto signal)
    {
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

    /// <summary>
    /// 群组信令迁移（无状态中继扇出语义，GROUP-CALL-1）：
    /// <list type="bullet">
    /// <item><c>participant-joined</c> 事件（带成员 Id）：成员加入；</item>
    /// <item><c>participant-left</c> 事件（带成员 Id）：成员离开；若离开者为发起者 → 全会话终态（发起者 end 终结通话）；</item>
    /// <item>Accept：主叫首个 accept 迁移 Active；成员加入（accept 即加入证据）；</item>
    /// <item>Reject / 无事件 End：成员离开（仅拆自身，不终结全会话）；发起者的 End 终结全会话；</item>
    /// <item>Cancel（发起者撤销）→ 被叫侧全会话终态；Invite（被叫首个）→ Ringing；</item>
    /// <item>unknown 事件值：容忍跳过（前向兼容，不迁移不报错）。</item>
    /// </list>
    /// </summary>
    private bool ApplyGroupSignalUnsafe(CallSignalDto signal, ref List<KeyValuePair<long, bool>>? pendingEvents)
    {
        if (!string.IsNullOrWhiteSpace(signal.Event))
        {
            if (string.Equals(signal.Event, TcpCallSignalEvents.SignalEventParticipantJoined, StringComparison.Ordinal))
            {
                var joined = signal.ParticipantUserId ?? signal.FromUserId;
                return TryAddParticipantUnsafe(joined, ref pendingEvents);
            }
            if (string.Equals(signal.Event, TcpCallSignalEvents.SignalEventParticipantLeft, StringComparison.Ordinal))
            {
                var left = signal.ParticipantUserId ?? signal.FromUserId;
                if (left == InitiatorUserId)
                {
                    // 发起者离开 = 发起者 end：按设计终结全会话（其余成员收到 call-ended 语义）。
                    TransitionToEndedUnsafe(CallEndReasonDto.HungUp);
                    return true;
                }
                return TryRemoveParticipantUnsafe(left, ref pendingEvents);
            }
            return false; // unknown 事件：容忍跳过。
        }

        switch (signal.Kind)
        {
            case CallCommandTypeDto.Accept:
            {
                var changed = false;
                if (Role == CallRole.Caller && State == CallStateDto.Ringing)
                {
                    State = CallStateDto.Active;
                    changed = true;
                }
                // 成员 accept 即加入证据（Mesh 扇出：所有成员都会看到）。
                return TryAddParticipantUnsafe(signal.FromUserId, ref pendingEvents) | changed;
            }
            case CallCommandTypeDto.Reject:
                // 成员拒绝仅自身离开；全会话由发起者 end/cancel 或超时终结。
                return TryRemoveParticipantUnsafe(signal.FromUserId, ref pendingEvents);
            case CallCommandTypeDto.Cancel:
                if (Role == CallRole.Callee && State is CallStateDto.Ringing or CallStateDto.Active)
                {
                    TransitionToEndedUnsafe(CallEndReasonDto.Cancelled);
                    return true;
                }
                return false;
            case CallCommandTypeDto.End:
                if (signal.FromUserId == InitiatorUserId)
                {
                    // 兼容无事件标注的发起者挂断中继：发起者 end 终结全会话。
                    if (State is CallStateDto.Ringing or CallStateDto.Active)
                    {
                        TransitionToEndedUnsafe(CallEndReasonDto.HungUp);
                        return true;
                    }
                    return false;
                }
                return TryRemoveParticipantUnsafe(signal.FromUserId, ref pendingEvents);
            case CallCommandTypeDto.Invite:
                if (State == CallStateDto.Idle)
                {
                    State = CallStateDto.Ringing;
                    return true;
                }
                return false;
            default:
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

    // ── 群组成员集合操作（GROUP-CALL-1） ──

    /// <summary>
    /// 成员加入（本地/管理器驱动的 participant-joined 路径）。仅群组会话有效；
    /// 名单已满（≤4）、成员已存在或已终态时拒绝。返回是否发生加入（触发 <see cref="ParticipantJoined"/>）。
    /// </summary>
    public bool TryAddParticipant(long userId)
    {
        List<KeyValuePair<long, bool>>? pendingEvents = null;
        bool added;
        lock (_gate)
        {
            added = TryAddParticipantUnsafe(userId, ref pendingEvents);
        }
        RaiseParticipantEvents(pendingEvents);
        return added;
    }

    /// <summary>
    /// 成员离开（本地/管理器驱动的 participant-left 路径）。仅群组会话有效；
    /// 发起者不可经此移除（发起者离开 = 会话终态）。返回是否发生移除（触发 <see cref="ParticipantLeft"/>）。
    /// </summary>
    public bool TryRemoveParticipant(long userId)
    {
        List<KeyValuePair<long, bool>>? pendingEvents = null;
        bool removed;
        lock (_gate)
        {
            removed = TryRemoveParticipantUnsafe(userId, ref pendingEvents);
        }
        RaiseParticipantEvents(pendingEvents);
        return removed;
    }

    /// <summary>
    /// 被叫会话晋升为群组（Mesh）：1:1 形态的被叫会话收到非主叫来源的成员信令（Mesh 扇出证据）时，
    /// 以既有主叫为发起者建立初始成员集合 [本端, 发起者]；随后该信令按群组语义继续应用。
    /// 仅可晋升一次；已群组或非被叫返回 false。
    /// </summary>
    public bool TryPromoteToGroup(long initiatorUserId, IReadOnlyList<long> initialParticipants)
    {
        ArgumentNullException.ThrowIfNull(initialParticipants);
        if (initiatorUserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(initiatorUserId));
        lock (_gate)
        {
            if (IsGroup || Role != CallRole.Callee || initiatorUserId != PeerUserId)
                return false;
            InitiatorUserId = initiatorUserId;
            IsGroup = true;
            foreach (var id in initialParticipants)
                AddParticipantUnsafe(id);
            return true;
        }
    }

    /// <summary>群组构造时解析对端 Id：升序名单中首个非发起者成员（主叫侧为"首个应答者"占位语义，与入参顺序无关）。</summary>
    private static long ResolveGroupPeerUserId(long initiatorUserId, IReadOnlyList<long> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        foreach (var id in participants.OrderBy(p => p))
        {
            if (id > 0 && id != initiatorUserId)
                return id;
        }
        throw new ArgumentOutOfRangeException(nameof(participants), "群组通话需要除发起者外的至少一名成员。");
    }

    private bool TryAddParticipantUnsafe(long userId, ref List<KeyValuePair<long, bool>>? pendingEvents)
    {
        if (!IsGroup || IsTerminal || userId <= 0 || _participants.Contains(userId))
            return false;
        if (_participants.Count >= TcpCallSignalEvents.MaxGroupCallParticipants)
            return false;
        InsertParticipantSortedUnsafe(userId);
        (pendingEvents ??= new List<KeyValuePair<long, bool>>()).Add(new(userId, true));
        return true;
    }

    private bool TryRemoveParticipantUnsafe(long userId, ref List<KeyValuePair<long, bool>>? pendingEvents)
    {
        if (!IsGroup || userId == InitiatorUserId || !_participants.Remove(userId))
            return false;
        (pendingEvents ??= new List<KeyValuePair<long, bool>>()).Add(new(userId, false));
        return true;
    }

    private void AddParticipantUnsafe(long userId)
    {
        if (IsGroup && userId > 0 && !_participants.Contains(userId)
            && _participants.Count < TcpCallSignalEvents.MaxGroupCallParticipants)
        {
            InsertParticipantSortedUnsafe(userId);
        }
    }

    private void InsertParticipantSortedUnsafe(long userId)
    {
        var index = _participants.FindIndex(p => p > userId);
        if (index < 0)
            _participants.Add(userId);
        else
            _participants.Insert(index, userId);
    }

    private void RaiseParticipantEvents(List<KeyValuePair<long, bool>>? pendingEvents)
    {
        if (pendingEvents is null)
            return;
        foreach (var (userId, joined) in pendingEvents)
        {
            var args = new CallParticipantEventArgs(this, userId);
            if (joined)
                ParticipantJoined?.Invoke(this, args);
            else
                ParticipantLeft?.Invoke(this, args);
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
