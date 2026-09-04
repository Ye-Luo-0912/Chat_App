using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Services;
using Xunit;
using TcpCallSignalEvents = ChatApp.Shared.Protocol.Tcp.TcpCallConstants;

// 与既有 UnitTests 一致：非 call 相关 DTO 以本地别名声明，避免与全局别名冲突。
using MessageHistoryPageDto = ChatApp.Shared.Protocol.Tcp.MessageHistoryResponse;
using SyncBootstrapResponseDto = ChatApp.Shared.Protocol.Tcp.SyncBootstrapResponse;
using ConversationSyncWatermarkDto = ChatApp.Shared.Protocol.Tcp.ConversationSyncWatermark;
using RelationshipSyncWatermarkDto = ChatApp.Shared.Protocol.Tcp.RelationshipSyncWatermark;

// 测试桩声明全部接口事件但从不触发属预期（CS0067）：仅实现接口成员以满足编译。
#pragma warning disable CS0067

namespace UnitTests;

/// <summary>
/// GROUP-CALL-1 群组（Mesh ≤4 人）客户端多方状态机与会话管理器验证。
/// <para>
/// 第一部分覆盖 <see cref="CallSession"/> 群组语义：成员集合（grant 名单/晋升初始集合）、
/// participant-joined/left 事件、成员 accept/reject 证据、发起者离开终结全会话、
/// unknown 事件容忍跳过与幂等去重。第二部分覆盖 <see cref="CallSessionManager"/>：
/// StartGroupCallAsync 逐成员 invite（每成员 offer/媒体实例）、逐成员 answer 收口、
/// 成员离开仅拆自身、被叫晋升与成员加入、1:1 Direct 路径零回归。
/// </para>
/// </summary>
public sealed class CallGroupSessionTests
{
    private const long CallerId = 7001;
    private const long CalleeBId = 7002;
    private const long CalleeCId = 7003;
    private const string CallId = "group-call-1";

    // ════════════════ 第一部分：CallSession 群组语义 ════════════════

    [Fact]
    public void GroupCtor_NormalizesRoster_SortedDistinct_WithFirstOtherAsPeer()
    {
        var s = new CallSession(CallId, CallRole.Caller, CallerId, new[] { CalleeCId, CallerId, CalleeBId });

        Assert.True(s.IsGroup);
        Assert.Equal(CallerId, s.InitiatorUserId);
        Assert.Equal(new[] { CallerId, CalleeBId, CalleeCId }, s.Participants);
        Assert.Equal(3, s.ParticipantCount);
        Assert.Equal(CalleeBId, s.PeerUserId); // 首个非发起者成员（首个应答者占位语义）
        Assert.Equal(CallStateDto.Idle, s.State);
    }

    [Fact]
    public void GroupCtor_WithoutOtherMembers_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CallSession(CallId, CallRole.Caller, CallerId, new[] { CallerId }));
    }

    [Fact]
    public void TryAddParticipant_RaisesJoined_DuplicateAndCapRefused()
    {
        // grant 名单外的新成员加入 → 事件；重复/满员 → 拒绝且无事件。
        var s = new CallSession(CallId, CallRole.Caller, CallerId, new[] { CallerId, CalleeBId });
        var joined = new List<long>();
        s.ParticipantJoined += (_, e) => joined.Add(e.ParticipantUserId);

        Assert.True(s.TryAddParticipant(CalleeCId));
        Assert.Equal(new[] { CallerId, CalleeBId, CalleeCId }, s.Participants);
        Assert.Equal(CalleeCId, joined.Single());

        Assert.False(s.TryAddParticipant(CalleeCId)); // 已在名单：幂等拒绝
        Assert.False(s.TryAddParticipant(0)); // 非法 Id
        Assert.Single(joined);

        // 名单已满（4 人）。
        var full = new CallSession("c3", CallRole.Caller, CallerId,
            new[] { CallerId, CalleeBId, CalleeCId, 7004L });
        Assert.False(full.TryAddParticipant(7005L));
    }

    [Fact]
    public void TryRemoveParticipant_RemovesAndRaises_InitiatorRefused()
    {
        var s = new CallSession(CallId, CallRole.Caller, CallerId,
            new[] { CallerId, CalleeBId, CalleeCId }, CallStateDto.Active);
        var left = new List<long>();
        s.ParticipantLeft += (_, e) => left.Add(e.ParticipantUserId);

        Assert.True(s.TryRemoveParticipant(CalleeBId));
        Assert.Equal(new[] { CallerId, CalleeCId }, s.Participants);
        Assert.Equal(new[] { CalleeBId }, left);
        Assert.False(s.TryRemoveParticipant(CalleeBId)); // 已不在名单
        Assert.False(s.TryRemoveParticipant(CallerId)); // 发起者不可经此移除（发起者离开 = 会话终态）
        Assert.Single(left);
    }

    [Fact]
    public void GroupSignal_ParticipantJoined_AddsMember_AndRaisesEvent()
    {
        var s = NewCalleeGroupRinging(); // 被叫初始名单 [本端, 发起者]
        var joined = new List<long>();
        s.ParticipantJoined += (_, e) => joined.Add(e.ParticipantUserId);

        Assert.True(s.ApplyRemoteSignal(GroupSignal(
            "sig-j1", CallCommandTypeDto.Ringing, from: CallerId,
            evt: TcpCallSignalEvents.SignalEventParticipantJoined, participant: CalleeCId)));

        Assert.True(s.IsGroup);
        Assert.Equal(CallerId, s.InitiatorUserId);
        Assert.Equal(new[] { CallerId, CalleeBId, CalleeCId }, s.Participants);
        Assert.Equal(CalleeCId, joined.Single());
        Assert.Equal(CallStateDto.Ringing, s.State);
    }

    [Fact]
    public void GroupSignal_MemberLeft_RemovesOnlyThatMember_SessionContinues()
    {
        var s = NewCallerGroupActive();
        var left = new List<long>();
        s.ParticipantLeft += (_, e) => left.Add(e.ParticipantUserId);

        Assert.True(s.ApplyRemoteSignal(GroupSignal(
            "sig-l1", CallCommandTypeDto.End, from: CalleeBId,
            evt: TcpCallSignalEvents.SignalEventParticipantLeft, participant: CalleeBId)));

        Assert.False(s.IsTerminal);
        Assert.Equal(CallStateDto.Active, s.State);
        Assert.Equal(new[] { CallerId, CalleeCId }, s.Participants);
        Assert.Equal(new[] { CalleeBId }, left);
    }

    [Fact]
    public void GroupSignal_InitiatorLeft_TerminatesWholeSession_NoMemberLeftEvent()
    {
        var s = NewCalleeGroupActive(); // 发起者 = CallerId
        var left = new List<long>();
        s.ParticipantLeft += (_, e) => left.Add(e.ParticipantUserId);

        Assert.True(s.ApplyRemoteSignal(GroupSignal(
            "sig-l2", CallCommandTypeDto.End, from: CallerId,
            evt: TcpCallSignalEvents.SignalEventParticipantLeft, participant: CallerId)));

        Assert.True(s.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, s.EndReason);
        Assert.Empty(left); // 发起者离开走会话终态，不触发成员离开事件
    }

    [Fact]
    public void GroupSignal_MemberAccept_CallerFirstAccepts_Active_SubsequentKeepsActive()
    {
        var s = NewCallerGroup(); // grant 名单 [主叫, B, C]，创建后 invite → Ringing
        Assert.True(s.TryApplyLocalCommand(CallCommandTypeDto.Invite));

        Assert.True(s.ApplyRemoteSignal(GroupSignal("sig-a1", CallCommandTypeDto.Accept, from: CalleeBId, sdp: "answer-b")));
        Assert.Equal(CallStateDto.Active, s.State);
        Assert.Equal("answer-b", s.RemoteSdp);

        // 第二个成员 accept：状态保持 Active，名单不变（grant 名单语义）。
        Assert.False(s.ApplyRemoteSignal(GroupSignal("sig-a2", CallCommandTypeDto.Accept, from: CalleeCId, sdp: "answer-c")));
        Assert.Equal(CallStateDto.Active, s.State);
        Assert.Equal(3, s.ParticipantCount);
    }

    [Fact]
    public void GroupSignal_MemberReject_RemovesMemberOnly_SessionContinues()
    {
        var s = NewCallerGroup();
        Assert.True(s.TryApplyLocalCommand(CallCommandTypeDto.Invite));

        Assert.True(s.ApplyRemoteSignal(GroupSignal("sig-r1", CallCommandTypeDto.Reject, from: CalleeBId)));

        Assert.False(s.IsTerminal);
        Assert.Equal(CallStateDto.Ringing, s.State);
        Assert.Equal(new[] { CallerId, CalleeCId }, s.Participants);
    }

    [Fact]
    public void GroupSignal_InitiatorEndWithoutEvent_TerminatesSession()
    {
        var s = NewCalleeGroupActive();
        Assert.True(s.ApplyRemoteSignal(GroupSignal("sig-e1", CallCommandTypeDto.End, from: CallerId)));
        Assert.True(s.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, s.EndReason);
    }

    [Fact]
    public void GroupSignal_InitiatorCancel_DrivesCalleeToEndedCancelled()
    {
        var s = NewCalleeGroupRinging();
        Assert.True(s.ApplyRemoteSignal(GroupSignal("sig-x1", CallCommandTypeDto.Cancel, from: CallerId)));
        Assert.True(s.IsTerminal);
        Assert.Equal(CallEndReasonDto.Cancelled, s.EndReason);
    }

    [Fact]
    public void GroupSignal_UnknownEvent_ToleratedSkipped()
    {
        var s = NewCallerGroupActive();
        Assert.False(s.ApplyRemoteSignal(GroupSignal("sig-u1", CallCommandTypeDto.Ringing, from: CalleeBId, evt: "offer")));
        Assert.False(s.ApplyRemoteSignal(GroupSignal("sig-u2", CallCommandTypeDto.Ringing, from: CalleeBId, evt: "ice")));
        Assert.False(s.IsTerminal);
        Assert.Equal(3, s.ParticipantCount);
        Assert.Equal(CallStateDto.Active, s.State);
    }

    [Fact]
    public void GroupSignal_DuplicateSignalId_Ignored()
    {
        var s = NewCallerGroupActive();
        Assert.True(s.ApplyRemoteSignal(GroupSignal(
            "sig-dup", CallCommandTypeDto.End, from: CalleeBId,
            evt: TcpCallSignalEvents.SignalEventParticipantLeft, participant: CalleeBId)));
        // 同一 signal id 重传：去重忽略。
        Assert.False(s.ApplyRemoteSignal(GroupSignal(
            "sig-dup", CallCommandTypeDto.End, from: CalleeBId,
            evt: TcpCallSignalEvents.SignalEventParticipantLeft, participant: CalleeCId)));
        Assert.Equal(new[] { CallerId, CalleeCId }, s.Participants);
    }

    [Fact]
    public void GroupSignal_AfterTerminal_NoMutation()
    {
        var s = NewCallerGroupActive();
        s.ForceEnd(CallEndReasonDto.HungUp);
        Assert.False(s.ApplyRemoteSignal(GroupSignal("sig-t1", CallCommandTypeDto.Accept, from: CalleeBId)));
        Assert.False(s.TryAddParticipant(7009L));
        Assert.True(s.IsTerminal);
    }

    [Fact]
    public void TryPromoteToGroup_CalleeOnly_Once()
    {
        var direct = new CallSession(CallId, CallRole.Callee, CallerId, CallStateDto.Ringing);
        Assert.True(direct.TryPromoteToGroup(CallerId, new[] { CalleeBId, CallerId }));
        Assert.True(direct.IsGroup);
        Assert.Equal(CallerId, direct.InitiatorUserId);
        Assert.Equal(new[] { CallerId, CalleeBId }, direct.Participants);
        Assert.False(direct.TryPromoteToGroup(CallerId, new[] { CallerId, CalleeCId })); // 仅一次
        Assert.Equal(new[] { CallerId, CalleeBId }, direct.Participants);

        var callerSession = new CallSession("c4", CallRole.Caller, CalleeBId);
        Assert.False(callerSession.TryPromoteToGroup(CalleeBId, new[] { CalleeBId })); // 主叫角色不晋升
        Assert.False(callerSession.IsGroup);
    }

    [Fact]
    public void DirectSession_HasNoGroupSemantics_ZeroRegression()
    {
        // 1:1 主叫视角：accept/End 信令迁移与 48 项既有测试一致，群组语义缺省关闭。
        var s = new CallSession("direct-1", CallRole.Caller, CalleeBId);
        Assert.False(s.IsGroup);
        Assert.Equal(0, s.InitiatorUserId);
        Assert.Empty(s.Participants);
        Assert.Equal(0, s.ParticipantCount);
        Assert.True(s.TryApplyLocalCommand(CallCommandTypeDto.Invite));
        Assert.True(s.ApplyRemoteSignal(Signal("d-1", CallCommandTypeDto.Accept, from: CalleeBId)));
        Assert.Equal(CallStateDto.Active, s.State);
        Assert.True(s.ApplyRemoteSignal(Signal("d-2", CallCommandTypeDto.End, from: CalleeBId)));
        Assert.True(s.IsTerminal);
    }

    // ════════════════ 第二部分：CallSessionManager 群组编排 ════════════════

    [Fact]
    public async Task StartGroupCall_InvitesEachMember_WithPerMemberOffer()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        manager.PeerMediaFactory = (callId, peerUserId) => new FakeMediaSession { Offer = $"offer-for-{peerUserId}" };
        var grant = NewGroupGrant();

        var session = await manager.StartGroupCallAsync(CallId, grant);

        Assert.Equal(CallStateDto.Ringing, session.State);
        Assert.True(session.IsGroup);
        Assert.Equal(new[] { CallerId, CalleeBId, CalleeCId }, session.Participants);
        Assert.Same(session, manager.GetCall(CallId));

        // 逐成员 invite：每个非本端成员一条命令，各自携带该成员媒体实例生成的 offer。
        var invites = client.Sent.Where(r => r.Type == CallCommandTypeDto.Invite).ToArray();
        Assert.Equal(2, invites.Length);
        Assert.All(invites, r => Assert.Same(grant, r.Grant));
        // 升序邀请：先 B 后 C（PeerMediaFactory 按成员升序建立实例并生成逐成员 offer）。
        Assert.Equal("offer-for-7002", invites[0].Sdp);
        Assert.Equal("offer-for-7003", invites[1].Sdp);
    }

    [Fact]
    public async Task StartGroupCall_Validation_GroupGrantRequired()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.StartGroupCallAsync(CallId, NewDirectGrant()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.StartGroupCallAsync("other-call", NewGroupGrant()));
    }

    [Fact]
    public async Task StartGroupCall_MemberAccepts_RoutesAnswerToOwnMedia_OneInstancePerMember()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        var mediaByPeer = new Dictionary<long, FakeMediaSession>();
        manager.PeerMediaFactory = (_, peerUserId) =>
        {
            var m = new FakeMediaSession { Offer = $"offer-for-{peerUserId}", Answer = $"answer-{peerUserId}" };
            mediaByPeer[peerUserId] = m;
            return m;
        };
        var grant = NewGroupGrant();

        var session = await manager.StartGroupCallAsync(CallId, grant);
        Assert.Equal(2, mediaByPeer.Count); // 每成员一个媒体实例

        // 成员 B accept → 其 answer 应用到 B 的实例并启动；首个 accept 使会话 Active。
        client.RaiseCallSignal(GroupSignal("acc-b", CallCommandTypeDto.Accept,
            callId: CallId, from: CalleeBId, to: CallerId, sdp: "answer-b", revision: 2));
        Assert.Equal(CallStateDto.Active, session.State);
        Assert.Equal(1, mediaByPeer[CalleeBId].StartCalls);
        Assert.Contains("answer-b", mediaByPeer[CalleeBId].SetRemoteSdps);

        // 成员 C accept → 独立收口到 C 的实例；状态保持 Active。
        client.RaiseCallSignal(GroupSignal("acc-c", CallCommandTypeDto.Accept,
            callId: CallId, from: CalleeCId, to: CallerId, sdp: "answer-c", revision: 3));
        Assert.Equal(CallStateDto.Active, session.State);
        Assert.Equal(1, mediaByPeer[CalleeCId].StartCalls);
        Assert.Contains("answer-c", mediaByPeer[CalleeCId].SetRemoteSdps);
        Assert.Equal(1, mediaByPeer[CalleeBId].StartCalls); // B 实例不受 C accept 影响
        Assert.Equal(2, mediaByPeer.Count); // 媒体实例每成员一个，无新建
    }

    [Fact]
    public async Task Group_MemberLeft_TearsDownOwnMediaOnly_OthersIntact()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        var mediaByPeer = new Dictionary<long, FakeMediaSession>();
        manager.PeerMediaFactory = (_, peerUserId) =>
        {
            var m = new FakeMediaSession { Offer = $"offer-for-{peerUserId}" };
            mediaByPeer[peerUserId] = m;
            return m;
        };
        var grant = NewGroupGrant();
        var session = await manager.StartGroupCallAsync(CallId, grant);
        client.RaiseCallSignal(GroupSignal("acc-b", CallCommandTypeDto.Accept,
            callId: CallId, from: CalleeBId, to: CallerId, sdp: "answer-b", revision: 2));
        client.RaiseCallSignal(GroupSignal("acc-c", CallCommandTypeDto.Accept,
            callId: CallId, from: CalleeCId, to: CallerId, sdp: "answer-c", revision: 3));

        // 成员 B 离开（participant-left）：B 媒体停止并释放，C 媒体不受影响，会话继续。
        client.RaiseCallSignal(GroupSignal("left-b", CallCommandTypeDto.End,
            callId: CallId, from: CalleeBId, to: CallerId,
            evt: TcpCallSignalEvents.SignalEventParticipantLeft, participant: CalleeBId, revision: 4));

        Assert.False(session.IsTerminal);
        Assert.Equal(CallStateDto.Active, session.State);
        Assert.Equal(new[] { CallerId, CalleeCId }, session.Participants);
        Assert.Equal(1, mediaByPeer[CalleeBId].StopCalls);
        Assert.Equal(0, mediaByPeer[CalleeCId].StopCalls);
        Assert.Single(manager.ActiveCalls);

        // 发起者挂断 → 全会话终态，剩余成员媒体统一收尾。
        await manager.EndAsync(CallId);
        Assert.True(session.IsTerminal);
        Assert.Empty(manager.ActiveCalls);
        Assert.Equal(1, mediaByPeer[CalleeCId].StopCalls);
        // 发起者的 End 命令自动携带群组 grant（无状态中继要求）。
        var end = Assert.Single(client.Sent, r => r.Type == CallCommandTypeDto.End);
        Assert.Same(grant, end.Grant);
    }

    [Fact]
    public async Task GroupCallee_IncomingInvite_Accept_ThenMemberJoined_PromotesAndRecords()
    {
        var ctx = new FakeUserContext { UserId = CalleeBId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        CallSession? incoming = null;
        manager.IncomingCall += (_, s) => incoming = s;

        // 来电（wire 不透出群组名单，先以 1:1 形态呈现主叫来电）。
        client.RaiseCallSignal(Signal("inv-1", CallCommandTypeDto.Invite,
            callId: CallId, from: CallerId, to: CalleeBId, sdp: "offer-b", revision: 1));
        Assert.NotNull(incoming);
        Assert.False(incoming!.IsGroup);
        Assert.Equal(CallStateDto.Ringing, incoming.State);

        // 接听：Accept 命令上行，本端 Active。
        await manager.AcceptAsync(CallId, sdpAnswer: "answer-b");
        Assert.Equal(CallStateDto.Active, incoming.State);
        Assert.Equal(CallCommandTypeDto.Accept, client.Sent[^1].Type);

        // 第三成员 accept 扇出到本端：晋升群组 + 成员加入记录（事件与名单）。
        var joined = new List<long>();
        incoming.ParticipantJoined += (_, e) => joined.Add(e.ParticipantUserId);
        client.RaiseCallSignal(GroupSignal("acc-c", CallCommandTypeDto.Accept,
            callId: CallId, from: CalleeCId, to: CalleeBId, sdp: "answer-c", revision: 3));

        Assert.True(incoming.IsGroup);
        Assert.Equal(CallerId, incoming.InitiatorUserId);
        Assert.Equal(new[] { CallerId, CalleeBId, CalleeCId }, incoming.Participants);
        Assert.Equal(CalleeCId, joined.Single());
        Assert.Equal(CallStateDto.Active, incoming.State);
    }

    [Fact]
    public async Task GroupCallee_InitiatorLeft_EndsWholeSession_AndStopsMedia()
    {
        var ctx = new FakeUserContext { UserId = CalleeBId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        var media = new FakeMediaSession();
        manager.MediaFactory = _ => media;
        CallSession? incoming = null;
        manager.IncomingCall += (_, s) => incoming = s;
        client.RaiseCallSignal(Signal("inv-1", CallCommandTypeDto.Invite,
            callId: CallId, from: CallerId, to: CalleeBId, sdp: "offer-b", revision: 1));
        await manager.AcceptAsync(CallId, sdpAnswer: "answer-b");

        client.RaiseCallSignal(GroupSignal("left-initiator", CallCommandTypeDto.End,
            callId: CallId, from: CallerId, to: CalleeBId,
            evt: TcpCallSignalEvents.SignalEventParticipantLeft, participant: CallerId, revision: 5));

        Assert.NotNull(incoming);
        Assert.True(incoming!.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, incoming.EndReason);
        Assert.Empty(manager.ActiveCalls);
        Assert.Equal(1, media.StopCalls);
    }

    [Fact]
    public async Task StartGroupCall_AllInvitesFail_FailClosed_EndsSession()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient { ThrowOn = _ => true };
        using var manager = CreateManager(client, ctx, new ManualDelay());
        CallSession? ended = null;
        manager.CallEnded += (_, s) => ended = s;

        await Assert.ThrowsAsync<AggregateException>(
            () => manager.StartGroupCallAsync(CallId, NewGroupGrant()));

        Assert.NotNull(ended);
        Assert.True(ended!.IsTerminal);
        Assert.Empty(manager.ActiveCalls);
        Assert.Null(manager.GetCall(CallId));
    }

    [Fact]
    public async Task StartGroupCall_PartialInviteFailure_ContinuesRemainingMembers()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        manager.PeerMediaFactory = (_, peerUserId) => new FakeMediaSession { Offer = $"offer-for-{peerUserId}" };
        client.ThrowOn = r => r.Type == CallCommandTypeDto.Invite && r.Sdp == "offer-for-7002"; // 仅 B 的 invite 失败
        var grant = NewGroupGrant();

        var session = await manager.StartGroupCallAsync(CallId, grant);

        Assert.Equal(CallStateDto.Ringing, session.State);
        Assert.Single(manager.ActiveCalls);
        // B 的 invite 已上行但失败；C 的 invite 成功，逐成员语义保持。
        Assert.Equal(2, client.Sent.Count(r => r.Type == CallCommandTypeDto.Invite));
        Assert.Contains(client.Sent, r => r.Type == CallCommandTypeDto.Invite && r.Sdp == "offer-for-7003");
    }

    [Fact]
    public async Task GroupCallee_MemberReject_Evidence_PromotesWithoutRosterChange()
    {
        var ctx = new FakeUserContext { UserId = CalleeBId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        client.RaiseCallSignal(Signal("inv-1", CallCommandTypeDto.Invite,
            callId: CallId, from: CallerId, to: CalleeBId, sdp: "offer-b", revision: 1));
        var incoming = Assert.Single(manager.ActiveCalls);

        // 第三成员 reject 扇出：构成群组证据（晋升），但该成员未加入也不在名单。
        client.RaiseCallSignal(GroupSignal("rej-c", CallCommandTypeDto.Reject,
            callId: CallId, from: CalleeCId, to: CalleeBId, revision: 2));

        Assert.True(incoming!.IsGroup);
        Assert.Equal(new[] { CallerId, CalleeBId }, incoming.Participants);
        Assert.False(incoming.IsTerminal);
    }

    [Fact]
    public async Task Group_InviteTimeout_StillCancelsWhenNobodyAccepts()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        var delay = new ManualDelay();
        using var manager = CreateManager(client, ctx, delay);
        manager.InviteTimeout = TimeSpan.FromSeconds(30);
        var grant = NewGroupGrant();

        var session = await manager.StartGroupCallAsync(CallId, grant);
        await delay.WaitPendingAsync();
        delay.CompleteAll();
        await WaitUntilAsync(() => session.IsTerminal);

        Assert.True(session.IsTerminal);
        Assert.Equal(CallEndReasonDto.TimedOut, session.EndReason);
        Assert.Empty(manager.ActiveCalls);
        Assert.Equal(CallCommandTypeDto.Cancel, client.Sent[^1].Type);
        Assert.Same(grant, client.Sent[^1].Grant); // 取消命令同样携带群组 grant
    }

    // ── 构造与驱动 ─────────────────────────────────────────────

    private static CallSessionManager CreateManager(FakeCallClient client, FakeUserContext ctx, ManualDelay delay)
        => new(client, ctx, delay.Func);

    private static CallSession NewCallerGroup() =>
        new(CallId, CallRole.Caller, CallerId, new[] { CallerId, CalleeBId, CalleeCId });

    private static CallSession NewCallerGroupActive() =>
        new(CallId, CallRole.Caller, CallerId, new[] { CallerId, CalleeBId, CalleeCId }, CallStateDto.Active);

    private static CallSession NewCalleeGroupRinging() =>
        new(CallId, CallRole.Callee, CallerId, new[] { CalleeBId, CallerId }, CallStateDto.Ringing);

    private static CallSession NewCalleeGroupActive() =>
        new(CallId, CallRole.Callee, CallerId, new[] { CalleeBId, CallerId }, CallStateDto.Active);

    private static CallSignalDto Signal(
        string id,
        CallCommandTypeDto kind,
        string callId = "direct-1",
        long from = 0,
        long to = 0,
        string? sdp = null,
        long revision = 0)
        => new()
        {
            SignalId = id,
            CallId = callId,
            FromUserId = from,
            ToUserId = to,
            Kind = kind,
            Sdp = sdp ?? string.Empty,
            Revision = revision
        };

    private static CallSignalDto GroupSignal(
        string id,
        CallCommandTypeDto kind,
        string callId = CallId,
        long from = 0,
        long to = 0,
        string? sdp = null,
        string? evt = null,
        long? participant = null,
        long revision = 0)
        => new()
        {
            SignalId = id,
            CallId = callId,
            FromUserId = from,
            ToUserId = to,
            Kind = kind,
            Sdp = sdp ?? string.Empty,
            Revision = revision,
            Event = evt,
            ParticipantUserId = participant,
        };

    private static CallGrantDto NewGroupGrant() => new()
    {
        CallId = CallId,
        CallerUserId = CallerId,
        CalleeUserId = 0, // 群组 grant 恒 0
        ExpiresAtMs = 1_900_000_000_000L,
        Nonce = "nonce-group-1",
        Signature = "sig-group-1",
        CallKind = ChatApp.Shared.Protocol.Tcp.TcpCallKind.Group,
        Participants = new[] { CallerId, CalleeBId, CalleeCId },
    };

    private static CallGrantDto NewDirectGrant() => new()
    {
        CallId = CallId,
        CallerUserId = CallerId,
        CalleeUserId = CalleeBId,
        ExpiresAtMs = 1_900_000_000_000L,
        Nonce = "nonce-1",
        Signature = "sig-1",
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("等待条件超时");
            await Task.Delay(10);
        }
    }

    /// <summary>手动延迟：捕获超时注册，由测试显式触发完成/取消，避免真实等待。</summary>
    private sealed class ManualDelay
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _pending = new();

        public Func<TimeSpan, CancellationToken, Task> Func => (_, ct) =>
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _pending.Add(tcs);
            if (ct.CanBeCanceled)
                ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        };

        public async Task WaitPendingAsync(int count = 1)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                lock (_gate)
                {
                    if (_pending.Count >= count)
                        return;
                }
                if (sw.ElapsedMilliseconds > 3000)
                    throw new TimeoutException("等待超时注册");
                await Task.Delay(5);
            }
        }

        public void CompleteAll()
        {
            TaskCompletionSource[] all;
            lock (_gate)
            {
                all = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var tcs in all)
                tcs.TrySetResult();
        }
    }

    // ── 最小测试桩 ────────────────────────────────────────────

    private sealed class FakeUserContext : ICurrentUserContext
    {
        public long? UserId { get; set; }
        public UserSessionSnapshot Snapshot => new(UserId ?? 0, 0, null, null, null);
        public long Generation { get; set; }
        public string? UserName => null;
        public bool IsAuthenticated => UserId is > 0;
        public bool HasUserId => UserId is > 0;
        public long RequireUserId() => UserId is > 0 ? UserId!.Value : throw new InvalidOperationException("未登录");
        public bool TryGetUserId(out long userId)
        {
            userId = UserId ?? 0;
            return UserId is > 0;
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

    private sealed class FakeCallClient : IChatSessionClient
    {
        public bool SupportsCallSignaling { get; set; } = true;
        public bool IsConnected { get; set; } = true;
        public bool IsAuthenticated { get; set; }
        public long CurrentUserId { get; set; }
        public long ConnectionGeneration { get; set; }
        public Guid ConnectionId { get; set; } = Guid.NewGuid();
        public SessionStamp CurrentSession => new(CurrentUserId, ConnectionGeneration, ConnectionId);

        public List<CallCommandRequestDto> Sent { get; } = new();
        public Func<CallCommandRequestDto, bool>? ThrowOn { get; set; }

        public Task<CallCommandResponseDto> SendCallCommandAsync(CallCommandRequestDto request, CancellationToken ct = default)
        {
            Sent.Add(request);
            if (ThrowOn is not null && ThrowOn(request))
                throw new IOException("命令上行失败");
            return Task.FromResult(DefaultResponse(request));
        }

        public void RaiseCallSignal(CallSignalDto signal)
            => CallSignalReceived?.Invoke(this, signal);

        private static CallCommandResponseDto DefaultResponse(CallCommandRequestDto request) => new()
        {
            RequestId = request.RequestId,
            CallId = request.CallId,
            Succeeded = true,
            State = request.Type switch
            {
                CallCommandTypeDto.Accept => CallStateDto.Active,
                CallCommandTypeDto.Reconnect => CallStateDto.Active,
                CallCommandTypeDto.Reject => CallStateDto.Ended,
                CallCommandTypeDto.Cancel => CallStateDto.Ended,
                CallCommandTypeDto.End => CallStateDto.Ended,
                _ => CallStateDto.Ringing,
            },
            EndReason = request.Type switch
            {
                CallCommandTypeDto.Reject => CallEndReasonDto.Rejected,
                CallCommandTypeDto.Cancel => CallEndReasonDto.Cancelled,
                CallCommandTypeDto.End => CallEndReasonDto.HungUp,
                _ => CallEndReasonDto.None,
            },
            Revision = request.Revision
        };

        public event EventHandler<CallSignalDto>? CallSignalReceived;
        public event EventHandler? Connected;
        public event EventHandler<long>? Authenticated;
        public event EventHandler<string>? AuthenticationFailed;
        public event EventHandler<ProtocolErrorDto>? ProtocolError;
        public event EventHandler<ChatMessageDto>? ChatMessageReceived;
        public event EventHandler<MessageAcknowledgementDto>? MessageAcknowledged;
        public event EventHandler<ConversationChangedDto>? ConversationChanged;
        public event EventHandler<MessageRecalledUpdateDto>? MessageRecalled;
        public event EventHandler<MessageEditedUpdateDto>? MessageEdited;
        public event EventHandler<TypingUpdateDto>? TypingUpdated;
        public event EventHandler<PresenceChangedDto>? PresenceChanged;
        public event EventHandler<string>? ConnectionClosed;
        public event EventHandler<MessageReceiptDto>? MessageReceiptReceived;
        public event EventHandler<MessageReceiptUpdatedDto>? MessageReceiptUpdated;
        public event EventHandler<MessageHistoryPageDto>? MessageHistoryPageReceived;
        public event EventHandler<ConversationMarkReadResponseDto>? ConversationMarkReadResponse;
        public event EventHandler<UnreadCountChangedDto>? UnreadCountChanged;
        public event EventHandler<MemberJoinedUpdateDto>? GroupMemberJoined;
        public event EventHandler<MemberLeftUpdateDto>? GroupMemberLeft;
        public event EventHandler<MemberRemovedUpdateDto>? GroupMemberRemoved;
        public event EventHandler<RoleChangedUpdateDto>? GroupRoleChanged;
        public event EventHandler<MembersAddedUpdateDto>? GroupMembersAdded;
        public event EventHandler<ConversationDissolvedUpdateDto>? GroupConversationDissolved;

        public ResumeAttemptResult? LastResumeResult => null;
        public string? LastIssuedResumeToken => null;

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken ct = default, string? resumeToken = null) => Task.CompletedTask;
        public Task AuthenticateAsync(string accessToken, long userId, string? sessionId, ulong? deviceIdHash, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task DisconnectAsync(string? reason = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendChatMessageAsync(long targetUserId, string? content, IReadOnlyList<string>? attachmentIds = null, string? replyToMessageId = null, long? replyToSenderUserId = null, string? replyToPreview = null, string? forwardedFromMessageId = null, long? forwardedFromSenderUserId = null, string? forwardedFromPreview = null, string? clientMessageId = null, string? conversationId = null, IReadOnlyList<long>? mentionedUserIds = null, IReadOnlyList<global::ChatApp.Shared.Protocol.Tcp.TcpAttachmentRef>? attachments = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ConversationListResponseDto> QueryConversationListAsync(int limit = 50, bool? beforeIsPinned = null, long? beforePinnedAtMs = null, long? beforeLastMessageAtMs = null, string? beforeConversationId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ConversationSetPrefsResponseDto> SetConversationPrefsAsync(string conversationId, bool? pinned = null, bool? muted = null, long? mutedUntilMs = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageRecallAcknowledgementDto> RecallMessageAsync(string messageId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageEditAcknowledgementDto> EditMessageAsync(string messageId, string content, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SendTypingNotifyAsync(long targetUserId, bool isTyping, string? conversationId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<PresenceSnapshotResponseDto> QueryPresenceAsync(IReadOnlyList<long> userIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UnwatchPresenceAsync(IReadOnlyList<long> userIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SyncBootstrapResponseDto> QuerySyncBootstrapAsync(int listLimit = 50, int historyLimitPerConversation = 20, int maxConversationsWithHistory = 10, IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SyncBootstrapResponseDto> QuerySyncBootstrapWithRelationshipsAsync(int listLimit = 50, int historyLimitPerConversation = 20, int maxConversationsWithHistory = 10, IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null, IReadOnlyList<RelationshipSyncWatermarkDto>? relationshipWatermarks = null, int? relationshipListLimit = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageHistoryPageDto> QueryMessageHistoryAsync(string conversationId, int limit = 50, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageHistoryPageDto> QueryMessageHistoryAfterAsync(string conversationId, int limit = 50, string? afterMessageId = null, long? afterReceivedAtMs = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageReceiptAckDto> SendMessageReceiptAsync(string conversationId, string? lastReadMessageId, long? lastReadAtMs, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ConversationMarkReadResponseDto> MarkConversationReadAsync(string conversationId, string? lastReadMessageId = null, long? lastReadAtMs = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<CreateGroupResponseDto> CreateGroupAsync(string title, IReadOnlyList<long>? memberUserIds = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AddGroupMembersResponseDto> AddGroupMembersAsync(string conversationId, IReadOnlyList<long> memberUserIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<RemoveGroupMemberResponseDto> RemoveGroupMemberAsync(string conversationId, long targetUserId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<LeaveGroupResponseDto> LeaveGroupAsync(string conversationId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<DissolveGroupResponseDto> DissolveGroupAsync(string conversationId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ChangeMemberRoleResponseDto> ChangeMemberRoleAsync(string conversationId, long targetUserId, ConversationMemberRole newRole, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ListGroupMembersResponseDto> ListGroupMembersAsync(string conversationId, int? pageSize = null, string? cursor = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }
}
