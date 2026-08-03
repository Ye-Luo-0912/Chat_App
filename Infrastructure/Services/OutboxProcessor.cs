using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Events;
using Core.Diagnostics;
using Core.Interfaces;
using Core.Models;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Serialization;
using Serilog;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 后台排空 Outbox 的处理器（事务化 Outbox）。
/// 发送租约模型：
/// - 认领（Claim）：Queued/可重试 Failed → Sending + AttemptId/LeaseUntil，条件更新防并发。
/// - 恢复（Recover）：启动/周期将租约过期（LeaseUntil &lt; now）的陈旧 Sending 回收为 Queued。
/// - 失败（Mark）：分类为可重试/永久，指数退避安排 NextRetryAt；重试次数达上限或永久失败则不再自动重试。
/// - 结束（Ack/Cleanup）：ACK 单事务推进 Sent 并清租约；Sent/Cancelled 定期归档清理。
/// UI 只做事务化入库（OutboxEnqueuedEvent 触发即时排空），全部网络发送均在本处理器。
/// </summary>
public sealed class OutboxProcessor : IDisposable, IMetricsSource
{
    /// <summary>轮询 Outbox 的周期间隔（秒）。</summary>
    private const int DrainIntervalSec = 5;
    /// <summary>单条 Outbox 最大自动重试次数（RetryCount 达到该值后不再自动重试）。</summary>
    private const int MaxRetryCount = 10;
    /// <summary>发送租约时长（分钟）：认领后 Sending 的 LeaseUntil = now + 该时长。</summary>
    private const int LeaseMinutes = 2;
    /// <summary>指数退避基数（秒）。</summary>
    private const int BackoffBaseSec = 30;
    /// <summary>退避上限（秒）。</summary>
    private const int MaxBackoffSec = 15 * 60;
    /// <summary>停止处理器时等待循环退出的超时（秒）。</summary>
    private const int StopTimeoutSec = 2;
    /// <summary>Sent/Cancelled 归档清理的最小间隔（小时）。</summary>
    private const int CleanupIntervalHours = 1;
    /// <summary>Sent/Cancelled 记录保留时长（天）。</summary>
    private const int SentRetentionDays = 7;
    /// <summary>单批认领条数：小批次降低停止时滞留 Sending 的数量（配合停止时释放租约）。</summary>
    private const int BatchSize = 8;

    private readonly IDatabaseService _db;
    private readonly IChatSessionClient _chatSession;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IEventBus _eventBus;
    private CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _drainLock = new(1, 1);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    /// <summary>唤醒信号：新条目入队/鉴权成功时释放，后台循环立即排空（替代每事件 Task.Run）。</summary>
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly IDisposable _enqueuedSubscription;
    private readonly IDisposable _sentSubscription;
    private Task? _loopTask;
    private DateTime _lastCleanupUtc = DateTime.MinValue;
    private bool _disposed;

    // ── 诊断指标 ──
    private long _processedCount;
    private long _transportWrites;
    private long _ackedCount;
    private long _retryableFailures;
    private long _permanentFailures;
    private long _leaseRecoveries;
    private long _claimEmptyRounds;
    private long _leaseReleasedOnStop;
    private long _oldestOutboxAgeMs;
    private readonly LatencyHistogram _sendLatency = new();

    public OutboxProcessor(
        IDatabaseService db,
        IChatSessionClient chatSession,
        ICurrentUserContext currentUserContext,
        IEventBus eventBus)
    {
        _db = db;
        _chatSession = chatSession;
        _currentUserContext = currentUserContext;
        _eventBus = eventBus;

        _chatSession.Authenticated += OnAuthenticated;
        // UI 事务入库后即时触发排空，避免等待下一个轮询周期。
        _enqueuedSubscription = _eventBus.Subscribe<OutboxEnqueuedEvent>(OnOutboxEnqueued);
        // 服务端 ACK 确认计数（acked 指标：区别于仅 Socket 写成功的 transport_writes）。
        _sentSubscription = _eventBus.Subscribe<OutboxStatusChangedEvent>(e =>
        {
            if (e.NewStatus == OutboxStatus.Sent)
                Interlocked.Increment(ref _ackedCount);
        });
    }

    /// <summary>启动后台排空循环。幂等：已在运行则无操作；被 Stop 后可重新 Start（换新 CTS）。</summary>
    public void Start()
    {
        lock (_startLock)
        {
            if (_loopTask is { IsCompleted: false })
                return;
            if (_cts.IsCancellationRequested)
                _cts = new CancellationTokenSource();
            _loopTask = Task.Run(DrainLoopAsync);
        }
    }

    /// <summary>停止后台排空循环并等待退出。幂等。</summary>
    public void Stop()
    {
        lock (_startLock)
        {
            _cts.Cancel();
            try
            {
                _loopTask?.Wait(TimeSpan.FromSeconds(StopTimeoutSec));
            }
            catch
            {
                // 忽略关闭时的等待异常
            }
            _loopTask = null;
        }
    }

    private void OnAuthenticated(object? sender, long userId)
    {
        // 鉴权成功后立即触发一次排空（唤醒信号，不创建每事件任务）
        Wake();
    }

    private void OnOutboxEnqueued(OutboxEnqueuedEvent e)
    {
        Wake();
    }

    /// <summary>释放唤醒信号：后台排空循环立即运行一次（SingleReader 语义由 _drainLock 串行化保证）。</summary>
    private void Wake()
    {
        try
        {
            if (_wakeSignal.CurrentCount == 0)
                _wakeSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // 处理器已释放
        }
    }

    private async Task DrainLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Outbox 排空循环异常");
            }

            // 等待唤醒信号（新条目/鉴权 → 立即排空）或周期轮询兜底。
            try
            {
                await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(DrainIntervalSec), _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DrainOnceAsync()
    {
        await _drainLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_chatSession.IsAuthenticated)
                return;
            if (!_currentUserContext.TryGetUserId(out var userId))
                return;

            var now = DateTime.UtcNow;

            // 1. 租约恢复：回收陈旧 Sending（上次崩溃/断线遗留）。
            try
            {
                var recovered = await _db.RecoverStaleSendingAsync(userId, now).ConfigureAwait(false);
                if (recovered > 0)
                {
                    Log.Information("回收陈旧 Sending Outbox {Count} 条", recovered);
                    Interlocked.Add(ref _leaseRecoveries, recovered);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "回收陈旧 Sending Outbox 失败");
            }

            // 2. 认领待发送条目（原子 Sending + 租约）。
            List<LocalOutboxMessage> claimed;
            try
            {
                claimed = await _db.ClaimPendingOutboxAsync(userId, BatchSize, now, now.AddMinutes(LeaseMinutes), MaxRetryCount)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "认领待发送 Outbox 失败");
                return;
            }

            Interlocked.Add(ref _processedCount, claimed.Count);
            if (claimed.Count == 0)
                Interlocked.Increment(ref _claimEmptyRounds);
            if (claimed.Count > 0)
            {
                // 最旧待发消息年龄（队首 QueuedAt → 本轮 claim 时刻），反映端到端发送积压
                var oldest = claimed.Min(c => c.QueuedAt);
                Interlocked.Exchange(ref _oldestOutboxAgeMs, (long)(now - oldest).TotalMilliseconds);
            }

            // 3. 有限并发发送（MaxConcurrentSends 上限，含失败/取消/停止释放语义）。
            // Sending 事件按批次顺序统一发布；停止时仅释放"尚未开始"条目的租约
            //（在途条目保持 Sending，等待租约过期或 ack）。
            await SendBatchAsync(userId, claimed).ConfigureAwait(false);

            // 4. 归档清理（节流）：删除超过保留期的 Sent/Cancelled。
            if (now - _lastCleanupUtc >= TimeSpan.FromHours(CleanupIntervalHours))
            {
                _lastCleanupUtc = now;
                try
                {
                    await _db.CleanupOutboxAsync(userId, now.AddDays(-SentRetentionDays)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "清理 Outbox 归档失败");
                }
            }
        }
        finally
        {
            _drainLock.Release();
        }
    }

    /// <summary>发送批次最大并发会话组数（全局有界并发；组内同一会话严格 FIFO）。</summary>
    private const int MaxConcurrentSends = 2;

    /// <summary>
    /// 有界并发发送一个批次：按会话分组（PerConversation FIFO：组内保持 claim 顺序串行发送，
    /// 不同会话组间并发，Global bounded concurrency）；Sending 事件按批次顺序发布；
    /// 停止/取消时仅释放"尚未开始"条目的租约（在途条目保持 Sending 等租约/ack）。
    /// </summary>
    private async Task SendBatchAsync(long userId, List<LocalOutboxMessage> claimed)
    {
        if (claimed.Count == 0)
            return;

        // 按批次顺序发布 Sending（UI 顺序一致）
        foreach (var entry in claimed)
            _eventBus.Publish(new OutboxStatusChangedEvent(entry.ClientMessageId, OutboxStatus.Sending, null));

        // 尚未开始（未获信号量即被取消）的条目：停止时释放租约
        var notStarted = new ConcurrentDictionary<string, LocalOutboxMessage>();
        foreach (var entry in claimed)
            notStarted[entry.ClientMessageId] = entry;

        // 按会话分组：组内保持 claim 顺序（FIFO），组间并行（全局有界并发）
        var groups = new List<List<LocalOutboxMessage>>();
        foreach (var entry in claimed)
        {
            var key = entry.ConversationId ?? string.Empty;
            var group = groups.FirstOrDefault(g => string.Equals(g[0].ConversationId, key, StringComparison.Ordinal));
            if (group is null)
            {
                group = [];
                groups.Add(group);
            }
            group.Add(entry);
        }

        var gate = new SemaphoreSlim(MaxConcurrentSends);
        var tasks = new List<Task>(groups.Count);
        foreach (var group in groups)
        {
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (var entry in group) // 同一会话严格顺序
                    {
                        if (_cts.IsCancellationRequested)
                            return; // 尚未开始：留在 notStarted，统一释放租约

                        notStarted.TryRemove(entry.ClientMessageId, out _); // 已开始：在途

                        var sw = Stopwatch.StartNew();
                        try
                        {
                            await _chatSession.SendChatMessageAsync(
                                entry.TargetUserId ?? 0,
                                entry.Content,
                                AttachmentJson.DeserializeIds(entry.AttachmentIdsJson),
                                entry.ReplyToMessageId,
                                entry.ReplyToSenderUserId,
                                entry.ReplyToPreview,
                                entry.ForwardedFromMessageId,
                                entry.ForwardedFromSenderUserId,
                                entry.ForwardedFromPreview,
                                entry.ClientMessageId,
                                conversationId: entry.ConversationId,
                                ct: _cts.Token).ConfigureAwait(false);

                            // 已上行成功（transport 写入）：保持 Sending，等待 MessageAck 推进 Sent（acked 计数）。
                            Interlocked.Increment(ref _transportWrites);
                        }
                        catch (OperationCanceledException)
                        {
                            // 处理器关闭：在途条目保持 Sending（租约过期后由下轮恢复）；不释放（防重复发送）。
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Outbox 发送失败 ClientMessageId={ClientMessageId}", entry.ClientMessageId);
                            await MarkFailedAsync(userId, entry, ex).ConfigureAwait(false);
                        }
                        finally
                        {
                            sw.Stop();
                            _sendLatency.Add(sw.Elapsed);
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // 停止/取消后：释放"尚未开始"条目的租约（Sending → Queued），重启后立即可重新发送
        if (notStarted.Count > 0)
        {
            var attemptId = claimed[0].AttemptId ?? string.Empty;
            try
            {
                var released = await _db.ReleaseOutboxLeasesAsync(
                        userId, notStarted.Keys.ToList(), attemptId)
                    .ConfigureAwait(false);
                if (released > 0)
                    Interlocked.Add(ref _leaseReleasedOnStop, released);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "释放未开始发送的 Outbox 租约失败 Count={Count}", notStarted.Count);
            }
        }
    }

    private async Task MarkFailedAsync(long userId, LocalOutboxMessage entry, Exception ex)
    {
        var (kind, errorCode) = ClassifyFailure(ex);
        DateTime? nextRetryAt = kind == OutboxFailureKind.Retryable ? NextRetryAt(entry.RetryCount) : null;
        try
        {
            var updated = await _db.MarkOutboxFailureAsync(
                    userId, entry.ClientMessageId, errorCode, ex.Message, kind, nextRetryAt)
                .ConfigureAwait(false);
            if (!updated)
                return;
        }
        catch (Exception ex2)
        {
            Log.Error(ex2, "更新 Outbox 为 Failed 失败 ClientMessageId={ClientMessageId}", entry.ClientMessageId);
            return;
        }

        if (kind == OutboxFailureKind.Permanent)
        {
            Interlocked.Increment(ref _permanentFailures);
            Log.Warning("Outbox 永久失败（不再自动重试）ClientMessageId={ClientMessageId}", entry.ClientMessageId);
        }
        else
        {
            Interlocked.Increment(ref _retryableFailures);
        }
        _eventBus.Publish(new OutboxStatusChangedEvent(
            entry.ClientMessageId, OutboxStatus.Failed, null, $"{errorCode}: {ex.Message}"));
    }

    /// <summary>失败分类：参数校验为永久失败，其余（网络/超时/未鉴权）可重试。</summary>
    private static (OutboxFailureKind Kind, string ErrorCode) ClassifyFailure(Exception ex) => ex switch
    {
        ArgumentException => (OutboxFailureKind.Permanent, "INVALID_ARGUMENT"),
        InvalidOperationException => (OutboxFailureKind.Retryable, "NOT_CONNECTED"),
        TimeoutException => (OutboxFailureKind.Retryable, "TIMEOUT"),
        IOException => (OutboxFailureKind.Retryable, "IO_ERROR"),
        _ => (OutboxFailureKind.Retryable, "UNKNOWN")
    };

    /// <summary>指数退避 + jitter：30s * 2^RetryCount，上限 15 分钟。</summary>
    private static DateTime NextRetryAt(int retryCount)
    {
        var seconds = Math.Min(BackoffBaseSec * (1 << Math.Min(retryCount, 10)), MaxBackoffSec);
        return DateTime.UtcNow.AddSeconds(seconds + Random.Shared.Next(0, 5000) / 1000.0);
    }

    public string Name => "outbox";

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        ["claimed"] = Volatile.Read(ref _processedCount),
        ["transport_writes"] = Volatile.Read(ref _transportWrites),
        ["acked"] = Volatile.Read(ref _ackedCount),
        ["retryable_failures"] = Volatile.Read(ref _retryableFailures),
        ["permanent_failures"] = Volatile.Read(ref _permanentFailures),
        ["lease_recoveries"] = Volatile.Read(ref _leaseRecoveries),
        ["lease_released_on_stop"] = Volatile.Read(ref _leaseReleasedOnStop),
        ["claim_empty_rounds"] = Volatile.Read(ref _claimEmptyRounds),
        // 最旧待发消息年龄（ms）：0 表示当前无积压
        ["oldest_outbox_age_ms"] = Volatile.Read(ref _oldestOutboxAgeMs)
    };

    public IReadOnlyDictionary<string, HistogramSnapshot> Histograms =>
        new Dictionary<string, HistogramSnapshot> { ["send_latency_ms"] = _sendLatency.Snapshot() };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _chatSession.Authenticated -= OnAuthenticated;
        _enqueuedSubscription.Dispose();
        _sentSubscription.Dispose();
        Stop();
        _cts.Dispose();
        _drainLock.Dispose();
        _startLock.Dispose();
        _wakeSignal.Dispose();
    }
}

