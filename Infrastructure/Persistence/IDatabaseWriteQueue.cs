using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Infrastructure.Persistence;

/// <summary>
/// 数据库单写入队列。
/// 将所有写入操作路由到单个消费者串行执行，消除 SQLite 并发写入导致的 SQLITE_BUSY 冲突。
/// 委托为自包含操作：内部自行创建 DbContext、调用 SaveChangesAsync 并处理幂等冲突，
/// 队列只负责串行化调度，不共享 DbContext（避免读-改-写跨操作竞态）。
/// 真正的大批量写入（如历史同步）应在 DatabaseService 层以单 DbContext+单事务的
/// 批量方法完成（如 ApplyHistoryBatchAsync），以减少队列往返。
///
/// 取消语义（明确）：
/// - 入队前取消（ct 在等待空位时取消）：操作不会入队、不会执行。
/// - 入队成功后取消（ct 在等待结果时取消）：操作已被消费者 claim，必须完成，
///   调用方只能放弃等待；DB 副作用必然发生。
/// - 队列关闭：已入队的操作仍会执行完毕，消费者随后退出。
/// </summary>
public interface IDatabaseWriteQueue
{
    /// <summary>将自包含写入操作排入队列并等待完成（无返回值）。</summary>
    Task EnqueueAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default);

    /// <summary>将自包含写入操作排入队列并等待完成（有返回值）。</summary>
    Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
}
