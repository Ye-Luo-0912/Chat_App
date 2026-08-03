using Chat_App.Infrastructure.Identity;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Services;
using Core.Contracts.Auth;
using Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Core.Interfaces;
using System.Collections.Concurrent;
using Xunit;

namespace UnitTests;

/// <summary>
/// Token 刷新 single-flight 验收（S0）：
/// 100 个并发 401 只产生一次 refresh HTTP 请求；
/// 共享刷新不受任一调用方取消影响（其余调用方仍拿到结果）。
/// </summary>
public class TokenRefreshSingleFlightTests
{
    private sealed class CountingAuthClient : IAuthClientService
    {
        public int RefreshCalls;
        public Task<LoginResult> RefreshTokenAsync(string refreshToken, long userId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref RefreshCalls);
            // 模拟网络延迟，放大并发窗口
            return Task.Delay(50, ct).ContinueWith(_ => new LoginResult
            {
                IsSuccess = true,
                AccessToken = $"access-{RefreshCalls}",
                AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                RefreshToken = "refresh-new",
                RefreshTokenExpiresAtUtc = DateTime.UtcNow.AddDays(7)
            }, ct);
        }

        public Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LogoutAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> SendRegisterCodeAsync(string email, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RegisterResponse> RegisterAsync(string email, string code, string password, CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>真实 DatabaseService + 文件库（TokenInfo 全链路走真实现）。</summary>
    private sealed class Fixture : IDisposable
    {
        public readonly string DbPath;
        public readonly IDbContextFactory<ClientDbContext> Factory;
        public readonly DatabaseService Db;

        public Fixture()
        {
            DbPath = Path.Combine(Path.GetTempPath(), $"chat_token_{Guid.NewGuid():N}.db");
            Factory = new F(DbPath);
            Db = new DatabaseService(Factory);
            using var ctx = Factory.CreateDbContext();
            ctx.Database.EnsureCreated();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 40; attempt++)
            {
                try { File.Delete(DbPath); return; }
                catch (IOException) { Thread.Sleep(50); }
            }
        }

        private sealed class F(string path) : IDbContextFactory<ClientDbContext>
        {
            public ClientDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ClientDbContext>().UseSqlite($"Data Source={path}").Options);
            public Task<ClientDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
        }
    }

    [Fact]
    public async Task Concurrent_100_Refresh_401s_Issue_Single_Http_Request()
    {
        using var fx = new Fixture();
        await fx.Db.SaveTokenAsync(new AuthToken
        {
            UserId = 1001,
            AccessToken = "access-old",
            AccessTokenExpires = DateTime.UtcNow.AddMinutes(-1),
            RefreshToken = "refresh-old",
            RefreshTokenExpires = DateTime.UtcNow.AddDays(7)
        });

        var auth = new CountingAuthClient();
        var tokenInfo = new TokenInfo(fx.Db, auth, new CurrentUserContext());

        var tasks = new List<Task<bool>>();
        for (var i = 0; i < 100; i++)
            tasks.Add(tokenInfo.RefreshTokensAsync());

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r));
        // 100 个并发 401 只产生一次 refresh HTTP 请求
        Assert.Equal(1, auth.RefreshCalls);

        // 刷新后的 token 已更新（内存 + DB）
        Assert.NotNull(tokenInfo.Token);
        Assert.StartsWith("access-", tokenInfo.Token!.TokenValue);
        var stored = await fx.Db.GetTokenAsync();
        Assert.StartsWith("access-", stored!.AccessToken);
    }

    [Fact]
    public async Task Caller_Cancellation_Does_Not_Cancel_Shared_Refresh()
    {
        using var fx = new Fixture();
        await fx.Db.SaveTokenAsync(new AuthToken
        {
            UserId = 1001,
            AccessToken = "access-old",
            AccessTokenExpires = DateTime.UtcNow.AddMinutes(-1),
            RefreshToken = "refresh-old",
            RefreshTokenExpires = DateTime.UtcNow.AddDays(7)
        });

        var auth = new CountingAuthClient();
        var tokenInfo = new TokenInfo(fx.Db, auth, new CurrentUserContext());

        // 第一个调用方很快取消自己的等待；其余调用方仍应拿到刷新结果
        using var cts = new CancellationTokenSource(30);
        var first = tokenInfo.RefreshTokensAsync(cts.Token);
        var others = new List<Task<bool>>();
        for (var i = 0; i < 10; i++)
            others.Add(tokenInfo.RefreshTokensAsync());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var results = await Task.WhenAll(others);
        Assert.All(results, r => Assert.True(r));
        // 共享刷新完成且只发生一次 HTTP 请求
        Assert.Equal(1, auth.RefreshCalls);
    }
}



