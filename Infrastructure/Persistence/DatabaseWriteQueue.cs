using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Chat_App.Infrastructure.Persistence;

/// <summary>
/// 数据库单写入队列实现。
/// 使用有界 Channel 串行化所有写入操作，消除 SQLite WAL 模式下的 SQLITE_BUSY 并发冲突。
/// 委托为自包含操作：内部自行管理 DbContext 生命周期与 SaveChangesAsync（含幂等冲突处理），
/// 队列只负责单消费者串行调度。不跨操作共享 DbContext，避免读-改-写竞态与批处理回滚污染。
/// </summary>
public sealed class DatabaseWriteQueue : IDatabaseWriteQueue, IAsyncDisposable
{
    private readonly Channel<WriteOperation> _channel;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>写入操作封装：自包含执行并通过 TCS 通知完成。</summary>
    private abstract class WriteOperation
    {
        public abstract Task ExecuteAsync(CancellationToken ct);
        public abstract void SetResult();
        public abstract void SetException(Exception ex);
    }

    private sealed class WriteOperation<T> : WriteOperation
    {
        private readonly Func<CancellationToken, Task<T>> _func;
        private readonly TaskCompletionSource<T> _tcs;
        private T? _result;

        public WriteOperation(Func<CancellationToken, Task<T>> func, TaskCompletionSource<T> tcs)
        {
            _func = func;
            _tcs = tcs;
        }

        public override async Task ExecuteAsync(CancellationToken ct)
        {
            _result = await _func(ct);
        }

        public override void SetResult() => _tcs.SetResult(_result!);
        public override void SetException(Exception ex) => _tcs.SetException(ex);
    }

    private sealed class WriteOperationVoid : WriteOperation
    {
        private readonly Func<CancellationToken, Task> _func;
        private readonly TaskCompletionSource _tcs;

        public WriteOperationVoid(Func<CancellationToken, Task> func, TaskCompletionSource tcs)
        {
            _func = func;
            _tcs = tcs;
        }

        public override Task ExecuteAsync(CancellationToken ct) => _func(ct);
        public override void SetResult() => _tcs.SetResult();
        public override void SetException(Exception ex) => _tcs.SetException(ex);
    }

    public DatabaseWriteQueue()
    {
        _channel = Channel.CreateBounded<WriteOperation>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _consumerTask = Task.Run(ConsumeLoopAsync);
    }

    public Task EnqueueAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var op = new WriteOperationVoid(operation, tcs);
        if (!_channel.Writer.TryWrite(op))
            tcs.SetException(new InvalidOperationException("写入队列已关闭"));
        return tcs.Task.WaitAsync(ct);
    }

    public Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var op = new WriteOperation<T>(operation, tcs);
        if (!_channel.Writer.TryWrite(op))
            tcs.SetException(new InvalidOperationException("写入队列已关闭"));
        return tcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// 消费者循环：单消费者逐个执行写入操作，保证串行化。
    /// 单个操作失败不影响队列继续运行（按操作隔离）。
    /// </summary>
    private async Task ConsumeLoopAsync()
    {
        await foreach (var op in _channel.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                await op.ExecuteAsync(_cts.Token);
                op.SetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"写入队列操作失败: {ex.Message}");
                op.SetException(ex);
            }
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
