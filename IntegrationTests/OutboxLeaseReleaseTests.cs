using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Outbox 租约生命周期测试（P1-5）：
/// 处理器停止时主动释放未开始条目的租约（Sending → Queued，AttemptId 匹配），
/// 无需等待 2 分钟租约过期即可重新发送；非本批次 AttemptId 的条目不受影响。
/// </summary>
public class OutboxLeaseReleaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 7401;
    private const long PeerId = 9401;
    private const string ConvId = "conv-7401-9401";

    public OutboxLeaseReleaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_lease_{Guid.NewGuid():N}.db");
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

    private LocalOutboxMessage NewOutbox(string clientId) => new()
    {
        OwnerUserId = OwnerId,
        ClientMessageId = clientId,
        ConversationId = ConvId,
        TargetUserId = PeerId,
        Content = "租约测试",
        Status = OutboxStatus.Queued,
        QueuedAt = DateTime.UtcNow
    };

    /// <summary>停止释放：未开始条目的租约立即归还（Queued + 清 AttemptId），可立即重新认领。</summary>
    [Fact]
    public async Task Released_Leases_Return_To_Queued_And_Reclaimable()
    {
        // 入队 3 条并认领（Sending + 租约）
        var ids = new[] { "c-1", "c-2", "c-3" };
        foreach (var id in ids)
            await _db.EnqueueOutboxAsync(NewOutbox(id));

        var now = DateTime.UtcNow;
        var claimed = await _db.ClaimPendingOutboxAsync(OwnerId, 8, now, now.AddMinutes(2), 10);
        Assert.Equal(3, claimed.Count);
        Assert.All(claimed, c => Assert.Equal(OutboxStatus.Sending, c.Status));
        var attemptId = claimed[0].AttemptId;
        Assert.False(string.IsNullOrWhiteSpace(attemptId));

        // 模拟停止：释放第 2、3 条（第 1 条已在发送中不释放）
        var released = await _db.ReleaseOutboxLeasesAsync(OwnerId, new[] { "c-2", "c-3" }, attemptId!);
        Assert.Equal(2, released);

        var c2 = await _db.GetOutboxByClientIdAsync(OwnerId, "c-2");
        var c3 = await _db.GetOutboxByClientIdAsync(OwnerId, "c-3");
        Assert.Equal(OutboxStatus.Queued, c2!.Status);
        Assert.Null(c2.AttemptId);
        Assert.Equal(OutboxStatus.Queued, c3!.Status);
        Assert.Null(c3.AttemptId);
        // 第 1 条保持 Sending（不误释放）
        var c1 = await _db.GetOutboxByClientIdAsync(OwnerId, "c-1");
        Assert.Equal(OutboxStatus.Sending, c1!.Status);

        // 释放后立即可重新认领
        var reClaimed = await _db.ClaimPendingOutboxAsync(OwnerId, 8, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), 10);
        Assert.Equal(2, reClaimed.Count);
    }

    /// <summary>跨批次保护：AttemptId 不匹配的批次不被释放（防止误释放他人/旧批次租约）。</summary>
    [Fact]
    public async Task Release_Only_Touches_Matching_AttemptId()
    {
        await _db.EnqueueOutboxAsync(NewOutbox("c-a"));
        var now = DateTime.UtcNow;
        var claimedA = await _db.ClaimPendingOutboxAsync(OwnerId, 8, now, now.AddMinutes(2), 10);
        var attemptA = claimedA[0].AttemptId;

        // 新条目入队后被第二批次认领（独立 attemptId）
        await _db.EnqueueOutboxAsync(NewOutbox("c-b"));
        var claimedB = await _db.ClaimPendingOutboxAsync(OwnerId, 8, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), 10);
        var attemptB = claimedB[0].AttemptId;
        Assert.NotEqual(attemptA, attemptB);

        // 用批次 A 的 attemptId 释放批次 B 的条目：不应生效
        var released = await _db.ReleaseOutboxLeasesAsync(OwnerId, new[] { "c-b" }, attemptA!);
        Assert.Equal(0, released);
        var cB = await _db.GetOutboxByClientIdAsync(OwnerId, "c-b");
        Assert.Equal(OutboxStatus.Sending, cB!.Status);

        // 用批次 B 自己的 attemptId 释放：生效
        var released2 = await _db.ReleaseOutboxLeasesAsync(OwnerId, new[] { "c-b" }, attemptB!);
        Assert.Equal(1, released2);
        cB = await _db.GetOutboxByClientIdAsync(OwnerId, "c-b");
        Assert.Equal(OutboxStatus.Queued, cB!.Status);
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
}
