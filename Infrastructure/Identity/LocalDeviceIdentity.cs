using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Infrastructure.Identity;

/// <summary>
/// 生成本机稳定设备 ID 并持久化到 Data/device.id。
/// 格式与服务端 DeviceIdHashHelper 兼容：32 字节随机 → Base64url（43 字符）。
/// </summary>
public sealed class LocalDeviceIdentity : ILocalDeviceIdentity
{
    private const string FileName = "device.id";
    private const int MinLength = 16;
    private const int MaxLength = 128;

    public string DeviceId { get; }
    public string UserAgent { get; }

    public LocalDeviceIdentity()
    {
        DeviceId = LoadOrCreate();
        UserAgent = BuildUserAgent();
        Log.Information("本机设备 ID 已就绪（长度 {Length}）", DeviceId.Length);
    }

    private static string LoadOrCreate()
    {
        var path = ResolvePath();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (IsValid(existing))
                    return existing;

                Log.Warning("本地 device.id 非法，将重新生成");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取本地 device.id 失败，将重新生成");
        }

        var created = CreateNew();
        try
        {
            File.WriteAllText(path, created, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "写入本地 device.id 失败（本次仍使用内存中的 ID）");
        }

        return created;
    }

    private static string ResolvePath()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        return Path.Combine(dataDir, FileName);
    }

    private static string CreateNew()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return ToBase64Url(bytes);
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool IsValid(string value)
    {
        if (value.Length is < MinLength or > MaxLength)
            return false;

        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
                continue;
            return false;
        }

        return true;
    }

    private static string BuildUserAgent()
    {
        var os = OperatingSystem.IsWindows() ? "Windows" :
            OperatingSystem.IsMacOS() ? "macOS" :
            OperatingSystem.IsLinux() ? "Linux" : "Unknown";

        string machine;
        try
        {
            machine = Environment.MachineName;
            if (string.IsNullOrWhiteSpace(machine))
                machine = "Desktop";
        }
        catch
        {
            machine = "Desktop";
        }

        // 仅保留 URL/HTTP 头安全字符，避免服务端解析异常。
        machine = SanitizeHeaderToken(machine);
        return $"ChatApp-Desktop/1.0 ({os}; {machine})";
    }

    private static string SanitizeHeaderToken(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch))
                sb.Append('-');
        }

        return sb.Length > 0 ? sb.ToString() : "Desktop";
    }
}
