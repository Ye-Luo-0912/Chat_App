using Microsoft.Data.Sqlite;

namespace Chat_App.Infrastructure.Persistence;

/// <summary>
/// 集中管理 SQLite 数据库路径与连接字符串：
/// - 路径固定在用户应用数据目录，避免只读安装目录无法持久化。
/// - 连接字符串只使用 Microsoft.Data.Sqlite 官方支持的关键字，
/// 不再使用 Journal Mode / Synchronous / Busy Timeout 等非官方项。
/// - WAL / NORMAL / busy_timeout / foreign_keys 由 ClientDbContext.OnConfiguring 通过 PRAGMA 执行。
/// </summary>
public static class DbPathProvider
{
    public static string DbPath { get; } = BuildDbPath();

    /// <summary>用户应用数据目录（LocalApplicationData/ChatApp/Data），供 DB、日志、device.id 等复用。</summary>
    public static string GetAppDataDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatApp",
            "Data");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string BuildConnectionString()
    {
        var dbPath = Path.Combine(GetAppDataDir(), "ChatApp.db");

        // 仅使用 Microsoft.Data.Sqlite 官方关键字
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default, // 不使用 Shared：官方不建议 Cache=Shared + WAL 并存
            ForeignKeys = true,
            DefaultTimeout = 5, // 秒，对应 busy_timeout 5000ms
            Pooling = true
        };
        return builder.ConnectionString;
    }

    private static string BuildDbPath()
        => Path.Combine(GetAppDataDir(), "ChatApp.db");
}
