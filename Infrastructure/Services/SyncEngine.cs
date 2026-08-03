using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Persistence;
using Core.Diagnostics;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Serilog;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 独立数据同步引擎：水位 → Bootstrap → 持久化会话列表（含翻页）→ 持久化各会话
/// catch-up（含继续分页）→ 完成事件。水位推进由 MessageStore.PersistHistoryAsync 单调完成。
/// 单同步任务：每次 Start 取消旧任务，断线/重连/切账户时旧任务安全退出。
/// </summary>
public sealed class SyncEngine : ISyncEngine, IMetricsSource
{
    private const int ConversationListLimit = 100;
    private const int HistoryLimitPerConversation = 30;
    private const int MaxConversationsWithHistory = 10;
    private const int MaxConversationPages = 10;
    private const int MaxCatchUpPagesPerConversation = 5;

    private readonly IChatSessionClient _chatSession;
    private readonly IMessageStore _messageStore;
    private readonly IDatabaseService _db;
    private readonly ISyncCheckpointStore _checkpoints;
    private readonly ISyncConflictResolver _conflicts;
    private readonly SyncDiagnostics _diagnostics = new();

    private readonly object _startLock = new();
    private CancellationTokenSource? _syncCts;
    private Task? _syncTask;

    public string Name => "sync_engine";

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        ["sync_count"] = _diagnostics.SyncCount,
        ["conversations_synced"] = _diagnostics.ConversationsSynced,
        ["messages_synced"] = _diagnostics.MessagesSynced,
        ["is_running"] = _diagnostics.IsRunning ? 1 : 0
    };

    public IReadOnlyDictionary<string, HistogramSnapshot> Histograms =>
        new Dictionary<string, HistogramSnapshot>
        {
            ["sync_duration_ms"] = _diagnostics.LastDurationMs > 0
                ? HistogramSnapshot.Point(_diagnostics.LastDurationMs)
                : HistogramSnapshot.Empty
        };

    public SyncEngine(
        IChatSessionClient chatSession,
        IMessageStore messageStore,
        IDatabaseService db,
        ISyncCheckpointStore checkpoints,
        ISyncConflictResolver conflicts)
    {
        _chatSession = chatSession;
        _messageStore = messageStore;
        _db = db;
        _checkpoints = checkpoints;
        _conflicts = conflicts;
    }

    public ISyncDiagnostics Diagnostics => _diagnostics;

    public bool IsSyncing
    {
        get { lock (_startLock) return _syncTask is { IsCompleted: false }; }
    }

    public event EventHandler<SyncCompletedEventArgs>? Completed;

    public void Start(SessionStamp session, CancellationToken ct = default)
    {
        lock (_startLock)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var oldCts = _syncCts;
            _syncCts = cts;
            oldCts?.Cancel();
            oldCts?.Dispose();
            _syncTask = Task.Run(() => RunAsync(session, cts.Token));
        }
    }

    /// <summary>
    /// 停止当前同步任务：取消旧 CTS 并清引用，供会话停止/退出登录/应用退出调用。
    /// 与 Start 幂等：未运行时调用无副作用。
    /// </summary>
    public void Stop()
    {
        lock (_startLock)
        {
            var oldCts = _syncCts;
            _syncCts = null;
            _syncTask = null;
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
    }

    private async Task RunAsync(SessionStamp session, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!session.IsValid)
            {
                Fail(session, "INVALID_SESSION", null);
                return;
            }

            // 1. 本地水位
            var watermarks = await _checkpoints.GetWatermarksAsync(session, ct).ConfigureAwait(false);
            var watermarkList = watermarks.Count == 0 ? null : watermarks;

            // 2. Bootstrap
            var sync = await _chatSession.QuerySyncBootstrapAsync(
                    ConversationListLimit,
                    HistoryLimitPerConversation,
                    MaxConversationsWithHistory,
                    watermarkList,
                    ct)
                .ConfigureAwait(false);

            if (!sync.Succeeded)
            {
                Fail(session, sync.ErrorCode ?? "BOOTSTRAP_FAILED", sync.ErrorMessage);
                return;
            }

            var conversations = new List<ConversationListItemDto>(sync.Conversations);
            var catchUps = new List<ConversationHistoryCatchUpDto>(sync.CatchUps);

            // 3. 持久化会话列表 + 继续翻页
            foreach (var item in conversations)
                await PersistConversationAsync(session, item, ct).ConfigureAwait(false);

            var listCursor = sync.ConversationsNextCursor;
            for (var page = 0; sync.ConversationsHasMore && listCursor is not null && page < MaxConversationPages; page++)
            {
                var resp = await _chatSession.QueryConversationListAsync(
                        ConversationListLimit,
                        listCursor.IsPinned,
                        listCursor.PinnedAtMs,
                        listCursor.LastMessageAtMs,
                        listCursor.ConversationId,
                        ct)
                    .ConfigureAwait(false);
                if (!resp.Succeeded)
                    break;
                foreach (var item in resp.Items)
                {
                    await PersistConversationAsync(session, item, ct).ConfigureAwait(false);
                    conversations.Add(item);
                }
                if (!resp.HasMore || resp.NextCursor is null)
                    break;
                listCursor = resp.NextCursor;
            }

            // 4. 持久化 catch-up（forward 方向：以正向水位判定 + 推进水位）+ 对仍有更多的会话继续拉历史
            foreach (var cu in catchUps)
                await PersistCatchUpAsync(session, cu, ct).ConfigureAwait(false);

            foreach (var cu in catchUps.Where(c => c.HasMore && c.NextCursor is not null).ToArray())
            {
                var cursor = cu.NextCursor;
                for (var page = 0; page < MaxCatchUpPagesPerConversation && cursor is not null; page++)
                {
                    var resp = await _chatSession.QueryMessageHistoryAsync(
                            cu.ConversationId,
                            HistoryLimitPerConversation,
                            cursor.ReceivedAtMs,
                            cursor.MessageId,
                            ct)
                        .ConfigureAwait(false);
                    if (!resp.Succeeded || resp.Items.Count == 0)
                        break;
                    // backward 方向：Before... 游标返回的是"更早"的消息，天然小于正向水位，
                    // 绝不能复用 HasNewerMessages（会被整页跳过）；这里无条件幂等合并
                    // （ApplyHistoryBatchAsync 内部按 MessageId 幂等），且不推进正向水位。
                    await PersistHistoryPageAsync(session, cu.ConversationId, resp.Items, ct).ConfigureAwait(false);
                    if (!resp.HasMore || resp.NextCursor is null)
                        break;
                    cursor = resp.NextCursor;
                }
            }

            _diagnostics.MarkSuccess(sw.ElapsedMilliseconds);
            Completed?.Invoke(this, new SyncCompletedEventArgs
            {
                Session = session,
                Conversations = conversations,
                CatchUps = catchUps,
                Succeeded = true
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 新任务启动或断线：旧任务静默退出，不触发事件。
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "同步失败 OwnerUserId={OwnerUserId}", session.OwnerUserId);
            Fail(session, "SYNC_ERROR", ex.Message);
        }
    }

    private void Fail(SessionStamp session, string errorCode, string? errorMessage)
    {
        _diagnostics.MarkFailed(errorCode, errorMessage);
        Completed?.Invoke(this, new SyncCompletedEventArgs
        {
            Session = session,
            Succeeded = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        });
    }

    private async Task PersistConversationAsync(SessionStamp session, ConversationListItemDto item, CancellationToken ct)
    {
        var conversation = new LocalConversation
        {
            OwnerUserId = session.OwnerUserId,
            ConversationId = item.ConversationId,
            Type = (byte)item.Type,
            PeerUserId = item.PeerUserId,
            GroupTitle = item.Title,
            LastMessageId = item.LastMessageId,
            LastMessagePreview = item.LastMessagePreview,
            LastMessageAtMs = item.LastMessageAtMs,
            LastSenderUserId = item.LastSenderUserId,
            UnreadCount = item.UnreadCount,
            LastReadMessageId = item.LastReadMessageId,
            LastReadAtMs = item.LastReadAtMs,
            IsPinned = item.IsPinned,
            PinnedAtMs = item.PinnedAtMs,
            IsMuted = item.IsMuted,
            MutedUntilMs = item.MutedUntilMs,
            LastSynced = DateTime.UtcNow
        };
        // 服务端投影专用：不覆盖本地草稿/归档/删除等本地专属字段。
        await _db.ApplyRemoteConversationProjectionAsync(conversation).ConfigureAwait(false);
        _diagnostics.AddConversations(1);
    }

    private async Task PersistCatchUpAsync(SessionStamp session, ConversationHistoryCatchUpDto cu, CancellationToken ct)
    {
        if (cu.Items.Count == 0)
            return;
        var cursor = await _db.GetSyncCursorAsync(session.OwnerUserId, cu.ConversationId).ConfigureAwait(false);
        if (!_conflicts.HasNewerMessages(cursor?.AfterReceivedAtMs, cursor?.AfterMessageId, cu.Items))
            return;

        // 目标水位：批次最大 (ReceivedAtMs, MessageId) 复合游标（批量方法内做单调判断，不回退旧水位）。
        var maxItem = MaxItem(cu.Items);
        var target = new LocalSyncCursor
        {
            OwnerUserId = session.OwnerUserId,
            ConversationId = cu.ConversationId,
            AfterReceivedAtMs = maxItem.ReceivedAtMs,
            AfterMessageId = maxItem.MessageId
        };

        await _messageStore.ApplyHistoryBatchAsync(session, cu.ConversationId, cu.Items, target, ct).ConfigureAwait(false);
        _diagnostics.AddMessages(cu.Items.Count);
    }

    /// <summary>
    /// backward 历史续页落库：无条件幂等合并（不判定水位、不推进正向水位，cursor=null）。
    /// 早于水位的消息正是分页要补的历史，若用正向水位过滤会整页丢失。
    /// </summary>
    private async Task PersistHistoryPageAsync(
        SessionStamp session,
        string conversationId,
        IReadOnlyList<MessageHistoryItemDto> items,
        CancellationToken ct)
    {
        if (items.Count == 0)
            return;
        await _messageStore.ApplyHistoryBatchAsync(session, conversationId, items, cursor: null, ct).ConfigureAwait(false);
        _diagnostics.AddMessages(items.Count);
    }

    private static MessageHistoryItemDto MaxItem(IReadOnlyList<MessageHistoryItemDto> items)
    {
        var max = items[0];
        for (var i = 1; i < items.Count; i++)
        {
            var item = items[i];
            if (item.ReceivedAtMs > max.ReceivedAtMs
                || (item.ReceivedAtMs == max.ReceivedAtMs
                    && string.CompareOrdinal(item.MessageId, max.MessageId) > 0))
                max = item;
        }
        return max;
    }
}

/// <summary>同步水位存取：委托 IMessageStore / IDatabaseService 现有能力。</summary>
public sealed class SyncCheckpointStore(
    IMessageStore messageStore,
    IDatabaseService db) : ISyncCheckpointStore
{
    public Task<IReadOnlyList<ConversationSyncWatermarkDto>> GetWatermarksAsync(SessionStamp session, CancellationToken ct = default)
        => messageStore.GetSyncWatermarksAsync(session, ct);

    public async Task SaveWatermarkAsync(SessionStamp session, string conversationId, long afterReceivedAtMs, string afterMessageId, CancellationToken ct = default)
    {
        var cursor = await db.GetSyncCursorAsync(session.OwnerUserId, conversationId).ConfigureAwait(false)
            ?? new LocalSyncCursor { OwnerUserId = session.OwnerUserId, ConversationId = conversationId };
        cursor.AfterReceivedAtMs = afterReceivedAtMs;
        cursor.AfterMessageId = afterMessageId;
        cursor.UpdatedAt = DateTime.UtcNow;
        await db.UpsertSyncCursorAsync(cursor).ConfigureAwait(false);
    }
}

/// <summary>
/// 冲突判定：存在比本地正向水位更新的消息才值得持久化。
/// 仅用于 bootstrap catch-up（forward 方向）；backward 历史续页不经过此判定。
/// 同时间戳按 (ReceivedAtMs, MessageId) 复合比较：本地水位消息 Id 更早时视为有更新。
/// </summary>
public sealed class SyncConflictResolver : ISyncConflictResolver
{
    public bool HasNewerMessages(
        long? localAfterReceivedAtMs,
        string? localAfterMessageId,
        IReadOnlyList<MessageHistoryItemDto> items)
    {
        if (items.Count == 0)
            return false;
        if (localAfterReceivedAtMs is not { } after)
            return true;
        foreach (var item in items)
        {
            if (item.ReceivedAtMs > after)
                return true;
            // 同时间戳：仅当本地水位消息 Id 字典序更早（即本地落后）才视为有更新。
            if (item.ReceivedAtMs == after
                && localAfterMessageId is not null
                && string.CompareOrdinal(item.MessageId, localAfterMessageId) > 0)
                return true;
        }
        return false;
    }
}

/// <summary>同步诊断实现（线程安全计数）。</summary>
public sealed class SyncDiagnostics : ISyncDiagnostics
{
    private int _isRunning;
    private DateTime? _lastSyncUtc;
    private long _lastDurationMs;
    private string? _lastError;
    private int _syncCount;
    private long _conversationsSynced;
    private long _messagesSynced;
    private readonly object _lock = new();

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public DateTime? LastSyncUtc { get { lock (_lock) return _lastSyncUtc; } }

    public long LastDurationMs { get { lock (_lock) return _lastDurationMs; } }

    public string? LastError { get { lock (_lock) return _lastError; } }

    public int SyncCount { get { lock (_lock) return _syncCount; } }

    public long ConversationsSynced => Interlocked.Read(ref _conversationsSynced);

    public long MessagesSynced => Interlocked.Read(ref _messagesSynced);

    public void MarkSuccess(long durationMs)
    {
        Volatile.Write(ref _isRunning, 0);
        lock (_lock)
        {
            _lastSyncUtc = DateTime.UtcNow;
            _lastDurationMs = durationMs;
            _lastError = null;
            _syncCount++;
        }
    }

    public void MarkFailed(string? errorCode, string? errorMessage)
    {
        Volatile.Write(ref _isRunning, 0);
        lock (_lock)
            _lastError = string.IsNullOrWhiteSpace(errorMessage) ? errorCode : $"{errorCode}: {errorMessage}";
    }

    public void AddConversations(int count) => Interlocked.Add(ref _conversationsSynced, count);

    public void AddMessages(int count) => Interlocked.Add(ref _messagesSynced, count);
}
