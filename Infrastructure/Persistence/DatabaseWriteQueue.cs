using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Core.Diagnostics;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Infrastructure.Persistence;

/// <summary>
/// 数据库单写入队列实现。
/// 使用有界 Channel 串行化所有写入操作，消除 SQLite WAL 模式下的 SQLITE_BUSY 并发冲突。
/// 委托为自包含操作：内部自行管理 DbContext 生命周期与 SaveChangesAsync（含幂等冲突处理），
/// 队列只负责单消费者串行调度。不跨操作共享 DbContext，避免读-改-写竞态与批处理回滚污染。
///
/// 取消语义：
/// - 入队等待空位时 ct 取消：操作不入队、不执行（WriteAsync 抛 OperationCanceledException）。
/// - 入队成功后调用方取消等待：操作已被消费者 claim，仍会执行完毕（CancellationToken.None），
///   调用方只能放弃等待；DB 副作用必然发生。
/// - 队列关闭：先完成通道写入，已入队操作仍执行完毕（限时等待），消费者随后退出。
/// </summary>
public sealed class DatabaseWriteQueue : IDatabaseWriteQueue, IAsyncDisposable, IMetricsSource
{
    private const int QueueCapacity = 1024;

    private readonly Channel<WriteOperation> _channel;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _cts = new();

    // ── 诊断指标 ──
    private long _processedCount;
    private long _failedCount;
    private long _inFlightCount;
    private long _batchSize;
    private readonly LatencyHistogram _queueWait = new();
    private readonly LatencyHistogram _execution = new();
    private readonly LatencyHistogram _endToEnd = new();

    /// <summary>写入操作封装：携带唯一 Id 便于诊断，自包含执行并通过 TCS 通知完成。</summary>
    private abstract class WriteOperation
    {
        public Guid Id { get; } = Guid.NewGuid();
        public long EnqueuedAtTicks { get; } = Stopwatch.GetTimestamp();
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
        _channel = Channel.CreateBounded<WriteOperation>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _consumerTask = Task.Run(ConsumeLoopAsync);
    }

    public async Task EnqueueAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var op = new WriteOperationVoid(operation, tcs);
        // 真等待背压：队列满时等待空位；入队前 ct 取消则操作不执行。
        await _channel.Writer.WriteAsync(op, ct).ConfigureAwait(false);
        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var op = new WriteOperation<T>(operation, tcs);
        await _channel.Writer.WriteAsync(op, ct).ConfigureAwait(false);
        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 消费者循环：单消费者逐个执行写入操作，保证串行化。
    /// 已入队的操作必须完成（执行不随队列关闭/调用方取消而中断），
    /// 单个操作失败不影响队列继续运行（按操作隔离）。
    /// </summary>
    private async Task ConsumeLoopAsync()
    {
        await foreach (var op in _channel.Reader.ReadAllAsync(_cts.Token))
        {
            // 排队等待 = 入队时刻 → 执行开始（背压下等待空位/前序操作的时间）
            var waitElapsed = TimeSpan.FromTicks(Stopwatch.GetTimestamp() - op.EnqueuedAtTicks);
            var sw = Stopwatch.StartNew();
            Interlocked.Increment(ref _inFlightCount);
            try
            {
                await op.ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
                op.SetResult();
                Interlocked.Increment(ref _processedCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "写入队列操作失败 OperationId={OperationId}", op.Id);
                op.SetException(ex);
                Interlocked.Increment(ref _failedCount);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightCount);
                sw.Stop();
                // 语义拆分：queue_wait（背压排队） / execution（DB 操作） / end_to_end（全链路）
                _queueWait.Add(waitElapsed);
                _execution.Add(sw.Elapsed);
                _endToEnd.Add(sw.Elapsed + waitElapsed);
            }
        }
    }

    public string Name => "db_write_queue";

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        ["queued"] = _channel.Reader.Count,
        ["in_flight"] = Volatile.Read(ref _inFlightCount),
        ["processed"] = Volatile.Read(ref _processedCount),
        ["failed"] = Volatile.Read(ref _failedCount),
        ["capacity"] = QueueCapacity,
        // 单消费者串行执行器：领域批处理落地前 batch_size 恒为 1（标记未来批处理粒度）
        ["batch_size"] = Volatile.Read(ref _batchSize) == 0 ? 1 : Volatile.Read(ref _batchSize)
    };

    public IReadOnlyDictionary<string, HistogramSnapshot> Histograms =>
        new Dictionary<string, HistogramSnapshot>
        {
            ["queue_wait_ms"] = _queueWait.Snapshot(),
            ["execution_ms"] = _execution.Snapshot(),
            ["end_to_end_ms"] = _endToEnd.Snapshot()
        };

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            // 已入队操作必须完成：限时等待消费者耗尽队列，超时后强停兜底。
            await _consumerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            _cts.Cancel();
            try { await _consumerTask.ConfigureAwait(false); } catch { /* 消费者异常忽略 */ }
        }
        _cts.Dispose();
    }
}
