using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Helpers;

/// <summary>
/// 文件哈希计算工具。支持流式计算与进度回调。
/// </summary>
public static class FileHasher
{
    private const int BufferSize = 65536; // 64KB

    /// <summary>计算流的 SHA256 哈希（十六进制小写字符串）。流会被完整读取。</summary>
    public static async Task<string> ComputeSha256Async(Stream stream, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Position = 0;

        using var sha = SHA256.Create();
        var buffer = new byte[BufferSize];
        long totalRead = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            totalRead += read;
            sha.TransformBlock(buffer, 0, read, buffer, 0);
            progress?.Report(totalRead);
        }

        sha.TransformFinalBlock([], 0, 0);
        var hash = sha.Hash;
        if (hash is null)
            throw new InvalidOperationException("SHA256 计算失败");

        // 转十六进制小写
        var sb = new System.Text.StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>计算文件的 SHA256 哈希。文件会被完整读取。</summary>
    public static async Task<string> ComputeFileSha256Async(string filePath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(filePath);
        return await ComputeSha256Async(fs, progress, ct).ConfigureAwait(false);
    }
}