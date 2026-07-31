using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Infrastructure.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace Chat_App.Infrastructure.Persistence;

/// <summary>
/// 数据库单写入队列实现（P0-数据库优化）。
/// 使用有界 Channel 串行化所有写入操作，消除 SQLite WAL 模式下的 SQLITE_BUSY 并发冲突。
/// 消费者统一管理 DbContext 生命周期和 SaveChangesAsync，委托只负责实体变更操作。
/// 批量合并：无返回值的写入操作可合并到同一 DbContext + 事务，一次 SaveChangesAsync。
/// </summary>
public sealed class DatabaseWriteQueue : IDatabaseWriteQueue, IAsyncDisposable
{
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly Channel<WriteOperation> _channel;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>批量合并上限：单次事务最多合并的操作数，避免长事务阻塞其他写入。</summary>
    private const int MaxBatchSize = 16;

    /// <summary>写入操作封装：委托只做实体变更，不创建 DbContext 也不调用 SaveChangesAsync。</summary>
    private abstract class WriteOperation
    {
        public abstract Task ExecuteAsync(ClientDbContext db, CancellationToken ct);
        public abstract void SetResult();
        public abstract void SetException(Exception ex);
    }

    private sealed class WriteOperation<T> : WriteOperation
    {
        private readonly Func<ClientDbContext, CancellationToken, Task<T>> _func;
        private readonly TaskCompletionSource<T> _tcs;
        private T? _result;

        public WriteOperation(Func<ClientDbContext, CancellationToken, Task<T>> func, TaskCompletionSource<T> tcs)
        {
            _func = func;
            _tcs = tcs;
        }

        public override async Task ExecuteAsync(ClientDbContext db, CancellationToken ct)
        {
            _result = await _func(db, ct);
        }

        public override void SetResult() => _tcs.SetResult(_result!);
        public override void SetException(Exception ex) => _tcs.SetException(ex);
    }

    private sealed class WriteOperationVoid : WriteOperation
    {
        private readonly Func<ClientDbContext, CancellationToken, Task> _func;
        private readonly TaskCompletionSource _tcs;

        public WriteOperationVoid(Func<ClientDbContext, CancellationToken, Task> func, TaskCompletionSource tcs)
        {
            _func = func;
            _tcs = tcs;
        }

        public override Task ExecuteAsync(ClientDbContext db, CancellationToken ct) => _func(db, ct);
        public override void SetResult() => _tcs.SetResult();
        public override void SetException(Exception ex) => _tcs.SetException(ex);
    }

    public DatabaseWriteQueue(IDbContextFactory<ClientDbContext> factory)
    {
        _factory = factory;
        _channel = Channel.CreateBounded<WriteOperation>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _consumerTask = Task.Run(ConsumeLoopAsync);
    }

    public Task<T> EnqueueAsync<T>(
        Func<ClientDbContext, CancellationToken, Task<T>> writeOperation,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var op = new WriteOperation<T>(writeOperation, tcs);
        if (!_channel.Writer.TryWrite(op))
            tcs.SetException(new InvalidOperationException("写入队列已关闭"));
        return tcs.Task.WaitAsync(ct);
    }

    public Task EnqueueAsync(
        Func<ClientDbContext, CancellationToken, Task> writeOperation,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var op = new WriteOperationVoid(writeOperation, tcs);
        if (!_channel.Writer.TryWrite(op))
            tcs.SetException(new InvalidOperationException("写入队列已关闭"));
        return tcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// 消费者循环：串行执行写入操作，尝试批量合并无返回值操作到同一事务。
    /// 单个操作失败不影响队列继续运行（按帧隔离）。
    /// </summary>
    private async Task ConsumeLoopAsync()
    {
        var batch = new List<WriteOperation>(MaxBatchSize);

        await foreach (var firstOp in _channel.Reader.ReadAllAsync(_cts.Token))
        {
            batch.Clear();
            batch.Add(firstOp);

            // 非阻塞地收集后续待执行操作，合并到同一批次
            while (batch.Count < MaxBatchSize && _channel.Reader.TryRead(out var nextOp))
                batch.Add(nextOp);

            await ExecuteBatchAsync(batch);
        }
    }

    /// <summary>
    /// 执行一批写入操作：在单个 DbContext 中执行所有操作，一次 SaveChangesAsync。
    /// 单个操作失败时回滚事务并通知所有操作失败。
    /// </summary>
    private async Task ExecuteBatchAsync(List<WriteOperation> batch)
    {
        await using var db = await _factory.CreateDbContextAsync(_cts.Token);
        try
        {
            // 批量场景用事务，单操作也走同一路径（简化代码）
            await using var transaction = await db.Database.BeginTransactionAsync(_cts.Token);

            foreach (var op in batch)
                await op.ExecuteAsync(db, _cts.Token);

            await db.SaveChangesAsync(_cts.Token);
            await transaction.CommitAsync(_cts.Token);

            foreach (var op in batch)
                op.SetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"写入队列批量操作失败（{batch.Count} 个操作回滚）: {ex.Message}");
            foreach (var op in batch)
                op.SetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _consumerTask.ConfigureAwait(false);
        }
        catch
        {
            // 消费者循环异常不影响释放
        }
        _cts.Cancel();
        _cts.Dispose();
    }
}
