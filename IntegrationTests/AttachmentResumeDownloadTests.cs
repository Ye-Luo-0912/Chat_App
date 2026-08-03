using System.Net;
using System.Security.Cryptography;
using System.Text;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Services;
using Core.Contracts.Attachments;
using Core.Interfaces;
using Core.Models;
using Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 附件下载断点续传集成测试。
/// 覆盖：中断后 Range 续传、服务端忽略 Range（200 全量覆盖）、
/// 本地 partial 损坏哈希校验失败后整文件重下、Range 无效（416）后重置重下。
/// 存储层使用临时目录（AttachmentStorageService 支持注入 basePath）。
/// </summary>
public class AttachmentResumeDownloadTests : IDisposable
{
    private const long OwnerId = 9101;
    private readonly string _dbPath;
    private readonly string _storageRoot;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;
    private readonly FakeAttachmentClient _attachments;
    private readonly AttachmentStorageService _storage;
    private readonly AttachmentDownloadService _service;

    private const string AttachmentId = "att-resume-1";
    private const string FileName = "resume.bin";

    public AttachmentResumeDownloadTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_resume_{Guid.NewGuid():N}.db");
        _storageRoot = Path.Combine(Path.GetTempPath(), $"chat_resume_{Guid.NewGuid():N}");
        _factory = new DbContextFactoryStub(_dbPath);
        _db = new DatabaseService(_factory);
        using var ctx = _factory.CreateDbContext();
        ctx.Database.EnsureCreated();

        _attachments = new FakeAttachmentClient();
        var userContext = new StubCurrentUserContext(OwnerId);
        _storage = new AttachmentStorageService(userContext, _storageRoot);
        _service = new AttachmentDownloadService(_attachments, _storage, _db, userContext);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDeleteWithRetry(_dbPath);
        try { Directory.Delete(_storageRoot, recursive: true); } catch { /* 忽略 */ }
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
    public async Task InterruptedDownload_Resumes_WithRange_AndAssemblesCompleteFile()
    {
        // 首次下载：网络中断于 40KB 处 → 返回 null，但 partial 保留。
        _attachments.Payload = CreatePayload(100_000);
        _attachments.FailAfterBytes = 40_000;

        var first = await _service.GetOrDownloadAsync(AttachmentId, FileName, null);

        Assert.Null(first);
        Assert.True(_attachments.RangeRequestCount == 0); // 首次应为全量请求

        // 第二次：网络恢复 → 应以 Range 从 40KB 续传并成功。
        _attachments.FailAfterBytes = null;
        var second = await _service.GetOrDownloadAsync(AttachmentId, FileName, null);

        Assert.NotNull(second);
        Assert.Equal(40_000, _attachments.LastRangeFrom);
        Assert.Equal(1, _attachments.RangeRequestCount);
        Assert.Equal(_attachments.Payload, File.ReadAllBytes(second!));

        // 完成后不得残留 partial。
        Assert.False(Directory.EnumerateFiles(_storage.GetDownloadsDir(), "*.partial").Any());
    }

    [Fact]
    public async Task ServerIgnoresRange_ReturnsFull200_ReplacesPartial()
    {
        _attachments.Payload = CreatePayload(50_000);
        _attachments.FailAfterBytes = 20_000;

        // 先制造一个 20KB 的 partial（模拟上次中断残留）。
        Assert.Null(await _service.GetOrDownloadAsync(AttachmentId, FileName, null));

        // 服务端不支持 Range：即使带 Range 也返回 200 全量。
        _attachments.IgnoreRange = true;
        _attachments.FailAfterBytes = null;

        var result = await _service.GetOrDownloadAsync(AttachmentId, FileName, null);

        Assert.NotNull(result);
        Assert.Equal(_attachments.Payload, File.ReadAllBytes(result!));
        Assert.False(Directory.EnumerateFiles(_storage.GetDownloadsDir(), "*.partial").Any());
    }

    [Fact]
    public async Task CorruptPartial_HashMismatch_ResetsAndDownloadsFresh()
    {
        _attachments.Payload = CreatePayload(60_000);
        await SeedExpectedSha256Async();

        // 制造一个内容损坏的 partial：直接落盘与真实 payload 不一致的字节。
        var garbage = new byte[30_000];
        RandomNumberGenerator.Fill(garbage);
        File.WriteAllBytes(PartialPath(), garbage);

        var result = await _service.GetOrDownloadAsync(AttachmentId, FileName, null);

        Assert.NotNull(result);
        var finalBytes = File.ReadAllBytes(result!);
        Assert.Equal(_attachments.Payload, finalBytes);
        // 修复路径走整文件重下（第一次 Range 尝试哈希失败后发起全量请求）。
        Assert.True(_attachments.RangeRequestCount >= 1);
        Assert.True(_attachments.FullRequestCount >= 1);
        Assert.Equal(_attachments.Payload.Length, new FileInfo(result!).Length);
        Assert.False(Directory.EnumerateFiles(_storage.GetDownloadsDir(), "*.partial").Any());
    }

    [Fact]
    public async Task PartialLongerThanServer_Range416_ResetsAndDownloadsFresh()
    {
        _attachments.Payload = CreatePayload(30_000);
        await SeedExpectedSha256Async();

        // 制造一个比服务端文件还长的 partial（模拟服务端文件被替换/截断）。
        var oversized = new byte[40_000];
        RandomNumberGenerator.Fill(oversized);
        File.WriteAllBytes(PartialPath(), oversized);

        var result = await _service.GetOrDownloadAsync(AttachmentId, FileName, null);

        Assert.NotNull(result);
        Assert.Equal(_attachments.Payload, File.ReadAllBytes(result!));
        Assert.Equal(1, _attachments.Range416Count);
        Assert.True(_attachments.FullRequestCount >= 1);
        Assert.False(Directory.EnumerateFiles(_storage.GetDownloadsDir(), "*.partial").Any());
    }

    [Fact]
    public async Task CacheHit_DoesNotDownload()
    {
        _attachments.Payload = CreatePayload(10_000);

        var first = await _service.GetOrDownloadAsync(AttachmentId, FileName, null);
        var second = await _service.GetOrDownloadAsync(AttachmentId, FileName, null);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, _attachments.FullRequestCount + _attachments.RangeRequestCount);
    }

    private static byte[] CreatePayload(int size)
    {
        var payload = new byte[size];
        RandomNumberGenerator.Fill(payload);
        return payload;
    }

    /// <summary>下载缓存目录中该附件的 .partial 路径（与存储层命名规则一致）。</summary>
    private string PartialPath()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{OwnerId}:{AttachmentId}")));
        return Path.Combine(_storage.GetDownloadsDir(), $"{hash}_{FileName}.partial");
    }

    private async Task SeedExpectedSha256Async()
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(_attachments.Payload));
        await _db.UpsertAttachmentAsync(new LocalAttachment
        {
            OwnerUserId = OwnerId,
            ClientAttachmentId = AttachmentId,
            AttachmentId = AttachmentId,
            FileName = FileName,
            ContentType = "application/octet-stream",
            SizeBytes = _attachments.Payload.Length,
            Status = AttachmentStatus.Available,
            Sha256 = sha256
        });
    }

    /// <summary>假附件下载客户端：支持 Range 206 / 200 / 416，可注入中途断流。</summary>
    private sealed class FakeAttachmentClient : IAttachmentClientService
    {
        public byte[] Payload { get; set; } = [];
        public long? FailAfterBytes { get; set; }
        public bool IgnoreRange { get; set; }

        public long? LastRangeFrom { get; private set; }
        public int RangeRequestCount { get; private set; }
        public int FullRequestCount { get; private set; }
        public int Range416Count { get; private set; }

        public Task<AttachmentDownloadResult> DownloadAsync(
            string attachmentIdOrHint, long? rangeFrom = null, long? rangeTo = null, CancellationToken ct = default)
        {
            if (rangeFrom is long from)
            {
                if (IgnoreRange)
                {
                    FullRequestCount++;
                    return Task.FromResult(FullResult());
                }

                if (from >= Payload.Length)
                {
                    Range416Count++;
                    throw new HttpRequestException("请求范围无效", null, HttpStatusCode.RequestedRangeNotSatisfiable);
                }

                RangeRequestCount++;
                LastRangeFrom = from;
                return Task.FromResult(new AttachmentDownloadResult
                {
                    Content = new FailingStream(new MemoryStream(Payload[(int)from..]), FailAfterBytes),
                    ContentType = "application/octet-stream",
                    ContentLength = Payload.Length - from,
                    IsPartialContent = true
                });
            }

            FullRequestCount++;
            return Task.FromResult(FullResult());
        }

        private AttachmentDownloadResult FullResult() => new()
        {
            Content = new FailingStream(new MemoryStream(Payload), FailAfterBytes),
            ContentType = "application/octet-stream",
            ContentLength = Payload.Length,
            IsPartialContent = false
        };

        public Task<AttachmentPresignResponseDto> PresignAsync(AttachmentPresignRequestDto request, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task UploadAsync(AttachmentPresignResponseDto ticket, Stream content, string contentType, long contentLength, IProgress<AttachmentUploadProgress>? progress = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<ConfirmAttachmentResponseDto> ConfirmAsync(ConfirmAttachmentRequestDto request, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AttachmentUploadResult> UploadAndConfirmAsync(Stream content, string contentType, long contentLength, string? originalName = null, string? clientAttachmentId = null, IProgress<AttachmentUploadProgress>? progress = null, int maxAttempts = 3, string? sha256 = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task AbandonAsync(string attachmentId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    /// <summary>读取 FailAfterBytes 字节后抛 IOException 的流，模拟网络中断。</summary>
    private sealed class FailingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long? _failAfter;
        private long _read;

        public FailingStream(Stream inner, long? failAfter)
        {
            _inner = inner;
            _failAfter = failAfter;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_failAfter is not long limit)
                return _inner.Read(buffer, offset, count);
            if (_read >= limit)
                throw new IOException("模拟网络中断");
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, limit - _read));
            _read += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_failAfter is not long limit)
                return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (_read >= limit)
                throw new IOException("模拟网络中断");
            var n = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - _read)], cancellationToken).ConfigureAwait(false);
            _read += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
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


