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
/// 请求模板配对验收测试。
/// 验收场景：9 类请求的 RequestId 只由发送模板生成一次（请求帧可见、与 pending 键一致），
/// 服务器原样回显后客户端按 Id 精确配对返回；并发同类型请求互不串扰；
/// 服务器回显错误 Id 时请求不配对、最终超时（不误返回他请求的响应）。
/// </summary>
public class RequestTemplateE2ETests
{
    private const long OwnerId = 7101;
    private const long PeerId = 9101;
    private const string ConvId = "conv-7101-9101";

    // ── 9 类请求正例：发出帧 RequestId 非空且唯一，服务器回显后配对成功 ──

    [Fact]
    public async Task ConversationList_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.QueryConversationListAsync(limit: 30);
            Assert.True(resp.Succeeded);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.ConversationListRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
            Assert.False(string.IsNullOrWhiteSpace(seen.RequestId));
        });
    }

    [Fact]
    public async Task ConversationSetPrefs_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.SetConversationPrefsAsync(ConvId, pinned: true);
            Assert.True(resp.Succeeded);
            Assert.True(resp.IsPinned);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.ConversationSetPrefsRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    [Fact]
    public async Task MessageRecall_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.RecallMessageAsync("msg-1");
            Assert.True(resp.Succeeded);
            Assert.Equal("msg-1", resp.MessageId);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.MessageRecallRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    [Fact]
    public async Task MessageEdit_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.EditMessageAsync("msg-1", "编辑后内容");
            Assert.True(resp.Succeeded);
            Assert.Equal(2, resp.EditVersion);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.MessageEditRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    [Fact]
    public async Task PresenceQuery_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.QueryPresenceAsync([PeerId]);
            Assert.Single(resp.Items);
            Assert.True(resp.Items[0].IsOnline);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.PresenceQuery);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    [Fact]
    public async Task SyncBootstrap_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.QuerySyncBootstrapAsync();
            Assert.True(resp.Succeeded);
            Assert.Equal(1, resp.Conversations.Count);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.SyncBootstrapRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    [Fact]
    public async Task MessageHistory_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.QueryMessageHistoryAsync(ConvId, limit: 20);
            Assert.True(resp.Succeeded);
            Assert.Single(resp.Items);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.MessageHistoryRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    [Fact]
    public async Task MessageReceipt_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.SendMessageReceiptAsync(ConvId, "msg-1", 1234567890);
            Assert.True(resp.Accepted);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.MessageReceipt);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    [Fact]
    public async Task ConversationMarkRead_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.MarkConversationReadAsync(ConvId, "msg-1", 1234567890);
            Assert.True(resp.Succeeded);
            Assert.Equal(0, resp.UnreadCount);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.ConversationMarkReadRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    // ── 配对正确性 ──

    /// <summary>同一请求类型连续两次调用：RequestId 每次由模板重新生成，各配对各的响应，互不串扰。</summary>
    [Fact]
    public async Task Same_Request_Type_Concurrent_Pairing_Is_Isolated()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var t1 = client.QueryPresenceAsync([PeerId]);
            var t2 = client.QueryPresenceAsync([PeerId]);
            var r1 = await t1.WaitAsync(TimeSpan.FromSeconds(5));
            var r2 = await t2.WaitAsync(TimeSpan.FromSeconds(5));

            var frames = server.Frames.Where(f => f.Command == PacketCommand.PresenceQuery).ToList();
            Assert.Equal(2, frames.Count);
            Assert.Equal(frames[0].RequestId, r1.RequestId);
            Assert.Equal(frames[1].RequestId, r2.RequestId);
            Assert.NotEqual(frames[0].RequestId, frames[1].RequestId);
        });
    }

    /// <summary>服务器回显错误 RequestId：请求不配对，最终超时；绝不以错配响应完成（配对他请求）。</summary>
    [Fact]
    public async Task Mismatched_Echo_RequestId_TimesOut_Without_Pairing()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var client = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);

        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.ConversationListRequest)
                return;
            // 回显错误的 RequestId（例如服务端 bug 写成了别的请求的 Id）
            InjectPacket(tcp, serializer, PacketCommand.ConversationListPage,
                new ConversationListResponseDto { RequestId = "some-other-request-id", Succeeded = true });
        };

        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await client.AuthenticateAsync("token", OwnerId, null, null);

        // 配对键不存在：请求按模板超时失败（绝不以错配响应完成）
        // 注：Task.WaitAsync(TimeSpan) 超时在当前运行时抛 TaskCanceledException。
        await Assert.ThrowsAnyAsync<Exception>(() => client.QueryConversationListAsync());
    }

    // ── 测试底座 ──

    private static async Task RunAsync(
        Func<ChatSessionClient, SimServer, IPacketBodySerializer, Task> act)
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var client = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        var server = new SimServer(tcp, serializer);

        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await client.AuthenticateAsync("token", OwnerId, null, null);
        Assert.True(client.IsAuthenticated);

        await act(client, server, serializer);
    }

    /// <summary>模拟服务器：记录每个请求帧的 RequestId，并按原样回显构造响应。</summary>
    private sealed class SimServer
    {
        public List<(PacketCommand Command, string RequestId)> Frames { get; } = [];

        public SimServer(ScriptedTcpClient tcp, IPacketBodySerializer serializer)
        {
            tcp.OnFrameSent += (cmd, body) =>
            {
                try
                {
                    switch (cmd)
                    {
                        case PacketCommand.ConversationListRequest:
                        {
                            var req = serializer.Deserialize<ConversationListRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            Frames.Add((cmd, req.RequestId!));
                            InjectPacket(tcp, serializer, PacketCommand.ConversationListPage,
                                new ConversationListResponseDto { RequestId = req.RequestId, Succeeded = true });
                            break;
                        }
                        case PacketCommand.ConversationSetPrefsRequest:
                        {
                            var req = serializer.Deserialize<ConversationSetPrefsRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            Frames.Add((cmd, req.RequestId!));
                            InjectPacket(tcp, serializer, PacketCommand.ConversationSetPrefsResponse,
                                new ConversationSetPrefsResponseDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    ConversationId = req.ConversationId,
                                    IsPinned = req.Pinned ?? false
                                });
                            break;
                        }
                        case PacketCommand.MessageRecallRequest:
                        {
                            var req = serializer.Deserialize<MessageRecallRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            Frames.Add((cmd, req.RequestId!));
                            InjectPacket(tcp, serializer, PacketCommand.MessageRecallAck,
                                new MessageRecallAcknowledgementDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    MessageId = req.MessageId,
                                    ConversationId = ConvId,
                                    RecalledAtMs = 1234567890
                                });
                            break;
                        }
                        case PacketCommand.MessageEditRequest:
                        {
                            var req = serializer.Deserialize<MessageEditRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            Frames.Add((cmd, req.RequestId!));
                            InjectPacket(tcp, serializer, PacketCommand.MessageEditAck,
                                new MessageEditAcknowledgementDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    MessageId = req.MessageId,
                                    ConversationId = ConvId,
                                    Content = req.Content,
                                    EditVersion = 2,
                                    EditedAtMs = 1234567890
                                });
                            break;
                        }
                        case PacketCommand.PresenceQuery:
                        {
                            var req = serializer.Deserialize<PresenceQueryRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            Frames.Add((cmd, req.RequestId!));
                            InjectPacket(tcp, serializer, PacketCommand.PresenceSnapshot,
                                new PresenceSnapshotResponseDto
                                {
                                    RequestId = req.RequestId,
                                    Items = new[]
                                    {
                                        new PresenceSnapshotItemDto { UserId = PeerId, IsOnline = true }
                                    }
                                });
                            break;
                        }
                        case PacketCommand.SyncBootstrapRequest:
                        {
                            var req = serializer.Deserialize<SyncBootstrapRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            Frames.Add((cmd, req.RequestId!));
                            InjectPacket(tcp, serializer, PacketCommand.SyncBootstrapResponse,
                                new SyncBootstrapResponseDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    Conversations = new[]
                                    {
                                        new ConversationListItemDto
                                        {
                                            ConversationId = ConvId,
                                            Type = ConversationTypeDto.Direct,
                                            PeerUserId = PeerId,
                                            LastMessageId = "svr-1",
                                            LastMessagePreview = "预览",
                                            LastMessageAtMs = 1234567890,
                                            LastSenderUserId = PeerId
                                        }
                                    }
                                });
                            break;
                        }
                        case PacketCommand.MessageHistoryRequest:
                        {
                            var req = serializer.Deserialize<MessageHistoryRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            Frames.Add((cmd, req.RequestId!));
                            InjectPacket(tcp, serializer, PacketCommand.MessageHistoryPage,
                                new MessageHistoryPageDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    ConversationId = req.ConversationId,
                                    Items = new[]
                                    {
                                        new MessageHistoryItemDto
                                        {
                                            MessageId = "svr-1",
                                            SenderUserId = PeerId,
                                            ReceiverUserId = OwnerId,
                                            Content = "历史消息",
                                            ReceivedAtMs = 1234567890
                                        }
                                    },
                                    HasMore = false
                                });
                            break;
                        }
                        case PacketCommand.MessageReceipt:
                        {
                            var req = serializer.Deserialize<MessageReceiptDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            Frames.Add((cmd, req.RequestId!));
                            InjectPacket(tcp, serializer, PacketCommand.MessageReceiptAck,
                                new MessageReceiptAckDto { RequestId = req.RequestId, Accepted = true });
                            break;
                        }
                        case PacketCommand.ConversationMarkReadRequest:
                        {
                            var req = serializer.Deserialize<ConversationMarkReadRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            Frames.Add((cmd, req.RequestId!));
                            InjectPacket(tcp, serializer, PacketCommand.ConversationMarkReadResponse,
                                new ConversationMarkReadResponseDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    ConversationId = req.ConversationId,
                                    UnreadCount = 0
                                });
                            break;
                        }
                    }
                }
                catch
                {
                    // 模拟服务器解析失败：忽略该帧
                }
            };
        }
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
