using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog;

namespace Chat_App.Infrastructure.Persistence;

/// <summary>
/// SQLite PRAGMA 拦截器：作为 EF Core DbConnectionInterceptor 注册（AddPooledDbContextFactory 的
/// options.AddInterceptors），在每次连接打开时（ConnectionOpened/ConnectionOpenedAsync）统一执行
/// 性能与正确性相关的 PRAGMA。
///
/// 为什么需要（区别于连接字符串）：
/// - journal_mode=WAL 与 synchronous=NORMAL 虽是持久设置（写入文件头），但只在启动迁移时显式确认一次；
///   对每次打开的连接再执行一次可自愈异常状态下被改回的文件头（如外部工具恢复/拷回旧库文件）。
/// - foreign_keys / busy_timeout 由连接字符串 ForeignKeys=true / DefaultTimeout=5 保证，
///   此处重复执行不产生额外锁竞争（幂等），作为双保险。
///
/// 连接池语义：Microsoft.Data.Sqlite 池化连接每次真实打开都会触发 EF 拦截器，
/// 因此该拦截器保证"每个物理连接打开时执行"。
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string PragmaScript = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=NORMAL;
        PRAGMA foreign_keys=ON;
        PRAGMA busy_timeout=5000;
        """;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ExecutePragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ExecutePragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 连接打开后执行。PRAGMA 是每次打开的前置不变式，失败视为环境异常，不静默继续。
    /// </summary>
    private static void ExecutePragmas(DbConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = PragmaScript;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接打开后执行 PRAGMA 失败（ConnectionString={ConnectionString}）",
                Sanitize(connection.ConnectionString));
            throw;
        }
    }

    private static async Task ExecutePragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = PragmaScript;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接打开后执行 PRAGMA 失败（ConnectionString={ConnectionString}）",
                Sanitize(connection.ConnectionString));
            throw;
        }
    }

    /// <summary>避免日志输出完整连接字符串（含路径与配置），仅保留 Data Source。</summary>
    private static string Sanitize(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return "(empty)";
        foreach (var part in connectionString.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                return $"{kv[0].Trim()}={kv[1].Trim()}";
        }
        return "(redacted)";
    }
}
