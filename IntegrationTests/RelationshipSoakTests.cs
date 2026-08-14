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
/// REL-E2E-4 关系同步 5–20 分钟短测（模拟分钟级持续同步流量）。
/// 覆盖：多页分页重建、断线 fail-closed、重连续跑、reset 全量重建、
/// 重建中断（分页 projection_changed）与后续恢复、增量追赶。
/// 每一轮后校验不变量：成功轮 → 投影收敛到服务端权威状态、水位等于服务端序列；
/// 失败轮 → 投影与水位保持上一成功状态（fail-closed 不破坏旧投影）。
/// 以确定性脚本注入故障，数秒真实时间模拟 20 分钟同步。
/// </summary>
public class RelationshipSoakTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 8001;
    private const long PeerBase = 9000;
    private const int SoakMinutes = 20; // 模拟分钟数

    private static readonly SessionStamp Session = new(OwnerId, 1, Guid.NewGuid());

    public RelationshipSoakTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_relsoak_{Guid.NewGuid():N}.db");
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

    /// <summary>
    /// 20 分钟关系同步短测：分页重建 + 断线 fail-closed + 重连续跑 + reset 重建 +
    /// 重建中断恢复。全程投影收敛到服务端权威状态，水位单调不回退。
    /// </summary>
    [Fact]
    public async Task Relationship_Soak_Pagination_Disconnect_Reconnect_Converges()
    {
        var server = new SoakServer();
        server.Seed(SoakServer.ListType.Friends, 250);         // 250 项 → 重建 3 页（page size 100）
        server.Seed(SoakServer.ListType.FriendRequests, 40);
        server.Seed(SoakServer.ListType.BlockedUsers, 25);

        var serializer = new JsonPacketBodySerializer();
        var tcp = new ScriptedTcpClient();
        var engine = await BuildSoakEngineAsync(tcp, serializer, server);

        var converged = SoakServer.EmptySnapshot(); // 客户端起始为空
        var lastGoodWatermarks = new Dictionary<RelationshipListTypeDto, long>();
        var maxWatermarks = new Dictionary<RelationshipListTypeDto, long>();
        var succeededRounds = 0;
        var failedRounds = 0;
        var reconnectCount = 0;

        try
        {
            for (var minute = 1; minute <= SoakMinutes; minute++)
            {
                server.SimulateMinute(minute);

                // 断网重连：销毁旧会话链路，从持久化水位重新建链续跑
                if (minute is 8 or 14)
                {
                    tcp.Dispose();
                    tcp = new ScriptedTcpClient();
                    engine = await BuildSoakEngineAsync(tcp, serializer, server);
                    reconnectCount++;
                }

                var result = await RunSoakRoundAsync(engine);

                if (result.Succeeded)
                {
                    succeededRounds++;
                    await AssertConvergedToServerAsync(server);
                    var watermarks = await ReadWatermarksAsync();
                    Assert.Equal(SoakServer.AllTypes.Length, watermarks.Count);
                    foreach (var type in SoakServer.AllTypes)
                        Assert.Equal(server.Sequence(type), watermarks[ToDto(type)]);
                    lastGoodWatermarks = watermarks;
                    UpdateMax(maxWatermarks, watermarks);
                    converged = server.Snapshot();
                }
                else
                {
                    failedRounds++;
                    await AssertConvergedToSnapshotAsync(converged);
                    var watermarks = await ReadWatermarksAsync();
                    if (lastGoodWatermarks.Count == 0)
                        Assert.Empty(watermarks);
                    else
                        Assert.Equal(lastGoodWatermarks, watermarks);
                }

                server.Outage = false;
                server.ForceReset = false;
                server.FailRebuildOnPage = -1;
            }
        }
        finally
        {
            tcp.Dispose();
        }

        // 终局不变量：投影等于最后一次成功轮的服务端快照；水位等于历史最大值（未因断线回退）。
        await AssertConvergedToSnapshotAsync(converged);
        var finalWatermarks = await ReadWatermarksAsync();
        Assert.Equal(SoakServer.AllTypes.Length, finalWatermarks.Count);
        foreach (var type in SoakServer.AllTypes)
            Assert.Equal(maxWatermarks[ToDto(type)], finalWatermarks[ToDto(type)]);

        Assert.True(succeededRounds > 0, "短测应包含成功轮次");
        Assert.True(failedRounds > 0, "短测应包含断线 fail-closed 轮次");
        Assert.Equal(2, reconnectCount);
        Assert.True(server.RebuildCount >= 3, $"分页重建应发生多次，实际 {server.RebuildCount}");
        Assert.Equal(SoakMinutes, succeededRounds + failedRounds);
    }

    /// <summary>
    /// HTTP mutation 与 TCP read 一致性（NEXT-STAGE 完成标准：同一账户 HTTP 权威列表
    /// 与 TCP 读取投影逐项一致，客户端不引入第二权威）。
    /// 假 HTTP 服务（对应 Server FriendshipController）作为权威写/读路径，SoakServer
    /// 作为同一权威的 TCP producer；每步 HTTP mutation 后跑一轮 TCP 增量同步，断言
    /// 客户端关系投影与 HTTP 权威列表逐项一致；断线轮 fail-closed 保持旧一致状态，恢复后收敛。
    /// </summary>
    [Fact]
    public async Task Relationship_HttpMutation_TcpRead_Converges_ItemByItem()
    {
        var server = new SoakServer();
        var serializer = new JsonPacketBodySerializer();
        var tcp = new ScriptedTcpClient();
        var engine = await BuildSoakEngineAsync(tcp, serializer, server);
        var http = new FakeHttpFriendshipService(server);

        try
        {
            // 首屏：空权威 → 三个列表均空（空页 HasMore=false 路径）。
            await RunSoakRoundAsync(engine);
            await AssertHttpEqualsTcpAsync(http);

            // 1) peer1 发好友申请 → 权威 FriendRequests += 1；TCP 增量同步后一致。
            http.SendFriendRequest(PeerBase + 1);
            await RunSoakRoundAsync(engine);
            await AssertHttpEqualsTcpAsync(http);

            // 2) 接受申请 → 权威 Friends += 1、FriendRequests -= 1。
            http.AcceptRequest(PeerBase + 1);
            await RunSoakRoundAsync(engine);
            await AssertHttpEqualsTcpAsync(http);

            // 3) 拉黑 peer2 → BlockedUsers += 1。
            http.BlockUser(PeerBase + 2);
            await RunSoakRoundAsync(engine);
            await AssertHttpEqualsTcpAsync(http);

            // 4) 删除好友 peer1 → Friends -= 1。
            http.DeleteFriend(PeerBase + 1);
            await RunSoakRoundAsync(engine);
            await AssertHttpEqualsTcpAsync(http);

            // 5) 解除拉黑 peer2 → BlockedUsers -= 1。
            http.UnblockUser(PeerBase + 2);
            await RunSoakRoundAsync(engine);
            await AssertHttpEqualsTcpAsync(http);

            // 6) 断线 fail-closed：权威新增申请但 TCP 同步失败 → 投影/水位保持上一一致状态。
            var lastGood = server.Snapshot();
            var lastWatermarks = await ReadWatermarksAsync();
            server.Outage = true;
            http.SendFriendRequest(PeerBase + 3);
            var failed = await RunSoakRoundAsync(engine);
            server.Outage = false;
            Assert.False(failed.Succeeded, "断线轮应 fail-closed 失败");
            await AssertConvergedToSnapshotAsync(lastGood);
            Assert.Equal(lastWatermarks, await ReadWatermarksAsync());

            // 7) 恢复后增量同步 → 收敛到权威（含断线期间的新申请）。
            await RunSoakRoundAsync(engine);
            await AssertHttpEqualsTcpAsync(http);

            // 终局：三个列表 TCP 投影与 HTTP 权威逐项一致，水位 = 权威序列。
            var finalWatermarks = await ReadWatermarksAsync();
            foreach (var type in SoakServer.AllTypes)
                Assert.Equal(server.Sequence(type), finalWatermarks[ToDto(type)]);
        }
        finally
        {
            tcp.Dispose();
        }
    }

    /// <summary>TCP 读取投影必须与同一账户的 HTTP 权威列表逐项一致（客户端无第二权威）。</summary>
    private Task AssertHttpEqualsTcpAsync(FakeHttpFriendshipService http)
        => AssertConvergedToSnapshotAsync(http.QueryAll());

    // ---- 不变量断言 ----

    private async Task AssertConvergedToServerAsync(SoakServer server)
        => await AssertConvergedToSnapshotAsync(server.Snapshot());

    private async Task AssertConvergedToSnapshotAsync(Dictionary<SoakServer.ListType, SoakServer.Item[]> expected)
    {
        foreach (var type in SoakServer.AllTypes)
        {
            var projection = await _db.GetRelationshipProjectionAsync(OwnerId, ToDto(type));
            Assert.Equal(expected[type].Length, projection.Count);
            var byResource = projection.ToDictionary(x => x.ResourceId, StringComparer.Ordinal);
            foreach (var item in expected[type])
            {
                Assert.True(byResource.TryGetValue(item.ResourceId, out var actual),
                    $"列表 {type} 缺少资源 {item.ResourceId}");
                Assert.Equal(item.UserId, actual.UserId);
                Assert.Equal(item.Status, actual.Status);
            }
        }
    }

    private async Task<Dictionary<RelationshipListTypeDto, long>> ReadWatermarksAsync()
        => (await _db.GetRelationshipWatermarksAsync(OwnerId))
            .ToDictionary(w => w.ListType, w => w.AfterSequence);

    private static void UpdateMax(
        Dictionary<RelationshipListTypeDto, long> max,
        IReadOnlyDictionary<RelationshipListTypeDto, long> current)
    {
        foreach (var (type, sequence) in current)
        {
            if (!max.TryGetValue(type, out var existing) || sequence > existing)
                max[type] = sequence;
        }
    }

    // ---- 链路构造 ----

    private async Task<SyncEngine> BuildSoakEngineAsync(
        ScriptedTcpClient tcp,
        JsonPacketBodySerializer serializer,
        SoakServer server)
    {
        server.Bind(tcp, serializer);
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);
        Assert.True(session.IsAuthenticated);
        var store = new MessageStore(_db, new InMemoryEventBus(), session);
        return new SyncEngine(session, store, _db,
            new SyncCheckpointStore(store, _db), new SyncConflictResolver());
    }

    private static async Task<SyncCompletedEventArgs> RunSoakRoundAsync(SyncEngine engine)
    {
        var completed = new TaskCompletionSource<SyncCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, result) => completed.TrySetResult(result);
        engine.Start(Session);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        // 等待引擎任务真正退出，避免下一轮 Start 因「仍在运行」被忽略。
        while (engine.IsSyncing)
            await Task.Delay(10);
        return result;
    }

    private static RelationshipListTypeDto ToDto(SoakServer.ListType type) => (RelationshipListTypeDto)type;

    private static SoakServer.ListType FromDto(RelationshipListTypeDto type) => (SoakServer.ListType)type;

    // ---- 测试替身：状态化服务端（权威列表 + 增量日志 + 故障注入） ----

    /// <summary>
    /// 模拟服务端权威关系状态：持列表快照、按序增量日志与每列表序列，
    /// 依据客户端上报水位计算 catch-up；reset 时经分页 list 重建。
    /// 故障注入：Outage（投影不可用）、ForceReset（强制全量重建）、
    /// FailRebuildOnPage（分页期间 projection_changed 中断重建）。
    /// </summary>
    private sealed class SoakServer
    {
        public enum ListType : byte { Friends = 1, FriendRequests = 2, BlockedUsers = 3 }

        public readonly record struct Item(string ResourceId, long UserId, string? Status);

        public static readonly ListType[] AllTypes = [ListType.Friends, ListType.FriendRequests, ListType.BlockedUsers];

        private sealed class Entry
        {
            public required string ResourceId;
            public long UserId;
            public string? Status;
        }

        private readonly Dictionary<ListType, SortedDictionary<string, Entry>> _lists =
            AllTypes.ToDictionary(t => t, _ => new SortedDictionary<string, Entry>(StringComparer.Ordinal));
        private readonly Dictionary<ListType, List<(long Seq, string ResourceId, long UserId, string? Status, bool IsDelete)>> _log =
            AllTypes.ToDictionary(t => t, _ => new List<(long, string, long, string?, bool)>());
        private readonly Dictionary<ListType, long> _sequence =
            AllTypes.ToDictionary(t => t, _ => 0L);
        private long _uid;

        /// <summary>下一轮所有列表类型返回投影不可用（断线）。</summary>
        public bool Outage;

        /// <summary>下一轮所有列表类型返回 ResetRequired（强制全量重建）。</summary>
        public bool ForceReset;

        /// <summary>分页重建在该 0-based 页返回 projection_changed（-1 表示不注入）。</summary>
        public int FailRebuildOnPage = -1;

        /// <summary>累计分页 list 请求数（确认重建确实走分页）。</summary>
        public int RebuildCount;

        public long Sequence(ListType type) => _sequence[type];

        public void Seed(ListType type, int count)
        {
            var list = _lists[type];
            for (var i = 0; i < count; i++)
            {
                var resourceId = $"{type}-seed-{i:D3}";
                var userId = PeerBase + ++_uid;
                var status = type == ListType.FriendRequests ? "Pending"
                    : type == ListType.BlockedUsers ? "Blocked"
                    : "Accepted";
                list[resourceId] = new Entry { ResourceId = resourceId, UserId = userId, Status = status };
            }
        }

        /// <summary>确定性分钟脚本：每轮状态变更 + 故障注入。</summary>
        public void SimulateMinute(int minute)
        {
            switch (minute)
            {
                case 2: Add(ListType.Friends, 1, "Accepted"); Add(ListType.FriendRequests, 1, "Pending"); break;
                case 3: Outage = true; break;
                case 5: RemoveFirst(ListType.Friends, 2); Add(ListType.BlockedUsers, 1, "Blocked"); break;
                case 6: ForceReset = true; break;
                case 7: UpdateStatus(ListType.Friends, "Blocked", 2); Add(ListType.FriendRequests, 3, "Pending"); break;
                case 8: Add(ListType.Friends, 1, "Accepted"); break; // 重连
                case 10: ForceReset = true; FailRebuildOnPage = 1; break; // 重建第 2 页中断
                case 12: Outage = true; break;
                case 13: Add(ListType.Friends, 2, "Accepted"); Add(ListType.FriendRequests, 1, "Pending"); RemoveFirst(ListType.BlockedUsers, 1); break;
                case 14: break; // 重连
                case 15: ForceReset = true; break;
                case 16: Add(ListType.Friends, 3, "Accepted"); break;
                case 20: Outage = true; break;
            }
        }

        public void Add(ListType type, int count, string status)
        {
            var list = _lists[type];
            var log = _log[type];
            for (var i = 0; i < count; i++)
            {
                var sequence = ++_sequence[type];
                var resourceId = $"{type}-u{_uid + 1:D4}";
                var userId = PeerBase + ++_uid;
                list[resourceId] = new Entry { ResourceId = resourceId, UserId = userId, Status = status };
                log.Add((sequence, resourceId, userId, status, false));
            }
        }

        public void RemoveFirst(ListType type, int count)
        {
            var list = _lists[type];
            var log = _log[type];
            for (var i = 0; i < count && list.Count > 0; i++)
            {
                var sequence = ++_sequence[type];
                var first = list.First();
                var resourceId = first.Key;
                var userId = first.Value.UserId;
                list.Remove(resourceId);
                log.Add((sequence, resourceId, userId, null, true));
            }
        }

        public void UpdateStatus(ListType type, string status, int count)
        {
            var list = _lists[type];
            var log = _log[type];
            foreach (var entry in list.Values.Take(count).ToList())
            {
                var sequence = ++_sequence[type];
                entry.Status = status;
                log.Add((sequence, entry.ResourceId, entry.UserId, status, false));
            }
        }

        /// <summary>按稳定资源键 Upsert 单项（HTTP mutation 落地的目标化写入）。</summary>
        public void AddItem(ListType type, string resourceId, long userId, string status)
        {
            var list = _lists[type];
            var log = _log[type];
            var sequence = ++_sequence[type];
            list[resourceId] = new Entry { ResourceId = resourceId, UserId = userId, Status = status };
            log.Add((sequence, resourceId, userId, status, false));
        }

        /// <summary>按稳定资源键删除单项（HTTP mutation 落地的目标化删除；不存在视为无操作）。</summary>
        public void RemoveItem(ListType type, string resourceId)
        {
            var list = _lists[type];
            if (!list.TryGetValue(resourceId, out var removed))
                return;
            var log = _log[type];
            var sequence = ++_sequence[type];
            list.Remove(resourceId);
            log.Add((sequence, resourceId, removed.UserId, null, true));
        }

        public Dictionary<ListType, Item[]> Snapshot()
            => AllTypes.ToDictionary(
                t => t,
                t => _lists[t].Values
                    .OrderBy(x => x.ResourceId, StringComparer.Ordinal)
                    .Select(x => new Item(x.ResourceId, x.UserId, x.Status))
                    .ToArray());

        public static Dictionary<ListType, Item[]> EmptySnapshot()
            => AllTypes.ToDictionary(t => t, _ => Array.Empty<Item>());

        public IReadOnlyList<RelationshipCatchUpDto> BuildCatchUps(
            IReadOnlyDictionary<RelationshipListTypeDto, long> clientWatermarks)
        {
            var result = new List<RelationshipCatchUpDto>();
            foreach (var type in AllTypes)
            {
                var dto = ToDto(type);
                if (Outage)
                {
                    result.Add(CatchUp(dto,
                        errorCode: "relationship_read_projection_unavailable",
                        errorMessage: "断网"));
                    continue;
                }

                var current = _sequence[type];
                if (ForceReset || !clientWatermarks.TryGetValue(dto, out var watermark))
                {
                    result.Add(CatchUp(dto, resetRequired: true, nextSequence: current));
                    continue;
                }

                var changes = _log[type]
                    .Where(x => x.Seq > watermark)
                    .Select(x => new RelationshipChangeLogEntryDto
                    {
                        Operation = x.IsDelete
                            ? RelationshipChangeOperationDto.Delete
                            : RelationshipChangeOperationDto.Upsert,
                        ResourceId = x.ResourceId,
                        UserId = x.UserId,
                        Status = x.Status,
                        CreatedAtMs = 1_700_000_000_000,
                        OccurredAtMs = 1_700_000_000_000
                    })
                    .ToList();
                result.Add(CatchUp(dto, changes: changes, nextSequence: current));
            }
            return result;
        }

        public RelationshipListResponseDto RespondList(RelationshipListRequestDto request)
        {
            var type = FromDto(request.ListType);
            var items = _lists[type].Values.OrderBy(x => x.ResourceId, StringComparer.Ordinal).ToList();
            var pageSize = request.PageSize is > 0 ? request.PageSize.Value : 100;
            var pageIndex = request.Cursor is null or "" ? 0 : ParseCursor(request.Cursor);

            if (FailRebuildOnPage == pageIndex)
            {
                return new RelationshipListResponseDto
                {
                    Succeeded = false,
                    ListType = request.ListType,
                    ErrorCode = "relationship_projection_changed",
                    ErrorMessage = "分页期间列表版本变化",
                    ResetRequired = true,
                    HasMore = false
                };
            }

            var totalPages = (int)Math.Ceiling(items.Count / (double)pageSize);
            var hasMore = pageIndex + 1 < totalPages;
            return new RelationshipListResponseDto
            {
                Succeeded = true,
                ListType = request.ListType,
                Items = items.Skip(pageIndex * pageSize).Take(pageSize)
                    .Select(x => new RelationshipListItemDto
                    {
                        ResourceId = x.ResourceId,
                        UserId = x.UserId,
                        Status = x.Status,
                        CreatedAtMs = 1_700_000_000_000
                    })
                    .ToList(),
                HasMore = hasMore,
                NextCursor = hasMore ? $"p{pageIndex + 1}" : null
            };
        }

        public void Bind(ScriptedTcpClient tcp, IPacketBodySerializer serializer)
        {
            tcp.OnFrameSent += (command, body) =>
            {
                if (command == PacketCommand.SyncBootstrapRequest)
                {
                    var request = serializer.Deserialize<SyncBootstrapRequestDto>(new ReadOnlySequence<byte>(body));
                    if (request is null)
                        return;
                    var watermarks = request.RelationshipWatermarks?.ToDictionary(w => w.ListType, w => w.AfterSequence)
                        ?? new Dictionary<RelationshipListTypeDto, long>();
                    InjectPacket(tcp, serializer, PacketCommand.SyncBootstrapResponse, new SyncBootstrapResponseDto
                    {
                        RequestId = request.RequestId ?? string.Empty,
                        Succeeded = true,
                        RelationshipCatchUps = BuildCatchUps(watermarks)
                    });
                }
                else if (command == PacketCommand.RelationshipListRequest)
                {
                    var request = serializer.Deserialize<RelationshipListRequestDto>(new ReadOnlySequence<byte>(body));
                    if (request is null)
                        return;
                    RebuildCount++;
                    var response = RespondList(request);
                    response.RequestId = request.RequestId ?? string.Empty;
                    InjectPacket(tcp, serializer, PacketCommand.RelationshipListResponse, response);
                }
            };
        }

        private static RelationshipCatchUpDto CatchUp(
            RelationshipListTypeDto listType,
            IReadOnlyList<RelationshipChangeLogEntryDto>? changes = null,
            long nextSequence = 0,
            bool? resetRequired = null,
            string? errorCode = null,
            string? errorMessage = null) => new()
        {
            ListType = listType,
            Changes = changes ?? [],
            NextSequence = nextSequence,
            ResetRequired = resetRequired,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };

        private static int ParseCursor(string cursor)
            => int.Parse(cursor.TrimStart('p'));
    }

    // ---- HTTP 权威写/读路径（模拟 Server FriendshipController 对同一权威的状态变更与查询） ----

    /// <summary>
    /// 假 HTTP 服务：HTTP mutation 落地到 <see cref="SoakServer"/> 权威（与 TCP producer
    /// 共享同一底层列表/日志/序列），HTTP 权威读直接以该权威快照返回。用于验证
    /// 「mutation 走 Server HTTP、TCP 读取结果与同一账户 HTTP 权威列表逐项一致」。
    /// </summary>
    private sealed class FakeHttpFriendshipService
    {
        private readonly SoakServer _server;

        public FakeHttpFriendshipService(SoakServer server) => _server = server;

        /// <summary>对应 POST api/Friendship/requests：对方发来好友申请。</summary>
        public void SendFriendRequest(long requesterId)
            => _server.AddItem(SoakServer.ListType.FriendRequests, $"fr-{requesterId:D6}", requesterId, "Pending");

        /// <summary>对应 PUT api/Friendship/requests/{requesterId}/accept：接受申请并成为好友。</summary>
        public void AcceptRequest(long requesterId)
        {
            _server.RemoveItem(SoakServer.ListType.FriendRequests, $"fr-{requesterId:D6}");
            _server.AddItem(SoakServer.ListType.Friends, $"f-{requesterId:D6}", requesterId, "Accepted");
        }

        /// <summary>对应 POST api/Friendship/block：拉黑。</summary>
        public void BlockUser(long targetUserId)
            => _server.AddItem(SoakServer.ListType.BlockedUsers, $"blk-{targetUserId:D6}", targetUserId, "Blocked");

        /// <summary>对应 DELETE api/Friendship/{friendId}：删除好友。</summary>
        public void DeleteFriend(long friendId)
            => _server.RemoveItem(SoakServer.ListType.Friends, $"f-{friendId:D6}");

        /// <summary>对应 DELETE api/Friendship/block/{blockedUserId}：解除拉黑。</summary>
        public void UnblockUser(long blockedUserId)
            => _server.RemoveItem(SoakServer.ListType.BlockedUsers, $"blk-{blockedUserId:D6}");

        /// <summary>HTTP 权威列表（GET api/Friendship/all、requests/incoming、blocked）。</summary>
        public Dictionary<SoakServer.ListType, SoakServer.Item[]> QueryAll() => _server.Snapshot();
    }

    // ---- 通用测试替身 ----

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
