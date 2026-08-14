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
/// REL-E2E-4 关系同步边界验收测试：缺失/重复 cursor、空页 HasMore、分页中断、
/// 断网重连（失败不推进水位）、多设备 last-write-wins、账户隔离、能力关闭 fail-closed。
/// 全部经由真实 SyncEngine + ScriptedTcpClient wire 路径（或投影持久化路径）验证。
/// </summary>
public class RelationshipSyncTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 7001;
    private const long PeerId = 9003;
    private static readonly SessionStamp Session = new(OwnerId, 1, Guid.NewGuid());

    public RelationshipSyncTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_relsync_{Guid.NewGuid():N}.db");
        _factory = new DbContextFactoryStub(_dbPath);
        _db = new DatabaseService(_factory);
        using var ctx = _factory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                File.Delete(_dbPath);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    // ---- 辅助构造 ----

    private static RelationshipChangeLogEntryDto Upsert(string resourceId, long userId, string? status = null) => new()
    {
        Operation = RelationshipChangeOperationDto.Upsert,
        ResourceId = resourceId,
        UserId = userId,
        Status = status,
        CreatedAtMs = 1_700_000_000_000,
        OccurredAtMs = 1_700_000_000_000
    };

    private static RelationshipChangeLogEntryDto Delete(string resourceId, long userId) => new()
    {
        Operation = RelationshipChangeOperationDto.Delete,
        ResourceId = resourceId,
        UserId = userId
    };

    private static RelationshipListItemDto Item(string resourceId, long userId, string? status = null) => new()
    {
        ResourceId = resourceId,
        UserId = userId,
        Status = status,
        CreatedAtMs = 1_700_000_000_000
    };

    private static RelationshipCatchUpDto CatchUp(
        RelationshipListTypeDto listType,
        IReadOnlyList<RelationshipChangeLogEntryDto>? changes = null,
        long nextSequence = 1,
        bool? resetRequired = null,
        bool hasMore = false,
        string? nextCursor = null,
        string? errorCode = null,
        string? errorMessage = null) => new()
    {
        ListType = listType,
        Changes = changes ?? [],
        NextSequence = nextSequence,
        ResetRequired = resetRequired,
        HasMore = hasMore,
        NextCursor = nextCursor,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };

    /// <summary>应答 bootstrap：回显请求 Id 并返回给定关系增量（可选检查请求体）。</summary>
    private static void RespondBootstrap(
        ScriptedTcpClient tcp,
        IPacketBodySerializer serializer,
        IReadOnlyList<RelationshipCatchUpDto>? catchUps,
        Action<SyncBootstrapRequestDto>? inspect = null)
    {
        tcp.OnFrameSent += (command, body) =>
        {
            if (command != PacketCommand.SyncBootstrapRequest)
                return;
            var request = serializer.Deserialize<SyncBootstrapRequestDto>(new ReadOnlySequence<byte>(body));
            if (request is null)
                return;
            inspect?.Invoke(request);
            InjectPacket(tcp, serializer, PacketCommand.SyncBootstrapResponse, new SyncBootstrapResponseDto
            {
                RequestId = request.RequestId ?? string.Empty,
                Succeeded = true,
                RelationshipCatchUps = catchUps
            });
        };
    }

    /// <summary>应答关系列表分页：按请求（含游标）返回页面。</summary>
    private static void RespondRelationshipList(
        ScriptedTcpClient tcp,
        IPacketBodySerializer serializer,
        Func<RelationshipListRequestDto, RelationshipListResponseDto> responder)
    {
        tcp.OnFrameSent += (command, body) =>
        {
            if (command != PacketCommand.RelationshipListRequest)
                return;
            var request = serializer.Deserialize<RelationshipListRequestDto>(new ReadOnlySequence<byte>(body));
            if (request is null)
                return;
            var response = responder(request);
            response.RequestId = request.RequestId ?? string.Empty;
            InjectPacket(tcp, serializer, PacketCommand.RelationshipListResponse, response);
        };
    }

    /// <summary>构建一条已鉴权的真实会话链路（可选能力关闭替身）。</summary>
    private static async Task<(ChatSessionClient Session, SyncEngine Engine)> BuildSyncAsync(
        ScriptedTcpClient tcp,
        JsonPacketBodySerializer serializer,
        DatabaseService db,
        bool relationshipDisabled = false)
    {
        ChatSessionClient session = relationshipDisabled
            ? new RelationshipDisabledSession(tcp, new MessagePacketCodec(), serializer)
            : new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);
        Assert.True(session.IsAuthenticated);
        var store = new MessageStore(db, new InMemoryEventBus(), session);
        var engine = new SyncEngine(session, store, db, new SyncCheckpointStore(store, db), new SyncConflictResolver());
        return (session, engine);
    }

    private static async Task<SyncCompletedEventArgs> RunSyncAsync(SyncEngine engine)
    {
        var completed = new TaskCompletionSource<SyncCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, result) => completed.TrySetResult(result);
        engine.Start(Session);
        return await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    // ---- 缺失 cursor / 重复 cursor / 水位回退 ----

    /// <summary>bootstrap 关系增量声明 HasMore 但引擎不支持 bootstrap 外续页 → fail-closed，不推进水位、不落库。</summary>
    [Fact]
    public async Task Relationship_Bootstrap_CatchUp_HasMore_Fails_Closed_Without_Applying()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp(RelationshipListTypeDto.Friends,
                changes: [Upsert("friendship-1", PeerId)],
                hasMore: true,
                nextCursor: "cursor-1",
                nextSequence: 99)
        ]);
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var result = await RunSyncAsync(engine);

        Assert.False(result.Succeeded);
        Assert.Equal("RELATIONSHIP_CURSOR_UNSUPPORTED", result.ErrorCode);
        Assert.Empty(await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends));
        Assert.Empty(await _db.GetRelationshipWatermarksAsync(OwnerId));
    }

    /// <summary>同一列表类型在 bootstrap 增量中重复出现 → RELATIONSHIP_DUPLICATE_LIST。
    /// 各列表类型按独立水位顺序应用：首个列表已落库（其水位自洽），重复列表被拒且整批失败。</summary>
    [Fact]
    public async Task Relationship_Duplicate_List_Type_Fails_Closed()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp(RelationshipListTypeDto.Friends, changes: [Upsert("friendship-1", PeerId)], nextSequence: 1),
            CatchUp(RelationshipListTypeDto.Friends, changes: [Upsert("friendship-2", PeerId)], nextSequence: 2)
        ]);
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var result = await RunSyncAsync(engine);

        Assert.False(result.Succeeded);
        Assert.Equal("RELATIONSHIP_DUPLICATE_LIST", result.ErrorCode);
        // 首个 Friends 列表已按水位 1 应用；重复列表未再推进（仍为 1）。
        var projection = await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends);
        var item = Assert.Single(projection);
        Assert.Equal("friendship-1", item.ResourceId);
        var watermark = Assert.Single(await _db.GetRelationshipWatermarksAsync(OwnerId));
        Assert.Equal(RelationshipListTypeDto.Friends, watermark.ListType);
        Assert.Equal(1, watermark.AfterSequence);
    }

    /// <summary>服务端返回的水位低于本地已持久化水位 → RELATIONSHIP_WATERMARK_REGRESSED，本地水位保持。</summary>
    [Fact]
    public async Task Relationship_Watermark_Regression_Fails_And_Keeps_Local()
    {
        await _db.ApplyRelationshipChangesAsync(OwnerId, RelationshipListTypeDto.Friends,
            [Upsert("friendship-1", PeerId)], afterSequence: 5);

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp(RelationshipListTypeDto.Friends, changes: [], nextSequence: 3)
        ]);
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var result = await RunSyncAsync(engine);

        Assert.False(result.Succeeded);
        Assert.Equal("RELATIONSHIP_WATERMARK_REGRESSED", result.ErrorCode);
        var watermark = Assert.Single(await _db.GetRelationshipWatermarksAsync(OwnerId));
        Assert.Equal(5, watermark.AfterSequence);
        var projection = await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends);
        var item = Assert.Single(projection);
        Assert.Equal("friendship-1", item.ResourceId);
    }

    /// <summary>未知列表类型 → RELATIONSHIP_LIST_TYPE_INVALID（fail-closed，不猜测列表）。</summary>
    [Fact]
    public async Task Relationship_Unknown_List_Type_Fails_Closed()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp((RelationshipListTypeDto)9, changes: [Upsert("friendship-1", PeerId)], nextSequence: 1)
        ]);
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var result = await RunSyncAsync(engine);

        Assert.False(result.Succeeded);
        Assert.Equal("RELATIONSHIP_LIST_TYPE_INVALID", result.ErrorCode);
        Assert.Empty(await _db.GetRelationshipProjectionAsync(OwnerId, (RelationshipListTypeDto)9));
    }

    /// <summary>未知变更操作 → RELATIONSHIP_CHANGE_INVALID（fail-closed，不得跳过或猜测）。</summary>
    [Fact]
    public async Task Relationship_Unknown_Change_Operation_Fails_Closed()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp(RelationshipListTypeDto.Friends, nextSequence: 1, changes:
            [
                new RelationshipChangeLogEntryDto
                {
                    Operation = (RelationshipChangeOperationDto)99,
                    ResourceId = "friendship-1",
                    UserId = PeerId
                }
            ])
        ]);
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var result = await RunSyncAsync(engine);

        Assert.False(result.Succeeded);
        Assert.Equal("RELATIONSHIP_CHANGE_INVALID", result.ErrorCode);
        Assert.Empty(await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends));
    }

    /// <summary>空变更页只推进水位、不改投影（opaque 水位语义）。</summary>
    [Fact]
    public async Task Relationship_Empty_Changes_Advances_Watermark_Only()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp(RelationshipListTypeDto.Friends, changes: [], nextSequence: 7)
        ]);
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var result = await RunSyncAsync(engine);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Empty(await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends));
        var watermark = Assert.Single(await _db.GetRelationshipWatermarksAsync(OwnerId));
        Assert.Equal(RelationshipListTypeDto.Friends, watermark.ListType);
        Assert.Equal(7, watermark.AfterSequence);
    }

    // ---- 分页中断 / reset ----

    /// <summary>reset 全量重建：空列表页 → 投影清空、水位被新值替换（覆盖「空页 HasMore=false」）。</summary>
    [Fact]
    public async Task Relationship_Reset_With_Empty_Result_Rebuilds_To_Empty_And_Replaces_Watermark()
    {
        // 预置旧投影 + 水位 1
        await _db.ApplyRelationshipChangesAsync(OwnerId, RelationshipListTypeDto.Friends,
            [Upsert("friendship-1", PeerId)], afterSequence: 1);

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp(RelationshipListTypeDto.Friends, resetRequired: true, nextSequence: 42)
        ]);
        RespondRelationshipList(tcp, serializer, _ => new RelationshipListResponseDto
        {
            Succeeded = true,
            ListType = RelationshipListTypeDto.Friends,
            Items = [],
            HasMore = false,
            NextCursor = null
        });
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var result = await RunSyncAsync(engine);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Empty(await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends));
        var watermark = Assert.Single(await _db.GetRelationshipWatermarksAsync(OwnerId));
        Assert.Equal(42, watermark.AfterSequence);
    }

    /// <summary>reset 重建页声明 HasMore 但缺 NextCursor → 重建失败，旧投影与水位保留（不静默截断）。</summary>
    [Fact]
    public async Task Relationship_Reset_Page_HasMore_Without_Cursor_Fails_And_Retains_Prior()
    {
        await _db.ApplyRelationshipChangesAsync(OwnerId, RelationshipListTypeDto.Friends,
            [Upsert("friendship-1", PeerId)], afterSequence: 1);

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp(RelationshipListTypeDto.Friends, resetRequired: true, nextSequence: 42)
        ]);
        RespondRelationshipList(tcp, serializer, _ => new RelationshipListResponseDto
        {
            Succeeded = true,
            ListType = RelationshipListTypeDto.Friends,
            Items = [Item("friendship-2", PeerId)],
            HasMore = true,
            NextCursor = null
        });
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var result = await RunSyncAsync(engine);

        Assert.False(result.Succeeded);
        Assert.Equal("RELATIONSHIP_RESET_FAILED", result.ErrorCode);
        var projection = await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends);
        var item = Assert.Single(projection);
        Assert.Equal("friendship-1", item.ResourceId);
        var watermark = Assert.Single(await _db.GetRelationshipWatermarksAsync(OwnerId));
        Assert.Equal(1, watermark.AfterSequence);
    }

    /// <summary>reset 重建游标不前进（重复）→ 检测到死循环 → 重建失败，旧状态保留。</summary>
    [Fact]
    public async Task Relationship_Reset_Non_Advancing_Cursor_Fails()
    {
        await _db.ApplyRelationshipChangesAsync(OwnerId, RelationshipListTypeDto.Friends,
            [Upsert("friendship-1", PeerId)], afterSequence: 1);

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp(RelationshipListTypeDto.Friends, resetRequired: true, nextSequence: 42)
        ]);
        RespondRelationshipList(tcp, serializer, _ => new RelationshipListResponseDto
        {
            Succeeded = true,
            ListType = RelationshipListTypeDto.Friends,
            Items = [Item("friendship-2", PeerId)],
            HasMore = true,
            NextCursor = "cursor-1"
        });
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var result = await RunSyncAsync(engine);

        Assert.False(result.Succeeded);
        Assert.Equal("RELATIONSHIP_RESET_FAILED", result.ErrorCode);
        var projection = await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends);
        var item = Assert.Single(projection);
        Assert.Equal("friendship-1", item.ResourceId);
    }

    /// <summary>分页中断（第 2 页失败）→ 重建失败且不替换旧投影/水位（可安全重连续跑）。</summary>
    [Fact]
    public async Task Relationship_Reset_Paging_Interruption_Retains_Prior_State()
    {
        await _db.ApplyRelationshipChangesAsync(OwnerId, RelationshipListTypeDto.Friends,
            [Upsert("friendship-1", PeerId)], afterSequence: 1);

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp(RelationshipListTypeDto.Friends, resetRequired: true, nextSequence: 42)
        ]);
        var page = 0;
        RespondRelationshipList(tcp, serializer, _ =>
        {
            page++;
            return page == 1
                ? new RelationshipListResponseDto
                {
                    Succeeded = true,
                    ListType = RelationshipListTypeDto.Friends,
                    Items = [Item("friendship-2", PeerId)],
                    HasMore = true,
                    NextCursor = "cursor-1"
                }
                : new RelationshipListResponseDto
                {
                    Succeeded = false,
                    ListType = RelationshipListTypeDto.Friends,
                    ErrorCode = "relationship_read_projection_unavailable",
                    ErrorMessage = "投影未就绪",
                    HasMore = false
                };
        });
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var result = await RunSyncAsync(engine);

        Assert.False(result.Succeeded);
        Assert.Equal("RELATIONSHIP_RESET_FAILED", result.ErrorCode);
        Assert.Equal(2, page);
        var projection = await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends);
        var item = Assert.Single(projection);
        Assert.Equal("friendship-1", item.ResourceId);
        var watermark = Assert.Single(await _db.GetRelationshipWatermarksAsync(OwnerId));
        Assert.Equal(1, watermark.AfterSequence);
    }

    // ---- 断网重连 / 能力关闭 ----

    /// <summary>断网重连场景：服务端返回同步错误 → 整批失败且不推进水位、不改投影（fail-closed）。</summary>
    [Fact]
    public async Task Relationship_Server_Error_Retains_Prior_Watermark()
    {
        await _db.ApplyRelationshipChangesAsync(OwnerId, RelationshipListTypeDto.Friends,
            [Upsert("friendship-1", PeerId)], afterSequence: 5);

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp(RelationshipListTypeDto.Friends,
                errorCode: "relationship_read_projection_unavailable",
                errorMessage: "投影尚未完成快照基线",
                nextSequence: 5)
        ]);
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var result = await RunSyncAsync(engine);

        Assert.False(result.Succeeded);
        Assert.Equal("RELATIONSHIP_SYNC_FAILED", result.ErrorCode);
        var watermark = Assert.Single(await _db.GetRelationshipWatermarksAsync(OwnerId));
        Assert.Equal(5, watermark.AfterSequence);
        var projection = await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends);
        var item = Assert.Single(projection);
        Assert.Equal("friendship-1", item.ResourceId);
    }

    /// <summary>能力关闭时服务端仍返回关系增量 → RELATIONSHIP_SYNC_UNSUPPORTED，不应用任何关系水位。</summary>
    [Fact]
    public async Task Relationship_Disabled_Capability_Rejects_Server_Payload()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        RespondBootstrap(tcp, serializer,
        [
            CatchUp(RelationshipListTypeDto.Friends, changes: [Upsert("friendship-1", PeerId)], nextSequence: 1)
        ]);
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db, relationshipDisabled: true);

        var result = await RunSyncAsync(engine);

        Assert.False(result.Succeeded);
        Assert.Equal("RELATIONSHIP_SYNC_UNSUPPORTED", result.ErrorCode);
        Assert.Empty(await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends));
        Assert.Empty(await _db.GetRelationshipWatermarksAsync(OwnerId));
    }

    /// <summary>Upsert → Delete 经由 wire 两轮同步：投影移除、水位单调推进。</summary>
    [Fact]
    public async Task Relationship_Upsert_Then_Delete_Through_Wire_Removes_Projection()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var syncCall = 0;
        tcp.OnFrameSent += (command, body) =>
        {
            if (command != PacketCommand.SyncBootstrapRequest)
                return;
            var request = serializer.Deserialize<SyncBootstrapRequestDto>(new ReadOnlySequence<byte>(body));
            if (request is null)
                return;
            syncCall++;
            var catchUp = syncCall == 1
                ? CatchUp(RelationshipListTypeDto.Friends, changes: [Upsert("friendship-1", PeerId)], nextSequence: 1)
                : CatchUp(RelationshipListTypeDto.Friends, changes: [Delete("friendship-1", PeerId)], nextSequence: 2);
            InjectPacket(tcp, serializer, PacketCommand.SyncBootstrapResponse, new SyncBootstrapResponseDto
            {
                RequestId = request.RequestId ?? string.Empty,
                Succeeded = true,
                RelationshipCatchUps = [catchUp]
            });
        };
        var (_, engine) = await BuildSyncAsync(tcp, serializer, _db);

        var first = await RunSyncAsync(engine);
        Assert.True(first.Succeeded, first.ErrorMessage);
        var afterUpsert = await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends);
        var item = Assert.Single(afterUpsert);
        Assert.Equal("friendship-1", item.ResourceId);

        var second = await RunSyncAsync(engine);
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.Empty(await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends));
        var watermark = Assert.Single(await _db.GetRelationshipWatermarksAsync(OwnerId));
        Assert.Equal(2, watermark.AfterSequence);
    }

    /// <summary>多设备/多批次：同一 ResourceId 多次 Upsert → 末次生效（last-write-wins）；不同 owner 互不串扰（账户隔离）。</summary>
    [Fact]
    public async Task Relationship_LastWriteWins_And_Account_Isolation()
    {
        const long ownerB = 7002;

        // 设备 A 批次 1：Pending
        await _db.ApplyRelationshipChangesAsync(OwnerId, RelationshipListTypeDto.Friends,
            [Upsert("friendship-x", PeerId, "Pending")], afterSequence: 1);
        // 设备 A 批次 2（另一设备/另一轮）：Accepted → 末次生效
        await _db.ApplyRelationshipChangesAsync(OwnerId, RelationshipListTypeDto.Friends,
            [Upsert("friendship-x", PeerId, "Accepted")], afterSequence: 2);

        // 其他账户用相同 ResourceId 写入 Blocked → 互不串扰
        await _db.ApplyRelationshipChangesAsync(ownerB, RelationshipListTypeDto.Friends,
            [Upsert("friendship-x", PeerId, "Blocked")], afterSequence: 1);

        var ownerA = await _db.GetRelationshipProjectionAsync(OwnerId, RelationshipListTypeDto.Friends);
        var a = Assert.Single(ownerA);
        Assert.Equal("Accepted", a.Status);
        var ownerBProj = await _db.GetRelationshipProjectionAsync(ownerB, RelationshipListTypeDto.Friends);
        var b = Assert.Single(ownerBProj);
        Assert.Equal("Blocked", b.Status);

        var watermarkA = Assert.Single(await _db.GetRelationshipWatermarksAsync(OwnerId));
        Assert.Equal(2, watermarkA.AfterSequence);
        var watermarkB = Assert.Single(await _db.GetRelationshipWatermarksAsync(ownerB));
        Assert.Equal(1, watermarkB.AfterSequence);
    }

    // ---- 测试替身 ----

    /// <summary>能力关闭替身：仅关闭关系只读能力，其余走真实 wire。</summary>
    private sealed class RelationshipDisabledSession : ChatSessionClient
    {
        public RelationshipDisabledSession(ITcpClient tcp, IMessagePacketCodec codec, IPacketBodySerializer serializer)
            : base(tcp, codec, serializer)
        {
        }

        public override bool SupportsRelationshipRead => false;
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
            if (cmd == PacketCommand.AuthenticationRequest)
            {
                InjectPacket(tcp, serializer, PacketCommand.AuthenticationResponse,
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
