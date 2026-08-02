using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Events;
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
public sealed class OutboxProcessor : IDisposable
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
    private const int BatchSize = 50;

    private readonly IDatabaseService _db;
    private readonly IChatSessionClient _chatSession;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IEventBus _eventBus;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _drainLock = new(1, 1);
    private readonly IDisposable _enqueuedSubscription;
    private Task? _loopTask;
    private DateTime _lastCleanupUtc = DateTime.MinValue;
    private bool _disposed;

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
    }

    /// <summary>启动后台排空循环。</summary>
    public void Start()
    {
        _loopTask = Task.Run(DrainLoopAsync);
    }

    private void OnAuthenticated(object? sender, long userId)
    {
        // 鉴权成功后立即触发一次排空（与后台循环通过信号量串行化）
        _ = Task.Run(DrainOnceAsync);
    }

    private void OnOutboxEnqueued(OutboxEnqueuedEvent e)
    {
        _ = Task.Run(DrainOnceAsync);
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

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(DrainIntervalSec), _cts.Token).ConfigureAwait(false);
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
                    Log.Information("回收陈旧 Sending Outbox {Count} 条", recovered);
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

            // 3. 逐条发送。
            foreach (var entry in claimed)
            {
                if (_cts.IsCancellationRequested)
                    break;

                _eventBus.Publish(new OutboxStatusChangedEvent(entry.ClientMessageId, OutboxStatus.Sending, null));

                try
                {
                    await _chatSession.SendChatMessageAsync(
                        entry.TargetUserId,
                        entry.Content,
                        AttachmentJson.DeserializeIds(entry.AttachmentIdsJson),
                        entry.ReplyToMessageId,
                        entry.ReplyToSenderUserId,
                        entry.ReplyToPreview,
                        entry.ForwardedFromMessageId,
                        entry.ForwardedFromSenderUserId,
                        entry.ForwardedFromPreview,
                        entry.ClientMessageId,
                        _cts.Token).ConfigureAwait(false);

                    // 已上行成功：保持 Sending，等待 MessageAck 单事务推进 Sent。
                }
                catch (OperationCanceledException)
                {
                    // 处理器关闭：保持 Sending，租约到期后由下轮恢复。
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Outbox 发送失败 ClientMessageId={ClientMessageId}", entry.ClientMessageId);
                    await MarkFailedAsync(userId, entry, ex).ConfigureAwait(false);
                }
            }

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
            Log.Warning("Outbox 永久失败（不再自动重试）ClientMessageId={ClientMessageId}", entry.ClientMessageId);
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _chatSession.Authenticated -= OnAuthenticated;
        _enqueuedSubscription.Dispose();
        _cts.Cancel();
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(StopTimeoutSec));
        }
        catch
        {
            // 忽略关闭时的等待异常
        }

        _cts.Dispose();
        _drainLock.Dispose();
    }
}
