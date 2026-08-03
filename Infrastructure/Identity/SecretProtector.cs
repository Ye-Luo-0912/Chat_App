using System;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace Chat_App.Infrastructure.Identity;

/// <summary>
/// 本地敏感信息（AccessToken/RefreshToken）的静态保护器。
/// Windows 平台使用 DPAPI（当前用户作用域 + 应用熵）加密后落库，
/// 明文不出现在 SQLite 文件中。
/// 非 Windows 平台（macOS/Linux）无 OS 级密钥存储：拒绝持久化明文令牌
/// （Protect 返回 null，令牌仅驻留内存，重启后自动登录禁用），
/// 且不信任存量明文（Unprotect 返回 null，强制重新登录）——
/// 明文落库不能作为正式安全方案。
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
            // 无 OS 级安全存储：拒绝明文落库，自动登录随之禁用（令牌仅内存驻留）。
            Log.Warning("当前平台无安全密钥存储（DPAPI 仅 Windows），令牌不持久化，自动登录禁用");
            return null;
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
        {
            // 不信任非 Windows 存量明文（历史版本可能落库）：强制重新登录。
            Log.Warning("非 Windows 平台检测到存量令牌，为安全起见拒绝使用，请重新登录");
            return null;
        }

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
