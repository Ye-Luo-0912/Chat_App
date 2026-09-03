using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;
using Core.Services;
using Xunit;

namespace UnitTests;

/// <summary>
/// 附件缓存治理回归测试：LRU 淘汰、.partial 在途文件豁免、cache.version 豁免、
/// 每账户容量隔离、哈希校验失败不落盘完整缓存、路径安全与缓存命中。
/// 通过注入极小容量上限驱动淘汰逻辑，避免写入 512MB 真实数据。
/// </summary>
public sealed class AttachmentCacheGovernanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chat_attachment_cache_gov", Guid.NewGuid().ToString("N"));
    private readonly SwitchableUserContext _ctx = new() { UserId = 1001 };
    private readonly AttachmentStorageService _storage;

    public AttachmentCacheGovernanceTests()
    {
        // 512MB 常量的默认淘汰在测试中不可行，注入小容量上限以确定性驱动 LRU；
        // 同时注入 10s 近期访问保护窗口（默认 10 分钟），使显式回拨访问时间的测试文件可被淘汰。
        _storage = new AttachmentStorageService(_ctx, _root, maxCacheBytes: 100,
            recentAccessProtection: TimeSpan.FromSeconds(10));
    }

    /// <summary>可切换用户上下文 stub（同账户隔离目录）。</summary>
    private sealed class SwitchableUserContext : ICurrentUserContext
    {
        public long Generation { get; set; } = 1;
        public long? UserId { get; set; }
        public string? UserName => UserId is { } id ? $"user-{id}" : null;
        public bool IsAuthenticated => UserId is > 0;
        public bool HasUserId => UserId is > 0;
        public UserSessionSnapshot Snapshot => new(UserId ?? 0, Generation, UserName, null, null);
        public long RequireUserId() => UserId ?? throw new InvalidOperationException("未登录");
        public bool TryGetUserId(out long id)
        {
            id = UserId ?? 0;
            return UserId is > 0;
        }
    }

    /// <summary>写入一个指定字节数的完整缓存文件，并显式设置访问时间以控制 LRU 顺序。</summary>
    private async Task<string> WriteFileAsync(long owner, string attachmentId, string fileName, int size, TimeSpan? accessAge = null)
    {
        var payload = new byte[size];
        new Random(42).NextBytes(payload);
        using var content = new MemoryStream(payload);
        var path = await _storage.WriteToDownloadsAsync(owner, attachmentId, fileName, content, CancellationToken.None);
        if (accessAge is { } age)
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private static void WritePartialFile(string downloadsDir, string name, int size, TimeSpan accessAge)
    {
        var path = Path.Combine(downloadsDir, name + ".partial");
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow - accessAge);
    }

    [Fact]
    public async Task Under_Capacity_Nothing_Is_Evicted()
    {
        var a = await WriteFileAsync(1001, "att-a", "a.bin", 40);
        var b = await WriteFileAsync(1001, "att-b", "b.bin", 40);

        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task Over_Capacity_Evicts_LeastRecentlyUsed_First()
    {
        // A(60, 最久未用) + B(40) + C(40) = 140 > 100：最久未用的 A 被淘汰，B/C 保留。
        var a = await WriteFileAsync(1001, "att-lru-a", "a.bin", 60, TimeSpan.FromSeconds(30));
        var b = await WriteFileAsync(1001, "att-lru-b", "b.bin", 40, TimeSpan.FromSeconds(20));
        var c = await WriteFileAsync(1001, "att-lru-c", "c.bin", 40);

        Assert.False(File.Exists(a), "最久未用的附件应被优先淘汰");
        Assert.True(File.Exists(b), "较新的附件应保留");
        Assert.True(File.Exists(c), "最新写入的附件应保留");
    }

    [Fact]
    public async Task Partial_Files_Are_Excluded_From_Eviction()
    {
        // 在途 .partial 不参与容量统计，也不被淘汰（即使访问时间最旧）。
        await WriteFileAsync(1001, "att-pa", "a.bin", 60, TimeSpan.FromSeconds(30));
        WritePartialFile(_storage.GetDownloadsDir(1001), "inflight", 60, TimeSpan.FromSeconds(40));

        // 触发淘汰：仅统计完整文件，淘汰最旧完整文件 a.bin；.partial 保留。
        var b = await WriteFileAsync(1001, "att-pb", "b.bin", 40);
        var partialPath = Path.Combine(_storage.GetDownloadsDir(1001), "inflight.partial");

        Assert.True(File.Exists(partialPath), "在途 .partial 不应被淘汰");
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task CacheVersionFile_Is_Never_Evicted()
    {
        await WriteFileAsync(1001, "att-cv", "a.bin", 100, TimeSpan.FromSeconds(30));
        var b = await WriteFileAsync(1001, "att-cv2", "b.bin", 40);

        var versionPath = Path.Combine(_storage.GetDownloadsDir(1001), "cache.version");
        Assert.True(File.Exists(versionPath), "cache.version 标记文件不应被淘汰");
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task Eviction_Is_Per_Owner_Isolated()
    {
        // A 账户超容量触发淘汰，B 账户的缓存不受影响。
        var a1 = await WriteFileAsync(1001, "att-own-a1", "a1.bin", 60, TimeSpan.FromSeconds(30));
        var a2 = await WriteFileAsync(1001, "att-own-a2", "a2.bin", 60);
        var b1 = await WriteFileAsync(2002, "att-own-b1", "b1.bin", 60);

        var dirB = Path.GetFullPath(_storage.GetDownloadsDir(2002));
        Assert.False(File.Exists(a1), "A 账户超容量应淘汰其最久未用文件");
        Assert.True(File.Exists(a2), "A 账户较新文件应保留");
        Assert.True(File.Exists(b1), "B 账户缓存不应受 A 账户超容量影响");
        Assert.StartsWith(dirB, Path.GetFullPath(b1), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hash_Mismatch_Throws_And_Leaves_No_Complete_Cache()
    {
        using var content = new MemoryStream("payload"u8.ToArray());
        var ex = await Assert.ThrowsAsync<IOException>(() =>
            _storage.WriteToDownloadsAsync(1001, "att-hash", "h.bin", content, CancellationToken.None,
                expectedSha256: "0000000000000000000000000000000000000000000000000000000000000000"));

        Assert.Contains("哈希校验失败", ex.Message);
        // 完整缓存不得落盘（仅残留 .partial 供续传）。
        Assert.Null(_storage.GetDownloadCachePath(1001, "att-hash", "h.bin"));
    }

    [Fact]
    public async Task GetDownloadCachePath_Hits_And_Misses_Correctly()
    {
        Assert.Null(_storage.GetDownloadCachePath(1001, "att-gc", "file.txt"));

        var path = await WriteFileAsync(1001, "att-gc", "file.txt", 10);
        var hit = _storage.GetDownloadCachePath(1001, "att-gc", "file.txt");
        Assert.NotNull(hit);
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(hit!));
    }

    [Fact]
    public void ResolvePath_Rejects_Traversal()
    {
        Assert.Throws<SecurityException>(() => _storage.ResolvePath(1001, "../../outside.txt"));
    }

    // ---- VOICE-MSG-3：80% 回落目标 / 近期访问保护 / 占用统计与手动清理 ----

    [Fact]
    public void MaxCacheBytes_Reflects_Configured_Limit_And_Default()
    {
        Assert.Equal(100, _storage.MaxCacheBytes);

        // 未配置回退默认 512MB；非法值（0/负数）同样回退默认。
        var defaultPath = Path.Combine(_root, "default-limit");
        Assert.Equal(512L * 1024 * 1024,
            new AttachmentStorageService(_ctx, defaultPath).MaxCacheBytes);
        Assert.Equal(512L * 1024 * 1024,
            new AttachmentStorageService(_ctx, Path.Combine(_root, "invalid-limit"), maxCacheBytes: -1).MaxCacheBytes);
    }

    [Fact]
    public async Task Eviction_Falls_Back_To_About_80_Percent_Of_Limit()
    {
        // A(60, 30s) + B(40, 25s) = 100 ≤ 100 不触发；写入 C(40) 后总量 140 > 100：
        // 淘汰目标为 ~80%（80B），删最旧的 A(60) 后即停 —— 只清到目标，不做全量清空。
        var a = await WriteFileAsync(1001, "att-80-a", "a.bin", 60, TimeSpan.FromSeconds(30));
        await WriteFileAsync(1001, "att-80-b", "b.bin", 40, TimeSpan.FromSeconds(25));
        var c = await WriteFileAsync(1001, "att-80-c", "c.bin", 40);

        Assert.False(File.Exists(a), "最久未用的文件应被淘汰以回落到 80% 目标");
        Assert.True(File.Exists(c));
        Assert.InRange(_storage.GetDownloadsCacheSizeBytes(1001), 0, 80);
    }

    [Fact]
    public async Task Eviction_Skips_Recently_Accessed_Files_Even_Still_Over_Limit()
    {
        // A(10, 30s) 可淘汰；B/C/D 均在 10s 保护窗口内（模拟正在播放/刚使用）：
        // 删 A 后仍超 80% 目标，但受保护文件全部保留（清理顺延到下一次触发）。
        var a = await WriteFileAsync(1001, "att-prot-a", "a.bin", 10, TimeSpan.FromSeconds(30));
        var b = await WriteFileAsync(1001, "att-prot-b", "b.bin", 60, TimeSpan.FromSeconds(5));
        var c = await WriteFileAsync(1001, "att-prot-c", "c.bin", 60, TimeSpan.FromSeconds(2));
        var d = await WriteFileAsync(1001, "att-prot-d", "d.bin", 20);

        Assert.False(File.Exists(a), "保护窗口外的旧文件应被淘汰");
        Assert.True(File.Exists(b), "窗口内文件即使最旧（除已删者）也不应被删");
        Assert.True(File.Exists(c), "窗口内文件不应被删");
        Assert.True(File.Exists(d), "刚写入的文件不应被删");
        Assert.Equal(140, _storage.GetDownloadsCacheSizeBytes(1001));
    }

    [Fact]
    public async Task GetDownloadsCacheSize_Excludes_Version_And_Partial()
    {
        await WriteFileAsync(1001, "att-size-a", "a.bin", 10);
        await WriteFileAsync(1001, "att-size-b", "b.bin", 20);
        WritePartialFile(_storage.GetDownloadsDir(1001), "inflight", 50, TimeSpan.FromSeconds(1));

        Assert.Equal(30, _storage.GetDownloadsCacheSizeBytes(1001));
        // 其他账户隔离：不串账。
        Assert.Equal(0, _storage.GetDownloadsCacheSizeBytes(2002));
    }

    [Fact]
    public async Task ClearDownloadsCache_Removes_Complete_Files_Keeps_Version_And_Partial()
    {
        await WriteFileAsync(1001, "att-clr-a", "a.bin", 10);
        await WriteFileAsync(1001, "att-clr-b", "b.bin", 20);
        await WriteFileAsync(1001, "att-clr-c", "c.bin", 30);
        WritePartialFile(_storage.GetDownloadsDir(1001), "inflight", 50, TimeSpan.FromSeconds(1));
        var versionPath = Path.Combine(_storage.GetDownloadsDir(1001), "cache.version");

        var freed = _storage.ClearDownloadsCache(1001);

        Assert.Equal(60, freed);
        Assert.Equal(0, _storage.GetDownloadsCacheSizeBytes(1001));
        Assert.True(File.Exists(versionPath), "cache.version 标记不应被清除");
        Assert.True(File.Exists(Path.Combine(_storage.GetDownloadsDir(1001), "inflight.partial")),
            "在途 .partial 不应被清除（断点续传不中断）");
    }

    [Fact]
    public async Task ClearDownloadsCache_Skips_File_Locked_By_Playback()
    {
        // 模拟播放器占用：以 FileShare.None 独占打开一个缓存文件。
        var locked = await WriteFileAsync(1001, "att-lock-a", "a.bin", 40);
        var free = await WriteFileAsync(1001, "att-lock-b", "b.bin", 40);
        using var stream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var freed = _storage.ClearDownloadsCache(1001);

        Assert.False(File.Exists(free), "未占用的文件应被清除");
        Assert.True(freed >= 40, "至少应释放未占用文件的字节数");
        if (OperatingSystem.IsWindows())
        {
            // Windows 上独占句柄使 File.Delete 抛共享冲突：被播放文件保留。
            Assert.True(File.Exists(locked), "正在播放（被占用）的文件不应被删除");
            Assert.Equal(40, freed);
        }
    }

    [Fact]
    public void ClearDownloadsCache_On_Empty_Dir_Returns_Zero()
    {
        Assert.Equal(0, _storage.ClearDownloadsCache(2002));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略清理失败 */ }
        GC.SuppressFinalize(this);
    }
}