using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace UnitTests;

/// <summary>
/// ResumeToken 持久化测试：真实 DatabaseService + 临时 SQLite。
/// 验证保存/读取（DPAPI 密文落库）、清除、本地新鲜度过滤，
/// 以及新登录会话（PersistLoginSessionAsync）清空残留 token 的生命周期语义。
/// </summary>
public class ResumeTokenPersistenceTests
{
    [Fact]
    public async Task SaveAndGet_RoundTrips_DecryptedToken()
    {
        using var fx = new Fixture();
        await fx.SeedTokenRowAsync();

        await fx.Db.SaveResumeTokenAsync("resume-token-1");

        Assert.Equal("resume-token-1", await fx.Db.GetResumeTokenAsync());

        // 密文落库：SQLite 文件中不出现明文（连接池持有文件，需以共享读打开）。
        using var stream = new FileStream(fx.DbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var raw = await reader.ReadToEndAsync();
        Assert.DoesNotContain("resume-token-1", raw);
    }

    [Fact]
    public async Task ClearResumeToken_RemovesStoredValue()
    {
        using var fx = new Fixture();
        await fx.SeedTokenRowAsync();
        await fx.Db.SaveResumeTokenAsync("resume-token-1");

        await fx.Db.ClearResumeTokenAsync();

        Assert.Null(await fx.Db.GetResumeTokenAsync());
        // 其余会话字段不受影响。
        var token = await fx.Db.GetTokenAsync();
        Assert.NotNull(token);
        Assert.Equal("access-token", token!.AccessToken);
    }

    [Fact]
    public async Task GetResumeToken_ReturnsNull_WhenNeverSavedOrStale()
    {
        using var fx = new Fixture();
        await fx.SeedTokenRowAsync();

        // 从未保存：null。
        Assert.Null(await fx.Db.GetResumeTokenAsync());

        // 超过本地新鲜度窗口：视为明显陈旧，返回 null（真实 TTL 由网关裁决）。
        await fx.Db.SaveResumeTokenAsync("stale-token");
        fx.Db.ResumeTokenMaxAge = TimeSpan.Zero;
        Assert.Null(await fx.Db.GetResumeTokenAsync());

        // 宽松窗口内仍可读取。
        fx.Db.ResumeTokenMaxAge = TimeSpan.FromMinutes(2);
        Assert.Equal("stale-token", await fx.Db.GetResumeTokenAsync());
    }

    [Fact]
    public async Task SaveResumeToken_WithoutTokenRow_IsIgnored()
    {
        using var fx = new Fixture();
        // 未完整登录（无 token 行）：ResumeToken 缺少归属上下文，忽略写入。
        await fx.Db.SaveResumeTokenAsync("orphan-token");
        Assert.Null(await fx.Db.GetResumeTokenAsync());
    }

    [Fact]
    public async Task PersistLoginSession_ClearsResumeToken_FromPreviousAccount()
    {
        using var fx = new Fixture();
        await fx.SeedTokenRowAsync();
        await fx.Db.SaveResumeTokenAsync("old-account-token");

        // 新账户完整登录（PersistLoginSessionAsync 原子提交）。
        await fx.Db.PersistLoginSessionAsync(
            new AuthToken
            {
                UserId = 2002,
                AccessToken = "access-b",
                RefreshToken = "refresh-b",
                AccessTokenExpires = DateTime.UtcNow.AddHours(1),
                RefreshTokenExpires = DateTime.UtcNow.AddDays(7),
                SessionId = "session-b"
            },
            new LocalUser { UserId = 2002, Username = "user-b" },
            endpoint: null);

        // 残留上一账户的 token 已清零，避免账户切换后一次注定失败的 Resume。
        Assert.Null(await fx.Db.GetResumeTokenAsync());
        var token = await fx.Db.GetTokenAsync();
        Assert.Equal(2002, token!.UserId);
        Assert.Equal("access-b", token.AccessToken);
    }

    [Fact]
    public void ResumeToken_Migration_IsDiscoverable()
    {
        using var fx = new Fixture();
        using var db = fx.Factory.CreateDbContext();

        Assert.Contains(
            "20260904090000_AddResumeTokenColumns",
            db.Database.GetMigrations());
        Assert.NotNull(db.Model
            .FindEntityType(typeof(AuthToken))
            ?.FindProperty(nameof(AuthToken.ResumeToken)));
    }

    /// <summary>真实 DatabaseService + 文件库（与 TokenRefreshSingleFlightTests 同模式）。</summary>
    private sealed class Fixture : IDisposable
    {
        public readonly string DbPath;
        public readonly IDbContextFactory<ClientDbContext> Factory;
        public readonly DatabaseService Db;

        public Fixture()
        {
            DbPath = Path.Combine(Path.GetTempPath(), $"chat_resume_persist_{Guid.NewGuid():N}.db");
            Factory = new SingleFileContextFactory(DbPath);
            Db = new DatabaseService(Factory);
            using var ctx = Factory.CreateDbContext();
            ctx.Database.EnsureCreated();
        }

        public async Task SeedTokenRowAsync()
        {
            await Db.SaveTokenAsync(new AuthToken
            {
                UserId = 1001,
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                AccessTokenExpires = DateTime.UtcNow.AddHours(1),
                RefreshTokenExpires = DateTime.UtcNow.AddDays(7),
                SessionId = "session-1",
                DeviceIdHash = 987_654_321L
            });
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 40; attempt++)
            {
                try { File.Delete(DbPath); return; }
                catch (IOException) { Thread.Sleep(50); }
            }
        }

        private sealed class SingleFileContextFactory(string path) : IDbContextFactory<ClientDbContext>
        {
            public ClientDbContext CreateDbContext() =>
                new(new DbContextOptionsBuilder<ClientDbContext>().UseSqlite($"Data Source={path}").Options);

            public Task<ClientDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
                Task.FromResult(CreateDbContext());
        }
    }
}
