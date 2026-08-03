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
        TryDeleteWithRetry(_dbPath);
    }

    /// <summary>删除测试库文件：OutboxProcessor 的 fire-and-forget 排空任务可能仍在释放连接，重试等待。</summary>
    private static void TryDeleteWithRetry(string path)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
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
                                RequestId = req.RequestId ?? string.Empty,
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
                                RequestId = req.RequestId ?? string.Empty,
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
                                RequestId = req.RequestId ?? string.Empty,
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

        // 会话 A 水位：backward 续页不推进正向水位，保持 bootstrap 首页水位（a-2）
        var cursorA = await _db.GetSyncCursorAsync(OwnerId, ConvA);
        Assert.NotNull(cursorA);
        Assert.Equal("a-2", cursorA!.AfterMessageId);

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

    /// <summary>
    /// P0-2 回归：backward 历史续页返回"更早"消息（Before 游标协议语义）时必须落库，
    /// 且不得被正向水位过滤、不得推进正向水位。
    /// 旧实现用 HasNewerMessages（正向语义）判断续页，更早消息被整页跳过。
    /// </summary>
    [Fact]
    public async Task Backward_History_Page_Older_Messages_Are_Merged_Without_Watermark_Advance()
    {
        const long t0 = 1_800_000_000_000;
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);

        var historyPagesServed = 0;

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
                                RequestId = req.RequestId ?? string.Empty,
                                Succeeded = true,
                                Conversations = new[] { ConvItem(ConvA, "a-2", "A-2", t0 + 1000) },
                                ConversationsHasMore = false,
                                CatchUps = new[]
                                {
                                new ConversationHistoryCatchUpDto
                                {
                                    ConversationId = ConvA,
                                    Items = new[] { Item("a-2", "A-2", t0 + 1000) },
                                    HasMore = true,
                                    NextCursor = new MessageHistoryCursorDto { ReceivedAtMs = t0 + 1000, MessageId = "a-2" }
                                }
                            }
                            });
                            break;
                        }
                    case PacketCommand.MessageHistoryRequest:
                        {
                            var req = serializer.Deserialize<MessageHistoryRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            historyPagesServed++;
                            // 合法协议语义：Before (t0+1000, a-2) → 返回更早的 a-1（t0）
                            var page = new MessageHistoryPageDto
                            {
                                RequestId = req.RequestId ?? string.Empty,
                                Succeeded = true,
                                ConversationId = req.ConversationId ?? string.Empty,
                                HasMore = false
                            };
                            if (req.ConversationId == ConvA && req.BeforeMessageId == "a-2")
                                page.Items = new[] { Item("a-1", "A-1", t0) };
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
        Assert.True(historyPagesServed >= 1, "backward 续页未被调用");

        // 更早的 a-1 必须落库（旧实现被正向水位过滤跳过）
        var aMessages = await _db.GetMessagesAsync(OwnerId, ConvA);
        Assert.Equal(2, aMessages.Count);
        Assert.Contains(aMessages, m => m.MessageId == "a-1");
        Assert.Contains(aMessages, m => m.MessageId == "a-2");

        // 正向水位不得被更早消息回退/推进：仍为 bootstrap 首页的 a-2
        var cursorA = await _db.GetSyncCursorAsync(OwnerId, ConvA);
        Assert.NotNull(cursorA);
        Assert.Equal("a-2", cursorA!.AfterMessageId);
        Assert.Equal(t0 + 1000, cursorA.AfterReceivedAtMs);
    }

    /// <summary>
    /// P0-6 回归：同步必须保留本地草稿与归档状态，且群名（GroupTitle）随投影落库。
    /// 旧实现 Upsert 会以 null 覆盖 Draft，且不写 GroupTitle。
    /// </summary>
    [Fact]
    public async Task Sync_Projection_Preserves_Local_Draft_And_GroupTitle()
    {
        const long t0 = 1_900_000_000_000;
        // 预置本地会话：草稿 + 归档状态 + 旧群名（模拟用户本地状态）
        await _db.UpsertConversationAsync(new LocalConversation
        {
            OwnerUserId = OwnerId,
            ConversationId = ConvA,
            Type = 1,
            PeerUserId = null,
            GroupTitle = "旧群名",
            Draft = "未发送的草稿",
            Archived = true,
            LastMessageAtMs = t0
        });
        await _db.UpdateConversationDraftAsync(OwnerId, ConvA, "未发送的草稿");

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);

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
                                RequestId = req.RequestId ?? string.Empty,
                                Succeeded = true,
                                Conversations = new[]
                                {
                                new ConversationListItemDto
                                {
                                    ConversationId = ConvA,
                                    Type = ConversationTypeDto.Group,
                                    Title = "新群名",
                                    LastMessageId = "g-1",
                                    LastMessagePreview = "群消息",
                                    LastMessageAtMs = t0 + 500,
                                    LastSenderUserId = PeerId,
                                    UnreadCount = 2,
                                    IsPinned = false,
                                    IsMuted = false
                                }
                            },
                                ConversationsHasMore = false,
                                CatchUps = Array.Empty<ConversationHistoryCatchUpDto>()
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

        var conv = await _db.GetConversationAsync(OwnerId, ConvA);
        Assert.NotNull(conv);
        // 本地专属字段必须保留
        Assert.Equal("未发送的草稿", conv!.Draft);
        Assert.True(conv.Archived);
        // 服务端字段必须更新：群名 + 摘要
        Assert.Equal("新群名", conv.GroupTitle);
        Assert.Equal("g-1", conv.LastMessageId);
        Assert.Equal(2, conv.UnreadCount);
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



