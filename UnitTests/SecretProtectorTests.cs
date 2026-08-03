using Chat_App.Infrastructure.Identity;
using Xunit;

namespace UnitTests;

/// <summary>
/// 敏感令牌静态保护器测试。
/// Windows：DPAPI 加解密往返一致；空值直通；密文与明文不同（非 Base64 的明文不落库）。
/// 非 Windows：不支持 DPAPI，原样直通（开发环境）。
/// </summary>
public class SecretProtectorTests
{
    [Fact]
    public void Protect_Unprotect_RoundTrip_On_Windows()
    {
        var plain = "opaque-access-token-12345";

        var protectedValue = SecretProtector.Protect(plain);
        var restored = SecretProtector.Unprotect(protectedValue);

        if (OperatingSystem.IsWindows())
        {
            // 密文与明文不相等：DPAPI 密文落库，明文不出现在 SQLite 中
            Assert.NotEqual(plain, protectedValue);
            Assert.Equal(plain, restored);
        }
        else
        {
            Assert.Equal(plain, protectedValue);
            Assert.Equal(plain, restored);
        }
    }

    [Fact]
    public void Protect_Unprotect_Empty_Null_Are_Transparent()
    {
        Assert.Null(SecretProtector.Protect(null));
        Assert.Null(SecretProtector.Unprotect(null));
        Assert.Equal(string.Empty, SecretProtector.Protect(string.Empty));
        Assert.Equal(string.Empty, SecretProtector.Unprotect(string.Empty));
    }

    [Fact]
    public void Protect_Is_Non_Deterministic_On_Windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var a = SecretProtector.Protect("same-token");
        var b = SecretProtector.Protect("same-token");

        // DPAPI 每次加密使用新盐：两次密文不同（防同值对比），但都能还原
        Assert.NotEqual(a, b);
        Assert.Equal("same-token", SecretProtector.Unprotect(a));
        Assert.Equal("same-token", SecretProtector.Unprotect(b));
    }
}
