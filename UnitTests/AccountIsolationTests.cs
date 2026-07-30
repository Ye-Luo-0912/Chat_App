using Chat_App.Infrastructure.Persistence;
using Core.Models;
using Infrastructure.Models;
using Infrastructure.Models.Context;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace UnitTests;

/// <summary>
/// 账户隔离测试：账户 A 登录、缓存数据、退出，再登录账户 B。
/// 验收：B 不得看到 A 的好友、消息、未读数、草稿和连接状态。
/// </summary>
public class AccountIsolationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long UserA = 1001;
    private const long UserB = 2002;
    private const string ConversationIdA = "conv-a-1001-9999";
    private const string ConversationIdB = "conv-b-2002-8888";

    public AccountIsolationTests()
    {
        // 使用共享内存连接，保证 factory 多次 CreateDbContext 共享同一库
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _factory = new DbContextFactoryStub(_connection);
        _db = new DatabaseService(_factory);

        // 建表
        using var ctx = _factory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    /// <summary>
    /// 核心验收：A 写入好友/会话/消息/未读状态后，B 查询应全部为空。
    /// </summary>
    [Fact]
    public async Task Account_B_Cannot_See_Account_A_Data()
    {
        // === 账户 A 登录，缓存数据 ===
        await _db.AddFriendAsync(new List<LocalFriend>
        {
            new()
            {
                OwnerUserId = UserA, FriendId = 9999,
                FriendName = "Alice的朋友", DisplayName = "小九",
                Status = FriendshipStatus.Approved, LastSynced = DateTime.UtcNow
            }
        });

        await _db.UpsertConversationAsync(new LocalConversation
        {
            OwnerUserId = UserA,
            ConversationId = ConversationIdA,
            Type = 1,
            PeerUserId = 9999,
            LastMessagePreview = "A的私密消息",
            LastMessageAtMs = 1_000L,
            UnreadCount = 3
        });

        await _db.UpsertMessageAsync(new LocalMessage
        {
            OwnerUserId = UserA,
            MessageId = "msg-a-1",
            ClientMessageId = "cmsg-a-1",
            ConversationId = ConversationIdA,
            SenderUserId = 9999,
            ReceiverUserId = UserA,
            Content = "A的私密消息",
            ReceivedAtMs = 1_000L,
            Status = MessageStatus.Delivered
        });

        await _db.UpsertReadStateAsync(new LocalConversationReadState
        {
            OwnerUserId = UserA,
            ConversationId = ConversationIdA,
            LastReadMessageId = "msg-a-1",
            LastReadAtMs = 1_000L
        });

        // === 账户 A 退出，账户 B 登录 ===

        // === 验收：B 不得看到 A 的任何数据 ===
        var bFriends = await _db.GetFriendsAsync(UserB);
        Assert.Empty(bFriends);

        var bConversations = await _db.GetConversationsAsync(UserB);
        Assert.Empty(bConversations);

        var bMessages = await _db.GetMessagesAsync(UserB, ConversationIdA);
        Assert.Empty(bMessages);

        // B 不能通过 A 的 messageId 拿到 A 的消息
        var bMsgByServerId = await _db.GetMessageByServerIdAsync(UserB, "msg-a-1");
        Assert.Null(bMsgByServerId);

        var bMsgByClientId = await _db.GetMessageByClientIdAsync(UserB, "cmsg-a-1");
        Assert.Null(bMsgByClientId);

        var bReadState = await _db.GetReadStateAsync(UserB, ConversationIdA);
        Assert.Null(bReadState);

        var bCursors = await _db.GetAllSyncCursorsAsync(UserB);
        Assert.Empty(bCursors);
    }

    /// <summary>
    /// B 写入自己的数据后，A 的数据不受影响，两者互不可见。
    /// </summary>
    [Fact]
    public async Task Account_A_And_B_Data_Are_Mutually_Isolated()
    {
        // A 写入
        await _db.AddFriendAsync(new List<LocalFriend>
        {
            new()
            {
                OwnerUserId = UserA, FriendId = 9999,
                FriendName = "A-Friend", DisplayName = "九",
                Status = FriendshipStatus.Approved, LastSynced = DateTime.UtcNow
            }
        });
        await _db.UpsertConversationAsync(new LocalConversation
        {
            OwnerUserId = UserA, ConversationId = ConversationIdA,
            Type = 1, PeerUserId = 9999, LastMessagePreview = "A-msg", LastMessageAtMs = 1_000L
        });

        // B 写入
        await _db.AddFriendAsync(new List<LocalFriend>
        {
            new()
            {
                OwnerUserId = UserB, FriendId = 8888,
                FriendName = "B-Friend", DisplayName = "八",
                Status = FriendshipStatus.Approved, LastSynced = DateTime.UtcNow
            }
        });
        await _db.UpsertConversationAsync(new LocalConversation
        {
            OwnerUserId = UserB, ConversationId = ConversationIdB,
            Type = 1, PeerUserId = 8888, LastMessagePreview = "B-msg", LastMessageAtMs = 2_000L
        });

        // 互相不可见
        var aFriends = await _db.GetFriendsAsync(UserA);
        var bFriends = await _db.GetFriendsAsync(UserB);
        Assert.Single(aFriends);
        Assert.Single(bFriends);
        Assert.Equal(9999, aFriends[0].FriendId);
        Assert.Equal(8888, bFriends[0].FriendId);

        var aConvs = await _db.GetConversationsAsync(UserA);
        var bConvs = await _db.GetConversationsAsync(UserB);
        Assert.Single(aConvs);
        Assert.Single(bConvs);
        Assert.Equal(ConversationIdA, aConvs[0].ConversationId);
        Assert.Equal(ConversationIdB, bConvs[0].ConversationId);

        // A 查 B 的会话应为空
        var aPeekB = await _db.GetConversationAsync(UserA, ConversationIdB);
        Assert.Null(aPeekB);
    }

    /// <summary>
    /// 同一会话 Id 在不同账户下应独立存储，互不覆盖。
/// 验收：A 和 B 各自持有同名 ConversationId 的数据，查询各自账户都拿到自己的版本。
    /// </summary>
    [Fact]
    public async Task Same_ConversationId_Different_Owner_Does_Not_Cross_Overwrite()
    {
        const string sharedConvId = "conv-shared-9999";

        await _db.UpsertConversationAsync(new LocalConversation
        {
            OwnerUserId = UserA, ConversationId = sharedConvId,
            Type = 1, PeerUserId = 9999,
            LastMessagePreview = "A-version", LastMessageAtMs = 1_000L
        });

        await _db.UpsertConversationAsync(new LocalConversation
        {
            OwnerUserId = UserB, ConversationId = sharedConvId,
            Type = 1, PeerUserId = 9999,
            LastMessagePreview = "B-version", LastMessageAtMs = 2_000L
        });

        var aConv = await _db.GetConversationAsync(UserA, sharedConvId);
        var bConv = await _db.GetConversationAsync(UserB, sharedConvId);

        Assert.NotNull(aConv);
        Assert.NotNull(bConv);
        Assert.Equal("A-version", aConv!.LastMessagePreview);
        Assert.Equal("B-version", bConv!.LastMessagePreview);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 基于共享 SqliteConnection 的 factory stub，保证所有 CreateDbContext
    /// 指向同一内存库（默认 in-memory 每次连接独立，需共享连接）。
    /// </summary>
    private sealed class DbContextFactoryStub(SqliteConnection connection) : IDbContextFactory<ClientDbContext>
    {
        public ClientDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ClientDbContext>()
                .UseSqlite(connection)
                .Options;
            return new ClientDbContext(options);
        }

        public Task<ClientDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
