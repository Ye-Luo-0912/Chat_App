using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Persistence;
using Infrastructure.Events;
using Core.Interfaces;
using Core.Models;
using Infrastructure.Models;
using Infrastructure.Serialization;
using Serilog;

namespace Infrastructure.Services;

/// <summary>
/// 后台排空 Outbox 的处理器（P0-4 事务化 Outbox）。
/// 周期性拉取 Queued/Failed 的 Outbox 条目并重新发送，失败按指数退避重试。
/// 本文件依赖 Infrastructure 类型（IDatabaseService / Infrastructure.Models），
/// 故由 Infrastructure.csproj 编译（从 Core.csproj 排除），避免循环依赖。
/// </summary>
public sealed class OutboxProcessor : IDisposable
{
    /// <summary>轮询 Outbox 的周期间隔（秒）。</summary>
    private const int DrainIntervalSec = 5;
    /// <summary>单条 Outbox 最大重试次数，超过即放弃（避免无限重试占用资源）。</summary>
    private const int MaxRetryCount = 10;
    /// <summary>停止处理器时等待循环退出的超时（秒）。</summary>
    private const int StopTimeoutSec = 2;

    private readonly IDatabaseService _db;
    private readonly IChatSessionClient _chatSession;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IEventBus _eventBus;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _drainLock = new(1, 1);
    private Task? _loopTask;
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

            List<LocalOutboxMessage> pending;
            try
            {
                pending = await _db.GetPendingOutboxAsync(userId, 50).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "拉取待发送 Outbox 失败");
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var entry in pending)
            {
                if (_cts.IsCancellationRequested)
                    break;

                // 未到下次重试时间，跳过
                if (entry.NextRetryAt is { } nextRetry && nextRetry > now)
                    continue;

                // 永久失败：重试次数超限，不再发送
                if (entry.Status == OutboxStatus.Failed && entry.RetryCount > MaxRetryCount)
                    continue;

                // 发送前标记 Sending，避免被下一轮重复拉取
                try
                {
                    await _db.UpdateOutboxStatusWithRetryAsync(userId, entry.ClientMessageId, OutboxStatus.Sending)
                        .ConfigureAwait(false);
                    _eventBus.Publish(new OutboxStatusChangedEvent(entry.ClientMessageId, OutboxStatus.Sending, null));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "标记 Outbox 为 Sending 失败 ClientMessageId={ClientMessageId}", entry.ClientMessageId);
                    continue;
                }

                IReadOnlyList<string>? attachmentIds = AttachmentJson.DeserializeIds(entry.AttachmentIdsJson);

                try
                {
                    await _chatSession.SendChatMessageAsync(
                        entry.TargetUserId,
                        entry.Content,
                        attachmentIds,
                        entry.ReplyToMessageId,
                        entry.ReplyToSenderUserId,
                        entry.ReplyToPreview,
                        entry.ForwardedFromMessageId,
                        entry.ForwardedFromSenderUserId,
                        entry.ForwardedFromPreview,
                        entry.ClientMessageId,
                        _cts.Token).ConfigureAwait(false);

                    // 发送已上行成功，保持 Sending；后续 MessageAck 会推进到 Sent
                }
                catch (OperationCanceledException)
                {
                    // 关闭中，不标记失败
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Outbox 重试发送失败 ClientMessageId={ClientMessageId}", entry.ClientMessageId);
                    try
                    {
                        await _db.UpdateOutboxStatusWithRetryAsync(userId, entry.ClientMessageId, OutboxStatus.Failed, null, ex.Message)
                            .ConfigureAwait(false);
                        _eventBus.Publish(new OutboxStatusChangedEvent(entry.ClientMessageId, OutboxStatus.Failed, null));
                    }
                    catch (Exception ex2)
                    {
                        Log.Error(ex2, "更新 Outbox 为 Failed 失败 ClientMessageId={ClientMessageId}", entry.ClientMessageId);
                    }
                }
            }
        }
        finally
        {
            _drainLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _chatSession.Authenticated -= OnAuthenticated;
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
