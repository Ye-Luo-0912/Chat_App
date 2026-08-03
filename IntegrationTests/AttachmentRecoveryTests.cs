using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 附件恢复查询测试。
/// 验收场景：可恢复附件 = Uploading（上传中）+ Failed（可重试失败）；
/// Available（已可用）与 Abandoned（已放弃）不得进入恢复队列。
/// </summary>
public class AttachmentRecoveryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 9001;

    public AttachmentRecoveryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_att_{Guid.NewGuid():N}.db");
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

    /// <summary>删除测试库文件：后台任务可能仍在释放连接，重试等待。</summary>
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

    [Fact]
    public async Task Recoverable_Includes_Uploading_And_Failed_Excludes_Available_And_Abandoned()
    {
        await _db.UpsertAttachmentAsync(NewAttachment("a-uploading", AttachmentStatus.Uploading));
        await _db.UpsertAttachmentAsync(NewAttachment("a-failed", AttachmentStatus.Failed));
        await _db.UpsertAttachmentAsync(NewAttachment("a-available", AttachmentStatus.Available));
        await _db.UpsertAttachmentAsync(NewAttachment("a-abandoned", AttachmentStatus.Abandoned));

        var recoverable = await _db.GetRecoverableAttachmentsAsync(OwnerId);

        Assert.Equal(2, recoverable.Count);
        Assert.Contains(recoverable, a => a.ClientAttachmentId == "a-uploading");
        Assert.Contains(recoverable, a => a.ClientAttachmentId == "a-failed");
        Assert.DoesNotContain(recoverable, a => a.ClientAttachmentId == "a-available");
        Assert.DoesNotContain(recoverable, a => a.ClientAttachmentId == "a-abandoned");
    }

    [Fact]
    public async Task Recoverable_Is_Scoped_Per_Owner()
    {
        await _db.UpsertAttachmentAsync(NewAttachment("owner-a", AttachmentStatus.Failed, OwnerId));
        await _db.UpsertAttachmentAsync(NewAttachment("owner-b", AttachmentStatus.Failed, OwnerId + 1));

        var recoverable = await _db.GetRecoverableAttachmentsAsync(OwnerId);

        Assert.Single(recoverable);
        Assert.Equal("owner-a", recoverable[0].ClientAttachmentId);
    }

    private static LocalAttachment NewAttachment(
        string clientId, AttachmentStatus status, long ownerUserId = OwnerId) => new()
        {
            OwnerUserId = ownerUserId,
            ClientAttachmentId = clientId,
            FileName = $"{clientId}.bin",
            ContentType = "application/octet-stream",
            SizeBytes = 1024,
            Status = status,
            LocalUploadingPath = $"{clientId}.bin"
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
}


