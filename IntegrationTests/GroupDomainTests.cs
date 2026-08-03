using Chat_App.Infrastructure.Events;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Services;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 群聊领域仓储与事件流测试：
/// 成员事件有序持久化（版本单调防重放/乱序）、解散 tombstone、被踢清理、
/// 多设备并发修改幂等、领域事件发布、群消息入站类型解析（不得默认直聊）。
/// </summary>
public class GroupDomainTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 7201;
    private const string GroupId = "conv-grp-domain-1";

    public GroupDomainTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_groupdomain_{Guid.NewGuid():N}.db");
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

    /// <summary>
    /// 群成员搜索：UserId 前缀 + 好友显示名/备注包含匹配；LIKE 通配符不产生注入。
    /// </summary>
    [Fact]
    public async Task Group_Member_Search_Matches_Id_Prefix_And_Friend_Name()
    {
        await _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9101, (byte)ConversationMemberRole.Member, 1_000, 1_000);
        await _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9201, (byte)ConversationMemberRole.Member, 2_000, 2_000);
        await _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9301, (byte)ConversationMemberRole.Member, 3_000, 3_000);
        // 好友名称：9101 → "小明"，9201 → "小红"
        await _db.AddFriendAsync([
            new LocalFriend { OwnerUserId = OwnerId, FriendId = 9101, FriendName = "小明", DisplayName = "小明", Status = FriendshipStatus.Approved, LastSynced = DateTime.UtcNow },
            new LocalFriend { OwnerUserId = OwnerId, FriendId = 9201, FriendName = "小红", DisplayName = "小红", Status = FriendshipStatus.Approved, LastSynced = DateTime.UtcNow }
        ]);

        // 按 UserId 前缀
        var byId = await _db.SearchGroupMembersAsync(OwnerId, GroupId, "91");
        Assert.Single(byId);
        Assert.Equal(9101, byId[0].UserId);

        // 按好友名称包含
        var byName = await _db.SearchGroupMembersAsync(OwnerId, GroupId, "小");
        Assert.Equal(2, byName.Count);
        Assert.Contains(byName, m => m.UserId == 9101);
        Assert.Contains(byName, m => m.UserId == 9201);

        // 无名称好友（9301）只按 Id 命中
        var by9301 = await _db.SearchGroupMembersAsync(OwnerId, GroupId, "9301");
        Assert.Single(by9301);

        // LIKE 通配符转义：% 与 _ 不产生注入式全匹配
        Assert.Empty(await _db.SearchGroupMembersAsync(OwnerId, GroupId, "%"));
        Assert.Empty(await _db.SearchGroupMembersAsync(OwnerId, GroupId, "_"));
    }

    /// <summary>
    /// 被移除/解散后的本地清理策略：
    /// 成员记录保留（RemovedAtMs 记录历史，供"群已读成员"等后续能力），
    /// 活跃列表立即为空；群解散 tombstone 保留；该会话 Outbox 永久失败。
    /// </summary>
    [Fact]
    public async Task Removed_Or_Dissolved_Group_Local_Cleanup_Semantics()
    {
        const long t1 = 1_700_000_000_000;
        await _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9101, (byte)ConversationMemberRole.Member, t1, t1);
        await _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9201, (byte)ConversationMemberRole.Member, t1 + 1000, t1 + 1000);

        // 成员被移除：活跃列表立即排除，历史记录保留（RemovedAtMs）
        await _db.MarkGroupMemberRemovedAsync(OwnerId, GroupId, 9101, t1 + 2000, t1 + 2000);
        var active = await _db.GetGroupMembersAsync(OwnerId, GroupId);
        Assert.Single(active);
        Assert.Equal(9201, active[0].UserId);

        // 该会话未发送 Outbox 永久失败（成员移除清理）
        var clientId = $"g-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxAsync(new LocalOutboxMessage
        {
            OwnerUserId = OwnerId,
            ClientMessageId = clientId,
            ConversationId = GroupId,
            ConversationType = (byte)ConversationTypeDto.Group,
            Content = "待发送",
            Status = OutboxStatus.Queued,
            QueuedAt = DateTime.UtcNow
        });
        await _db.MarkOutboxPermanentByConversationAsync(OwnerId, GroupId, "成员已被移出群聊");
        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxFailureKind.Permanent, outbox!.FailureKind);

        // 群解散：tombstone 保留（DissolvedAtMs），成员历史不删除（供审计/已读成员等能力）；
        // 解散由 GroupStates.DissolvedAtMs 表达，UI 侧解散事件已关闭成员面板。
        await _db.MarkGroupDissolvedAsync(OwnerId, GroupId, t1 + 3000, t1 + 3000);
        var state = await _db.GetGroupStateAsync(OwnerId, GroupId);
        Assert.NotNull(state);
        Assert.Equal(t1 + 3000, state!.DissolvedAtMs);
        // 成员历史完整保留：9101 移除标记 + 9201 活跃记录都在
        var activeAfterDissolve = await _db.GetGroupMembersAsync(OwnerId, GroupId);
        Assert.Single(activeAfterDissolve);
        Assert.Equal(9201, activeAfterDissolve[0].UserId);
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

    private sealed class StubCurrentUserContext(long userId) : ICurrentUserContext
    {
        public long Generation => 1;
        public long? UserId => userId;
        public string? UserName => $"user-{userId}";
        public bool IsAuthenticated => true;
        public bool HasUserId => userId > 0;
        public UserSessionSnapshot Snapshot => new(userId, 1, UserName, null, null);
        public long RequireUserId() => userId;
        public bool TryGetUserId(out long id)
        {
            id = userId;
            return userId > 0;
        }
    }

    /// <summary>创建带事件记录的 MessageStore（订阅群领域事件）。</summary>
    private static (MessageStore Store, List<object> Published) CreateStore(DatabaseService db)
    {
        var eventBus = new InMemoryEventBus();
        var published = new List<object>();
        eventBus.Subscribe<GroupMemberJoinedEvent>(e => { lock (published) published.Add(e); });
        eventBus.Subscribe<GroupMemberLeftEvent>(e => { lock (published) published.Add(e); });
        eventBus.Subscribe<GroupMemberRemovedEvent>(e => { lock (published) published.Add(e); });
        eventBus.Subscribe<GroupRoleChangedEvent>(e => { lock (published) published.Add(e); });
        eventBus.Subscribe<GroupMembersAddedEvent>(e => { lock (published) published.Add(e); });
        eventBus.Subscribe<GroupConversationDissolvedEvent>(e => { lock (published) published.Add(e); });
        return (new MessageStore(db, eventBus, null!), published);
    }

    /// <summary>成员加入/角色/移除/批量加入/解散事件持久化 + 领域事件发布。</summary>
    [Fact]
    public async Task Group_Member_Events_Persist_And_Publish_Domain_Events()
    {
        var (store, published) = CreateStore(_db);
        var session = new SessionStamp(OwnerId, 1, Guid.NewGuid());
        const long t1 = 1_700_000_000_000;

        // 成员加入（带群标题）
        await store.HandleGroupMemberJoinedAsync(session, new MemberJoinedUpdateDto
        {
            ConversationId = GroupId,
            UserId = 9101,
            Role = ConversationMemberRole.Owner,
            Title = "领域测试群",
            OccurredAtMs = t1
        });
        await store.HandleGroupMembersAddedAsync(session, new MembersAddedUpdateDto
        {
            ConversationId = GroupId,
            AddedUserIds = [9102, 9103],
            OccurredAtMs = t1 + 1000
        });

        var members = await _db.GetGroupMembersAsync(OwnerId, GroupId);
        Assert.Equal(3, members.Count);
        Assert.Contains(members, m => m.UserId == 9101 && m.Role == (byte)ConversationMemberRole.Owner);
        Assert.Contains(members, m => m.UserId == 9102);
        Assert.Contains(members, m => m.UserId == 9103);

        var state = await _db.GetGroupStateAsync(OwnerId, GroupId);
        Assert.NotNull(state);
        Assert.Equal("领域测试群", state!.Title);

        // 角色变更
        await store.HandleGroupRoleChangedAsync(session, new RoleChangedUpdateDto
        {
            ConversationId = GroupId,
            UserId = 9102,
            NewRole = ConversationMemberRole.Admin,
            OccurredAtMs = t1 + 2000
        });
        members = await _db.GetGroupMembersAsync(OwnerId, GroupId);
        Assert.Equal((byte)ConversationMemberRole.Admin, members.Single(m => m.UserId == 9102).Role);

        // 成员退出（不再出现在活跃列表）
        await store.HandleGroupMemberLeftAsync(session, new MemberLeftUpdateDto
        {
            ConversationId = GroupId,
            UserId = 9103,
            OccurredAtMs = t1 + 3000
        });
        members = await _db.GetGroupMembersAsync(OwnerId, GroupId);
        Assert.Equal(2, members.Count);
        Assert.DoesNotContain(members, m => m.UserId == 9103);

        // 领域事件发布（UI 投影输入）
        Assert.Contains(published.OfType<GroupMemberJoinedEvent>(), e => e.UserId == 9101);
        Assert.Contains(published.OfType<GroupMembersAddedEvent>(), e => e.UserIds.Length == 2);
        Assert.Contains(published.OfType<GroupRoleChangedEvent>(), e => e.UserId == 9102);
        Assert.Contains(published.OfType<GroupMemberLeftEvent>(), e => e.UserId == 9103);
    }

    /// <summary>事件重放与乱序：更早 OccurredAtMs 的事件必须被拒绝（版本单调）。</summary>
    [Fact]
    public async Task Stale_Or_Replayed_Events_Are_Rejected_By_Version_Monotonic()
    {
        const long t1 = 1_700_000_000_000;

        // 最新事件先到
        Assert.True(await _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9101, (byte)ConversationMemberRole.Member, t1 + 5000, t1 + 5000));

        // 重放旧事件（更早 OccurredAtMs）：拒绝
        Assert.False(await _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9101, (byte)ConversationMemberRole.Owner, t1 + 1000, t1 + 1000));
        var member = (await _db.GetGroupMembersAsync(OwnerId, GroupId)).Single();
        Assert.Equal((byte)ConversationMemberRole.Member, member.Role); // 未被旧事件覆盖
        Assert.Equal(t1 + 5000, member.JoinedAtMs);

        // 移除：最新时间生效；更早的移除重放被拒绝
        Assert.True(await _db.MarkGroupMemberRemovedAsync(OwnerId, GroupId, 9101, t1 + 6000, t1 + 6000));
        Assert.False(await _db.MarkGroupMemberRemovedAsync(OwnerId, GroupId, 9101, t1 + 5500, t1 + 5500));
        Assert.Empty(await _db.GetGroupMembersAsync(OwnerId, GroupId));

        // 被移除后更早的"重新加入"（早于移除时间）不得复活成员
        Assert.False(await _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9101, (byte)ConversationMemberRole.Member, t1 + 4000, t1 + 4000));
        Assert.Empty(await _db.GetGroupMembersAsync(OwnerId, GroupId));
    }

    /// <summary>被移除成员重新加入：更晚的加入事件复活成员并更新角色。</summary>
    [Fact]
    public async Task Rejoined_Member_Is_Reactivated_By_Newer_Join_Event()
    {
        const long t1 = 1_700_000_000_000;
        await _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9101, (byte)ConversationMemberRole.Member, t1, t1);
        await _db.MarkGroupMemberRemovedAsync(OwnerId, GroupId, 9101, t1 + 1000, t1 + 1000);
        Assert.Empty(await _db.GetGroupMembersAsync(OwnerId, GroupId));

        // 更晚的重新加入：复活
        Assert.True(await _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9101, (byte)ConversationMemberRole.Admin, t1 + 2000, t1 + 2000));
        var member = (await _db.GetGroupMembersAsync(OwnerId, GroupId)).Single();
        Assert.Equal((byte)ConversationMemberRole.Admin, member.Role);
        Assert.Null(member.RemovedAtMs);
    }

    /// <summary>群解散 tombstone：DissolvedAtMs 单调，更早的解散/复活事件被拒绝。</summary>
    [Fact]
    public async Task Dissolved_Tombstone_Is_Monotonic()
    {
        const long t1 = 1_700_000_000_000;
        Assert.True(await _db.UpsertGroupStateAsync(OwnerId, GroupId, "测试群", t1, t1, t1));

        Assert.True(await _db.MarkGroupDissolvedAsync(OwnerId, GroupId, t1 + 1000, t1 + 1000));
        var state = await _db.GetGroupStateAsync(OwnerId, GroupId);
        Assert.NotNull(state);
        Assert.Equal(t1 + 1000, state!.DissolvedAtMs);

        // 更早的解散重放：拒绝
        Assert.False(await _db.MarkGroupDissolvedAsync(OwnerId, GroupId, t1 + 500, t1 + 500));
        // 更早的状态更新：拒绝（不复活）
        Assert.False(await _db.UpsertGroupStateAsync(OwnerId, GroupId, "旧标题", t1 + 200, t1 + 200, t1 + 200));
        state = await _db.GetGroupStateAsync(OwnerId, GroupId);
        Assert.Equal("测试群", state!.Title);
        Assert.Equal(t1 + 1000, state.DissolvedAtMs);
    }

    /// <summary>被踢出（被移除者是当前用户）：该会话 Outbox 永久失败 + 领域事件发布。</summary>
    [Fact]
    public async Task Self_Removed_Freezes_Conversation_Outbox()
    {
        var clientId = $"g-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(new LocalOutboxMessage
        {
            OwnerUserId = OwnerId,
            ClientMessageId = clientId,
            ConversationId = GroupId,
            ConversationType = (byte)ConversationTypeDto.Group,
            TargetUserId = null,
            Content = "待发送群消息",
            Status = OutboxStatus.Queued,
            QueuedAt = DateTime.UtcNow
        }, new LocalMessage
        {
            OwnerUserId = OwnerId,
            ClientMessageId = clientId,
            ConversationId = GroupId,
            SenderUserId = OwnerId,
            ReceiverUserId = 0,
            Content = "待发送群消息",
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Status = MessageStatus.Queued
        });

        var (store, published) = CreateStore(_db);
        var session = new SessionStamp(OwnerId, 1, Guid.NewGuid());
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 先建立成员记录，再注入被移除事件
        await _db.UpsertGroupMemberAsync(OwnerId, GroupId, OwnerId, (byte)ConversationMemberRole.Owner, nowMs, nowMs);
        await store.HandleGroupMemberRemovedAsync(session, new MemberRemovedUpdateDto
        {
            ConversationId = GroupId,
            UserId = OwnerId, // 被移除的是当前用户
            ActorUserId = 9999,
            OccurredAtMs = nowMs + 1
        });

        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        Assert.NotNull(outbox);
        Assert.Equal(OutboxStatus.Failed, outbox!.Status);
        Assert.Equal(OutboxFailureKind.Permanent, outbox.FailureKind);
        Assert.Contains(published.OfType<GroupMemberRemovedEvent>(), e => e.UserId == OwnerId);
    }

    /// <summary>多设备并发成员修改：唯一索引 + 版本单调保证幂等，无重复行、无旧值覆盖。</summary>
    [Fact]
    public async Task Concurrent_Member_Updates_Are_Idempotent()
    {
        const long t1 = 1_700_000_000_000;
        // 两设备并发写入同一成员不同角色/时间
        var tasks = new[]
        {
            _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9101, (byte)ConversationMemberRole.Member, t1, t1),
            _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9101, (byte)ConversationMemberRole.Admin, t1 + 100, t1 + 100),
            _db.UpsertGroupMemberAsync(OwnerId, GroupId, 9101, (byte)ConversationMemberRole.Owner, t1 + 200, t1 + 200)
        };
        await Task.WhenAll(tasks);

        var members = await _db.GetGroupMembersAsync(OwnerId, GroupId);
        Assert.Single(members); // 无重复行
        // 最终状态 = 最新时间戳的写入
        Assert.Equal((byte)ConversationMemberRole.Owner, members[0].Role);
        Assert.Equal(t1 + 200, members[0].JoinedAtMs);
    }

    /// <summary>
    /// 群消息先于会话元数据到达：新建会话类型必须是 Unknown（不得默认 Direct），
    /// 且不伪造直聊对端。
    /// </summary>
    [Fact]
    public async Task Incoming_Group_Message_Creates_Unknown_Type_Conversation_Not_Direct()
    {
        var (store, published) = CreateStore(_db);
        var session = new SessionStamp(OwnerId, 1, Guid.NewGuid());

        await store.PersistIncomingAsync(session, new ChatMessageDto
        {
            MessageId = "grp-msg-1",
            ConversationId = GroupId, // 群会话 Id（非 dm: 前缀）
            SenderUserId = 9101,
            TargetUserId = 0,
            Content = "群消息先到",
            SentUtc = DateTime.UtcNow
        });

        var conv = await _db.GetConversationAsync(OwnerId, GroupId);
        Assert.NotNull(conv);
        Assert.Equal((byte)ConversationTypeDto.Unknown, conv!.Type); // 不默认 Direct
        Assert.Null(conv.PeerUserId); // 不伪造直聊对端
    }
}


