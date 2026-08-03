using System;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace Chat_App.Infrastructure.Identity;

/// <summary>
/// 本地敏感信息（AccessToken/RefreshToken）的静态保护器（P0-10）。
/// Windows 平台使用 DPAPI（当前用户作用域 + 应用熵）加密后落库，
/// 明文不出现在 SQLite 文件中；非 Windows 平台无 OS 级保护，保持原样并警告。
/// 加密值以 Base64 存储；解密失败视为数据损坏，返回 null 交由上层走重新登录。
/// </summary>
public static class SecretProtector
{
    // 固定应用熵：仅用于区分同一用户上下文内的不同应用数据，不增加额外安全性。
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("chat-app-secret-protector-v1");

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;
        if (!OperatingSystem.IsWindows())
        {
            Log.Warning("当前平台不支持 DPAPI，敏感令牌将明文存储（仅限非 Windows 开发环境）");
            return plaintext;
        }

        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return stored;
        if (!OperatingSystem.IsWindows())
            return stored;

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Warning(ex, "令牌解密失败，可能因用户配置文件变更或数据损坏；将触发重新登录");
            return null;
        }
    }
}
