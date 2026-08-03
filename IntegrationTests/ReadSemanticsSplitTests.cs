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
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 已读语义拆分测试（P0-7 整改）。
/// 验收场景：本地打开会话清未读（MarkConversationReadAsync）只发布 LocalUnreadClearedEvent，
/// 绝不发布对端已读事件，UI 不得借此伪造"对方已读"；
/// 对端已读只能由服务端序列水位推进（103 MessageReceipt / 105 MessageReceiptUpdated）
/// 发布 PeerReadWatermarkAdvancedEvent 驱动。
/// </summary>
public class ReadSemanticsSplitTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;
    private readonly InMemoryEventBus _eventBus = new();

    private const long OwnerId = 8101;
    private const long PeerId = 9101;
    private const string ConvId = "conv-8101-9101";

    private static readonly SessionStamp Session = new(OwnerId, 1, Guid.NewGuid());

    public ReadSemanticsSplitTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_read_{Guid.NewGuid():N}.db");
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

    /// <summary>本地清未读：仅发布 LocalUnreadClearedEvent，不得发布对端已读事件。</summary>
    [Fact]
    public async Task Local_MarkRead_Publishes_Only_LocalUnreadCleared()
    {
        var store = NewStore(_dbPath, _eventBus);
        var cleared = new List<LocalUnreadClearedEvent>();
        var advanced = new List<PeerReadWatermarkAdvancedEvent>();
        _eventBus.Subscribe<LocalUnreadClearedEvent>(cleared.Add);
        _eventBus.Subscribe<PeerReadWatermarkAdvancedEvent>(advanced.Add);

        await store.MarkConversationReadAsync(Session, ConvId, "msg-1");

        var evt = Assert.Single(cleared);
        Assert.Equal(ConvId, evt.ConversationId);
        Assert.Empty(advanced);
    }

    /// <summary>对端已读回执（103）：仅发布 PeerReadWatermarkAdvancedEvent，携带水位与 LastReadMessageId。</summary>
    [Fact]
    public async Task Peer_Receipt103_Publishes_Watermark_Advanced()
    {
        var store = NewStore(_dbPath, _eventBus);
        var cleared = new List<LocalUnreadClearedEvent>();
        var advanced = new List<PeerReadWatermarkAdvancedEvent>();
        _eventBus.Subscribe<LocalUnreadClearedEvent>(cleared.Add);
        _eventBus.Subscribe<PeerReadWatermarkAdvancedEvent>(advanced.Add);

        await store.HandleReceiptAsync(Session, new MessageReceiptDto
        {
            ConversationId = ConvId,
            LastReadMessageId = "msg-2",
            LastReadAtMs = 1234567890,
            ReaderUserId = PeerId,
            ReceiverUserId = OwnerId
        });

        var evt = Assert.Single(advanced);
        Assert.Equal(ConvId, evt.ConversationId);
        Assert.Equal("msg-2", evt.LastReadMessageId);
        Assert.Equal(1234567890, evt.ReadAtMs);
        Assert.Empty(cleared);
    }

    /// <summary>对端批量已读水位（105）：仅发布 PeerReadWatermarkAdvancedEvent。</summary>
    [Fact]
    public async Task Peer_ReceiptUpdated105_Publishes_Watermark_Advanced()
    {
        var store = NewStore(_dbPath, _eventBus);
        var cleared = new List<LocalUnreadClearedEvent>();
        var advanced = new List<PeerReadWatermarkAdvancedEvent>();
        _eventBus.Subscribe<LocalUnreadClearedEvent>(cleared.Add);
        _eventBus.Subscribe<PeerReadWatermarkAdvancedEvent>(advanced.Add);

        await store.HandleReceiptUpdatedAsync(Session, new MessageReceiptUpdatedDto
        {
            ConversationId = ConvId,
            LastReadMessageId = "msg-3",
            LastReadAtMs = 1234567891
        });

        var evt = Assert.Single(advanced);
        Assert.Equal("msg-3", evt.LastReadMessageId);
        Assert.Equal(1234567891, evt.ReadAtMs);
        Assert.Empty(cleared);
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

    private static MessageStore NewStore(string dbPath, IEventBus eventBus)
        => new(new DatabaseService(new DbContextFactoryStub(dbPath)), eventBus, null!);
}
