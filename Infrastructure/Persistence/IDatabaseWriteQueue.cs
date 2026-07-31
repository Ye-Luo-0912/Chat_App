using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Infrastructure.Persistence;

/// <summary>
/// 数据库单写入队列（P0-数据库优化）。
/// 将所有写入操作路由到单个消费者串行执行，消除 SQLite 并发写入导致的 SQLITE_BUSY 冲突。
/// 委托为自包含操作：内部自行创建 DbContext、调用 SaveChangesAsync 并处理幂等冲突，
/// 队列只负责串行化调度，不共享 DbContext（避免读-改-写跨操作竞态）。
/// </summary>
public interface IDatabaseWriteQueue
{
    /// <summary>将自包含写入操作排入队列并等待完成（无返回值）。</summary>
    Task EnqueueAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default);

    /// <summary>将自包含写入操作排入队列并等待完成（有返回值）。</summary>
    Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
}
