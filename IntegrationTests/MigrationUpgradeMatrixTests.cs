using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 迁移升级矩阵测试：13 个历史迁移，任意中间版本都必须能升级到最新；
/// 最早版本写入的数据在完整升级后必须原样保留（不丢列、不改值）。
/// </summary>
public class MigrationUpgradeMatrixTests
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "chat_migration_matrix_tests");

    private static string NewTempDbPath()
    {
        Directory.CreateDirectory(TempRoot);
        return Path.Combine(TempRoot, $"{Guid.NewGuid():N}.db");
    }

    private static IDbContextFactory<ClientDbContext> CreateFactory(string dbPath)
        => new TestContextFactory(dbPath);

    private static List<string> GetAllMigrationIds()
    {
        var factory = CreateFactory(NewTempDbPath());
        using var ctx = factory.CreateDbContext();
        return ctx.Database.GetMigrations().ToList();
    }

    private sealed class TestContextFactory(string dbPath) : IDbContextFactory<ClientDbContext>
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

    /// <summary>
    /// 矩阵核心：以每个历史迁移为起点（迁移到该版本），随后必须能完整升级到最新，
    /// 且升级后 __EFMigrationsHistory 与迁移清单一致。
    /// </summary>
    [Fact]
    public void Every_Intermediate_Version_Upgrades_To_Latest()
    {
        var migrations = GetAllMigrationIds();
        Assert.True(migrations.Count >= 10, $"迁移数量异常: {migrations.Count}");
        Assert.Equal(migrations.Count, migrations.Distinct().Count());

        for (var i = 0; i < migrations.Count; i++)
        {
            var dbPath = NewTempDbPath();
            try
            {
                var factory = CreateFactory(dbPath);
                using var ctx = factory.CreateDbContext();
                var migrator = ctx.Database.GetService<IMigrator>();

                // 迁移到第 i 个中间版本：历史表应恰好有 i+1 行
                migrator.Migrate(migrations[i]);
                var applied = ctx.Database.GetAppliedMigrations().ToList();
                Assert.Equal(i + 1, applied.Count);
                Assert.Equal(migrations[i], applied[^1]);

                // 升级到最新：全部迁移应用完毕
                try
                {
                    migrator.Migrate();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"从中间版本 [{i}] {migrations[i]} 升级到最新失败（dbPath={dbPath}）", ex);
                }
                Assert.Equal(migrations.Count, ctx.Database.GetAppliedMigrations().Count());
            }
            finally
            {
                TryDelete(dbPath);
            }
        }
    }

    /// <summary>
    /// 数据保留：在最早版本（初始 schema）写入 Users 行，完整升级后必须原样存在
    /// （迁移不得静默丢数据，列值必须保持）。
    /// </summary>
    [Fact]
    public async Task Data_Written_At_Oldest_Version_Survives_Full_Upgrade()
    {
        var migrations = GetAllMigrationIds();
        var dbPath = NewTempDbPath();
        var lastLogin = new DateTime(2025, 3, 1, 10, 30, 0, DateTimeKind.Utc);

        try
        {
            var factory = CreateFactory(dbPath);
            {
                using var ctx = factory.CreateDbContext();
                var migrator = ctx.Database.GetService<IMigrator>();
                migrator.Migrate(migrations[0]);

                // 以最早版本 schema 写入（只有初始列）
                ctx.Database.ExecuteSqlRaw(
                    "INSERT INTO \"Users\" (\"UserId\", \"Username\", \"AvatarUrl\", \"LastLoginTime\") " +
                    "VALUES (4242, 'alice', NULL, {0})", lastLogin);

                // 完整升级到最新
                migrator.Migrate();
            }

            // 当前模型读取：行保留且值不变
            var factory2 = CreateFactory(dbPath);
            {
                using var ctx = factory2.CreateDbContext();
                var user = await ctx.Users.FindAsync(4242L);
                Assert.NotNull(user);
                Assert.Equal("alice", user!.Username);
                Assert.Equal(lastLogin, user.LastLoginTime);

                // 最新 schema 端到端可用：会话/消息/Outbox 写入与查询
                ctx.Conversations.Add(new LocalConversation
                {
                    OwnerUserId = 4242,
                    ConversationId = "conv-upgraded-1",
                    Type = 1,
                    PeerUserId = 9999,
                    LastMessagePreview = "upgraded",
                    LastMessageAtMs = 1_000L
                });
                ctx.Messages.Add(new LocalMessage
                {
                    OwnerUserId = 4242,
                    MessageId = "msg-upgraded-1",
                    ClientMessageId = "cmsg-upgraded-1",
                    ConversationId = "conv-upgraded-1",
                    SenderUserId = 9999,
                    ReceiverUserId = 4242,
                    Content = "post-upgrade",
                    ReceivedAtMs = 1_000L,
                    Status = MessageStatus.Delivered
                });
                ctx.OutboxMessages.Add(new LocalOutboxMessage
                {
                    OwnerUserId = 4242,
                    ClientMessageId = "out-upgraded-1",
                    ConversationId = "conv-upgraded-1",
                    TargetUserId = 9999,
                    Content = "queued-after-upgrade",
                    Status = OutboxStatus.Queued,
                    QueuedAt = DateTime.UtcNow
                });
                await ctx.SaveChangesAsync();

                Assert.Equal(1, await ctx.Conversations.CountAsync(c => c.OwnerUserId == 4242));
                Assert.Equal(1, await ctx.Messages.CountAsync(m => m.OwnerUserId == 4242));
                Assert.Equal(1, await ctx.OutboxMessages.CountAsync(o => o.OwnerUserId == 4242));
            }
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    private static void TryDelete(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); }
            catch { /* 忽略清理失败 */ }
        }
    }
}
