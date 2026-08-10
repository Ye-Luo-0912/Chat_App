using Chat_App.Infrastructure.Events;
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
/// 离线启动同步状态机验收测试（产品主线 4-1）。
/// 验收场景：
/// - 离线启动首帧同步：启动即断网时本地数据可用；恢复连接鉴权后自动同步（Bootstrap → 会话列表 → catch-up）；
/// - 幂等：同水位重复同步不重复入库、水位不回退；
/// - 冲突合并：同步历史与本地实时/离线消息按 MessageId/ClientMessageId 单调合并（MessageId 回填、编辑单调）。
/// </summary>
public class SyncStateMachineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 6101;
    private const long PeerId = 9101;
    private const string ConvId = "conv-6101-9101";

    private static readonly SessionStamp Session = new(OwnerId, 1, Guid.NewGuid());

    public SyncStateMachineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_sync_{Guid.NewGuid():N}.db");
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

    /// <summary>离线启动：本地离线消息可用；恢复连接后首帧同步合并服务器版本（MessageId 回填、不重复入库）。</summary>
    [Fact]
    public async Task OfflineStart_FirstSync_Merges_Server_Version()
    {
        // 启动即离线：本地事务化落库（Outbox Queued + Message Queued，无 MessageId）
        var clientId = $"c-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(NewOutbox(clientId), NewLocalMessage(clientId, content: "离线发送的消息"));

        // 离线期间本地数据可读（不依赖网络）
        var before = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.NotNull(before);
        Assert.Null(before!.MessageId);
        Assert.Equal(MessageStatus.Queued, before.Status);

        // 恢复连接：鉴权成功后服务器 Bootstrap 返回该消息的服务器版本（svr-1）+ 另一条新消息
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        SetupSyncServer(tcp, serializer,
            bootstrap: () => new SyncBootstrapResponseDto
            {
                Succeeded = true,
                Conversations = new[]
                {
                    new ConversationListItemDto
                    {
                        ConversationId = ConvId,
                        Type = ConversationTypeDto.Direct,
                        PeerUserId = PeerId,
                        LastMessageId = "svr-1",
                        LastMessagePreview = "离线发送的消息",
                        LastMessageAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000,
                        LastSenderUserId = OwnerId,
                        UnreadCount = 0,
                        IsPinned = false,
                        IsMuted = false
                    }
                },
                ConversationsHasMore = false,
                CatchUps = new[]
                {
                    new ConversationHistoryCatchUpDto
                    {
                        ConversationId = ConvId,
                        Items = new[]
                        {
                            // 本地离线消息的服务器确认版本（同一 ClientMessageId，服务器分配 MessageId）
                            NewHistoryItem("svr-1", clientId, "离线发送的消息", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000),
                            NewHistoryItem("svr-2", "", "同步来的新消息", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        }
                    }
                }
            });

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

        // 幂等合并：仍是 1 行，MessageId 回填为服务器分配值，状态不被覆盖
        var merged = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.NotNull(merged);
        Assert.Equal("svr-1", merged!.MessageId);
        Assert.Equal(MessageStatus.Queued, merged.Status); // 发送方消息不被 catch-up 覆盖为 Delivered
        Assert.Equal("离线发送的消息", merged.Content);

        // 新消息入库
        var added = await _db.GetMessageByServerIdAsync(OwnerId, "svr-2");
        Assert.NotNull(added);
        Assert.Equal(MessageStatus.Delivered, added!.Status);

        // 会话摘要与水位推进（以批次内最新消息 svr-2 为准）
        var conv = await _db.GetConversationAsync(OwnerId, ConvId);
        Assert.NotNull(conv);
        Assert.Equal("svr-2", conv!.LastMessageId);

        var cursor = await _db.GetSyncCursorAsync(OwnerId, ConvId);
        Assert.NotNull(cursor);
        Assert.Equal("svr-2", cursor!.AfterMessageId);

        // Outbox 保持 Queued（离线补发由 OutboxProcessor 负责，同步不消费 outbox）
        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxStatus.Queued, outbox!.Status);
    }

    /// <summary>幂等：同一水位重复同步（重连触发）不重复入库、水位不回退。</summary>
    [Fact]
    public async Task Resync_Same_Watermark_Is_Idempotent()
    {
        // 首次同步：2 条消息落库，水位 = svr-2
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        var serverMessages = new List<MessageHistoryItemDto>
        {
            NewHistoryItem("svr-1", "", "消息 1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 2000),
            NewHistoryItem("svr-2", "", "消息 2", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000)
        };
        SetupSyncServer(tcp, serializer, () => new SyncBootstrapResponseDto
        {
            Succeeded = true,
            Conversations = new[]
            {
                new ConversationListItemDto
                {
                    ConversationId = ConvId,
                    Type = ConversationTypeDto.Direct,
                    PeerUserId = PeerId,
                    LastMessageId = "svr-2",
                    LastMessagePreview = "消息 2",
                    LastMessageAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000,
                    LastSenderUserId = PeerId,
                    UnreadCount = 0,
                    IsPinned = false,
                    IsMuted = false
                }
            },
            ConversationsHasMore = false,
            CatchUps = new[] { new ConversationHistoryCatchUpDto { ConversationId = ConvId, Items = serverMessages } }
        });

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        var eventBus = new InMemoryEventBus();
        var store = new MessageStore(_db, eventBus, session);
        var engine = new SyncEngine(session, store, _db, new SyncCheckpointStore(store, _db), new SyncConflictResolver());

        await RunSyncAsync(engine, expectSuccess: true);
        var countAfterFirst = (await _db.GetMessagesAsync(OwnerId, ConvId, 100)).Count;
        Assert.Equal(2, countAfterFirst);

        // 服务器水位已推进，重复同步不再下发旧消息（模拟器清空服务端增量）
        serverMessages.Clear();
        var secondRun = await RunSyncAsync(engine, expectSuccess: true);
        Assert.All(secondRun.CatchUps, c => Assert.Empty(c.Items));
        Assert.Single(secondRun.Conversations); // 会话列表仍返回

        // 幂等：消息数不变、水位不回退
        var countAfterSecond = (await _db.GetMessagesAsync(OwnerId, ConvId, 100)).Count;
        Assert.Equal(2, countAfterSecond);
        var cursor = await _db.GetSyncCursorAsync(OwnerId, ConvId);
        Assert.Equal("svr-2", cursor!.AfterMessageId);
    }

    /// <summary>冲突合并：实时消息与同步历史相交（同 ClientMessageId 编辑/撤回）单调合并，不重复。</summary>
    [Fact]
    public async Task ConflictMerge_RealTime_And_CatchUp_Is_Monotonic()
    {
        // 本地已有：客户端离线发送（无 MessageId）+ 服务器已分配版本 svr-1（已编辑 v2）
        var clientId = $"c-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(NewOutbox(clientId), NewLocalMessage(clientId, content: "v1 草稿"));

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        SetupSyncServer(tcp, serializer, () => new SyncBootstrapResponseDto
        {
            Succeeded = true,
            Conversations = Array.Empty<ConversationListItemDto>(),
            ConversationsHasMore = false,
            CatchUps = new[]
            {
                new ConversationHistoryCatchUpDto
                {
                    ConversationId = ConvId,
                    Items = new[]
                    {
                        // 服务器端已编辑为 v2 的内容 + 撤回消息 svr-3
                        NewHistoryItem("svr-1", clientId, "v2 编辑后内容",
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 500, editVersion: 2),
                        NewHistoryItem("svr-3", "", "将被撤回", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            recalledAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 100)
                    }
                }
            }
        });

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        var eventBus = new InMemoryEventBus();
        var store = new MessageStore(_db, eventBus, session);
        var engine = new SyncEngine(session, store, _db, new SyncCheckpointStore(store, _db), new SyncConflictResolver());

        await RunSyncAsync(engine, expectSuccess: true);

        // 同 ClientMessageId 单调合并：v2 内容生效、MessageId 回填、不新增行
        var merged = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.NotNull(merged);
        Assert.Equal("svr-1", merged!.MessageId);
        Assert.Equal("v2 编辑后内容", merged.Content);
        Assert.Equal(2, merged.EditVersion);

        // 撤回消息落库为 Recalled
        var recalled = await _db.GetMessageByServerIdAsync(OwnerId, "svr-3");
        Assert.NotNull(recalled);
        Assert.Equal(MessageStatus.Recalled, recalled!.Status);

        // 总行数 = 2（1 合并 + 1 新增），无重复
        var all = await _db.GetMessagesAsync(OwnerId, ConvId, 100);
        Assert.Equal(2, all.Count);
    }

    // ── 辅助 ────────────────────────────────────────────

    /// <summary>跨设备漫游：新设备（无本地水位）首次同步完整漫游历史——编辑/撤回状态、会话摘要、已读水位。</summary>
    [Fact]
    public async Task NewDevice_Roams_All_History()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        SetupSyncServer(tcp, serializer, () => new SyncBootstrapResponseDto
        {
            Succeeded = true,
            Conversations = new[]
            {
                new ConversationListItemDto
                {
                    ConversationId = ConvId,
                    Type = ConversationTypeDto.Direct,
                    PeerUserId = PeerId,
                    LastMessageId = "svr-3",
                    LastMessagePreview = "消息 3",
                    LastMessageAtMs = nowMs,
                    LastSenderUserId = PeerId,
                    UnreadCount = 2,
                    LastReadMessageId = "svr-1",
                    LastReadAtMs = nowMs - 2000,
                    IsPinned = true,
                    PinnedAtMs = nowMs - 10000,
                    IsMuted = false
                }
            },
            ConversationsHasMore = false,
            CatchUps = new[]
            {
                new ConversationHistoryCatchUpDto
                {
                    ConversationId = ConvId,
                    Items = new[]
                    {
                        NewHistoryItem("svr-1", "", "消息 1", nowMs - 3000),
                        NewHistoryItem("svr-2", "", "消息 2（已编辑）", nowMs - 2000, editVersion: 2),
                        NewHistoryItem("svr-3", "", "消息 3（已撤回）", nowMs - 1000, recalledAtMs: nowMs)
                    }
                }
            }
        });

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        var eventBus = new InMemoryEventBus();
        var store = new MessageStore(_db, eventBus, session);
        var engine = new SyncEngine(session, store, _db, new SyncCheckpointStore(store, _db), new SyncConflictResolver());

        var result = await RunSyncAsync(engine, expectSuccess: true);

        // 完整漫游：3 条消息全部入库，编辑/撤回状态保留
        var all = await _db.GetMessagesAsync(OwnerId, ConvId, 100);
        Assert.Equal(3, all.Count);
        var edited = await _db.GetMessageByServerIdAsync(OwnerId, "svr-2");
        Assert.Equal(2, edited!.EditVersion);
        Assert.Equal("消息 2（已编辑）", edited.Content);
        var recalled = await _db.GetMessageByServerIdAsync(OwnerId, "svr-3");
        Assert.Equal(MessageStatus.Recalled, recalled!.Status);

        // 会话摘要漫游：未读数/已读水位/置顶
        var conv = await _db.GetConversationAsync(OwnerId, ConvId);
        Assert.NotNull(conv);
        Assert.Equal(2, conv!.UnreadCount);
        Assert.Equal("svr-1", conv.LastReadMessageId);
        Assert.True(conv.IsPinned);

        // 水位推进到最新
        var cursor = await _db.GetSyncCursorAsync(OwnerId, ConvId);
        Assert.Equal("svr-3", cursor!.AfterMessageId);
    }

    /// <summary>跨设备冲突解决：本地编辑版本与服务器版本按版本号单调合并——旧版本不回退、新版本覆盖。</summary>
    [Fact]
    public async Task CrossDevice_Conflict_Edit_Version_Is_Monotonic()
    {
        // 设备 A：先同步 v1，再本地离线编辑为 v2（同一条消息）
        await _db.ApplyHistoryBatchAsync(OwnerId, ConvId,
            new[] { NewHistoryItem("svr-1", "", "原始内容", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 3000, editVersion: 1) },
            null);
        var editResult = await _db.ApplyMessageEditAsync(
            OwnerId, "svr-1", "设备 A 本地编辑 v2", editVersion: 2,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(MessageMutationResult.Applied, editResult);

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        var stale = new List<MessageHistoryItemDto>
        {
            NewHistoryItem("svr-1", "", "服务器旧版本 v1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 3000, editVersion: 1)
        };
        SetupSyncServer(tcp, serializer, () => new SyncBootstrapResponseDto
        {
            Succeeded = true,
            ConversationsHasMore = false,
            CatchUps = new[] { new ConversationHistoryCatchUpDto { ConversationId = ConvId, Items = stale } }
        });

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        var eventBus = new InMemoryEventBus();
        var store = new MessageStore(_db, eventBus, session);
        var engine = new SyncEngine(session, store, _db, new SyncCheckpointStore(store, _db), new SyncConflictResolver());

        // 设备 B 拉回的旧版本（v1）不得覆盖设备 A 的本地编辑（v2）
        await RunSyncAsync(engine, expectSuccess: true);
        var afterStale = await _db.GetMessageByServerIdAsync(OwnerId, "svr-1");
        Assert.Equal(2, afterStale!.EditVersion);
        Assert.Equal("设备 A 本地编辑 v2", afterStale.Content);

        // 服务器新版本（v3）到达：版本号单调 → 覆盖本地 v2
        stale[0] = NewHistoryItem("svr-1", "", "服务器新版本 v3", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 3000, editVersion: 3);
        await RunSyncAsync(engine, expectSuccess: true);
        var afterNew = await _db.GetMessageByServerIdAsync(OwnerId, "svr-1");
        Assert.Equal(3, afterNew!.EditVersion);
        Assert.Equal("服务器新版本 v3", afterNew.Content);
    }

    [Fact]
    public async Task History_Response_Missing_ConversationId_Is_Inferred_And_Observable()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        tcp.OnFrameSent += (command, body) =>
        {
            if (command != PacketCommand.MessageHistoryRequest)
                return;
            var request = serializer.Deserialize<MessageHistoryRequestDto>(new ReadOnlySequence<byte>(body));
            InjectPacket(tcp, serializer, PacketCommand.MessageHistoryPage, new MessageHistoryPageDto
            {
                RequestId = request!.RequestId,
                Succeeded = true,
                ConversationId = null
            });
        };

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        var page = await session.QueryMessageHistoryAsync(ConvId);
        Assert.Equal(ConvId, page.ConversationId);
        Assert.Equal(1, session.Counters["history_conversation_ids_inferred"]);
    }

    [Fact]
    public async Task History_Response_Wrong_ConversationId_Fails_Request_Immediately()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        tcp.OnFrameSent += (command, body) =>
        {
            if (command != PacketCommand.MessageHistoryRequest)
                return;
            var request = serializer.Deserialize<MessageHistoryRequestDto>(new ReadOnlySequence<byte>(body));
            InjectPacket(tcp, serializer, PacketCommand.MessageHistoryPage, new MessageHistoryPageDto
            {
                RequestId = request!.RequestId,
                Succeeded = true,
                ConversationId = "wrong-conversation"
            });
        };

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        await Assert.ThrowsAsync<InvalidDataException>(() => session.QueryMessageHistoryAsync(ConvId));
        Assert.Equal(1, session.Counters["history_conversation_mismatches"]);
        Assert.Equal(0, session.PendingRequestCount);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Invalid_Sync_Response_Fails_Pending_Request_And_Increments_Diagnostic(string responseJson)
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        tcp.OnFrameSent += (command, _) =>
        {
            if (command == PacketCommand.SyncBootstrapRequest)
                InjectRawPacket(tcp, PacketCommand.SyncBootstrapResponse, responseJson);
        };

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        await Assert.ThrowsAsync<InvalidDataException>(() => session.QuerySyncBootstrapAsync());
        Assert.Equal(1, session.Counters["route_failures"]);
        Assert.Equal(0, session.PendingRequestCount);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Invalid_History_Response_Fails_Pending_Request_And_Increments_Diagnostic(string responseJson)
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        tcp.OnFrameSent += (command, _) =>
        {
            if (command == PacketCommand.MessageHistoryRequest)
                InjectRawPacket(tcp, PacketCommand.MessageHistoryPage, responseJson);
        };

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        await Assert.ThrowsAsync<InvalidDataException>(() => session.QueryMessageHistoryAsync(ConvId));
        Assert.Equal(1, session.Counters["route_failures"]);
        Assert.Equal(0, session.PendingRequestCount);
    }

    [Fact]
    public async Task Older_ChangedAt_Snapshot_Does_Not_Regress_Mutable_Message_State()
    {
        const long receivedAt = 1_700_000_000_000;
        var newer = NewHistoryItem("reaction-1", "", "消息", receivedAt);
        newer.ChangedAtMs = receivedAt + 2_000;
        newer.DeliveredAtMs = receivedAt + 100;
        newer.ReadAtMs = receivedAt + 200;
        newer.RecalledAtMs = receivedAt + 300;
        newer.Attachments =
        [
            new AttachmentRefDto
            {
                AttachmentId = "attachment-newer",
                FileName = "newer.bin",
                ContentType = "application/octet-stream",
                SizeBytes = 200
            }
        ];
        newer.Reactions =
        [
            new MessageReactionSummaryDto { Emoji = "👍", Count = 3, ReactedByMe = true }
        ];

        await _db.ApplyHistoryBatchAsync(OwnerId, ConvId, [newer], null);

        var stale = NewHistoryItem("reaction-1", "", "消息", receivedAt - 10_000);
        stale.ChangedAtMs = receivedAt + 1_000;
        stale.Attachments =
        [
            new AttachmentRefDto
            {
                AttachmentId = "attachment-stale",
                FileName = "stale.bin",
                ContentType = "application/octet-stream",
                SizeBytes = 100
            }
        ];
        stale.Reactions =
        [
            new MessageReactionSummaryDto { Emoji = "👍", Count = 1, ReactedByMe = false }
        ];
        await _db.ApplyHistoryBatchAsync(OwnerId, ConvId, [stale], null);

        var stored = await _db.GetMessageByServerIdAsync(OwnerId, "reaction-1");
        Assert.NotNull(stored);
        Assert.Equal(receivedAt + 2_000, stored!.ChangedAtMs);
        Assert.Equal(receivedAt, stored.ReceivedAtMs);
        Assert.Equal(receivedAt + 100, stored.DeliveredAtMs);
        Assert.Equal(receivedAt + 200, stored.ReadAtMs);
        Assert.Equal(receivedAt + 300, stored.RecalledAtMs);
        Assert.Equal(MessageStatus.Recalled, stored.Status);
        var attachment = Assert.Single(AttachmentJson.Deserialize(stored.AttachmentsJson)!);
        Assert.Equal("attachment-newer", attachment.AttachmentId);
        var reactions = ReactionJson.Deserialize(stored.ReactionsJson);
        var reaction = Assert.Single(reactions!);
        Assert.Equal(3, reaction.Count);
        Assert.True(reaction.ReactedByMe);

        var storedAttachments = await _db.GetAttachmentsByMessageIdAsync(OwnerId, "reaction-1");
        var storedAttachment = Assert.Single(storedAttachments);
        Assert.Equal("attachment-newer", storedAttachment.AttachmentId);
        Assert.Null(await _db.GetAttachmentByAttachmentIdAsync(OwnerId, "attachment-stale"));
    }

    /// <summary>历史滚动分页：游标递减逐页拉取 → 增量入库 → 无重复；同游标重复拉取幂等。</summary>
    [Fact]
    public async Task History_Paging_Incremental_And_Idempotent()
    {
        // 服务器消息池：120 条，ReceivedAtMs 递增（svr-001 最旧 → svr-120 最新）
        const int total = 120;
        var baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - total * 1000;
        var pool = new List<MessageHistoryItemDto>(total);
        for (var i = 1; i <= total; i++)
        {
            pool.Add(new MessageHistoryItemDto
            {
                MessageId = $"svr-{i:000}",
                SenderUserId = PeerId,
                ReceiverUserId = OwnerId,
                Content = $"历史消息 {i}",
                ReceivedAtMs = baseMs + i * 1000
            });
        }

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        SetupPagedHistoryServer(tcp, serializer, pool);

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        var eventBus = new InMemoryEventBus();
        var store = new MessageStore(_db, eventBus, session);

        // 第一页：无游标 → 最新 50 条（svr-071..svr-120），仍有更多
        var page1 = await store.FetchAndPersistHistoryAsync(Session, ConvId, limit: 50, ct: CancellationToken.None);
        Assert.Equal(50, page1.Count);
        Assert.Equal("svr-120", page1[^1].MessageId);
        Assert.Equal("svr-071", page1[0].MessageId);

        // 第二页：以 svr-071 为游标 → 更早 50 条（svr-021..svr-070）
        var page2 = await store.FetchAndPersistHistoryAsync(
            Session, ConvId, limit: 50,
            beforeReceivedAtMs: page1[0].ReceivedAtMs, beforeMessageId: page1[0].MessageId,
            ct: CancellationToken.None);
        Assert.Equal(50, page2.Count);
        Assert.Equal("svr-070", page2[^1].MessageId);
        Assert.Equal("svr-021", page2[0].MessageId);

        // 第三页：尽头（svr-001..svr-020），HasMore=false
        var page3 = await store.FetchAndPersistHistoryAsync(
            Session, ConvId, limit: 50,
            beforeReceivedAtMs: page2[0].ReceivedAtMs, beforeMessageId: page2[0].MessageId,
            ct: CancellationToken.None);
        Assert.Equal(20, page3.Count);
        Assert.Equal("svr-001", page3[0].MessageId);

        // 增量持久化：共 120 条、无重复、本地升序完整
        var all = await _db.GetMessagesAsync(OwnerId, ConvId, 200);
        Assert.Equal(total, all.Count);
        Assert.Equal(total, all.Select(m => m.MessageId).Distinct().Count());
        Assert.Equal("svr-001", all[0].MessageId);
        Assert.Equal("svr-120", all[^1].MessageId);

        // 幂等：同游标重复拉取不重复入库、水位不回退
        var page2Again = await store.FetchAndPersistHistoryAsync(
            Session, ConvId, limit: 50,
            beforeReceivedAtMs: page1[0].ReceivedAtMs, beforeMessageId: page1[0].MessageId,
            ct: CancellationToken.None);
        Assert.Equal(50, page2Again.Count);
        var allAgain = await _db.GetMessagesAsync(OwnerId, ConvId, 200);
        Assert.Equal(total, allAgain.Count);

        var cursor = await _db.GetSyncCursorAsync(OwnerId, ConvId);
        Assert.Equal("svr-120", cursor!.AfterMessageId);
    }

    /// <summary>历史分页服务器模拟：按 before 游标返回更早的 limit 条（升序），HasMore 反映是否还有更早。</summary>
    private static void SetupPagedHistoryServer(
        ScriptedTcpClient tcp,
        IPacketBodySerializer serializer,
        IReadOnlyList<MessageHistoryItemDto> pool)
    {
        tcp.OnFrameSent += (cmd, body) =>
        {
            try
            {
                switch (cmd)
                {
                    case PacketCommand.MessageHistoryRequest:
                        {
                            var req = serializer.Deserialize<MessageHistoryRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null)
                                return;

                            // 无游标 → 最新一页；有游标 → 更早一页（均按时间升序返回，NextCursor 指向页内最早）
                            var candidates = pool.AsEnumerable();
                            if (req.BeforeReceivedAtMs is { } before)
                                candidates = candidates.Where(m => m.ReceivedAtMs < before
                                    && !string.Equals(m.MessageId, req.BeforeMessageId, StringComparison.Ordinal));

                            var ordered = candidates.OrderByDescending(m => m.ReceivedAtMs).Take(req.Limit).Reverse().ToList();
                            var hasMore = candidates.Count() > ordered.Count;
                            var cursor = ordered.Count == 0 ? null : new MessageHistoryCursorDto
                            {
                                ReceivedAtMs = ordered[0].ReceivedAtMs,
                                MessageId = ordered[0].MessageId
                            };

                            InjectPacket(tcp, serializer, PacketCommand.MessageHistoryPage,
                                new MessageHistoryPageDto
                                {
                                    RequestId = req.RequestId ?? string.Empty,
                                    Succeeded = true,
                                    ConversationId = req.ConversationId ?? string.Empty,
                                    Items = ordered,
                                    HasMore = hasMore,
                                    NextCursor = cursor
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

    private async Task<SyncCompletedEventArgs> RunSyncAsync(SyncEngine engine, bool expectSuccess)
    {
        var completed = new TaskCompletionSource<SyncCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, e) => completed.TrySetResult(e);
        engine.Start(Session);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (expectSuccess)
            Assert.True(result.Succeeded, $"同步失败: {result.ErrorCode} {result.ErrorMessage}");
        return result;
    }

    private LocalOutboxMessage NewOutbox(string clientId) => new()
    {
        OwnerUserId = OwnerId,
        ClientMessageId = clientId,
        ConversationId = ConvId,
        TargetUserId = PeerId,
        Content = "离线发送测试",
        Status = OutboxStatus.Queued,
        QueuedAt = DateTime.UtcNow
    };

    private LocalMessage NewLocalMessage(string clientId, string? content = null) => new()
    {
        OwnerUserId = OwnerId,
        ClientMessageId = clientId,
        ConversationId = ConvId,
        SenderUserId = OwnerId,
        ReceiverUserId = PeerId,
        Content = content ?? "离线发送测试",
        ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 500,
        Status = MessageStatus.Queued,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static MessageHistoryItemDto NewHistoryItem(
        string messageId,
        string clientMessageId,
        string content,
        long receivedAtMs,
        int editVersion = 1,
        long? recalledAtMs = null) => new()
        {
            MessageId = messageId,
            ClientMessageId = clientMessageId,
            SenderUserId = messageId == "svr-2" ? PeerId : OwnerId,
            ReceiverUserId = PeerId,
            Content = content,
            ReceivedAtMs = receivedAtMs,
            EditVersion = editVersion,
            RecalledAtMs = recalledAtMs
        };

    /// <summary>同步服务器模拟：应答 SyncBootstrap / 会话列表 / 历史分页三类请求。</summary>
    private static void SetupSyncServer(
        ScriptedTcpClient tcp,
        IPacketBodySerializer serializer,
        Func<SyncBootstrapResponseDto> bootstrap)
    {
        tcp.OnFrameSent += (cmd, body) =>
        {
            try
            {
                switch (cmd)
                {
                    case PacketCommand.SyncBootstrapRequest:
                        {
                            var req = serializer.Deserialize<SyncBootstrapRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null)
                                return;
                            var resp = bootstrap();
                            resp.RequestId = req.RequestId ?? string.Empty;
                            InjectPacket(tcp, serializer, PacketCommand.SyncBootstrapResponse, resp);
                            break;
                        }
                    case PacketCommand.ConversationListRequest:
                        {
                            var req = serializer.Deserialize<ConversationListRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null)
                                return;
                            InjectPacket(tcp, serializer, PacketCommand.ConversationListPage,
                                new ConversationListResponseDto { RequestId = req.RequestId ?? string.Empty, Succeeded = true, HasMore = false });
                            break;
                        }
                    case PacketCommand.MessageHistoryRequest:
                        {
                            var req = serializer.Deserialize<MessageHistoryRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null)
                                return;
                            InjectPacket(tcp, serializer, PacketCommand.MessageHistoryPage,
                                new MessageHistoryPageDto
                                {
                                    RequestId = req.RequestId ?? string.Empty,
                                    Succeeded = true,
                                    ConversationId = req.ConversationId ?? string.Empty,
                                    HasMore = false
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

    private static void InjectRawPacket(
        ScriptedTcpClient tcp,
        PacketCommand command,
        string json)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var packet = new MessagePacket(command, new ReadOnlySequence<byte>(body));
        var frameWriter = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + body.Length);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
        tcp.InjectData(frameWriter.WrittenMemory);
    }

    /// <summary>设置自动鉴权响应：收到 AuthRequest 后立即注入 AuthResponse。</summary>
    private static void SetupAutoAuth(
        ScriptedTcpClient tcp,
        IPacketBodySerializer serializer,
        long userId,
        bool success = true)
    {
        tcp.OnFrameSent += (cmd, _) =>
        {
            if (cmd == PacketCommand.AuthenticationRequest)
            {
                InjectPacket(tcp, serializer, PacketCommand.AuthenticationResponse,
                    new AuthResponseDto { Success = success, UserId = userId });
            }
        };
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
                if (pkt.Command == PacketCommand.ClientHello)
                    OnDataChunkReceived?.Invoke(this, TcpHandshakeTestServer.ServerHelloFrame);
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



