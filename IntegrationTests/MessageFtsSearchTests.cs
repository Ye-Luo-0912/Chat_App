using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// FTS5 本地消息搜索测试：
/// - 迁移创建虚拟表与触发器（增删改自动同步索引）
/// - 全文匹配、中文短语、会话过滤、时间游标分页
/// - FTS 特殊字符转义（防注入）
/// - 账户隔离
/// </summary>
public class MessageFtsSearchTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 7501;
    private const long PeerId = 9501;
    private const string ConvA = "conv-fts-a";
    private const string ConvB = "conv-fts-b";

    public MessageFtsSearchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_fts_{Guid.NewGuid():N}.db");
        _factory = new DbContextFactoryStub(_dbPath);
        // FTS 虚拟表与触发器由迁移创建（EnsureCreated 不含）
        using var ctx = _factory.CreateDbContext();
        ctx.Database.GetService<IMigrator>().Migrate();
        _db = new DatabaseService(_factory);
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

    private LocalMessage NewMessage(string messageId, string content, string convId, long atMs, long owner = OwnerId) => new()
    {
        OwnerUserId = owner,
        MessageId = messageId,
        ConversationId = convId,
        SenderUserId = owner,
        ReceiverUserId = PeerId,
        Content = content,
        ReceivedAtMs = atMs,
        Status = MessageStatus.Delivered
    };

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

    [Fact]
    public async Task Search_Matches_Content_With_Account_Isolation()
    {
        await _db.UpsertMessageAsync(NewMessage("m-1", "分布式系统的最终一致性设计", ConvA, 1_000));
        await _db.UpsertMessageAsync(NewMessage("m-2", "消息队列的可靠投递", ConvA, 2_000));
        await _db.UpsertMessageAsync(NewMessage("m-3", "FTS5 全文搜索与索引同步", ConvB, 3_000));
        // 其他账户的同名消息：不得命中
        await _db.UpsertMessageAsync(NewMessage("m-x", "消息队列的可靠投递", ConvA, 4_000, owner: OwnerId + 1));

        var results = await _db.SearchMessagesAsync(OwnerId, "消息队列");
        Assert.Single(results);
        Assert.Equal("m-2", results[0].MessageId);

        var ftsResults = await _db.SearchMessagesAsync(OwnerId, "全文搜索");
        Assert.Single(ftsResults);
        Assert.Equal("m-3", ftsResults[0].MessageId);

        // 无匹配
        Assert.Empty(await _db.SearchMessagesAsync(OwnerId, "不存在的关键词"));
    }

    [Fact]
    public async Task Search_Filters_By_Conversation_And_Paginates_By_Time()
    {
        for (var i = 0; i < 10; i++)
        {
            await _db.UpsertMessageAsync(NewMessage($"a-{i}", $"会话A消息内容 {i}", ConvA, 1_000 + i * 100));
            await _db.UpsertMessageAsync(NewMessage($"b-{i}", $"会话B消息内容 {i}", ConvB, 1_000 + i * 100));
        }

        // 会话过滤：仅 ConvA
        var convResults = await _db.SearchMessagesAsync(OwnerId, "消息内容", ConvA, limit: 100);
        Assert.Equal(10, convResults.Count);
        Assert.All(convResults, m => Assert.Equal(ConvA, m.ConversationId));

        // 时间游标分页：倒序 + before 游标
        var page1 = await _db.SearchMessagesAsync(OwnerId, "消息内容", ConvA, limit: 4);
        Assert.Equal(4, page1.Count);
        Assert.Equal("a-9", page1[0].MessageId); // 最新在前
        var page2 = await _db.SearchMessagesAsync(OwnerId, "消息内容", ConvA, limit: 4,
            beforeReceivedAtMs: page1[^1].ReceivedAtMs);
        Assert.Equal(4, page2.Count);
        Assert.Equal("a-5", page2[0].MessageId);
        // 两页无重叠
        Assert.DoesNotContain(page1, m => page2.Any(p => p.Id == m.Id));
    }

    [Fact]
    public async Task Update_And_Delete_Sync_Fts_Index_Via_Triggers()
    {
        await _db.UpsertMessageAsync(NewMessage("m-1", "原始内容", ConvA, 1_000));
        Assert.Single(await _db.SearchMessagesAsync(OwnerId, "原始内容"));

        // 更新内容：索引同步（删除旧 + 插入新；EditVersion 严格递增才触发内容更新）
        var updated = NewMessage("m-1", "更新后的全新内容", ConvA, 1_000);
        updated.Id = (await _db.GetMessageByServerIdAsync(OwnerId, "m-1"))!.Id;
        updated.EditVersion = 2;
        await _db.UpsertMessageAsync(updated);
        Assert.Empty(await _db.SearchMessagesAsync(OwnerId, "原始内容"));
        Assert.Single(await _db.SearchMessagesAsync(OwnerId, "更新后的全新内容"));

        // 删除：索引同步（直接删除行，触发器清理 FTS）
        await using (var ctx = _factory.CreateDbContext())
        {
            var row = await ctx.Messages.FirstAsync(m => m.OwnerUserId == OwnerId && m.MessageId == "m-1");
            ctx.Messages.Remove(row);
            await ctx.SaveChangesAsync();
        }
        Assert.Empty(await _db.SearchMessagesAsync(OwnerId, "更新后的全新内容"));
    }

    [Fact]
    public async Task Fts_Special_Characters_Are_Escaped()
    {
        await _db.UpsertMessageAsync(NewMessage("m-1", "普通文本 123", ConvA, 1_000));

        // 特殊字符输入不得破坏查询（转义为空格）或抛异常
        var weird1 = await _db.SearchMessagesAsync(OwnerId, "\" OR 1=1 --");
        var weird2 = await _db.SearchMessagesAsync(OwnerId, "*:^-[()]{}~");
        var normal = await _db.SearchMessagesAsync(OwnerId, "普通文本 123");

        Assert.Empty(weird1);
        Assert.Empty(weird2);
        Assert.Single(normal);
        Assert.Equal("m-1", normal[0].MessageId);
    }

    [Fact]
    public async Task Partial_Term_Matches_With_Prefix_Semantics()
    {
        await _db.UpsertMessageAsync(NewMessage("m-1", "你好世界", ConvA, 1_000));

        // 前缀匹配：部分输入也能命中
        Assert.Single(await _db.SearchMessagesAsync(OwnerId, "你好"));
        Assert.Single(await _db.SearchMessagesAsync(OwnerId, "世界"));
        Assert.Empty(await _db.SearchMessagesAsync(OwnerId, "再见"));
    }
}
