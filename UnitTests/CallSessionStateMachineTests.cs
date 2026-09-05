using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Services;
using Xunit;

// 与既有 UnitTests 一致：非 call 相关 DTO 以本地别名声明，避免与全局别名冲突。
using MessageHistoryPageDto = ChatApp.Shared.Protocol.Tcp.MessageHistoryResponse;
using SyncBootstrapResponseDto = ChatApp.Shared.Protocol.Tcp.SyncBootstrapResponse;
using ConversationSyncWatermarkDto = ChatApp.Shared.Protocol.Tcp.ConversationSyncWatermark;
using RelationshipSyncWatermarkDto = ChatApp.Shared.Protocol.Tcp.RelationshipSyncWatermark;
using TcpCallKind = ChatApp.Shared.Protocol.Tcp.TcpCallKind;

// 测试桩声明全部接口事件但从不触发属预期（CS0067）：仅实现接口成员以满足编译。
#pragma warning disable CS0067


namespace UnitTests;

/// <summary>
/// CALL-E2E-2 客户端通话状态机与会话管理器验证。
/// <para>
/// 第一部分覆盖 <see cref="CallSession"/> 纯状态机：本地命令迁移表（invite/accept/reject/
/// cancel/end/reconnect）、终态不可逆、服务端权威覆盖、对端信令按 signal id 幂等去重。
/// 第二部分覆盖 <see cref="CallSessionManager"/>：命令编排（乐观迁移 + 权威收敛）、来电分派、
/// invite/ringing 超时收尾、媒体面启动与断线 fail-closed，使用假客户端 + 手动延迟确定性驱动。
/// </para>
/// </summary>
public sealed class CallSessionStateMachineTests
{
    private const long CallerId = 6001;
    private const long CalleeId = 6002;

    // ════════════════ 第一部分：CallSession 纯状态机 ════════════════

    [Fact]
    public void Caller_Invite_FromIdle_TransitionsToRinging()
    {
        var s = NewCaller();
        Assert.True(s.TryApplyLocalCommand(CallCommandTypeDto.Invite));
        Assert.Equal(CallStateDto.Ringing, s.State);
    }

    [Fact]
    public void Caller_Invite_FromNonIdle_Rejected()
    {
        var s = NewCaller();
        s.TryApplyLocalCommand(CallCommandTypeDto.Invite);
        Assert.False(s.TryApplyLocalCommand(CallCommandTypeDto.Invite)); // 已在 Ringing
    }

    [Fact]
    public void Callee_Accept_FromRinging_TransitionsToActive()
    {
        var s = NewCalleeRinging();
        Assert.True(s.TryApplyLocalCommand(CallCommandTypeDto.Accept));
        Assert.Equal(CallStateDto.Active, s.State);
    }

    [Fact]
    public void Callee_Accept_FromIdle_Rejected()
    {
        var s = NewCallee();
        Assert.False(s.TryApplyLocalCommand(CallCommandTypeDto.Accept));
    }

    [Fact]
    public void Callee_Reject_FromRinging_TransitionsToEndedRejected()
    {
        var s = NewCalleeRinging();
        Assert.True(s.TryApplyLocalCommand(CallCommandTypeDto.Reject));
        Assert.True(s.IsTerminal);
        Assert.Equal(CallEndReasonDto.Rejected, s.EndReason);
    }

    [Fact]
    public void Caller_Reject_RejectedByRole()
    {
        var s = NewCaller();
        s.TryApplyLocalCommand(CallCommandTypeDto.Invite);
        Assert.False(s.TryApplyLocalCommand(CallCommandTypeDto.Reject)); // 主叫无 reject 语义
    }

    [Fact]
    public void Caller_Cancel_FromRinging_TransitionsToEndedCancelled()
    {
        var s = NewCaller();
        s.TryApplyLocalCommand(CallCommandTypeDto.Invite);
        Assert.True(s.TryApplyLocalCommand(CallCommandTypeDto.Cancel));
        Assert.Equal(CallEndReasonDto.Cancelled, s.EndReason);
    }

    [Fact]
    public void Callee_Cancel_RejectedByRole()
    {
        var s = NewCalleeRinging();
        Assert.False(s.TryApplyLocalCommand(CallCommandTypeDto.Cancel)); // 被叫无 cancel 语义
    }

    [Theory]
    [InlineData(CallStateDto.Ringing)]
    [InlineData(CallStateDto.Active)]
    public void End_FromRingingOrActive_TransitionsToEndedHungUp(CallStateDto state)
    {
        var s = new CallSession("c", CallRole.Caller, CalleeId, state);
        Assert.True(s.TryApplyLocalCommand(CallCommandTypeDto.End));
        Assert.True(s.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, s.EndReason);
    }

    [Fact]
    public void End_FromIdle_Rejected()
    {
        var s = NewCaller();
        Assert.False(s.TryApplyLocalCommand(CallCommandTypeDto.End));
    }

    [Fact]
    public void Reconnect_OnlyAllowed_WhenActive()
    {
        var ringing = NewCalleeRinging();
        Assert.False(ringing.TryApplyLocalCommand(CallCommandTypeDto.Reconnect));

        var active = NewCalleeRinging();
        active.TryApplyLocalCommand(CallCommandTypeDto.Accept);
        Assert.True(active.TryApplyLocalCommand(CallCommandTypeDto.Reconnect));
        Assert.Equal(CallStateDto.Active, active.State);
    }

    [Fact]
    public void TerminalState_Irreversible_AllCommandsRejected()
    {
        var s = NewCalleeRinging();
        s.TryApplyLocalCommand(CallCommandTypeDto.Reject); // Ended
        Assert.False(s.TryApplyLocalCommand(CallCommandTypeDto.Accept));
        Assert.False(s.TryApplyLocalCommand(CallCommandTypeDto.End));
        Assert.False(s.TryApplyLocalCommand(CallCommandTypeDto.Reconnect));
        Assert.False(s.TryApplyLocalCommand(CallCommandTypeDto.Reject));
    }

    [Fact]
    public void ServerState_OverridesLocalView_AndEndedIsIrreversible()
    {
        var s = NewCaller();
        s.TryApplyLocalCommand(CallCommandTypeDto.Invite); // 本地乐观 → Ringing
        // 服务端权威：已在别的设备上接通。
        Assert.True(s.ApplyServerState(Resp(s, CallStateDto.Active, revision: 3)));
        Assert.Equal(CallStateDto.Active, s.State);
        Assert.True(s.ServerConfirmed);
        Assert.Equal(3, s.Revision);

        // 服务端终态：Ended 后不可再迁出。
        Assert.True(s.ApplyServerState(Resp(s, CallStateDto.Ended, CallEndReasonDto.HungUp, revision: 4)));
        Assert.False(s.ApplyServerState(Resp(s, CallStateDto.Active, revision: 5))); // 已终态忽略
        Assert.True(s.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, s.EndReason);
    }

    [Fact]
    public void ServerState_Revision_Monotonic()
    {
        var s = NewCaller();
        s.ApplyServerState(Resp(s, CallStateDto.Ringing, revision: 10));
        s.ApplyServerState(Resp(s, CallStateDto.Ringing, revision: 5)); // 过期 revision 不倒退
        Assert.Equal(10, s.Revision);
    }

    [Fact]
    public void ServerState_Terminal_WithNoneReason_DefaultsToHungUp()
    {
        var s = NewCalleeRinging();
        Assert.True(s.ApplyServerState(Resp(s, CallStateDto.Ended, CallEndReasonDto.None, revision: 1)));
        Assert.Equal(CallEndReasonDto.HungUp, s.EndReason);
    }

    [Fact]
    public void RemoteSignal_Accept_DrivesCallerToActive_AndCapturesSdp()
    {
        var s = NewCaller();
        s.TryApplyLocalCommand(CallCommandTypeDto.Invite);
        Assert.True(s.ApplyRemoteSignal(Signal("s1", CallCommandTypeDto.Accept, sdp: "answer-1")));
        Assert.Equal(CallStateDto.Active, s.State);
        Assert.Equal("answer-1", s.RemoteSdp);
    }

    [Fact]
    public void RemoteSignal_Reject_DrivesCallerToEndedRejected()
    {
        var s = NewCaller();
        s.TryApplyLocalCommand(CallCommandTypeDto.Invite);
        Assert.True(s.ApplyRemoteSignal(Signal("s1", CallCommandTypeDto.Reject)));
        Assert.True(s.IsTerminal);
        Assert.Equal(CallEndReasonDto.Rejected, s.EndReason);
    }

    [Fact]
    public void RemoteSignal_Cancel_DrivesCalleeToEndedCancelled()
    {
        var s = NewCalleeRinging();
        Assert.True(s.ApplyRemoteSignal(Signal("s1", CallCommandTypeDto.Cancel)));
        Assert.True(s.IsTerminal);
        Assert.Equal(CallEndReasonDto.Cancelled, s.EndReason);
    }

    [Fact]
    public void RemoteSignal_End_DrivesActiveToEndedHungUp()
    {
        var s = NewCalleeRinging();
        s.TryApplyLocalCommand(CallCommandTypeDto.Accept);
        Assert.True(s.ApplyRemoteSignal(Signal("s1", CallCommandTypeDto.End)));
        Assert.True(s.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, s.EndReason);
    }

    [Fact]
    public void RemoteSignal_DuplicateSignalId_Ignored()
    {
        var s = NewCaller();
        s.TryApplyLocalCommand(CallCommandTypeDto.Invite);
        Assert.True(s.ApplyRemoteSignal(Signal("dup-1", CallCommandTypeDto.Reject)));
        // 同一 signal id 重传：去重忽略，不产生新迁移。
        Assert.False(s.ApplyRemoteSignal(Signal("dup-1", CallCommandTypeDto.Accept)));
        Assert.True(s.IsTerminal);
    }

    [Fact]
    public void RemoteSignal_OutOfOrder_InviteOnExistingRinging_NoStateChange()
    {
        var s = NewCalleeRinging();
        // 乱序 invite（已振铃后）不改变状态，仅记录 SDP。
        Assert.False(s.ApplyRemoteSignal(Signal("s1", CallCommandTypeDto.Invite, sdp: "offer-1")));
        Assert.Equal(CallStateDto.Ringing, s.State);
        Assert.Equal("offer-1", s.RemoteSdp);
    }

    [Fact]
    public void CommandId_And_Revision_Monotonic()
    {
        var s = NewCaller();
        var ids = new[] { s.NextCommandId(CallerId), s.NextCommandId(CallerId), s.NextCommandId(CallerId) };
        Assert.Equal(3, new HashSet<string>(ids, StringComparer.Ordinal).Count);
        Assert.Equal(1, s.NextRevision());
        Assert.Equal(2, s.NextRevision());
        Assert.Equal(3, s.NextRevision());
    }

    [Fact]
    public void CommandId_RoleSegmentIsUserId_GroupCalleesNeverCollide()
    {
        // GROUP-CALL-CMDID-1 回归：多被叫群组中，各被叫会话的同序号命令 Id 不得撞车
        // （角色段 = 本端用户 Id；旧 "A"/"B" 角色段使全部被叫同为 "B:{callId}:c1"）。
        const string callId = "group-cmdid-1";
        var caller = new CallSession(callId, CallRole.Caller, 9001);
        var calleeB = new CallSession(callId, CallRole.Callee, 9001, CallStateDto.Ringing);
        var calleeC = new CallSession(callId, CallRole.Callee, 9001, CallStateDto.Ringing);
        var calleeD = new CallSession(callId, CallRole.Callee, 9001, CallStateDto.Ringing);

        var callerIds = new[] { caller.NextCommandId(9001), caller.NextCommandId(9001) };
        var calleeBIds = new[] { calleeB.NextCommandId(9002), calleeB.NextCommandId(9002) };
        var calleeCIds = new[] { calleeC.NextCommandId(9003), calleeC.NextCommandId(9003) };
        var calleeDIds = new[] { calleeD.NextCommandId(9004), calleeD.NextCommandId(9004) };

        var all = callerIds.Concat(calleeBIds).Concat(calleeCIds).Concat(calleeDIds).ToArray();
        Assert.Equal(all.Length, new HashSet<string>(all, StringComparer.Ordinal).Count);
        Assert.Contains(":u9002:c1", calleeBIds[0], StringComparison.Ordinal);
        Assert.Contains(":u9003:c1", calleeCIds[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CommandId_NonPositiveUserId_Throws()
    {
        var s = NewCaller();
        Assert.Throws<ArgumentOutOfRangeException>(() => s.NextCommandId(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.NextCommandId(-1));
    }

    [Fact]
    public void RemoteSignal_GroupCallee_OtherMemberAcceptAnswer_NotWrittenToRemoteSdp()
    {
        // GROUP-CALL-SDP-1（accept 形态）回归：群组 Mesh 扇出中，其他成员 accept 携带的 answer
        // 只与发起者（hub）相关——被叫的 RemoteSdp 不得被覆盖（否则接听时把别人的 answer
        // 应用到自己的媒体面），但成员集合仍记录该成员加入。
        var s = new CallSession("group-accept-1", CallRole.Callee, CallerId, CallStateDto.Ringing, 1);
        Assert.True(s.TryPromoteToGroup(CallerId, new long[] { CallerId, CalleeId }));
        s.ApplyRemoteSignal(Signal("inv", CallCommandTypeDto.Invite, callId: "group-accept-1", from: CallerId, sdp: "offer-for-self", revision: 1));

        var acceptFromThird = Signal("acc-9003", CallCommandTypeDto.Accept, callId: "group-accept-1", from: 9003, sdp: "answer-from-9003", revision: 2);
        Assert.True(s.ApplyRemoteSignal(acceptFromThird)); // 成员加入是可见变化

        Assert.Equal("offer-for-self", s.RemoteSdp);
        Assert.Contains((long)9003, s.Participants);
    }

    [Fact]
    public void RemoteSignal_GroupHub_OtherMemberAcceptAnswer_Applied()
    {
        // 发起者（hub）侧：成员 accept 的 answer 与本端相关——仍写入 RemoteSdp（Direct 语义不变）。
        var s = new CallSession("group-hub-1", CallRole.Caller, CallerId,
            new long[] { CallerId, CalleeId, 9003 }, CallStateDto.Ringing, 1);
        s.ApplyRemoteSignal(Signal("acc-b", CallCommandTypeDto.Accept, callId: "group-hub-1", from: CalleeId, sdp: "answer-from-b", revision: 2));

        Assert.Equal("answer-from-b", s.RemoteSdp);
    }

    [Fact]
    public void ForceEnd_OnlyOnce_TerminalStaysPut()
    {
        var s = NewCalleeRinging();
        s.ForceEnd(CallEndReasonDto.Missed);
        Assert.Equal(CallEndReasonDto.Missed, s.EndReason);
        s.ForceEnd(CallEndReasonDto.HungUp); // 已终态忽略
        Assert.Equal(CallEndReasonDto.Missed, s.EndReason);
    }

    [Fact]
    public void OverrideEndReason_OnlyWhenTerminal()
    {
        var s = NewCalleeRinging();
        s.OverrideEndReason(CallEndReasonDto.TimedOut); // 非终态：无效
        Assert.Equal(CallEndReasonDto.None, s.EndReason);
        s.ForceEnd(CallEndReasonDto.Cancelled);
        s.OverrideEndReason(CallEndReasonDto.TimedOut);
        Assert.Equal(CallEndReasonDto.TimedOut, s.EndReason);
    }

    // ════════════════ 第二部分：CallSessionManager（假客户端 + 手动延迟） ════════════════

    [Fact]
    public async Task StartCall_Invite_Sent_StateRinging()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        var grant = NewGrant();

        var session = await manager.StartCallAsync(CalleeId, sdpOffer: "offer-1", grant: grant);

        Assert.Equal(CallStateDto.Ringing, session.State);
        Assert.Same(session, manager.GetCall(session.CallId));
        Assert.Single(manager.ActiveCalls);
        var request = Assert.Single(client.Sent);
        Assert.Equal(CallCommandTypeDto.Invite, request.Type);
        Assert.Equal(CallerId, request.ActorUserId);
        Assert.Equal("offer-1", request.Sdp);
        Assert.Same(grant, request.Grant);
        Assert.Equal(1, request.Revision);
    }

    [Fact]
    public async Task StartCall_SendFailure_FailClosed_Throws_EndsAndRemovesSession()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient { ThrowOnSend = new IOException("断线") };
        using var manager = CreateManager(client, ctx, new ManualDelay());
        CallSession? ended = null;
        manager.CallEnded += (_, s) => ended = s;

        await Assert.ThrowsAsync<IOException>(() => manager.StartCallAsync(CalleeId));

        Assert.NotNull(ended);
        Assert.True(ended!.IsTerminal, "邀请上行失败应进入本端唯一终态");
        Assert.Empty(manager.ActiveCalls);
    }

    [Fact]
    public async Task StartCall_InviteTimeout_CancelsWithTimedOut()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        var delay = new ManualDelay();
        using var manager = CreateManager(client, ctx, delay);
        manager.InviteTimeout = TimeSpan.FromSeconds(30);
        CallSession? ended = null;
        manager.CallEnded += (_, s) => ended = s;

        var session = await manager.StartCallAsync(CalleeId);
        await delay.WaitPendingAsync();

        delay.CompleteAll();
        await WaitUntilAsync(() => session.IsTerminal);

        Assert.True(session.IsTerminal);
        Assert.Equal(CallEndReasonDto.TimedOut, session.EndReason);
        Assert.Same(session, ended);
        Assert.Empty(manager.ActiveCalls);
        // 超时走 Cancel 命令收尾。
        Assert.Equal(CallCommandTypeDto.Cancel, client.Sent[^1].Type);
    }

    [Fact]
    public async Task IncomingInvite_CreatesCalleeSession_RaisesIncomingCall()
    {
        var ctx = new FakeUserContext { UserId = CalleeId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        CallSession? incoming = null;
        manager.IncomingCall += (_, s) => incoming = s;

        client.RaiseCallSignal(Signal("inv-1", CallCommandTypeDto.Invite, from: CallerId, to: CalleeId, sdp: "offer-1", revision: 1));

        Assert.NotNull(incoming);
        Assert.Equal(CallRole.Callee, incoming!.Role);
        Assert.Equal(CallerId, incoming.PeerUserId);
        Assert.Equal(CallStateDto.Ringing, incoming.State);
        Assert.Equal("offer-1", incoming.RemoteSdp);
        Assert.Same(incoming, manager.GetCall(incoming.CallId));
    }

    [Fact]
    public async Task DuplicateInviteSignal_DoesNotRaiseIncomingCallTwice()
    {
        var ctx = new FakeUserContext { UserId = CalleeId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        var incomingCount = 0;
        manager.IncomingCall += (_, _) => incomingCount++;

        client.RaiseCallSignal(Signal("inv-dup", CallCommandTypeDto.Invite, from: CallerId, to: CalleeId, sdp: "offer-1", revision: 1));
        client.RaiseCallSignal(Signal("inv-dup", CallCommandTypeDto.Invite, from: CallerId, to: CalleeId, sdp: "offer-1", revision: 1)); // 重传

        Assert.Equal(1, incomingCount);
        Assert.Single(manager.ActiveCalls);
    }

    [Fact]
    public async Task IncomingInvite_TargetedAtOtherMember_IsIgnoredEntirely()
    {
        // GROUP-CALL-SDP-1 回归：逐成员 invite 经中继广播到全部成员——发给其他成员的
        // invite（ParticipantUserId=9999）不得建会话、不得污染 RemoteSdp。
        var ctx = new FakeUserContext { UserId = CalleeId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        CallSession? incoming = null;
        manager.IncomingCall += (_, s) => incoming = s;

        client.RaiseCallSignal(Signal(
            "inv-other", CallCommandTypeDto.Invite,
            callId: "call-1", from: CallerId, to: CalleeId,
            sdp: "offer-for-other-member", revision: 1,
            participantUserId: 9999));

        Assert.Null(incoming);
        Assert.Empty(manager.ActiveCalls);
        Assert.Null(manager.GetCall("call-1"));

        // 同一会话已存在（本端为目标）时：发给他人的 invite 也不得覆盖既有会话状态。
        client.RaiseCallSignal(Signal("inv-mine", CallCommandTypeDto.Invite, from: CallerId, to: CalleeId, sdp: "offer-1", revision: 1));
        client.RaiseCallSignal(Signal(
            "inv-other-2", CallCommandTypeDto.Invite,
            callId: "call-1", from: CallerId, to: CalleeId,
            sdp: "offer-for-other-member", revision: 1,
            participantUserId: 9999));

        Assert.NotNull(incoming);
        Assert.Equal("offer-1", incoming!.RemoteSdp);
    }

    [Fact]
    public async Task IncomingGroupInvite_WithGrant_PromotesSession_CachesGrant_AcceptCarriesGrant()
    {
        // GROUP-CALL-GAP-1 / MIDJOIN-1 回归：群组 invite 随信令下发 grant——被叫据此
        // 建立群组会话（成员集合=签发名单）并缓存 grant，accept 原样携带回中继
        // （harness 的 grant 播种桥接在真实语义下不再需要）。
        var ctx = new FakeUserContext { UserId = CalleeId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        CallSession? incoming = null;
        manager.IncomingCall += (_, s) => incoming = s;

        var groupGrant = new CallGrantDto
        {
            CallId = "group-invite-1",
            CallerUserId = CallerId,
            CalleeUserId = 0,
            ExpiresAtMs = 1_900_000_000_000L,
            Nonce = "nonce-g",
            Signature = "sig-g",
            CallKind = TcpCallKind.Group,
            Participants = [CallerId, CalleeId, 9003],
        };
        client.RaiseCallSignal(Signal(
            "inv-group-1", CallCommandTypeDto.Invite,
            callId: groupGrant.CallId, from: CallerId, to: CalleeId,
            sdp: "offer-for-callee", revision: 1,
            participantUserId: CalleeId,
            grant: groupGrant));

        Assert.NotNull(incoming);
        Assert.True(incoming!.IsGroup);
        Assert.Equal(new long[] { CallerId, CalleeId, 9003 }, incoming.Participants);
        Assert.Equal("offer-for-callee", incoming.RemoteSdp);

        await manager.AcceptAsync(groupGrant.CallId, sdpAnswer: "answer-1");

        var accept = Assert.Single(client.Sent);
        Assert.Equal(CallCommandTypeDto.Accept, accept.Type);
        Assert.Same(groupGrant, accept.Grant);
        // 命令 Id 角色段 = 本端用户 Id（GROUP-CALL-CMDID-1）。
        Assert.Contains(":u" + CalleeId + ":c1", accept.CommandId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptFlow_FromIncomingCall_ToActive_StartsMediaWithRemoteSdp()
    {
        var ctx = new FakeUserContext { UserId = CalleeId };
        using var client = new FakeCallClient();
        FakeMediaSession? bound = null;
        using var manager = CreateManager(client, ctx, new ManualDelay());
        manager.MediaFactory = callId =>
        {
            bound = new FakeMediaSession { CallId = callId, Answer = "answer-1" };
            return bound;
        };
        CallSession? changed = null;
        manager.CallStateChanged += (_, s) => changed = s;

        client.RaiseCallSignal(Signal("inv-1", CallCommandTypeDto.Invite, from: CallerId, to: CalleeId, sdp: "offer-1", revision: 1));
        var incoming = Assert.Single(manager.ActiveCalls);
        Assert.NotNull(bound);

        await manager.AcceptAsync(incoming.CallId, sdpAnswer: "answer-1");

        Assert.Equal(CallStateDto.Active, incoming.State);
        Assert.Same(incoming, changed);
        var sent = Assert.Single(client.Sent);
        Assert.Equal(CallCommandTypeDto.Accept, sent.Type);
        Assert.Equal("answer-1", sent.Sdp);
        // 媒体面启动：应用对端 offer + 开始采集。
        Assert.Equal(1, bound!.StartCalls);
        Assert.Equal(1, bound.SetRemoteCalls);
        Assert.Contains("offer-1", bound.SetRemoteSdps);
    }

    [Fact]
    public async Task RejectFlow_CalleeRejects_EndsRejected()
    {
        var ctx = new FakeUserContext { UserId = CalleeId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        CallSession? ended = null;
        manager.CallEnded += (_, s) => ended = s;

        client.RaiseCallSignal(Signal("inv-1", CallCommandTypeDto.Invite, from: CallerId, to: CalleeId, revision: 1));
        var incoming = Assert.Single(manager.ActiveCalls);

        await manager.RejectAsync(incoming.CallId);

        Assert.True(incoming.IsTerminal);
        Assert.Equal(CallEndReasonDto.Rejected, incoming.EndReason);
        Assert.Same(incoming, ended);
        Assert.Empty(manager.ActiveCalls);
        Assert.Null(manager.GetCall(incoming.CallId));
        Assert.Equal(CallCommandTypeDto.Reject, client.Sent[^1].Type);
    }

    [Fact]
    public async Task CancelFlow_CallerCancels_EndsCancelled()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        CallSession? ended = null;
        manager.CallEnded += (_, s) => ended = s;

        var session = await manager.StartCallAsync(CalleeId);

        await manager.CancelAsync(session.CallId);

        Assert.True(session.IsTerminal);
        Assert.Equal(CallEndReasonDto.Cancelled, session.EndReason);
        Assert.Same(session, ended);
        Assert.Empty(manager.ActiveCalls);
    }

    [Fact]
    public async Task EndFlow_ActiveCall_EndsHungUp()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());
        CallSession? ended = null;
        manager.CallEnded += (_, s) => ended = s;

        var session = await manager.StartCallAsync(CalleeId);
        // 被叫 accept 信令 → 本端 Active。
        client.RaiseCallSignal(Signal("acc-1", CallCommandTypeDto.Accept, callId: session.CallId, from: CalleeId, to: CallerId, sdp: "answer-1", revision: 2));
        Assert.Equal(CallStateDto.Active, session.State);

        await manager.EndAsync(session.CallId);

        Assert.True(session.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, session.EndReason);
        Assert.Same(session, ended);
        Assert.Empty(manager.ActiveCalls);
    }

    [Fact]
    public async Task ReconnectFlow_ActiveCall_SendsReconnect_StateUnchanged()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());

        var session = await manager.StartCallAsync(CalleeId);
        client.RaiseCallSignal(Signal("acc-1", CallCommandTypeDto.Accept, callId: session.CallId, from: CalleeId, to: CallerId, revision: 2));

        await manager.ReconnectAsync(session.CallId, sdp: "restart-1");

        Assert.Equal(CallStateDto.Active, session.State);
        var request = Assert.Single(client.Sent, r => r.Type == CallCommandTypeDto.Reconnect);
        Assert.Equal("restart-1", request.Sdp);
    }

    [Fact]
    public async Task RingingTimeout_CalleeMissed()
    {
        var ctx = new FakeUserContext { UserId = CalleeId };
        using var client = new FakeCallClient();
        var delay = new ManualDelay();
        using var manager = CreateManager(client, ctx, delay);
        manager.RingingTimeout = TimeSpan.FromSeconds(45);
        CallSession? ended = null;
        manager.CallEnded += (_, s) => ended = s;

        client.RaiseCallSignal(Signal("inv-1", CallCommandTypeDto.Invite, from: CallerId, to: CalleeId, revision: 1));
        var incoming = Assert.Single(manager.ActiveCalls);
        await delay.WaitPendingAsync();

        delay.CompleteAll();
        // 等待完整收敛：IsTerminal 由 ApplyServerState 先行置位（此刻 EndReason 仍为服务端回显的
        // HungUp），需待 RunTimeoutLoopAsync 续延执行 OverrideEndReason(Missed) 后再断言，
        // 避免并行调度下抢先读取中间态。
        await WaitUntilAsync(() => incoming.IsTerminal && incoming.EndReason == CallEndReasonDto.Missed);

        Assert.Equal(CallEndReasonDto.Missed, incoming.EndReason);
        Assert.Same(incoming, ended);
        Assert.Empty(manager.ActiveCalls);
        Assert.Equal(CallCommandTypeDto.End, client.Sent[^1].Type);
    }

    [Fact]
    public async Task ServerConvergence_ServerEndsCallDuringReconnect_ConvergesToEnded()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient();
        client.Responder = request => request.Type == CallCommandTypeDto.Reconnect
            ? RespEnded(request, CallEndReasonDto.HungUp)
            : DefaultResponse(request);
        using var manager = CreateManager(client, ctx, new ManualDelay());
        CallSession? ended = null;
        manager.CallEnded += (_, s) => ended = s;

        var session = await manager.StartCallAsync(CalleeId);
        client.RaiseCallSignal(Signal("acc-1", CallCommandTypeDto.Accept, callId: session.CallId, from: CalleeId, to: CallerId, revision: 2));

        await manager.ReconnectAsync(session.CallId); // 服务端权威：通话已在其他设备结束

        Assert.True(session.IsTerminal);
        Assert.Same(session, ended);
        Assert.Empty(manager.ActiveCalls);
    }

    [Fact]
    public async Task Manager_SupportsCallSignaling_False_Throws()
    {
        var ctx = new FakeUserContext { UserId = CallerId };
        using var client = new FakeCallClient { SupportsCallSignaling = false };
        using var manager = CreateManager(client, ctx, new ManualDelay());

        await Assert.ThrowsAsync<NotSupportedException>(() => manager.StartCallAsync(CalleeId));
    }

    [Fact]
    public async Task Manager_Accept_OnUnknownCall_Throws()
    {
        var ctx = new FakeUserContext { UserId = CalleeId };
        using var client = new FakeCallClient();
        using var manager = CreateManager(client, ctx, new ManualDelay());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.AcceptAsync("unknown-call"));
    }

    // ── 构造与驱动 ─────────────────────────────────────────────

    private static CallSessionManager CreateManager(FakeCallClient client, FakeUserContext ctx, ManualDelay delay)
        => new(client, ctx, delay.Func);

    private static CallSession NewCaller() => new("c", CallRole.Caller, CalleeId);
    private static CallSession NewCallee() => new("c", CallRole.Callee, CallerId);
    private static CallSession NewCalleeRinging() => new("c", CallRole.Callee, CallerId, CallStateDto.Ringing);

    private static CallSignalDto Signal(
        string id,
        CallCommandTypeDto kind,
        string callId = "call-1",
        long from = 0,
        long to = 0,
        string? sdp = null,
        long revision = 0,
        string? @event = null,
        long? participantUserId = null,
        CallGrantDto? grant = null)
        => new()
        {
            SignalId = id,
            CallId = callId,
            FromUserId = from,
            ToUserId = to,
            Kind = kind,
            Sdp = sdp ?? string.Empty,
            Revision = revision,
            Event = @event,
            ParticipantUserId = participantUserId,
            Grant = grant
        };

    private static CallCommandResponseDto Resp(CallSession s, CallStateDto state, CallEndReasonDto reason = CallEndReasonDto.None, long revision = 1)
        => new() { RequestId = "r", CallId = s.CallId, Succeeded = true, State = state, EndReason = reason, Revision = revision };

    private static CallCommandResponseDto RespEnded(CallCommandRequestDto request, CallEndReasonDto reason)
        => new() { RequestId = request.RequestId, CallId = request.CallId, Succeeded = true, State = CallStateDto.Ended, EndReason = reason, Revision = request.Revision };

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

    private static CallGrantDto NewGrant() => new()
    {
        CallId = "call-1",
        CallerUserId = CallerId,
        CalleeUserId = CalleeId,
        ExpiresAtMs = 1_900_000_000_000L,
        Nonce = "nonce-1",
        Signature = "sig-1"
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
        public Func<CallCommandRequestDto, CallCommandResponseDto>? Responder { get; set; }
        public Exception? ThrowOnSend { get; set; }

        public Task<CallCommandResponseDto> SendCallCommandAsync(CallCommandRequestDto request, CancellationToken ct = default)
        {
            Sent.Add(request);
            if (ThrowOnSend is not null)
                throw ThrowOnSend;
            return Task.FromResult(Responder?.Invoke(request) ?? DefaultResponse(request));
        }

        public void RaiseCallSignal(CallSignalDto signal)
            => CallSignalReceived?.Invoke(this, signal);

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
