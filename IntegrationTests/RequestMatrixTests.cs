using Chat_App.Infrastructure.Serialization;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using System.Buffers;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// S0 验收：九类请求 × 成功 / 拒绝 / 超时 / 断线 完整测试矩阵。
/// 每个场景结束后断言 PendingRequestCount == 0（pending 字典无残留）；
/// 超时场景通过 RequestTimeoutScale 将内部超时缩放到毫秒级，不使用固定 Delay 掩盖竞态。
/// 另含：通用 Error（带 RequestId）与 AuthenticationFailed 分离、鉴权 TCS 各失败路径、并发隔离。
/// </summary>
public class RequestMatrixTests
{
    private const long OwnerId = 7101;
    private const long PeerId = 9101;
    private const string ConvId = "conv-7101-9101";

    public static TheoryData<string> Kinds { get; } = new()
    {
        "list", "prefs", "recall", "edit", "presence", "sync", "history", "receipt", "markRead"
    };

    // 业务层拒绝（Succeeded/Accepted=false）：presence 无错误通道，走通用 Error 路径
    public static TheoryData<string> BusinessRejectKinds { get; } = new()
    {
        "list", "prefs", "recall", "edit", "sync", "history", "receipt", "markRead"
    };

    // ── 成功：响应按 RequestId 精确配对，pending 清零 ──

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Success_Response_Pairs_And_Pending_Empties(string kind)
    {
        await RunAsync(async (client, server) =>
        {
            var resp = await InvokeAsync(client, kind);
            Assert.NotNull(resp);
            var seen = server.Frames.Single(f => f.Command == RequestCommandOf(kind));
            Assert.Equal(seen.RequestId, RequestIdOf(resp));
            Assert.False(string.IsNullOrWhiteSpace(seen.RequestId));
            Assert.Equal(0, client.PendingRequestCount);
        });
    }

    // ── 业务层拒绝：Succeeded=false 响应原样返回，不抛异常，pending 清零 ──

    [Theory]
    [MemberData(nameof(BusinessRejectKinds))]
    public async Task BusinessReject_Returns_Error_Fields_And_Pending_Empties(string kind)
    {
        await RunAsync(async (client, server) =>
        {
            server.Behavior = Behavior.BusinessReject;
            var resp = await InvokeAsync(client, kind);
            var (ok, errorMessage) = OutcomeOf(resp);
            Assert.False(ok);
            Assert.Equal("BUSINESS_REJECT", errorMessage);
            Assert.Equal(0, client.PendingRequestCount);
        });
    }

    // ── 协议层拒绝：Error 包携带 RequestId → 仅该请求抛 ProtocolRequestException，pending 清零 ──

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ProtocolReject_Error_Packet_Fails_Only_That_Request(string kind)
    {
        await RunAsync(async (client, server) =>
        {
            var authFailed = 0;
            client.AuthenticationFailed += (_, _) => authFailed++;

            server.Behavior = Behavior.ErrorReject;
            var ex = await Assert.ThrowsAsync<ProtocolRequestException>(() => InvokeAsync(client, kind));
            Assert.Equal("REJECTED", ex.Error.ErrorCode);
            Assert.Equal("服务器拒绝该请求", ex.Error.ErrorMessage);
            Assert.False(ex.Error.IsFatal);
            // 通用 Error 不得误触发 AuthenticationFailed
            Assert.Equal(0, authFailed);
            Assert.Equal(0, client.PendingRequestCount);
        });
    }

    // ── 超时：服务器不响应 → 内部超时失败，pending 清零（超时缩放至毫秒级） ──

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Timeout_Server_Silent_Fails_Request_And_Pending_Empties(string kind)
    {
        await RunAsync(async (client, server) =>
        {
            client.RequestTimeoutScale = 0.02; // 8s→160ms / 15s→300ms
            server.Behavior = Behavior.Ignore;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(client, kind));
            Assert.Equal(0, client.PendingRequestCount);
        });
    }

    // ── 断线：连接断开 → 全部在途请求以 IOException 失败，pending 清零 ──

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Disconnect_Fails_Pending_With_IOException_And_Empties(string kind)
    {
        await RunAsync(async (client, server) =>
        {
            server.Behavior = Behavior.Ignore; // 请求挂起，不提前回显
            var request = InvokeAsync(client, kind);
            await WaitForFrameAsync(server, kind);
            server.Tcp.Disconnect("验收断线");

            var ex = await Assert.ThrowsAsync<IOException>(() => request);
            Assert.Contains("验收断线", ex.Message);
            Assert.Equal(0, client.PendingRequestCount);
        });
    }

    // ── 鉴权 TCS：超时 / 拒绝 / 断线 / 致命错误 / 非致命错误分离 ──

    [Fact]
    public async Task Auth_Timeout_Raises_TimeoutException_And_Event()
    {
        await RunAsync(async (client, server) =>
        {
            var authFailed = 0;
            client.AuthenticationFailed += (_, msg) => authFailed++;
            client.RequestTimeoutScale = 0.02; // 5s→100ms

            var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.AuthenticateAsync("token", OwnerId, null, null));
            Assert.Contains("鉴权超时", ex.Message);
            Assert.Equal(1, authFailed);
        }, autoAuth: false);
    }

    [Fact]
    public async Task Auth_Rejected_Server_Fails_With_UnauthorizedAccessException()
    {
        await RunAsync(async (client, server) =>
        {
            var authFailed = 0;
            client.AuthenticationFailed += (_, msg) =>
            {
                authFailed++;
                Assert.Contains("令牌已过期", msg);
            };
            server.Behavior = Behavior.AuthReject;

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => client.AuthenticateAsync("token", OwnerId, null, null));
            Assert.Equal(1, authFailed);
            Assert.False(client.IsAuthenticated);
        }, autoAuth: false);
    }

    [Fact]
    public async Task Auth_Disconnected_Fails_With_IOException()
    {
        await RunAsync(async (client, server) =>
        {
            var authTask = client.AuthenticateAsync("token", OwnerId, null, null);
            await WaitForFrameAsync(server, "auth");
            server.Tcp.Disconnect("鉴权中断线");

            var ex = await Assert.ThrowsAsync<IOException>(() => authTask);
            Assert.Contains("鉴权中断线", ex.Message);
            Assert.False(client.IsAuthenticated);
            Assert.Equal(0, client.PendingRequestCount);
        }, autoAuth: false);
    }

    [Fact]
    public async Task Auth_FatalError_Fails_With_ProtocolRequestException()
    {
        await RunAsync(async (client, server) =>
        {
            var authFailed = 0;
            client.AuthenticationFailed += (_, _) => authFailed++;
            server.Behavior = Behavior.FatalError;

            var ex = await Assert.ThrowsAsync<ProtocolRequestException>(
                () => client.AuthenticateAsync("token", OwnerId, null, null));
            Assert.True(ex.Error.IsFatal);
            Assert.Equal(1, authFailed);
        }, autoAuth: false);
    }

    [Fact]
    public async Task Auth_NonFatalError_Does_Not_Abort_Auth()
    {
        await RunAsync(async (client, server) =>
        {
            var authFailed = 0;
            var protocolErrors = 0;
            client.AuthenticationFailed += (_, _) => authFailed++;
            client.ProtocolError += (_, _) => protocolErrors++;
            server.Behavior = Behavior.NonFatalError;
            client.RequestTimeoutScale = 0.02;

            // 非致命错误只广播 ProtocolError；鉴权继续等待，最终超时兜底
            var ex = await Assert.ThrowsAsync<TimeoutException>(
                () => client.AuthenticateAsync("token", OwnerId, null, null));
            Assert.Equal(1, protocolErrors);
            Assert.Equal(1, authFailed);
            Assert.Contains("鉴权超时", ex.Message);
        }, autoAuth: false);
    }

    // ── 并发隔离：Error 只失败目标请求，并发的同型请求正常完成 ──

    [Fact]
    public async Task Error_Packet_Fails_Only_Target_Request_Concurrent_Sibling_Completes()
    {
        await RunAsync(async (client, server) =>
        {
            server.Behavior = Behavior.Ignore; // 手动控制两条请求各自的应答
            var t1 = client.QueryPresenceAsync([PeerId]);
            var t2 = client.QueryPresenceAsync([PeerId]);

            // 第一帧（t1）走 Error 拒绝；第二帧（t2）走成功回显
            await WaitForFrameCountAsync(server, PacketCommand.PresenceQuery, 2);
            var frames = server.Frames.Where(f => f.Command == PacketCommand.PresenceQuery).ToList();
            server.InjectError(frames[0].RequestId, "REJECTED", "目标请求被拒", isFatal: false);
            server.InjectPresenceSuccess(frames[1].RequestId);

            var ex = await Assert.ThrowsAsync<ProtocolRequestException>(() => t1);
            Assert.Equal("REJECTED", ex.Error.ErrorCode);

            var resp2 = await t2.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(frames[1].RequestId, resp2.RequestId);
            Assert.Equal(0, client.PendingRequestCount);
        });
    }

    // ── 测试底座 ──

    private static async Task RunAsync(Func<ChatSessionClient, MatrixServer, Task> act, bool autoAuth = true)
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var client = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        var server = new MatrixServer(tcp, serializer);
        if (autoAuth)
            SetupAutoAuth(tcp, serializer, OwnerId);

        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        if (autoAuth)
        {
            await client.AuthenticateAsync("token", OwnerId, null, null);
            Assert.True(client.IsAuthenticated);
        }

        await act(client, server);
    }

    private static async Task<object?> InvokeAsync(ChatSessionClient client, string kind) => kind switch
    {
        "list" => (object?)await client.QueryConversationListAsync(limit: 10),
        "prefs" => (object?)await client.SetConversationPrefsAsync(ConvId, pinned: true),
        "recall" => (object?)await client.RecallMessageAsync("msg-1"),
        "edit" => (object?)await client.EditMessageAsync("msg-1", "编辑后内容"),
        "presence" => (object?)await client.QueryPresenceAsync([PeerId]),
        "sync" => (object?)await client.QuerySyncBootstrapAsync(),
        "history" => (object?)await client.QueryMessageHistoryAsync(ConvId, limit: 10),
        "receipt" => (object?)await client.SendMessageReceiptAsync(ConvId, "msg-1", 1234567890),
        "markRead" => (object?)await client.MarkConversationReadAsync(ConvId, "msg-1", 1234567890),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static PacketCommand RequestCommandOf(string kind) => kind switch
    {
        "list" => PacketCommand.ConversationListRequest,
        "prefs" => PacketCommand.ConversationSetPrefsRequest,
        "recall" => PacketCommand.MessageRecallRequest,
        "edit" => PacketCommand.MessageEditRequest,
        "presence" => PacketCommand.PresenceQuery,
        "sync" => PacketCommand.SyncBootstrapRequest,
        "history" => PacketCommand.MessageHistoryRequest,
        "receipt" => PacketCommand.MessageReceipt,
        "markRead" => PacketCommand.ConversationMarkReadRequest,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string? RequestIdOf(object? resp) => resp switch
    {
        ConversationListResponseDto r => r.RequestId,
        ConversationSetPrefsResponseDto r => r.RequestId,
        MessageRecallAcknowledgementDto r => r.RequestId,
        MessageEditAcknowledgementDto r => r.RequestId,
        PresenceSnapshotResponseDto r => r.RequestId,
        SyncBootstrapResponseDto r => r.RequestId,
        MessageHistoryPageDto r => r.RequestId,
        MessageReceiptAckDto r => r.RequestId,
        ConversationMarkReadResponseDto r => r.RequestId,
        _ => null
    };

    private static (bool Ok, string? ErrorMessage) OutcomeOf(object? resp) => resp switch
    {
        ConversationListResponseDto r => (r.Succeeded, r.ErrorMessage),
        ConversationSetPrefsResponseDto r => (r.Succeeded, r.ErrorMessage),
        MessageRecallAcknowledgementDto r => (r.Succeeded, r.ErrorMessage),
        MessageEditAcknowledgementDto r => (r.Succeeded, r.ErrorMessage),
        SyncBootstrapResponseDto r => (r.Succeeded, r.ErrorMessage),
        MessageHistoryPageDto r => (r.Succeeded, r.ErrorMessage),
        MessageReceiptAckDto r => (r.Accepted, r.ErrorMessage),
        ConversationMarkReadResponseDto r => (r.Succeeded, r.ErrorMessage),
        _ => (false, null)
    };

    private static async Task WaitForFrameAsync(MatrixServer server, string kind)
    {
        var command = kind == "auth" ? PacketCommand.AuthRequest : RequestCommandOf(kind);
        await WaitForFrameCountAsync(server, command, 1);
    }

    private static async Task WaitForFrameCountAsync(MatrixServer server, PacketCommand command, int count)
    {
        for (var i = 0; i < 500; i++)
        {
            if (server.Frames.Count(f => f.Command == command) >= count)
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"等待帧超时: {command} × {count}");
    }

    private static void InjectPacket<T>(
        ScriptedTcpClient tcp,
        IPacketBodySerializer serializer,
        PacketCommand command,
        T? payload)
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

    /// <summary>设置自动鉴权响应：收到 AuthRequest 后立即注入 AuthResponse。</summary>
    private static void SetupAutoAuth(ScriptedTcpClient tcp, IPacketBodySerializer serializer, long userId)
    {
        tcp.OnFrameSent += (cmd, _) =>
        {
            if (cmd == PacketCommand.AuthRequest)
            {
                InjectPacket(tcp, serializer, PacketCommand.AuthResponse,
                    new AuthResponseDto { Success = true, UserId = userId });
            }
        };
    }

    private enum Behavior
    {
        /// <summary>回显成功响应。</summary>
        Success,
        /// <summary>回显 Succeeded/Accepted=false 的业务响应。</summary>
        BusinessReject,
        /// <summary>回显带 RequestId 的 Error 包（协议层拒绝）。</summary>
        ErrorReject,
        /// <summary>不回显任何内容（超时场景）。</summary>
        Ignore,
        /// <summary>鉴权响应 Success=false。</summary>
        AuthReject,
        /// <summary>鉴权期间注入 IsFatal=true 的 Error 包。</summary>
        FatalError,
        /// <summary>鉴权期间注入 IsFatal=false 的 Error 包（只广播，不打断鉴权）。</summary>
        NonFatalError
    }

    /// <summary>模拟服务器：按场景回显九类请求 / 鉴权请求。</summary>
    private sealed class MatrixServer
    {
        public ScriptedTcpClient Tcp { get; }
        public List<(PacketCommand Command, string RequestId)> Frames { get; } = [];
        public Behavior Behavior { get; set; } = Behavior.Success;

        private readonly IPacketBodySerializer _serializer;

        public MatrixServer(ScriptedTcpClient tcp, IPacketBodySerializer serializer)
        {
            Tcp = tcp;
            _serializer = serializer;
            tcp.OnFrameSent += (cmd, body) =>
            {
                try
                {
                    switch (cmd)
                    {
                        case PacketCommand.AuthRequest:
                            Frames.Add((cmd, string.Empty));
                            if (Behavior == Behavior.AuthReject)
                            {
                                Inject(PacketCommand.AuthResponse, new AuthResponseDto
                                {
                                    Success = false,
                                    UserId = OwnerId,
                                    ErrorMessage = "令牌已过期"
                                });
                            }
                            else if (Behavior == Behavior.FatalError)
                            {
                                InjectError(null, "AUTH_FATAL", "服务器致命错误：鉴权会话失效", isFatal: true);
                            }
                            else if (Behavior == Behavior.NonFatalError)
                            {
                                InjectError(null, "BUSINESS_WARN", "非致命业务警告", isFatal: false);
                            }
                            return;

                        case PacketCommand.ConversationListRequest:
                        {
                            var listRequestId = Record<ConversationListRequestDto>(cmd, body, out var listReq);
                            Respond(PacketCommand.ConversationListPage, listRequestId, new ConversationListResponseDto
                            {
                                RequestId = listRequestId,
                                Succeeded = true,
                                Items = []
                            });
                            return;
                        }
                        case PacketCommand.ConversationSetPrefsRequest:
                        {
                            var prefsRequestId = Record<ConversationSetPrefsRequestDto>(cmd, body, out var prefsReq);
                            Respond(PacketCommand.ConversationSetPrefsResponse, prefsRequestId, new ConversationSetPrefsResponseDto
                            {
                                RequestId = prefsRequestId,
                                Succeeded = true,
                                ConversationId = prefsReq.ConversationId,
                                IsPinned = prefsReq.Pinned ?? false,
                                IsMuted = false
                            });
                            return;
                        }
                        case PacketCommand.MessageRecallRequest:
                        {
                            var recallRequestId = Record<MessageRecallRequestDto>(cmd, body, out var recallReq);
                            Respond(PacketCommand.MessageRecallAck, recallRequestId, new MessageRecallAcknowledgementDto
                            {
                                RequestId = recallRequestId,
                                Succeeded = true,
                                MessageId = recallReq.MessageId,
                                ConversationId = ConvId,
                                RecalledAtMs = 1234567890
                            });
                            return;
                        }
                        case PacketCommand.MessageEditRequest:
                        {
                            var editRequestId = Record<MessageEditRequestDto>(cmd, body, out var editReq);
                            Respond(PacketCommand.MessageEditAck, editRequestId, new MessageEditAcknowledgementDto
                            {
                                RequestId = editRequestId,
                                Succeeded = true,
                                MessageId = editReq.MessageId,
                                ConversationId = ConvId,
                                Content = editReq.Content,
                                EditVersion = 2,
                                EditedAtMs = 1234567890
                            });
                            return;
                        }
                        case PacketCommand.PresenceQuery:
                        {
                            var presenceRequestId = Record<PresenceQueryRequestDto>(cmd, body, out var presenceReq);
                            Respond(PacketCommand.PresenceSnapshot, presenceRequestId, new PresenceSnapshotResponseDto
                            {
                                RequestId = presenceRequestId,
                                Items = [new PresenceSnapshotItemDto { UserId = PeerId, IsOnline = true }]
                            });
                            return;
                        }
                        case PacketCommand.SyncBootstrapRequest:
                        {
                            var syncRequestId = Record<SyncBootstrapRequestDto>(cmd, body, out var syncReq);
                            Respond(PacketCommand.SyncBootstrapResponse, syncRequestId, new SyncBootstrapResponseDto
                            {
                                RequestId = syncRequestId,
                                Succeeded = true,
                                Conversations = []
                            });
                            return;
                        }
                        case PacketCommand.MessageHistoryRequest:
                        {
                            var historyRequestId = Record<MessageHistoryRequestDto>(cmd, body, out var historyReq);
                            Respond(PacketCommand.MessageHistoryPage, historyRequestId, new MessageHistoryPageDto
                            {
                                RequestId = historyRequestId,
                                Succeeded = true,
                                ConversationId = historyReq.ConversationId,
                                Items = [],
                                HasMore = false
                            });
                            return;
                        }
                        case PacketCommand.MessageReceipt:
                        {
                            var receiptRequestId = Record<MessageReceiptDto>(cmd, body, out var receiptReq);
                            Respond(PacketCommand.MessageReceiptAck, receiptRequestId, new MessageReceiptAckDto
                            {
                                RequestId = receiptRequestId,
                                Accepted = true
                            });
                            return;
                        }
                        case PacketCommand.ConversationMarkReadRequest:
                        {
                            var markReadRequestId = Record<ConversationMarkReadRequestDto>(cmd, body, out var markReadReq);
                            Respond(PacketCommand.ConversationMarkReadResponse, markReadRequestId, new ConversationMarkReadResponseDto
                            {
                                RequestId = markReadRequestId,
                                Succeeded = true,
                                ConversationId = markReadReq.ConversationId,
                                UnreadCount = 0
                            });
                            return;
                        }
                    }
                }
                catch
                {
                    // 模拟服务器解析失败：忽略该帧
                }
            };
        }

        private string Record<T>(PacketCommand cmd, ReadOnlyMemory<byte> body, out T req)
            where T : class, IRequestDto
        {
            req = _serializer.Deserialize<T>(new ReadOnlySequence<byte>(body))!;
            var requestId = req.RequestId ?? string.Empty;
            Frames.Add((cmd, requestId));
            return requestId;
        }

        /// <summary>按场景统一应答：成功回显 / 业务拒绝 / Error 包拒绝 / 静默。</summary>
        private void Respond<T>(PacketCommand responseCommand, string? requestId, T successPayload)
        {
            switch (Behavior)
            {
                case Behavior.Success:
                    Inject(responseCommand, successPayload);
                    break;
                case Behavior.BusinessReject:
                    Inject(responseCommand, WithBusinessReject(successPayload!));
                    break;
                case Behavior.ErrorReject:
                    InjectError(requestId, "REJECTED", "服务器拒绝该请求", isFatal: false);
                    break;
                case Behavior.Ignore:
                case Behavior.AuthReject:
                case Behavior.FatalError:
                case Behavior.NonFatalError:
                    break;
            }
        }

        /// <summary>反射式将成功 DTO 改写为业务拒绝（Succeeded/Accepted=false）。</summary>
        private static object WithBusinessReject(object successPayload)
        {
            if (successPayload is MessageReceiptAckDto receipt)
            {
                receipt.Accepted = false;
                receipt.ErrorCode = "BUSINESS_REJECT";
                receipt.ErrorMessage = "BUSINESS_REJECT";
                return receipt;
            }
            var type = successPayload.GetType();
            foreach (var prop in new[] { type.GetProperty("Succeeded") })
            {
                prop?.SetValue(successPayload, false);
            }
            type.GetProperty("ErrorCode")?.SetValue(successPayload, "BUSINESS_REJECT");
            type.GetProperty("ErrorMessage")?.SetValue(successPayload, "BUSINESS_REJECT");
            return successPayload;
        }

        public void InjectError(string? requestId, string errorCode, string message, bool isFatal)
        {
            Inject(PacketCommand.Error, new ProtocolErrorDto
            {
                RequestId = requestId,
                Command = PacketCommand.Error,
                ErrorCode = errorCode,
                ErrorMessage = message,
                IsFatal = isFatal
            });
        }

        public void InjectPresenceSuccess(string requestId) =>
            Inject(PacketCommand.PresenceSnapshot, new PresenceSnapshotResponseDto
            {
                RequestId = requestId,
                Items = [new PresenceSnapshotItemDto { UserId = PeerId, IsOnline = true }]
            });

        public void Inject<T>(PacketCommand command, T? payload) =>
            InjectPacket(Tcp, _serializer, command, payload);
    }

    /// <summary>可控的假 TCP 客户端：解析每次发送的帧，触发回调让测试模拟服务端响应。</summary>
    private sealed class ScriptedTcpClient : ITcpClient
    {
        private volatile bool _connected;

        public bool IsConnected => _connected;

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;

        /// <summary>每次发送一帧后触发（command, bodyBytes）。</summary>
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
                OnFrameSent?.Invoke(pkt.Command, pkt.Body.ToArray());
            }

            return Task.CompletedTask;
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(-1, token);

        public void Disconnect(string? reason = null)
        {
            if (!_connected)
                return;
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason));
        }

        public void InjectData(ReadOnlyMemory<byte> chunk)
        {
            OnDataChunkReceived?.Invoke(this, chunk);
        }

        public void Dispose()
        {
            _connected = false;
            GC.SuppressFinalize(this);
        }
    }
}

