using System.Text;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Services;
using Core.Helpers;
using Core.Interfaces;
using Core.Models;
using Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 附件缩略图服务测试：缓存优先、并发合并、非图片跳过、DB 路径回填。
/// 生成器使用假 codec（记录调用次数），存储与数据库为真实临时目录/SQLite。
/// </summary>
public class AttachmentThumbnailTests : IDisposable
{
    private const long OwnerId = 9201;
    private readonly string _dbPath;
    private readonly string _storageRoot;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;
    private readonly AttachmentStorageService _storage;
    private readonly FakeThumbnailCodec _codec;
    private readonly AttachmentThumbnailService _service;

    private const string AttachmentId = "att-thumb-1";
    private const string FileName = "photo.png";

    public AttachmentThumbnailTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_thumb_{Guid.NewGuid():N}.db");
        _storageRoot = Path.Combine(Path.GetTempPath(), $"chat_thumb_{Guid.NewGuid():N}");
        _factory = new DbContextFactoryStub(_dbPath);
        _db = new DatabaseService(_factory);
        using var ctx = _factory.CreateDbContext();
        ctx.Database.EnsureCreated();

        var userContext = new StubCurrentUserContext(OwnerId);
        _storage = new AttachmentStorageService(userContext, _storageRoot);
        _codec = new FakeThumbnailCodec();
        _service = new AttachmentThumbnailService(_storage, _codec, _db, userContext);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDeleteWithRetry(_dbPath);
        try { Directory.Delete(_storageRoot, recursive: true); } catch { /* 忽略 */ }
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

    private string CreateSourceFile(string fileName = FileName)
    {
        var dir = _storage.GetDownloadsDir(OwnerId);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        return path;
    }

    private async Task SeedAttachmentRowAsync(string? thumbnailPath = null)
    {
        await _db.UpsertAttachmentAsync(new LocalAttachment
        {
            OwnerUserId = OwnerId,
            AttachmentId = AttachmentId,
            FileName = FileName,
            ContentType = "image/png",
            SizeBytes = 5,
            Status = AttachmentStatus.Available,
            LocalThumbnailPath = thumbnailPath
        });
    }

    [Fact]
    public async Task Creates_Thumbnail_For_Image_And_Backfills_Db()
    {
        await SeedAttachmentRowAsync();
        var source = CreateSourceFile();

        var path = await _service.EnsureThumbnailAsync(OwnerId, AttachmentId, FileName, "image/png", source);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.EndsWith("_thumb.jpg", Path.GetFileName(path));
        Assert.Equal(1, _codec.Calls);

        var row = await _db.GetAttachmentByAttachmentIdAsync(OwnerId, AttachmentId);
        Assert.Equal(Path.GetFileName(path), row!.LocalThumbnailPath);
    }

    [Fact]
    public async Task Second_Call_Hits_Cache_Without_Regeneration()
    {
        var source = CreateSourceFile();

        var first = await _service.EnsureThumbnailAsync(OwnerId, AttachmentId, FileName, "image/png", source);
        var second = await _service.EnsureThumbnailAsync(OwnerId, AttachmentId, FileName, "image/png", source);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(second));
        Assert.Equal(1, _codec.Calls);
    }

    [Fact]
    public async Task Concurrent_Calls_Generate_Once_And_Share_Result()
    {
        var source = CreateSourceFile();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            _service.EnsureThumbnailAsync(OwnerId, AttachmentId, FileName, "image/png", source)));

        Assert.All(results, r => Assert.NotNull(r));
        Assert.All(results, r => Assert.Equal(results[0], r));
        Assert.Equal(1, _codec.Calls);
    }

    [Fact]
    public async Task Non_Image_Returns_Null_Without_Codec_Call()
    {
        var source = CreateSourceFile();

        var path = await _service.EnsureThumbnailAsync(OwnerId, AttachmentId, FileName, "application/pdf", source);

        Assert.Null(path);
        Assert.Equal(0, _codec.Calls);
    }

    [Fact]
    public async Task Missing_Source_Returns_Null()
    {
        var path = await _service.EnsureThumbnailAsync(
            OwnerId, AttachmentId, FileName, "image/png", Path.Combine(_storageRoot, "nope.png"));

        Assert.Null(path);
        Assert.Equal(0, _codec.Calls);
    }

    [Fact]
    public async Task AttachmentType_IsImage_Detects_Image_Mime()
    {
        Assert.True(AttachmentType.IsImage("image/jpeg"));
        Assert.True(AttachmentType.IsImage("image/png"));
        Assert.True(AttachmentType.IsImage("image/webp"));
        Assert.False(AttachmentType.IsImage("application/pdf"));
        Assert.False(AttachmentType.IsImage(null));
        Assert.False(AttachmentType.IsImage(""));
    }

    /// <summary>假生成器：直接写固定内容到目标路径并计数。</summary>
    private sealed class FakeThumbnailCodec : IThumbnailImageCodec
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<bool> TryCreateThumbnailAsync(
            string sourceFullPath, string destinationPath, int maxDimension, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            File.WriteAllBytes(destinationPath, Encoding.UTF8.GetBytes("fake-thumbnail"));
            return Task.FromResult(true);
        }
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
