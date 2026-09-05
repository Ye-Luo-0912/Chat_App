using System.Buffers;
using Chat_App.Infrastructure.Serialization;
using ChatApp.Shared.Protocol.Tcp;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using Xunit;
using TcpCallSignalEvents = ChatApp.Shared.Protocol.Tcp.TcpCallConstants;

namespace IntegrationTests;

/// <summary>
/// GROUP-CALL-1 群组（Mesh ≤4 人）客户端三方信令端到端验证（over 真实 wire）。
/// <para>
/// 三设备 harness：主叫/两名被叫各持有一个真实 <see cref="ChatSessionClient"/>（协议编解码、
/// camelCase wire、RequestId 配对、CallSignal S2C push 全走真实实现），内存"群组中继"复刻
/// Gateway <c>GroupCallSignalRelay</c> 的无状态扇出语义：群组命令按参与者名单扇出到其余成员
/// （排除发起者），成员 End 映射为 participant-left 事件（带离开者 Id），响应回显提示状态。
/// 三端各挂一个 <see cref="CallSessionManager"/>，验证：发起者逐成员 invite（逐成员 offer）→
/// 成员逐个 accept 加入（主叫侧逐成员 answer 收口，每成员一个媒体实例）→ 成员离开仅拆自身 →
/// 发起者 end 终结全会话。
/// </para>
/// </summary>
/// <remarks>
/// grant 鉴权（HMAC/成员资格）由 Gateway 侧测试覆盖；本 harness 的中继按测试名单扇出，
/// 聚焦客户端多方可视状态收敛与媒体编排。
/// </remarks>
public sealed class GroupCallSessionE2ETests
{
    private const long CallerUserId = 9001;
    private const long CalleeBUserId = 9002;
    private const long CalleeCUserId = 9003;
    private const string CallId = "group-e2e-1";
    private static readonly long[] Roster = [CallerUserId, CalleeBUserId, CalleeCUserId];

    [Fact]
    public async Task Full3PartyGroupFlow_InvitesAcceptsLeaveInitiatorEnd_OverRealWire()
    {
        var serializer = new JsonPacketBodySerializer();
        var tcpByUser = new Dictionary<long, ScriptedTcpClient>();
        foreach (var id in Roster)
        {
            var tcp = new ScriptedTcpClient();
            tcpByUser[id] = tcp;
            SetupAutoAuth(tcp, serializer, id);
        }

        // 内存群组中继：任一端发出的 CallCommandRequest → 权威响应回发送方 + 按名单扇出 CallSignal。
        foreach (var (userId, tcp) in tcpByUser)
            WireGroupRelay(userId, tcp, tcpByUser, serializer, Roster);

        using var clientA = new ChatSessionClient(tcpByUser[CallerUserId], new MessagePacketCodec(), serializer);
        using var clientB = new ChatSessionClient(tcpByUser[CalleeBUserId], new MessagePacketCodec(), serializer);
        using var clientC = new ChatSessionClient(tcpByUser[CalleeCUserId], new MessagePacketCodec(), serializer);
        await ConnectAndAuthenticateAsync(clientA, tcpByUser[CallerUserId], CallerUserId);
        await ConnectAndAuthenticateAsync(clientB, tcpByUser[CalleeBUserId], CalleeBUserId);
        await ConnectAndAuthenticateAsync(clientC, tcpByUser[CalleeCUserId], CalleeCUserId);

        using var callerManager = new CallSessionManager(clientA, new StubUserContext(CallerUserId));
        using var calleeBManager = new CallSessionManager(clientB, new StubUserContext(CalleeBUserId));
        using var calleeCManager = new CallSessionManager(clientC, new StubUserContext(CalleeCUserId));

        // ── 主叫：每成员一个媒体实例（逐成员 offer）；被叫：通话级媒体实例（逐成员独立 answer 文本）。 ──
        var callerMediaByPeer = new Dictionary<long, FakeMediaSession>();
        callerManager.PeerMediaFactory = (_, peerUserId) =>
        {
            var media = new FakeMediaSession { Offer = $"offer-for-{peerUserId}", Answer = $"caller-answer-{peerUserId}" };
            callerMediaByPeer[peerUserId] = media;
            return media;
        };
        var mediaB = new FakeMediaSession { Offer = "offer-for-9002", Answer = "answer-from-b" };
        var mediaC = new FakeMediaSession { Offer = "offer-for-9003", Answer = "answer-from-c" };
        calleeBManager.MediaFactory = _ => mediaB;
        calleeCManager.MediaFactory = _ => mediaC;

        var incomingTcsB = new TaskCompletionSource<CallSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var incomingTcsC = new TaskCompletionSource<CallSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        calleeBManager.IncomingCall += (_, s) => incomingTcsB.TrySetResult(s);
        calleeCManager.IncomingCall += (_, s) => incomingTcsC.TrySetResult(s);

        // ── 主叫发起群组通话：逐成员 invite（grant 名单扇出）──
        var grant = NewGroupGrant();
        var callerSession = await callerManager.StartGroupCallAsync(CallId, grant);

        Assert.Equal(CallStateDto.Ringing, callerSession.State);
        Assert.True(callerSession.IsGroup);
        Assert.Equal(Roster, callerSession.Participants);
        Assert.Equal(2, callerMediaByPeer.Count); // 每成员一个媒体实例

        // ── 两名被叫经真实 wire 收到来电（首个 invite 建会话，重复 invite 幂等）──
        var sessionB = await incomingTcsB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sessionC = await incomingTcsC.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CallRole.Callee, sessionB.Role);
        Assert.Equal(CallerUserId, sessionB.PeerUserId);
        Assert.Equal(CallStateDto.Ringing, sessionB.State);
        Assert.False(string.IsNullOrWhiteSpace(sessionB.RemoteSdp));

        // GROUP-CALL-SDP-1 回归：逐成员 invite 广播到全部成员，但每个被叫只应用发给
        // 自己的 offer（invite 信号携带目标成员 Id，非目标 invite 被过滤，不覆盖 RemoteSdp）。
        Assert.Equal("offer-for-9002", sessionB.RemoteSdp);
        Assert.Equal("offer-for-9003", sessionC.RemoteSdp);

        // ── 成员 B 接听 → B Active；主叫首个 accept 转 Active 并收口 B 的 answer ──
        await calleeBManager.AcceptAsync(CallId, sdpAnswer: mediaB.Answer);
        Assert.Equal(CallStateDto.Active, sessionB.State);
        await WaitUntilAsync(() => callerSession.State == CallStateDto.Active);
        Assert.Equal(1, callerMediaByPeer[CalleeBUserId].StartCalls);
        Assert.Contains("answer-from-b", callerMediaByPeer[CalleeBUserId].SetRemoteSdps);

        // 成员 C 收到 B 的 accept 扇出：晋升群组并记录成员加入（尚未接听）。
        await WaitUntilAsync(() => sessionC.IsGroup && sessionC.ParticipantCount == 3);
        Assert.Equal(CallStateDto.Ringing, sessionC.State);
        Assert.Equal(Roster, sessionC.Participants);

        // ── 成员 C 接听 → C Active；主叫收口 C 的 answer 到 C 的实例 ──
        await calleeCManager.AcceptAsync(CallId, sdpAnswer: mediaC.Answer);
        Assert.Equal(CallStateDto.Active, sessionC.State);
        await WaitUntilAsync(() => callerMediaByPeer[CalleeCUserId].StartCalls == 1);
        Assert.Contains("answer-from-c", callerMediaByPeer[CalleeCUserId].SetRemoteSdps);
        Assert.Equal(1, callerMediaByPeer[CalleeBUserId].StartCalls); // B 实例不受影响

        // B 侧收到 C 的 accept 扇出：成员加入记录（名单收敛）。
        await WaitUntilAsync(() => sessionB.IsGroup && sessionB.ParticipantCount == 3);
        Assert.Equal(Roster, sessionB.Participants);

        // ── 成员 B 主动离开：End → participant-left；仅拆自身 ──
        await calleeBManager.EndAsync(CallId);
        Assert.True(sessionB.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, sessionB.EndReason);
        Assert.Empty(calleeBManager.ActiveCalls);

        await WaitUntilAsync(() => callerSession.ParticipantCount == 2);
        Assert.Equal(new[] { CallerUserId, CalleeCUserId }, callerSession.Participants);
        Assert.False(callerSession.IsTerminal); // 成员离开不终结全会话
        Assert.Equal(1, callerMediaByPeer[CalleeBUserId].StopCalls); // B 媒体被拆除
        Assert.Equal(0, callerMediaByPeer[CalleeCUserId].StopCalls); // C 媒体不受影响

        await WaitUntilAsync(() => sessionC.ParticipantCount == 2);
        Assert.Equal(new[] { CallerUserId, CalleeCUserId }, sessionC.Participants);
        Assert.False(sessionC.IsTerminal);

        // ── 发起者挂断：全会话终结（其余成员收到 call-ended 语义的 participant-left）──
        await callerManager.EndAsync(CallId);
        Assert.True(callerSession.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, callerSession.EndReason);
        Assert.Empty(callerManager.ActiveCalls);
        Assert.Equal(1, callerMediaByPeer[CalleeCUserId].StopCalls);

        await WaitUntilAsync(() => sessionC.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, sessionC.EndReason);
        Assert.Empty(calleeCManager.ActiveCalls);
    }

    [Fact]
    public async Task InitiatorEnd_BeforeAnyAccept_CalleesConvergeToEnded()
    {
        var serializer = new JsonPacketBodySerializer();
        var tcpByUser = new Dictionary<long, ScriptedTcpClient>();
        foreach (var id in Roster)
        {
            var tcp = new ScriptedTcpClient();
            tcpByUser[id] = tcp;
            SetupAutoAuth(tcp, serializer, id);
        }
        foreach (var (userId, tcp) in tcpByUser)
            WireGroupRelay(userId, tcp, tcpByUser, serializer, Roster);

        using var clientA = new ChatSessionClient(tcpByUser[CallerUserId], new MessagePacketCodec(), serializer);
        using var clientB = new ChatSessionClient(tcpByUser[CalleeBUserId], new MessagePacketCodec(), serializer);
        using var clientC = new ChatSessionClient(tcpByUser[CalleeCUserId], new MessagePacketCodec(), serializer);
        await ConnectAndAuthenticateAsync(clientA, tcpByUser[CallerUserId], CallerUserId);
        await ConnectAndAuthenticateAsync(clientB, tcpByUser[CalleeBUserId], CalleeBUserId);
        await ConnectAndAuthenticateAsync(clientC, tcpByUser[CalleeCUserId], CalleeCUserId);

        using var callerManager = new CallSessionManager(clientA, new StubUserContext(CallerUserId));
        using var calleeBManager = new CallSessionManager(clientB, new StubUserContext(CalleeBUserId));
        using var calleeCManager = new CallSessionManager(clientC, new StubUserContext(CalleeCUserId));

        var incomingTcsB = new TaskCompletionSource<CallSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var incomingTcsC = new TaskCompletionSource<CallSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        calleeBManager.IncomingCall += (_, s) => incomingTcsB.TrySetResult(s);
        calleeCManager.IncomingCall += (_, s) => incomingTcsC.TrySetResult(s);

        var callerSession = await callerManager.StartGroupCallAsync(CallId, NewGroupGrant());
        var sessionB = await incomingTcsB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sessionC = await incomingTcsC.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 发起者在无人接听时撤销（Cancel）：按名单扇出，两被叫收敛 Cancelled。
        await callerManager.CancelAsync(CallId);

        Assert.True(callerSession.IsTerminal);
        Assert.Equal(CallEndReasonDto.Cancelled, callerSession.EndReason);
        await WaitUntilAsync(() => sessionB.IsTerminal && sessionC.IsTerminal);
        Assert.Equal(CallEndReasonDto.Cancelled, sessionB.EndReason);
        Assert.Equal(CallEndReasonDto.Cancelled, sessionC.EndReason);
        Assert.Empty(callerManager.ActiveCalls);
        Assert.Empty(calleeBManager.ActiveCalls);
        Assert.Empty(calleeCManager.ActiveCalls);
    }

    [Fact]
    public async Task MidJoin_FourthMember_SameCallIdResign_ExistingSessionsUninterrupted()
    {
        // GROUP-CALL-MIDJOIN-1 客户端回归：三方建会后第 4 人经"同 CallId 重签 + InviteMemberAsync"
        // 中途加入——invite 携带目标成员（B/C 过滤），新成员经 invite 下发的 grant 直接 accept；
        // 既有三方会话全程不中断（同一会话对象持续 Active，无新批次/房间迁移）。
        const long MemberDUserId = 9004;
        long[] fourRoster = [CallerUserId, CalleeBUserId, CalleeCUserId, MemberDUserId];

        var serializer = new JsonPacketBodySerializer();
        var tcpByUser = new Dictionary<long, ScriptedTcpClient>();
        foreach (var id in fourRoster)
        {
            var tcp = new ScriptedTcpClient();
            tcpByUser[id] = tcp;
            SetupAutoAuth(tcp, serializer, id);
        }
        foreach (var (userId, tcp) in tcpByUser)
            WireGroupRelay(userId, tcp, tcpByUser, serializer, fourRoster);

        using var clientA = new ChatSessionClient(tcpByUser[CallerUserId], new MessagePacketCodec(), serializer);
        using var clientB = new ChatSessionClient(tcpByUser[CalleeBUserId], new MessagePacketCodec(), serializer);
        using var clientC = new ChatSessionClient(tcpByUser[CalleeCUserId], new MessagePacketCodec(), serializer);
        using var clientD = new ChatSessionClient(tcpByUser[MemberDUserId], new MessagePacketCodec(), serializer);
        await ConnectAndAuthenticateAsync(clientA, tcpByUser[CallerUserId], CallerUserId);
        await ConnectAndAuthenticateAsync(clientB, tcpByUser[CalleeBUserId], CalleeBUserId);
        await ConnectAndAuthenticateAsync(clientC, tcpByUser[CalleeCUserId], CalleeCUserId);
        await ConnectAndAuthenticateAsync(clientD, tcpByUser[MemberDUserId], MemberDUserId);

        using var callerManager = new CallSessionManager(clientA, new StubUserContext(CallerUserId));
        using var calleeBManager = new CallSessionManager(clientB, new StubUserContext(CalleeBUserId));
        using var calleeCManager = new CallSessionManager(clientC, new StubUserContext(CalleeCUserId));
        using var calleeDManager = new CallSessionManager(clientD, new StubUserContext(MemberDUserId));

        var callerMediaByPeer = new Dictionary<long, FakeMediaSession>();
        callerManager.PeerMediaFactory = (_, peerUserId) =>
        {
            var media = new FakeMediaSession
            {
                Offer = $"offer-for-{peerUserId}",
                Answer = $"caller-answer-{peerUserId}"
            };
            callerMediaByPeer[peerUserId] = media;
            return media;
        };
        var mediaD = new FakeMediaSession { Answer = "answer-from-d" };
        calleeDManager.MediaFactory = _ => mediaD;

        var incomingTcsB = new TaskCompletionSource<CallSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var incomingTcsC = new TaskCompletionSource<CallSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var incomingTcsD = new TaskCompletionSource<CallSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        calleeBManager.IncomingCall += (_, s) => incomingTcsB.TrySetResult(s);
        calleeCManager.IncomingCall += (_, s) => incomingTcsC.TrySetResult(s);
        calleeDManager.IncomingCall += (_, s) => incomingTcsD.TrySetResult(s);

        // 发起者上行 invite / 新成员上行 End 命令记录（经真实 wire 帧观测）。
        var callerInvites = new List<CallCommandRequestDto>();
        var memberDEnds = new List<CallCommandRequestDto>();
        tcpByUser[CallerUserId].OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.CallCommandRequest)
                return;
            var req = serializer.Deserialize<CallCommandRequestDto>(new ReadOnlySequence<byte>(body));
            if (req is { Type: CallCommandTypeDto.Invite })
                callerInvites.Add(req);
        };
        tcpByUser[MemberDUserId].OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.CallCommandRequest)
                return;
            var req = serializer.Deserialize<CallCommandRequestDto>(new ReadOnlySequence<byte>(body));
            if (req is { Type: CallCommandTypeDto.End })
                memberDEnds.Add(req);
        };

        // ── 三方建会（同 Full3PartyGroupFlow）──
        var grant3 = NewGroupGrant();
        var callerSession = await callerManager.StartGroupCallAsync(CallId, grant3);
        var sessionB = await incomingTcsB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sessionC = await incomingTcsC.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await calleeBManager.AcceptAsync(CallId, sdpAnswer: "answer-from-b");
        await WaitUntilAsync(() => callerSession.State == CallStateDto.Active);
        await calleeCManager.AcceptAsync(CallId, sdpAnswer: "answer-from-c");
        await WaitUntilAsync(() => callerMediaByPeer[CalleeCUserId].StartCalls == 1);
        await WaitUntilAsync(() => sessionB.ParticipantCount == 3 && sessionC.ParticipantCount == 3);

        var invitesBeforeMidJoin = callerInvites.Count;

        // ── 中途加人：同 CallId 重签（名单+第 4 人）→ InviteMemberAsync ──
        var grant4 = new CallGrantDto
        {
            CallId = CallId, // 同 CallId 重签：同一通话持续，不迁移房间
            CallerUserId = CallerUserId,
            CalleeUserId = 0,
            ExpiresAtMs = 1_900_000_100_000L,
            Nonce = "nonce-midjoin-4",
            Signature = "sig-midjoin-4",
            CallKind = TcpCallKind.Group,
            Participants = fourRoster,
        };
        await callerManager.InviteMemberAsync(CallId, MemberDUserId, grant4);

        // invite 命令携带目标成员与重签 grant（GROUP-CALL-SDP-1 / MIDJOIN-1）。
        // 注：观测帧经真实 wire 序列化，grant 为解码副本——按字段断言同一批次。
        var midJoinInvite = callerInvites.Last();
        Assert.Equal(invitesBeforeMidJoin + 1, callerInvites.Count);
        Assert.Equal(MemberDUserId, midJoinInvite.ParticipantUserId);
        Assert.Equal(grant4.CallId, midJoinInvite.Grant!.CallId);
        Assert.Equal(grant4.Nonce, midJoinInvite.Grant.Nonce);
        Assert.Equal(grant4.Signature, midJoinInvite.Grant.Signature);
        Assert.Equal(fourRoster, midJoinInvite.Grant.Participants);
        // 命令 Id 角色段 = 发起者用户 Id（GROUP-CALL-CMDID-1）。
        Assert.Contains(":u" + CallerUserId + ":c", midJoinInvite.CommandId, StringComparison.Ordinal);

        // B/C 收到发给他人的 invite：不建会话、不污染成员集合（仍 3 人直到 D accept）。
        Assert.Equal(3, sessionB.ParticipantCount);
        Assert.Equal(3, sessionC.ParticipantCount);

        // 新成员 D：收到目标为自己的 invite（携带 grant）→ 群组会话 + accept。
        var sessionD = await incomingTcsD.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sessionD.IsGroup);
        Assert.Equal(fourRoster, sessionD.Participants);
        Assert.Equal("offer-for-9004", sessionD.RemoteSdp);
        await calleeDManager.AcceptAsync(CallId, sdpAnswer: mediaD.Answer);

        // ── 四端收敛：成员集合四人；既有三方会话同一对象、全程 Active 不中断 ──
        await WaitUntilAsync(() =>
            callerSession.ParticipantCount == 4 && sessionB.ParticipantCount == 4
            && sessionC.ParticipantCount == 4 && sessionD.ParticipantCount == 4);
        Assert.Equal(fourRoster, callerSession.Participants);
        Assert.Equal(fourRoster, sessionB.Participants);
        Assert.Equal(fourRoster, sessionC.Participants);
        Assert.False(callerSession.IsTerminal);
        Assert.False(sessionB.IsTerminal);
        Assert.False(sessionC.IsTerminal);
        Assert.Equal(CallStateDto.Active, sessionB.State);
        Assert.Equal(CallStateDto.Active, sessionC.State);
        Assert.Equal(1, callerMediaByPeer[MemberDUserId].StartCalls);
        Assert.Contains("answer-from-d", callerMediaByPeer[MemberDUserId].SetRemoteSdps);

        // 中期加人后的成员命令携带重签 grant（缓存已换新批次；观测帧为解码副本，按字段断言）。
        await calleeDManager.EndAsync(CallId);
        var dEnd = memberDEnds.Last();
        Assert.Equal(grant4.Nonce, dEnd.Grant!.Nonce);
        Assert.Equal(grant4.Signature, dEnd.Grant.Signature);
        await WaitUntilAsync(() =>
            callerSession.ParticipantCount == 3 && sessionB.ParticipantCount == 3
            && sessionC.ParticipantCount == 3 && sessionD.IsTerminal);
        Assert.False(callerSession.IsTerminal, "新成员离开仅拆自身，既有三方继续");
    }

    // ── 内存群组中继：CallCommandRequest → 权威响应回发送方 + 按名单扇出 CallSignal ──

    private static void WireGroupRelay(
        long selfUserId,
        ScriptedTcpClient selfTcp,
        IReadOnlyDictionary<long, ScriptedTcpClient> tcpByUser,
        JsonPacketBodySerializer serializer,
        IReadOnlyList<long> roster)
    {
        selfTcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.CallCommandRequest)
                return;
            var req = serializer.Deserialize<CallCommandRequestDto>(new ReadOnlySequence<byte>(body));
            if (req is null || string.IsNullOrWhiteSpace(req.RequestId))
                return;

            // 1) 无状态中继的提示状态回显（与 GroupCallSignalRelay.ResolveRelayState 一致）。
            InjectPacket(selfTcp, serializer, PacketCommand.CallCommandResponse, new CallCommandResponseDto
            {
                RequestId = req.RequestId,
                CallId = req.CallId,
                Succeeded = true,
                State = req.Type switch
                {
                    CallCommandTypeDto.Invite or CallCommandTypeDto.Ringing => CallStateDto.Ringing,
                    CallCommandTypeDto.Accept or CallCommandTypeDto.Reconnect => CallStateDto.Active,
                    CallCommandTypeDto.Reject => CallStateDto.Ended,
                    CallCommandTypeDto.Cancel => CallStateDto.Ended,
                    CallCommandTypeDto.End => CallStateDto.Ended,
                    _ => CallStateDto.Idle,
                },
                EndReason = req.Type switch
                {
                    CallCommandTypeDto.Reject => CallEndReasonDto.Rejected,
                    CallCommandTypeDto.Cancel => CallEndReasonDto.Cancelled,
                    CallCommandTypeDto.End => CallEndReasonDto.HungUp,
                    _ => CallEndReasonDto.None,
                },
                Revision = req.Revision,
            });

            // 2) 按名单扇出到其余成员（排除发起者）；成员 End 映射为 participant-left 事件。
            //    0.5.8 真实语义：invite 信号携带目标成员 Id 与随信令下发的 grant（GROUP-CALL-SDP-1
            //    / GAP-1），与 Gateway GroupCallSignalRelay 一致。
            foreach (var recipient in roster)
            {
                if (recipient == selfUserId || !tcpByUser.TryGetValue(recipient, out var recipientTcp))
                    continue;
                var isMemberLeave = req.Type == CallCommandTypeDto.End;
                InjectPacket(recipientTcp, serializer, PacketCommand.CallSignal, new CallSignalDto
                {
                    // 幂等去重键：同一命令（含重放）对同一成员产生稳定 SignalId。
                    SignalId = $"{req.CommandId}:{recipient}",
                    CallId = req.CallId,
                    FromUserId = selfUserId,
                    ToUserId = recipient,
                    Kind = req.Type,
                    Sdp = req.Sdp ?? string.Empty,
                    Revision = req.Revision,
                    OccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Event = isMemberLeave ? TcpCallSignalEvents.SignalEventParticipantLeft : null,
                    ParticipantUserId = req.Type switch
                    {
                        CallCommandTypeDto.End => selfUserId,
                        CallCommandTypeDto.Invite => req.ParticipantUserId,
                        _ => null,
                    },
                    Grant = req.Type == CallCommandTypeDto.Invite ? req.Grant : null,
                });
            }
        };
    }

    private static CallGrantDto NewGroupGrant() => new()
    {
        CallId = CallId,
        CallerUserId = CallerUserId,
        CalleeUserId = 0,
        ExpiresAtMs = 1_900_000_000_000L,
        Nonce = "nonce-group-e2e",
        Signature = "sig-group-e2e",
        CallKind = TcpCallKind.Group,
        Participants = Roster,
    };

    // ── 测试底座（与 CallSessionManagerE2ETests 同模式） ──

    private static void SetupAutoAuth(ScriptedTcpClient tcp, JsonPacketBodySerializer serializer, long userId)
    {
        tcp.OnFrameSent += (cmd, _) =>
        {
            if (cmd == PacketCommand.AuthenticationRequest)
                InjectPacket(tcp, serializer, PacketCommand.AuthenticationResponse,
                    new AuthResponseDto { Success = true, UserId = userId });
        };
    }

    private static async Task ConnectAndAuthenticateAsync(
        ChatSessionClient client, ScriptedTcpClient tcp, long userId)
    {
        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await client.AuthenticateAsync("token", userId, null, null);
        Assert.True(client.IsAuthenticated);
        Assert.True(client.SupportsCallSignaling, "握手应协商到 CallSignaling 能力位");
    }

    private static void InjectPacket<T>(
        ScriptedTcpClient tcp, IPacketBodySerializer serializer, PacketCommand command, T? payload)
    {
        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        serializer.Serialize(writer, payload);
        var bodyLen = writer.WrittenCount;
        var packet = new MessagePacket(command,
            bodyLen == 0 ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
        var frameWriter = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + bodyLen);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
        tcp.InjectData(frameWriter.WrittenMemory);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("等待条件超时");
            await Task.Delay(10);
        }
    }

    // ── 测试替身 ──

    private sealed class StubUserContext(long userId) : ICurrentUserContext
    {
        public long? UserId => userId;
        public long Generation => 1;
        public string? UserName => $"user-{userId}";
        public bool IsAuthenticated => userId > 0;
        public bool HasUserId => userId > 0;
        public UserSessionSnapshot Snapshot => new(userId, 1, UserName, null, null);
        public long RequireUserId() => userId;
        public bool TryGetUserId(out long id)
        {
            id = userId;
            return userId > 0;
        }
    }

    private sealed class FakeMediaSession : ICallMediaSession
    {
        public string CallId { get; set; } = string.Empty;
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int SetRemoteCalls { get; private set; }
        public List<string> SetRemoteSdps { get; } = new();
        public string Offer { get; set; } = "v=0 offer";
        public string Answer { get; set; } = "v=0 answer";
        public string RestartOffer { get; set; } = "v=0 restart";

        public event EventHandler<CallMediaStateChangedEventArgs>? StateChanged;

        public string CreateOffer() => Offer;
        public string CreateAnswer() => Answer;
        public void SetRemoteDescription(string sdp) { SetRemoteCalls++; SetRemoteSdps.Add(sdp); }
        public void ApplyIceCandidate(string candidate) { }
        public void Start() => StartCalls++;
        public void Stop() => StopCalls++;
        public string RestartIce() => RestartOffer;
        public void Dispose() { }
    }

    /// <summary>可控的假 TCP 客户端：解析每次发送的帧，回显握手/鉴权，暴露帧观测与注入。</summary>
    private sealed class ScriptedTcpClient : ITcpClient
    {
        private volatile bool _connected;

        public bool IsConnected => _connected;
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;
        public event Action<PacketCommand, ReadOnlyMemory<byte>>? OnFrameSent;

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
        {
            _connected = true;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Connected));
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            var seq = new ReadOnlySequence<byte>(data);
            while (seq.Length > 0)
            {
                if (!MessagePacket.TryDeserialize(ref seq, out var pkt, out _))
                    break;
                if (pkt.Command == PacketCommand.ClientHello)
                    OnDataChunkReceived?.Invoke(this, TcpHandshakeTestServer.ServerHelloFrame);
                OnFrameSent?.Invoke(pkt.Command, pkt.Body.ToArray());
            }
            return Task.CompletedTask;
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(-1, token);

        public void Disconnect(string? reason = null)
        {
            if (!_connected) return;
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason));
        }

        public void InjectData(ReadOnlyMemory<byte> chunk)
            => OnDataChunkReceived?.Invoke(this, chunk);

        public void Dispose()
        {
            _connected = false;
            GC.SuppressFinalize(this);
        }
    }
}
