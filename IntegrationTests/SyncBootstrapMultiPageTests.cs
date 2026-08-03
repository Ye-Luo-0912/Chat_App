using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Serialization;
using Chat_App.Infrastructure.Services;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Buffers;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 多会话多分页 bootstrap 验收测试：
/// bootstrap 返回部分会话 + 会话列表翻页（ConversationsHasMore）+
/// 超限会话历史分页（CatchUp.HasMore → QueryMessageHistoryAsync 续页）。
/// 验收：两会话摘要都入库、会话 A 三页消息合并落库、水位推进到最后一条、
/// 会话之间数据零串扰（A 的消息不落到 B）。
/// </summary>
public class SyncBootstrapMultiPageTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 7001;
    private const long PeerId = 9003;
    private const string ConvA = "conv-multi-a-7001";
    private const string ConvB = "conv-multi-b-7001";

    public SyncBootstrapMultiPageTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_multipage_{Guid.NewGuid():N}.db");
        _factory = new DbContextFactoryStub(_dbPath);
        _db = new DatabaseService(_factory);
        using var ctx = _factory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private static readonly SessionStamp Session = new(OwnerId, 1, Guid.NewGuid());

    private static MessageHistoryItemDto Item(string messageId, string content, long receivedAtMs) => new()
    {
        MessageId = messageId,
        ClientMessageId = string.Empty,
        SenderUserId = PeerId,
        ReceiverUserId = OwnerId,
        ConversationId = ConvA,
        Content = content,
        ReceivedAtMs = receivedAtMs,
        EditVersion = 1
    };

    private static ConversationListItemDto ConvItem(string convId, string lastMessageId, string preview, long lastAtMs) => new()
    {
        ConversationId = convId,
        Type = ConversationTypeDto.Direct,
        PeerUserId = PeerId,
        LastMessageId = lastMessageId,
        LastMessagePreview = preview,
        LastMessageAtMs = lastAtMs,
        LastSenderUserId = PeerId,
        UnreadCount = 1,
        IsPinned = false,
        IsMuted = false
    };

    /// <summary>
    /// 多会话多分页同步：bootstrap 页 1（会话 A + CatchUp 两页）→
    /// 会话列表页 2（会话 B）→ 会话 A 历史续页（a-3）。
    /// </summary>
    [Fact]
    public async Task Multi_Conversation_Multi_Page_Bootstrap_Merges_All_Without_Cross_Contamination()
    {
        const long t0 = 1_700_000_000_000;
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);

        // 状态化 mock：会话列表翻页只应答一次（会话 B），历史续页只应答 conv-a 的 a-3
        var listPagesServed = 0;
        var historyPagesServed = 0;
        var historyBeforeParams = new System.Collections.Generic.List<string>();

        tcp.OnFrameSent += (cmd, body) =>
        {
            try
            {
                switch (cmd)
                {
                    case PacketCommand.AuthRequest:
                        InjectPacket(tcp, serializer, PacketCommand.AuthResponse,
                            new AuthResponseDto { Success = true, UserId = OwnerId });
                        break;
                    case PacketCommand.SyncBootstrapRequest:
                        {
                            var req = serializer.Deserialize<SyncBootstrapRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            InjectPacket(tcp, serializer, PacketCommand.SyncBootstrapResponse, new SyncBootstrapResponseDto
                            {
                                RequestId = req.RequestId,
                                Succeeded = true,
                                Conversations = new[]
                                {
                                ConvItem(ConvA, "a-1", "A-1", t0)
                            },
                                ConversationsNextCursor = new ConversationListCursorDto
                                {
                                    IsPinned = false,
                                    PinnedAtMs = null,
                                    LastMessageAtMs = t0,
                                    ConversationId = ConvA
                                },
                                ConversationsHasMore = true,
                                CatchUps = new[]
                                {
                                new ConversationHistoryCatchUpDto
                                {
                                    ConversationId = ConvA,
                                    Items = new[]
                                    {
                                        Item("a-1", "A-1", t0),
                                        Item("a-2", "A-2", t0 + 1000)
                                    },
                                    HasMore = true,
                                    NextCursor = new MessageHistoryCursorDto { ReceivedAtMs = t0 + 1000, MessageId = "a-2" }
                                }
                            }
                            });
                            break;
                        }
                    case PacketCommand.ConversationListRequest:
                        {
                            var req = serializer.Deserialize<ConversationListRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            listPagesServed++;
                            InjectPacket(tcp, serializer, PacketCommand.ConversationListPage, new ConversationListResponseDto
                            {
                                RequestId = req.RequestId,
                                Succeeded = true,
                                Items = new[] { ConvItem(ConvB, "b-1", "B-1", t0 + 2000) },
                                HasMore = false,
                                NextCursor = null
                            });
                            break;
                        }
                    case PacketCommand.MessageHistoryRequest:
                        {
                            var req = serializer.Deserialize<MessageHistoryRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            historyPagesServed++;
                            historyBeforeParams.Add($"{req.ConversationId}/{req.BeforeMessageId}/{req.BeforeReceivedAtMs}");
                            var page = new MessageHistoryPageDto
                            {
                                RequestId = req.RequestId,
                                Succeeded = true,
                                ConversationId = req.ConversationId ?? string.Empty,
                                HasMore = false
                            };
                            if (req.ConversationId == ConvA && req.BeforeMessageId == "a-2")
                                page.Items = new[] { Item("a-3", "A-3", t0 + 2000) };
                            InjectPacket(tcp, serializer, PacketCommand.MessageHistoryPage, page);
                            break;
                        }
                }
            }
            catch
            {
                // 模拟服务器解析失败：忽略该帧
            }
        };

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);
        Assert.True(session.IsAuthenticated);

        var eventBus = new InMemoryEventBus();
        var store = new MessageStore(_db, eventBus, session);
        var engine = new SyncEngine(session, store, _db, new SyncCheckpointStore(store, _db), new SyncConflictResolver());
        var completed = new TaskCompletionSource<SyncCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, e) => completed.TrySetResult(e);

        engine.Start(Session);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Succeeded, $"同步失败: {result.ErrorCode} {result.ErrorMessage}");
        Assert.True(listPagesServed >= 1, "会话列表翻页未被调用（多会话分页未执行）");
        Assert.True(historyPagesServed >= 1, $"历史续页未被调用（多分页未执行）; 历史请求参数: {string.Join("; ", historyBeforeParams)}");
        Assert.Contains(historyBeforeParams, p => p.StartsWith("conv-multi-a-7001/a-2/"));

        // 会话 A：3 条消息全部落库（bootstrap 2 条 + 续页 1 条）
        var aMessages = await _db.GetMessagesAsync(OwnerId, ConvA);
        Assert.Equal(3, aMessages.Count);
        Assert.Contains(aMessages, m => m.MessageId == "a-1");
        Assert.Contains(aMessages, m => m.MessageId == "a-2");
        Assert.Contains(aMessages, m => m.MessageId == "a-3");

        // 会话 A 水位推进到最后一条
        var cursorA = await _db.GetSyncCursorAsync(OwnerId, ConvA);
        Assert.NotNull(cursorA);
        Assert.Equal("a-3", cursorA!.AfterMessageId);

        // 会话 B：仅会话摘要入库，零消息（无串扰）
        var bMessages = await _db.GetMessagesAsync(OwnerId, ConvB);
        Assert.Empty(bMessages);
        var convB = await _db.GetConversationAsync(OwnerId, ConvB);
        Assert.NotNull(convB);
        Assert.Equal("b-1", convB!.LastMessageId);

        // 两会话都在会话列表
        var conversations = await _db.GetConversationsAsync(OwnerId);
        Assert.Contains(conversations, c => c.ConversationId == ConvA);
        Assert.Contains(conversations, c => c.ConversationId == ConvB);
    }

    private sealed class DbContextFactoryStub(string dbPath) : IDbContextFactory<ClientDbContext>
    {
        public ClientDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ClientDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            return new ClientDbContext(options);
        }

        public Task<ClientDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
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
