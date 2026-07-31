using System;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Models.Context;

namespace Chat_App.Infrastructure.Persistence;

/// <summary>
/// 数据库单写入队列（P0-数据库优化）。
/// 将所有写入操作路由到单个消费者串行执行，消除 SQLite 并发写入导致的 SQLITE_BUSY 冲突。
/// 消费者可批量合并多个操作到单个事务，减少 SaveChangesAsync 调用次数。
/// </summary>
public interface IDatabaseWriteQueue
{
    /// <summary>将写入操作排入队列并等待完成（有返回值）。</summary>
    Task<T> EnqueueAsync<T>(
        Func<ClientDbContext, CancellationToken, Task<T>> writeOperation,
        CancellationToken ct = default);

    /// <summary>将写入操作排入队列并等待完成（无返回值）。</summary>
    Task EnqueueAsync(
        Func<ClientDbContext, CancellationToken, Task> writeOperation,
        CancellationToken ct = default);
}
