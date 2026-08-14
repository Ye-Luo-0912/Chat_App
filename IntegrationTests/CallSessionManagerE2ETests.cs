using System.Buffers;
using System.Collections.Concurrent;
using Chat_App.Infrastructure.Serialization;
using ChatApp.Shared.Protocol.Tcp;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// CALL-E2E-2 客户端通话会话管理器端到端验证（over 真实 wire）。
/// <para>
/// 双设备 harness：主叫/被叫各自持有一个真实 <see cref="ChatSessionClient"/>（协议包编码/解码、
/// camelCase wire 序列化、RequestId 配对、CallSignal S2C push 全走真实实现），内存"网关路由"
/// 把一端的 CallCommandRequest 转换为权威 CallCommandResponse 回给发送方，同时以 CallSignal
/// 转发给对端。两端各挂一个 <see cref="CallSessionManager"/>，验证完整通话流：邀请 → 来电 →
/// 应答/拒绝/取消 → Active 媒体面启动（SDP 双向透传）→ 挂断终态收敛。
/// </para>
/// </summary>
public sealed class CallSessionManagerE2ETests
{
    private const long CallerUserId = 8001;
    private const long CalleeUserId = 8002;

    [Fact]
    public async Task FullCallFlow_InviteAccept_ActiveMedia_End_OverRealWire()
    {
        var serializer = new JsonPacketBodySerializer();
        var codec = new MessagePacketCodec();
        using var tcpA = new ScriptedTcpClient();
        using var tcpB = new ScriptedTcpClient();
        using var clientA = new ChatSessionClient(tcpA, codec, serializer);
        using var clientB = new ChatSessionClient(tcpB, codec, serializer);

        SetupAutoAuth(tcpA, serializer, CallerUserId);
        SetupAutoAuth(tcpB, serializer, CalleeUserId);
        await ConnectAndAuthenticateAsync(clientA, tcpA, CallerUserId);
        await ConnectAndAuthenticateAsync(clientB, tcpB, CalleeUserId);

        WireFullDuplexCall(tcpA, tcpB, serializer, CallerUserId, CalleeUserId);

        using var callerManager = new CallSessionManager(clientA, new StubUserContext(CallerUserId));
        using var calleeManager = new CallSessionManager(clientB, new StubUserContext(CalleeUserId));
        var callerMedia = new FakeMediaSession { Offer = "offer-1", Answer = "answer-1" };
        var calleeMedia = new FakeMediaSession { Offer = "offer-1", Answer = "answer-1" };
        callerManager.MediaFactory = _ => callerMedia;
        calleeManager.MediaFactory = _ => calleeMedia;

        var incomingTcs = new TaskCompletionSource<CallSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        calleeManager.IncomingCall += (_, s) => incomingTcs.TrySetResult(s);

        // ── 主叫发起邀请 ──
        var callerSession = await callerManager.StartCallAsync(CalleeUserId, sdpOffer: "offer-1");
        Assert.Equal(CallStateDto.Ringing, callerSession.State);

        // ── 被叫经真实 wire 收到来电 ──
        var calleeSession = await incomingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CallRole.Callee, calleeSession.Role);
        Assert.Equal(CallerUserId, calleeSession.PeerUserId);
        Assert.Equal(CallStateDto.Ringing, calleeSession.State);
        Assert.Equal("offer-1", calleeSession.RemoteSdp);

        // ── 被叫应答 → 双端 Active + 媒体面启动 ──
        await calleeManager.AcceptAsync(calleeSession.CallId, sdpAnswer: "answer-1");
        Assert.Equal(CallStateDto.Active, calleeSession.State);

        await WaitUntilAsync(() => callerSession.State == CallStateDto.Active);
        Assert.Equal("answer-1", callerSession.RemoteSdp);

        // 媒体面：被叫应用对端 offer、主叫应用对端 answer，双端均开始采集。
        Assert.Equal(1, calleeMedia.StartCalls);
        Assert.Contains("offer-1", calleeMedia.SetRemoteSdps);
        Assert.Equal(1, callerMedia.StartCalls);
        Assert.Contains("answer-1", callerMedia.SetRemoteSdps);

        // ── 主叫挂断 → 双端终态收敛 ──
        CallSession? callerEnded = null, calleeEnded = null;
        callerManager.CallEnded += (_, s) => callerEnded = s;
        calleeManager.CallEnded += (_, s) => calleeEnded = s;

        await callerManager.EndAsync(callerSession.CallId);

        Assert.True(callerSession.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, callerSession.EndReason);
        Assert.Same(callerSession, callerEnded);
        Assert.Empty(callerManager.ActiveCalls);

        await WaitUntilAsync(() => calleeSession.IsTerminal);
        Assert.Equal(CallEndReasonDto.HungUp, calleeSession.EndReason);
        Assert.Same(calleeSession, calleeEnded);
        Assert.Empty(calleeManager.ActiveCalls);
        // 终态收尾：媒体面停止并释放。
        Assert.True(callerMedia.StopCalls >= 1);
        Assert.True(calleeMedia.StopCalls >= 1);
    }

    [Fact]
    public async Task CallerCancelFlow_OverRealWire_CalleeConvergesToCancelled()
    {
        var serializer = new JsonPacketBodySerializer();
        var codec = new MessagePacketCodec();
        using var tcpA = new ScriptedTcpClient();
        using var tcpB = new ScriptedTcpClient();
        using var clientA = new ChatSessionClient(tcpA, codec, serializer);
        using var clientB = new ChatSessionClient(tcpB, codec, serializer);

        SetupAutoAuth(tcpA, serializer, CallerUserId);
        SetupAutoAuth(tcpB, serializer, CalleeUserId);
        await ConnectAndAuthenticateAsync(clientA, tcpA, CallerUserId);
        await ConnectAndAuthenticateAsync(clientB, tcpB, CalleeUserId);
        WireFullDuplexCall(tcpA, tcpB, serializer, CallerUserId, CalleeUserId);

        using var callerManager = new CallSessionManager(clientA, new StubUserContext(CallerUserId));
        using var calleeManager = new CallSessionManager(clientB, new StubUserContext(CalleeUserId));
        var incomingTcs = new TaskCompletionSource<CallSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        calleeManager.IncomingCall += (_, s) => incomingTcs.TrySetResult(s);

        var callerSession = await callerManager.StartCallAsync(CalleeUserId);
        var calleeSession = await incomingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        CallSession? calleeEnded = null;
        calleeManager.CallEnded += (_, s) => calleeEnded = s;

        await callerManager.CancelAsync(callerSession.CallId);

        Assert.True(callerSession.IsTerminal);
        Assert.Equal(CallEndReasonDto.Cancelled, callerSession.EndReason);
        await WaitUntilAsync(() => calleeSession.IsTerminal);
        Assert.Equal(CallEndReasonDto.Cancelled, calleeSession.EndReason);
        Assert.Same(calleeSession, calleeEnded);
        Assert.Empty(calleeManager.ActiveCalls);
    }

    [Fact]
    public async Task CalleeRejectFlow_OverRealWire_CallerConvergesToRejected()
    {
        var serializer = new JsonPacketBodySerializer();
        var codec = new MessagePacketCodec();
        using var tcpA = new ScriptedTcpClient();
        using var tcpB = new ScriptedTcpClient();
        using var clientA = new ChatSessionClient(tcpA, codec, serializer);
        using var clientB = new ChatSessionClient(tcpB, codec, serializer);

        SetupAutoAuth(tcpA, serializer, CallerUserId);
        SetupAutoAuth(tcpB, serializer, CalleeUserId);
        await ConnectAndAuthenticateAsync(clientA, tcpA, CallerUserId);
        await ConnectAndAuthenticateAsync(clientB, tcpB, CalleeUserId);
        WireFullDuplexCall(tcpA, tcpB, serializer, CallerUserId, CalleeUserId);

        using var callerManager = new CallSessionManager(clientA, new StubUserContext(CallerUserId));
        using var calleeManager = new CallSessionManager(clientB, new StubUserContext(CalleeUserId));
        var incomingTcs = new TaskCompletionSource<CallSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        calleeManager.IncomingCall += (_, s) => incomingTcs.TrySetResult(s);

        var callerSession = await callerManager.StartCallAsync(CalleeUserId);
        var calleeSession = await incomingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        CallSession? callerEnded = null;
        callerManager.CallEnded += (_, s) => callerEnded = s;

        await calleeManager.RejectAsync(calleeSession.CallId);

        Assert.True(calleeSession.IsTerminal);
        Assert.Equal(CallEndReasonDto.Rejected, calleeSession.EndReason);
        await WaitUntilAsync(() => callerSession.IsTerminal);
        Assert.Equal(CallEndReasonDto.Rejected, callerSession.EndReason);
        Assert.Same(callerSession, callerEnded);
        Assert.Empty(callerManager.ActiveCalls);
    }

    // ── 内存网关路由：一端 CallCommandRequest → 权威响应回送 + CallSignal 转发对端 ──

    private static void WireFullDuplexCall(
        ScriptedTcpClient tcpA, ScriptedTcpClient tcpB,
        JsonPacketBodySerializer serializer, long userA, long userB)
    {
        WireDirection(tcpA, tcpB, serializer, peerUserId: userB);
        WireDirection(tcpB, tcpA, serializer, peerUserId: userA);
    }

    private static void WireDirection(
        ScriptedTcpClient sender, ScriptedTcpClient receiver,
        JsonPacketBodySerializer serializer, long peerUserId)
    {
        sender.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.CallCommandRequest)
                return;
            var req = serializer.Deserialize<CallCommandRequestDto>(new ReadOnlySequence<byte>(body));
            if (req is null || string.IsNullOrWhiteSpace(req.RequestId))
                return;

            // 1) 权威响应回给发送方（同一 RequestId 精确配对）。
            InjectPacket(sender, serializer, PacketCommand.CallCommandResponse, ToResponse(req));
            // 2) 以 CallSignal S2C push 转发给对端（SignalId 取 command id，天然幂等）。
            InjectPacket(receiver, serializer, PacketCommand.CallSignal, ToSignal(req, peerUserId));
        };
    }

    private static CallCommandResponseDto ToResponse(CallCommandRequestDto req) => new()
    {
        RequestId = req.RequestId,
        CallId = req.CallId,
        Succeeded = true,
        State = req.Type switch
        {
            CallCommandTypeDto.Accept => CallStateDto.Active,
            CallCommandTypeDto.Reconnect => CallStateDto.Active,
            CallCommandTypeDto.Reject => CallStateDto.Ended,
            CallCommandTypeDto.Cancel => CallStateDto.Ended,
            CallCommandTypeDto.End => CallStateDto.Ended,
            _ => CallStateDto.Ringing,
        },
        EndReason = req.Type switch
        {
            CallCommandTypeDto.Reject => CallEndReasonDto.Rejected,
            CallCommandTypeDto.Cancel => CallEndReasonDto.Cancelled,
            CallCommandTypeDto.End => CallEndReasonDto.HungUp,
            _ => CallEndReasonDto.None,
        },
        Revision = req.Revision,
    };

    private static CallSignalDto ToSignal(CallCommandRequestDto req, long peerUserId) => new()
    {
        SignalId = req.CommandId,
        CallId = req.CallId,
        FromUserId = req.ActorUserId,
        ToUserId = peerUserId,
        Kind = req.Type,
        Sdp = req.Sdp ?? string.Empty,
        Revision = req.Revision,
        OccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    // ── 测试底座 ──

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
