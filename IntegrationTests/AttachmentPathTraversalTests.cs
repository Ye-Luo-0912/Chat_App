using Core.Interfaces;
using Core.Models;
using Core.Services;
using System.Security;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 附件路径穿越回归测试：AttachmentStorageService 三条防线——
/// 1. AttachmentId 绝不可直接用作路径段：SHA256(owner:attachmentId) 哈希派生文件名；
/// 2. 文件名经 SanitizeFileName 清洗非法字符；
/// 3. 相对路径经 SafeResolve 校验必须落在根目录内，逃逸抛 SecurityException。
/// </summary>
public class AttachmentPathTraversalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chat_attachment_traversal", Guid.NewGuid().ToString("N"));
    private readonly SwitchableUserContext _ctx = new() { UserId = 1001 };
    private readonly AttachmentStorageService _storage;

    public AttachmentPathTraversalTests()
    {
        _storage = new AttachmentStorageService(_ctx, _root);
    }

    /// <summary>可切换用户上下文 stub（同一账户隔离目录）。</summary>
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

    private static string EscapeProbe(string inside)
        => Path.GetFullPath(Path.Combine(inside, "..", "..", "..", "escape-probe.txt"));

    /// <summary>
    /// 恶意 AttachmentId（含 ../ 与 ..\）不得产生任何路径逃逸：
    /// 下载缓存文件名必须是 64 位十六进制哈希前缀 + 清洗后的文件名，
    /// 且文件落在账户 downloads 目录内。
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\..\\evil")]
    [InlineData("a/../../b")]
    [InlineData("..\\..\\..\\..\\windows\\system32\\evil.txt")]
    [InlineData("")]
    [InlineData("..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\tmp")]
    public async Task Malicious_AttachmentId_Cannot_Escape_Downloads_Dir(string attachmentId)
    {
        var downloadsDir = _storage.GetDownloadsDir(1001);
        var escapeProbe = EscapeProbe(downloadsDir);

        using var content = new MemoryStream("payload"u8.ToArray());
        var path = await _storage.WriteToDownloadsAsync(1001, attachmentId, "report.txt", content);

        var fullPath = Path.GetFullPath(path);
        var downloadsPrefix = Path.GetFullPath(downloadsDir) + Path.DirectorySeparatorChar;

        // 必须落在 downloads 目录内（前缀校验，杜绝任何逃逸）
        Assert.StartsWith(downloadsPrefix, fullPath, StringComparison.OrdinalIgnoreCase);
        // 文件名主段必须是 64 位十六进制哈希（AttachmentId 不能进入文件名）
        var fileName = Path.GetFileName(fullPath);
        var hashPart = fileName.Split('_')[0];
        Assert.Matches("^[0-9a-f]{64}$", hashPart);
        // 文件真实存在，且逃逸探针路径必须不存在
        Assert.True(File.Exists(fullPath));
        Assert.False(File.Exists(escapeProbe));
    }

    /// <summary>
    /// 恶意文件名不得逃逸：路径分隔符等非法字符被清洗为占位符，
    /// 最终文件仍为单一段文件名且位于 downloads 目录内。
    /// </summary>
    [Theory]
    [InlineData("..\\..\\..\\escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("..\\escape.exe")]
    [InlineData("..")]
    [InlineData("a:b\\c/d")]
    public async Task Malicious_FileName_Cannot_Escape_Downloads_Dir(string fileName)
    {
        var downloadsDir = _storage.GetDownloadsDir(1001);
        var escapeProbe = EscapeProbe(downloadsDir);

        using var content = new MemoryStream("payload"u8.ToArray());
        var path = await _storage.WriteToDownloadsAsync(1001, "att-1", fileName, content);

        var fullPath = Path.GetFullPath(path);
        var downloadsPrefix = Path.GetFullPath(downloadsDir) + Path.DirectorySeparatorChar;
        Assert.StartsWith(downloadsPrefix, fullPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(fullPath));
        Assert.False(File.Exists(escapeProbe));
    }

    /// <summary>
    /// 相对路径逃逸必须被 SafeResolve 拒绝（SecurityException），
    /// 合法相对路径正常解析。用例使用跨平台分隔符（正斜杠）；
    /// Windows 专属反斜杠用例仅在该平台生效（Linux 上反斜杠是合法文件名字符）。
    /// </summary>
    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("../../outside.txt")]
    [InlineData("../evil/../../x")]
    [InlineData("uploading/../../../root.txt")]
    public void RelativePath_Escape_Throws_SecurityException(string relativePath)
    {
        Assert.Throws<SecurityException>(() => _storage.ResolvePath(1001, relativePath));
        Assert.Throws<SecurityException>(() => _storage.OpenUploadingRead(1001, relativePath));
    }

    /// <summary>Windows 专属：反斜杠分隔符的逃逸路径同样被拒绝（Linux 上该字符串是普通文件名）。</summary>
    [Fact]
    public void RelativePath_Escape_Backslash_Throws_On_Windows()
    {
        if (!OperatingSystem.IsWindows())
            return; // 反斜杠在 Linux/macOS 不是路径分隔符，逃逸不成立
        Assert.Throws<SecurityException>(() => _storage.ResolvePath(1001, "..\\..\\..\\etc\\passwd"));
        Assert.Throws<SecurityException>(() => _storage.OpenUploadingRead(1001, "uploading\\..\\..\\..\\root.txt"));
    }

    /// <summary>对照组：合法 AttachmentId 与文件名正常工作，无误伤。</summary>
    [Fact]
    public async Task Benign_Ids_And_Names_Work_Normally()
    {
        using var content = new MemoryStream("hello"u8.ToArray());
        var path = await _storage.WriteToDownloadsAsync(1001, "att-benign-1", "notes.txt", content);
        Assert.True(File.Exists(path));
        Assert.StartsWith(Path.GetFullPath(_storage.GetDownloadsDir(1001)), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

        var resolved = _storage.ResolvePath(1001, "uploading");
        Assert.Equal(Path.GetFullPath(_storage.GetUploadingDir(1001)), Path.GetFullPath(resolved));
    }

    /// <summary>
    /// P1-6 回归：路径方法显式携带 owner——当前用户是 B（2002）时，
    /// 显式传 owner=1001 的操作必须落在 A 的目录，绝不落到 B 的目录。
    /// （原实现按全局当前用户决定目录，恢复 A 记录时会误读 B 目录。）
    /// </summary>
    [Fact]
    public async Task Explicit_Owner_Is_Used_Regardless_Of_Current_User()
    {
        // 当前用户是 B
        _ctx.UserId = 2002;

        // 显式 owner=1001：写入必须落在 A 的 downloads 目录
        using var content = new MemoryStream("owner-scoped"u8.ToArray());
        var path = await _storage.WriteToDownloadsAsync(1001, "att-owner-a", "a.txt", content);
        var downloadsA = Path.GetFullPath(_storage.GetDownloadsDir(1001));
        var downloadsB = Path.GetFullPath(_storage.GetDownloadsDir(2002));
        Assert.StartsWith(downloadsA + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        Assert.False(Path.GetFullPath(path).StartsWith(downloadsB, StringComparison.OrdinalIgnoreCase), "不得写入当前用户 B 的目录");

        // ResolvePath 同理：owner=1001 解析到 A 目录内
        var resolved = _storage.ResolvePath(1001, "downloads");
        Assert.StartsWith(downloadsA, Path.GetFullPath(resolved), StringComparison.OrdinalIgnoreCase);

        // 当前用户 B 的目录中没有 A 的文件
        Assert.False(File.Exists(Path.Combine(downloadsB, Path.GetFileName(path))));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略清理失败 */ }
        GC.SuppressFinalize(this);
    }
}

