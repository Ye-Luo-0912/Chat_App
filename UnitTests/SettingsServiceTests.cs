using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Services;
using Core.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace UnitTests;

/// <summary>
/// 设备与安全设置服务测试：默认值合并、写入/读取往返、账户隔离、
/// 非法持久化值规整回默认、只落盘非默认行。
/// </summary>
public class SettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly SettingsService _service;

    private const long UserA = 1001;
    private const long UserB = 2002;

    public SettingsServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new DbContextFactoryStub(_connection);
        _service = new SettingsService(_factory);

        using var ctx = _factory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    /// <summary>未存储任何设置时返回默认值。</summary>
    [Fact]
    public async Task Get_When_Nothing_Stored_Returns_Defaults()
    {
        var s = await _service.GetAsync(UserA);

        Assert.True(s.NotificationPreviewEnabled);
        Assert.True(s.AutoDownloadAttachments);
        Assert.False(s.AutoLockOnIdle);
        Assert.Equal(ClientSettings.DefaultAutoLockIdleMinutes, s.AutoLockIdleMinutes);
        // 无障碍默认值
        Assert.Equal(Core.Accessibility.AccessibilityFontSize.Standard, s.FontSize);
        Assert.False(s.ReduceMotion);
        Assert.False(s.HighContrast);
        // 语音输出设备默认（VOICE-MSG-3）：未设置 = 系统默认（null）。
        Assert.Null(s.AudioOutputDeviceId);
    }

    /// <summary>Set 后 Get 往返一致，且 4 个键均落盘（键集固定，写入幂等）。</summary>
    [Fact]
    public async Task Set_Then_Get_RoundTrips_And_All_Keys_Persisted()
    {
        var settings = new ClientSettings
        {
            NotificationPreviewEnabled = false,
            AutoDownloadAttachments = true,
            AutoLockOnIdle = true,
            AutoLockIdleMinutes = 15
        };
        await _service.SetAsync(UserA, settings);

        var s = await _service.GetAsync(UserA);
        Assert.False(s.NotificationPreviewEnabled);
        Assert.True(s.AutoDownloadAttachments);
        Assert.True(s.AutoLockOnIdle);
        Assert.Equal(15, s.AutoLockIdleMinutes);

        using var ctx = _factory.CreateDbContext();
        var rows = await ctx.Settings.Where(x => x.OwnerUserId == UserA).ToListAsync();
        Assert.Equal(8, rows.Count);
        Assert.Contains(rows, r => r.Key == "auto_download_attachments");
    }

    /// <summary>账户隔离：A 的设置不影响 B，B 仍返回默认值。</summary>
    [Fact]
    public async Task Settings_Are_Isolated_Per_Owner()
    {
        var settings = new ClientSettings
        {
            NotificationPreviewEnabled = false,
            AutoLockOnIdle = true,
            AutoLockIdleMinutes = 30
        };
        await _service.SetAsync(UserA, settings);

        var b = await _service.GetAsync(UserB);
        Assert.True(b.NotificationPreviewEnabled);
        Assert.False(b.AutoLockOnIdle);
        Assert.Equal(ClientSettings.DefaultAutoLockIdleMinutes, b.AutoLockIdleMinutes);
    }

    /// <summary>非法持久化值（越界 idle 分钟）规整回默认。</summary>
    [Fact]
    public async Task Invalid_Persisted_Value_Normalized_To_Default()
    {
        // 直接写入越界行，模拟遗留/损坏数据
        using (var ctx = _factory.CreateDbContext())
        {
            ctx.Settings.Add(new LocalSetting
            {
                OwnerUserId = UserA,
                Key = "auto_lock_idle_minutes",
                Value = "9999",
                UpdatedAtMs = 1
            });
            await ctx.SaveChangesAsync();
        }

        var s = await _service.GetAsync(UserA);
        Assert.Equal(ClientSettings.DefaultAutoLockIdleMinutes, s.AutoLockIdleMinutes);
    }

    /// <summary>UpdateAsync 在读取基础上变更后写回。</summary>
    [Fact]
    public async Task UpdateAsync_Mutates_And_Persists()
    {
        await _service.UpdateAsync(UserA, s => s.AutoLockOnIdle = true);

        var s = await _service.GetAsync(UserA);
        Assert.True(s.AutoLockOnIdle);
    }

    /// <summary>重复设置相同值不新增行（键集固定，幂等更新）。</summary>
    [Fact]
    public async Task Set_Same_Value_Twice_Is_Idempotent()
    {
        var settings = new ClientSettings { AutoLockOnIdle = true, AutoLockIdleMinutes = 10 };
        await _service.SetAsync(UserA, settings);
        await _service.SetAsync(UserA, settings);

        using var ctx = _factory.CreateDbContext();
        var rows = await ctx.Settings.Where(x => x.OwnerUserId == UserA).ToListAsync();
        Assert.Equal(8, rows.Count);
    }

    /// <summary>无障碍设置往返一致，且新键落盘。</summary>
    [Fact]
    public async Task Accessibility_Settings_RoundTrip_And_Persisted()
    {
        var settings = new ClientSettings
        {
            FontSize = Core.Accessibility.AccessibilityFontSize.ExtraLarge,
            ReduceMotion = true,
            HighContrast = true
        };
        await _service.SetAsync(UserA, settings);

        var s = await _service.GetAsync(UserA);
        Assert.Equal(Core.Accessibility.AccessibilityFontSize.ExtraLarge, s.FontSize);
        Assert.True(s.ReduceMotion);
        Assert.True(s.HighContrast);

        using var ctx = _factory.CreateDbContext();
        var rows = await ctx.Settings.Where(x => x.OwnerUserId == UserA).ToListAsync();
        Assert.Contains(rows, r => r.Key == "a11y_font_size" && r.Value == "2");
        Assert.Contains(rows, r => r.Key == "a11y_reduce_motion" && r.Value == "true");
        Assert.Contains(rows, r => r.Key == "a11y_high_contrast" && r.Value == "true");
    }

    /// <summary>非法持久化字体档位规整回标准档。</summary>
    [Fact]
    public async Task Invalid_FontSize_Persisted_Normalized_To_Standard()
    {
        using (var ctx = _factory.CreateDbContext())
        {
            ctx.Settings.Add(new LocalSetting
            {
                OwnerUserId = UserA,
                Key = "a11y_font_size",
                Value = "99",
                UpdatedAtMs = 1
            });
            await ctx.SaveChangesAsync();
        }

        var s = await _service.GetAsync(UserA);
        Assert.Equal(Core.Accessibility.AccessibilityFontSize.Standard, s.FontSize);
    }

    // ---- 语音输出设备偏好（VOICE-MSG-3）----

    /// <summary>输出设备 Id 设置后读取往返一致，且键落盘。</summary>
    [Fact]
    public async Task AudioOutputDeviceId_RoundTrips_And_Persists()
    {
        await _service.UpdateAsync(UserA, s => s.AudioOutputDeviceId = "2");

        var s = await _service.GetAsync(UserA);
        Assert.Equal("2", s.AudioOutputDeviceId);

        using var ctx = _factory.CreateDbContext();
        var rows = await ctx.Settings.Where(x => x.OwnerUserId == UserA).ToListAsync();
        Assert.Contains(rows, r => r.Key == "audio_output_device_id" && r.Value == "2");
    }

    /// <summary>空白设备 Id 规整为 null（系统默认）；空白持久化值读取后同样回默认。</summary>
    [Fact]
    public async Task AudioOutputDeviceId_Blank_Normalized_To_Null_SystemDefault()
    {
        await _service.UpdateAsync(UserA, s => s.AudioOutputDeviceId = "   ");
        var s = await _service.GetAsync(UserA);
        Assert.Null(s.AudioOutputDeviceId);

        // 直接写入空白行（模拟遗留数据）→ 读取回退系统默认。
        using (var ctx = _factory.CreateDbContext())
        {
            ctx.Settings.Add(new LocalSetting
            {
                OwnerUserId = UserB,
                Key = "audio_output_device_id",
                Value = " ",
                UpdatedAtMs = 1
            });
            await ctx.SaveChangesAsync();
        }
        var b = await _service.GetAsync(UserB);
        Assert.Null(b.AudioOutputDeviceId);
    }

    /// <summary>输出设备偏好按账户隔离。</summary>
    [Fact]
    public async Task AudioOutputDeviceId_Is_Isolated_Per_Owner()
    {
        await _service.UpdateAsync(UserA, s => s.AudioOutputDeviceId = "1");
        var b = await _service.GetAsync(UserB);
        Assert.Null(b.AudioOutputDeviceId);
    }

    private sealed class DbContextFactoryStub(SqliteConnection connection) : IDbContextFactory<ClientDbContext>
    {
        public ClientDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ClientDbContext>()
                .UseSqlite(connection)
                .Options;
            return new ClientDbContext(options);
        }

        public Task<ClientDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}