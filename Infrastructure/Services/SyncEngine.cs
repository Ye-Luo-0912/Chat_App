using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    private const int RelationshipListLimit = 100;
    private const int MaxRelationshipPages = 20;
    private const string ConversationListCursorMissing = "CONVERSATION_LIST_CURSOR_MISSING";
    private const string ConversationListPageFailed = "CONVERSATION_LIST_PAGE_FAILED";

    private readonly IChatSessionClient _chatSession;
    private readonly IMessageStore _messageStore;
    private readonly IDatabaseService _db;
    private readonly ISyncCheckpointStore _checkpoints;
    private readonly ISyncConflictResolver _conflicts;
    private readonly SyncDiagnostics _diagnostics = new();

    private readonly object _startLock = new();
    private long _lifecycleIntent;
    private CancellationTokenSource? _syncCts;
    private Task? _syncTask;

    public string Name => "sync_engine";

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        ["sync_count"] = _diagnostics.SyncCount,
        ["conversations_synced"] = _diagnostics.ConversationsSynced,
        ["messages_synced"] = _diagnostics.MessagesSynced,
        ["sync_lag_ms"] = SyncLagMs(_diagnostics),
        ["is_running"] = _diagnostics.IsRunning ? 1 : 0,
        ["sync_fail_count"] = _diagnostics.FailCount,
        ["sync_consecutive_failures"] = _diagnostics.ConsecutiveFailures
    };

    public IReadOnlyDictionary<string, HistogramSnapshot> Histograms =>
        new Dictionary<string, HistogramSnapshot>
        {
            ["sync_duration_ms"] = _diagnostics.LastDurationMs > 0
                ? HistogramSnapshot.Point(_diagnostics.LastDurationMs)
                : HistogramSnapshot.Empty
        };

    /// <summary>设备端同步滞后：当前时刻 − 最近同步到的消息时间（未同步到消息时为 0）。</summary>
    private static long SyncLagMs(SyncDiagnostics diagnostics)
        => diagnostics.LastSyncedMessageAtMs > 0
            ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - diagnostics.LastSyncedMessageAtMs)
            : 0;

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

    /// <summary>
    /// 重启同步：严格取消并等待旧任务退出后启动新任务。
    /// 旧任务即使已发出 RPC/占用 DB，也在启动新任务前完成收尾，避免竞争与跨会话污染。
    /// </summary>
    public async Task RestartAsync(SessionStamp session, CancellationToken ct = default)
    {
        Task? oldTask;
        CancellationTokenSource? oldCts;
        long intent;
        lock (_startLock)
        {
            intent = ++_lifecycleIntent;
            oldCts = _syncCts;
            oldTask = _syncTask;
        }

        TryCancel(oldCts);
        await WaitForExitAsync(oldTask).ConfigureAwait(false);

        lock (_startLock)
        {
            // Stop、Start 或更新的 Restart 已表达了更新意图时，本次 Restart 不再有启动权限。
            // 等待期间始终保留旧任务引用，Start 因而不会让新旧 RunAsync 重叠。
            if (intent != _lifecycleIntent)
                return;

            if (ReferenceEquals(_syncTask, oldTask))
            {
                _syncTask = null;
                _syncCts = null;
            }
            oldCts?.Dispose();

            if (ct.IsCancellationRequested)
                return;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _syncCts = cts;
            _syncTask = Task.Run(() => RunAsync(session, cts.Token));
        }
    }

    /// <summary>启动一次同步（仅当无任务在运行时启动；已运行则忽略）。</summary>
    public void Start(SessionStamp session, CancellationToken ct = default)
    {
        lock (_startLock)
        {
            if (_syncTask is { IsCompleted: false })
                return;

            ++_lifecycleIntent;
            _syncCts?.Dispose();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _syncCts = cts;
            _syncTask = Task.Run(() => RunAsync(session, cts.Token));
        }
    }

    /// <summary>
    /// 停止当前同步任务并等待其退出。幂等。
    /// </summary>
    public async Task StopAsync()
    {
        Task? oldTask;
        CancellationTokenSource? oldCts;
        long intent;
        lock (_startLock)
        {
            intent = ++_lifecycleIntent;
            oldCts = _syncCts;
            oldTask = _syncTask;
        }

        TryCancel(oldCts);
        await WaitForExitAsync(oldTask).ConfigureAwait(false);

        lock (_startLock)
        {
            // 只允许仍为最新意图的 Stop 清理它观察到的任务；否则由更新意图接管。
            if (intent == _lifecycleIntent && ReferenceEquals(_syncTask, oldTask))
            {
                _syncTask = null;
                _syncCts = null;
                oldCts?.Dispose();
            }
        }
    }

    private static void TryCancel(CancellationTokenSource? cts)
    {
        if (cts is null)
            return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 并发的更新意图已完成并回收同一代 CTS。
        }
    }

    private static async Task WaitForExitAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // RunAsync 已处理业务异常；生命周期入口只负责等待唯一运行实例退出。
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
            var relationshipReadEnabled = _chatSession.SupportsRelationshipRead;
            var relationshipWatermarks = relationshipReadEnabled
                ? await _db.GetRelationshipWatermarksAsync(session.OwnerUserId).ConfigureAwait(false)
                : null;

            // 2. Bootstrap
            var sync = await _chatSession.QuerySyncBootstrapWithRelationshipsAsync(
                    ConversationListLimit,
                    HistoryLimitPerConversation,
                    MaxConversationsWithHistory,
                    watermarkList,
                    relationshipWatermarks,
                    relationshipReadEnabled ? RelationshipListLimit : null,
                    ct)
                .ConfigureAwait(false);

            if (!sync.Succeeded)
            {
                Fail(session, sync.ErrorCode ?? "BOOTSTRAP_FAILED", sync.ErrorMessage);
                return;
            }

            if (!relationshipReadEnabled && sync.RelationshipCatchUps?.Any(HasRelationshipPayload) == true)
            {
                Fail(session, "RELATIONSHIP_SYNC_UNSUPPORTED",
                    "服务端返回了客户端尚未启用的关系增量；本轮未应用任何关系水位。");
                return;
            }

            if (relationshipReadEnabled)
            {
                var relationshipResult = await ApplyRelationshipSyncAsync(
                        session, sync.RelationshipCatchUps, relationshipWatermarks ?? [], ct)
                    .ConfigureAwait(false);
                if (relationshipResult is not null)
                {
                    Fail(session, relationshipResult.Value.Code, relationshipResult.Value.Message);
                    return;
                }
            }

            if (sync.ConversationsHasMore && sync.ConversationsNextCursor is null)
            {
                Fail(session, ConversationListCursorMissing,
                    "bootstrap 会话列表声明仍有更多数据但未返回游标。");
                return;
            }

            var conversations = new List<ConversationListItemDto>(sync.Conversations ?? []);
            var catchUps = new List<ConversationHistoryCatchUpDto>(sync.CatchUps ?? []);
            var resetsRequired = sync.ResetsRequired ?? [];

            // 预算截断标记：会话列表或历史续页达到页数上限且服务端仍有更多 → Partial（不得静默成功）。
            var partial = false;

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
                {
                    var detail = string.IsNullOrWhiteSpace(resp.ErrorCode)
                        ? resp.ErrorMessage
                        : string.IsNullOrWhiteSpace(resp.ErrorMessage)
                            ? resp.ErrorCode
                            : $"{resp.ErrorCode}: {resp.ErrorMessage}";
                    Fail(session, ConversationListPageFailed, detail);
                    return;
                }
                if (resp.HasMore && resp.NextCursor is null)
                {
                    Fail(session, ConversationListCursorMissing,
                        "会话列表续页声明仍有更多数据但未返回游标。");
                    return;
                }
                foreach (var item in resp.Items)
                {
                    await PersistConversationAsync(session, item, ct).ConfigureAwait(false);
                    conversations.Add(item);
                }
                if (!resp.HasMore)
                    break;
                listCursor = resp.NextCursor;
                if (page == MaxConversationPages - 1)
                    partial = true; // 达到会话列表页数上限且仍有更多
            }

            // 4. 失效水位恢复：先清除已确认的本地服务端投影，再从最新页向后完整重建。
            // 仅当所有页完成后才原子替换 changed-at 水位；中断或失败保留旧失效水位以便重入。
            var resetConversationIds = resetsRequired
                .Where(static reset => !string.IsNullOrWhiteSpace(reset.ConversationId))
                .Select(static reset => reset.ConversationId)
                .ToHashSet(StringComparer.Ordinal);
            var missingForwardCursor = catchUps.FirstOrDefault(c =>
                !resetConversationIds.Contains(c.ConversationId)
                && c.HasMore
                && c.NextCursor is null);
            if (missingForwardCursor is not null)
            {
                Fail(session, "FORWARD_CURSOR_MISSING",
                    $"bootstrap 正向同步声明仍有更多数据但未返回游标: {missingForwardCursor.ConversationId}");
                return;
            }
            foreach (var reset in resetsRequired)
            {
                var recovered = await RecoverResetConversationAsync(session, reset, ct).ConfigureAwait(false);
                if (!recovered)
                    partial = true;
            }

            // 5. 持久化 catch-up（forward 方向：changed-at 水位判定 + 推进水位）。
            // reset 会话已由全量恢复处理，不重复应用 bootstrap 中可能残留的 catch-up。
            foreach (var cu in catchUps.Where(c => !resetConversationIds.Contains(c.ConversationId)))
                await PersistCatchUpAsync(session, cu, ct).ConfigureAwait(false);

            foreach (var cu in catchUps.Where(c =>
                         !resetConversationIds.Contains(c.ConversationId)
                         && c.HasMore
                         && c.NextCursor is not null).ToArray())
            {
                var cursor = cu.NextCursor;
                for (var page = 0; page < MaxCatchUpPagesPerConversation && cursor is not null; page++)
                {
                    var resp = await _chatSession.QueryMessageHistoryAfterAsync(
                            cu.ConversationId,
                            EffectiveChangedAt(cursor),
                            cursor.MessageId,
                            HistoryLimitPerConversation,
                            ct)
                        .ConfigureAwait(false);
                    if (!resp.Succeeded)
                    {
                        Fail(session, resp.ErrorCode ?? "FORWARD_CATCHUP_FAILED", resp.ErrorMessage);
                        return;
                    }
                    if (resp.HasMore && resp.NextCursor is null)
                    {
                        Fail(session, "FORWARD_CURSOR_MISSING",
                            $"正向同步声明仍有更多数据但未返回游标: {cu.ConversationId}");
                        return;
                    }
                    if (resp.Items.Count == 0)
                    {
                        if (resp.HasMore)
                            partial = true; // 服务端宣称有更多但没有可推进条目，禁止死循环。
                        break;
                    }

                    await PersistForwardHistoryPageAsync(session, cu.ConversationId, resp.Items, ct)
                        .ConfigureAwait(false);
                    if (!resp.HasMore || resp.NextCursor is null)
                        break;
                    if (EffectiveChangedAt(cursor) == EffectiveChangedAt(resp.NextCursor)
                        && string.Equals(cursor.MessageId, resp.NextCursor.MessageId, StringComparison.Ordinal))
                    {
                        Fail(session, "FORWARD_CURSOR_NOT_ADVANCED",
                            $"正向同步游标未推进: {cu.ConversationId}/{cursor.MessageId}/{EffectiveChangedAt(cursor)}");
                        return;
                    }
                    cursor = resp.NextCursor;
                    if (page == MaxCatchUpPagesPerConversation - 1)
                        partial = true; // 达到单会话历史页数上限且仍有更多
                }
            }

            _diagnostics.MarkSuccess(sw.ElapsedMilliseconds);
            Completed?.Invoke(this, new SyncCompletedEventArgs
            {
                Session = session,
                Conversations = conversations,
                CatchUps = catchUps,
                Succeeded = true,
                Outcome = partial ? SyncOutcome.PartialLimitReached : SyncOutcome.Completed
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
        _diagnostics.MarkFailed(errorCode, errorMessage, IsTransientSyncFailure(errorCode));
        Completed?.Invoke(this, new SyncCompletedEventArgs
        {
            Session = session,
            Succeeded = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        });
    }

    /// <summary>
    /// 同步失败可重试性分类：临时性失败（网络/服务端瞬时或承载能力未就绪）标记为可自动重试，
    /// 其余契约违例/会话失效/能力不匹配视为永久失败，需引导用户处理而非盲目重试。
    /// </summary>
    private static bool IsTransientSyncFailure(string errorCode) => errorCode switch
    {
        // 服务端瞬时/网络类：可自动重试。
        "SYNC_ERROR"
        or "BOOTSTRAP_FAILED"
        or "CONVERSATION_LIST_PAGE_FAILED"
        or "RELATIONSHIP_SYNC_FAILED"
        or "RELATIONSHIP_SYNC_PROJECTION_UNAVAILABLE" => true,
        // 契约违例/会话失效/能力不匹配：永久失败，重试无益。
        _ => false,
    };

    private async Task<(string Code, string Message)?> ApplyRelationshipSyncAsync(
        SessionStamp session,
        IReadOnlyList<RelationshipCatchUpDto>? catchUps,
        IReadOnlyList<RelationshipSyncWatermarkDto> watermarks,
        CancellationToken ct)
    {
        if (catchUps is null or { Count: 0 })
            return null;

        var currentByType = watermarks
            .Where(static x => IsRelationshipListType(x.ListType) && x.AfterSequence >= 0)
            .GroupBy(static x => x.ListType)
            .ToDictionary(static x => x.Key, static x => x.Last().AfterSequence);
        var seen = new HashSet<RelationshipListTypeDto>();

        foreach (var catchUp in catchUps)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsRelationshipListType(catchUp.ListType))
                return ("RELATIONSHIP_LIST_TYPE_INVALID", $"未知关系列表类型: {(byte)catchUp.ListType}");
            if (!seen.Add(catchUp.ListType))
                return ("RELATIONSHIP_DUPLICATE_LIST", $"关系列表类型重复: {catchUp.ListType}");

            currentByType.TryGetValue(catchUp.ListType, out var currentSequence);
            if (catchUp.ErrorCode is not null && catchUp.ResetRequired != true)
                return ("RELATIONSHIP_SYNC_FAILED",
                    $"{catchUp.ErrorCode}: {catchUp.ErrorMessage}".TrimEnd(':', ' '));

            if (catchUp.ResetRequired == true)
            {
                var rebuilt = await RebuildRelationshipListAsync(
                        session, catchUp.ListType, catchUp.NextSequence, ct)
                    .ConfigureAwait(false);
                if (!rebuilt)
                    return ("RELATIONSHIP_RESET_FAILED", $"关系列表重建失败: {catchUp.ListType}");
                continue;
            }

            if (catchUp.HasMore)
                return ("RELATIONSHIP_CURSOR_UNSUPPORTED",
                    $"关系增量分页暂不支持在 bootstrap 外续页: {catchUp.ListType}");
            if (catchUp.NextSequence < currentSequence)
                return ("RELATIONSHIP_WATERMARK_REGRESSED",
                    $"关系水位回退: {catchUp.ListType} {catchUp.NextSequence} < {currentSequence}");

            foreach (var change in catchUp.Changes ?? [])
            {
                var validation = ValidateRelationshipChange(change);
                if (validation is not null)
                    return ("RELATIONSHIP_CHANGE_INVALID", validation);
            }

            await _db.ApplyRelationshipChangesAsync(
                    session.OwnerUserId,
                    catchUp.ListType,
                    catchUp.Changes ?? [],
                    catchUp.NextSequence)
                .ConfigureAwait(false);
        }

        return null;
    }

    private async Task<bool> RebuildRelationshipListAsync(
        SessionStamp session,
        RelationshipListTypeDto listType,
        long afterSequence,
        CancellationToken ct)
    {
        var items = new List<RelationshipListItemDto>();
        string? cursor = null;
        for (var page = 0; page < MaxRelationshipPages; page++)
        {
            var response = await _chatSession.QueryRelationshipListAsync(
                    listType, RelationshipListLimit, cursor, ct)
                .ConfigureAwait(false);
            if (!response.Succeeded)
                return false;
            if (response.ListType != listType)
                return false;
            if (response.ResetRequired == true)
                return false;

            foreach (var item in response.Items ?? [])
            {
                if (ValidateRelationshipListItem(item) is not null)
                    return false;
                items.Add(item);
            }

            if (!response.HasMore)
            {
                await _db.ReplaceRelationshipProjectionAsync(
                        session.OwnerUserId, listType, items, Math.Max(0, afterSequence))
                    .ConfigureAwait(false);
                return true;
            }

            if (string.IsNullOrWhiteSpace(response.NextCursor)
                || string.Equals(cursor, response.NextCursor, StringComparison.Ordinal))
                return false;
            cursor = response.NextCursor;
        }

        return false;
    }

    private static bool HasRelationshipPayload(RelationshipCatchUpDto catchUp)
        => catchUp.ResetRequired == true
           || catchUp.HasMore
           || catchUp.Changes?.Count > 0
           || catchUp.ErrorCode is not null;

    private static bool IsRelationshipListType(RelationshipListTypeDto listType)
        => listType is RelationshipListTypeDto.Friends
            or RelationshipListTypeDto.FriendRequests
            or RelationshipListTypeDto.BlockedUsers;

    private static string? ValidateRelationshipListItem(RelationshipListItemDto item)
    {
        if (string.IsNullOrWhiteSpace(item.ResourceId)
            || Encoding.UTF8.GetByteCount(item.ResourceId) > 64)
            return "关系列表项 ResourceId 无效。";
        if (item.UserId <= 0)
            return "关系列表项 UserId 无效。";
        if (item.Status is not null && Encoding.UTF8.GetByteCount(item.Status) > 32)
            return "关系列表项 Status 超限。";
        if (item.Message is not null && Encoding.UTF8.GetByteCount(item.Message) > 512)
            return "关系列表项 Message 超限。";
        return null;
    }

    private static string? ValidateRelationshipChange(RelationshipChangeLogEntryDto change)
    {
        if (change.Operation is not RelationshipChangeOperationDto.Upsert
            and not RelationshipChangeOperationDto.Delete)
            return "关系变更 Operation 未知。";
        if (string.IsNullOrWhiteSpace(change.ResourceId)
            || Encoding.UTF8.GetByteCount(change.ResourceId) > 64)
            return "关系变更 ResourceId 无效。";
        if (change.UserId <= 0)
            return "关系变更 UserId 无效。";
        if (change.Status is not null && Encoding.UTF8.GetByteCount(change.Status) > 32)
            return "关系变更 Status 超限。";
        if (change.Message is not null && Encoding.UTF8.GetByteCount(change.Message) > 512)
            return "关系变更 Message 超限。";
        return null;
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

        // 目标水位：批次最大 (ChangedAtMs, MessageId) 复合游标。
        var maxItem = MaxChangedItem(cu.Items);
        var target = new LocalSyncCursor
        {
            OwnerUserId = session.OwnerUserId,
            ConversationId = cu.ConversationId,
            AfterReceivedAtMs = EffectiveChangedAt(maxItem),
            AfterMessageId = maxItem.MessageId
        };

        await _messageStore.ApplyHistoryBatchAsync(session, cu.ConversationId, cu.Items, target, ct).ConfigureAwait(false);
        _diagnostics.AddMessages(cu.Items.Count);
        _diagnostics.RecordSyncedMessages(maxItem.ReceivedAtMs);
    }

    /// <summary>
    /// forward catch-up 续页：按 changed-at 最大项推进正向水位。
    /// </summary>
    private async Task PersistForwardHistoryPageAsync(
        SessionStamp session,
        string conversationId,
        IReadOnlyList<MessageHistoryItemDto> items,
        CancellationToken ct)
    {
        if (items.Count == 0)
            return;

        var max = MaxChangedItem(items);
        var cursor = new LocalSyncCursor
        {
            OwnerUserId = session.OwnerUserId,
            ConversationId = conversationId,
            AfterReceivedAtMs = EffectiveChangedAt(max),
            AfterMessageId = max.MessageId
        };
        await _messageStore.ApplyHistoryBatchAsync(session, conversationId, items, cursor, ct).ConfigureAwait(false);
        _diagnostics.AddMessages(items.Count);
        _diagnostics.RecordSyncedMessages(MaxReceivedItem(items).ReceivedAtMs);
    }

    private async Task PersistBackwardHistoryPageAsync(
        SessionStamp session,
        string conversationId,
        IReadOnlyList<MessageHistoryItemDto> items,
        CancellationToken ct)
    {
        if (items.Count == 0)
            return;
        await _messageStore.ApplyHistoryBatchAsync(session, conversationId, items, cursor: null, ct)
            .ConfigureAwait(false);
        _diagnostics.AddMessages(items.Count);
        _diagnostics.RecordSyncedMessages(MaxReceivedItem(items).ReceivedAtMs);
    }

    private async Task<bool> RecoverResetConversationAsync(
        SessionStamp session,
        SyncCursorResetRequiredDto reset,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reset.ConversationId))
            throw new InvalidDataException("SyncBootstrap reset 缺少 ConversationId。");

        var conversationId = reset.ConversationId;
        await _db.ResetConversationSyncStateAsync(session.OwnerUserId, conversationId).ConfigureAwait(false);

        if (reset.Reason == SyncCursorResetReasonDto.MembershipLost)
        {
            await _db.DeleteSyncCursorAsync(session.OwnerUserId, conversationId).ConfigureAwait(false);
            await _messageStore.MarkOutboxPermanentByConversationAsync(
                session.OwnerUserId,
                conversationId,
                "membership_lost",
                ct).ConfigureAwait(false);
            await _db.SetConversationLocalStateAsync(
                session.OwnerUserId,
                conversationId,
                archived: true,
                deleted: true).ConfigureAwait(false);
            return true;
        }

        MessageHistoryCursorDto? before = null;
        MessageHistoryItemDto? maxChanged = null;
        for (var page = 0; ; page++)
        {
            var response = await _chatSession.QueryMessageHistoryAsync(
                    conversationId,
                    ConversationListLimit,
                    before?.ReceivedAtMs,
                    before?.MessageId,
                    ct)
                .ConfigureAwait(false);

            if (!response.Succeeded)
                throw new InvalidOperationException(
                    $"reset recovery 失败: {response.ErrorCode ?? "history_failed"} {response.ErrorMessage}".Trim());

            if (response.Items.Count > 0)
            {
                await PersistBackwardHistoryPageAsync(session, conversationId, response.Items, ct)
                    .ConfigureAwait(false);
                var pageMax = MaxChangedItem(response.Items);
                if (maxChanged is null || CompareChanged(pageMax, maxChanged) > 0)
                    maxChanged = pageMax;
            }

            if (!response.HasMore)
            {
                if (maxChanged is not null)
                {
                    await _db.ReplaceSyncCursorAsync(new LocalSyncCursor
                    {
                        OwnerUserId = session.OwnerUserId,
                        ConversationId = conversationId,
                        AfterReceivedAtMs = EffectiveChangedAt(maxChanged),
                        AfterMessageId = maxChanged.MessageId,
                        UpdatedAt = DateTime.UtcNow
                    }).ConfigureAwait(false);
                }
                else
                    await _db.DeleteSyncCursorAsync(session.OwnerUserId, conversationId).ConfigureAwait(false);
                return true;
            }

            if (response.Items.Count == 0 || response.NextCursor is null)
            {
                throw new InvalidDataException(
                    $"reset recovery 无法推进分页: {conversationId}, page={page}");
            }
            if (before is not null
                && before.ReceivedAtMs == response.NextCursor.ReceivedAtMs
                && string.Equals(before.MessageId, response.NextCursor.MessageId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"reset recovery 游标未推进: {conversationId}/{before.MessageId}/{before.ReceivedAtMs}");
            }
            before = response.NextCursor;
        }
    }

    private static MessageHistoryItemDto MaxChangedItem(IReadOnlyList<MessageHistoryItemDto> items)
    {
        var max = items[0];
        for (var i = 1; i < items.Count; i++)
        {
            var item = items[i];
            if (CompareChanged(item, max) > 0)
                max = item;
        }
        return max;
    }

    private static MessageHistoryItemDto MaxReceivedItem(IReadOnlyList<MessageHistoryItemDto> items)
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

    private static int CompareChanged(MessageHistoryItemDto left, MessageHistoryItemDto right)
    {
        var byTime = EffectiveChangedAt(left).CompareTo(EffectiveChangedAt(right));
        return byTime != 0 ? byTime : string.CompareOrdinal(left.MessageId, right.MessageId);
    }

    private static long EffectiveChangedAt(MessageHistoryItemDto item)
        => item.ChangedAtMs > 0 ? item.ChangedAtMs : item.ReceivedAtMs;

    private static long EffectiveChangedAt(MessageHistoryCursorDto cursor)
        => cursor.ChangedAtMs ?? cursor.ReceivedAtMs;
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
/// 同时间戳按 (ChangedAtMs, MessageId) 复合比较：本地水位消息 Id 更早时视为有更新。
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
            var changedAtMs = item.ChangedAtMs > 0 ? item.ChangedAtMs : item.ReceivedAtMs;
            if (changedAtMs > after)
                return true;
            // 同时间戳：仅当本地水位消息 Id 字典序更早（即本地落后）才视为有更新。
            if (changedAtMs == after
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
    private SyncFailureRecord? _lastFailure;
    private long _failCount;
    private int _consecutiveFailures;
    private int _syncCount;
    private long _conversationsSynced;
    private long _messagesSynced;
    private long _lastSyncedMessageAtMs;
    private readonly object _lock = new();

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public DateTime? LastSyncUtc { get { lock (_lock) return _lastSyncUtc; } }

    public long LastDurationMs { get { lock (_lock) return _lastDurationMs; } }

    public string? LastError { get { lock (_lock) return _lastError; } }

    public SyncFailureRecord? LastFailure { get { lock (_lock) return _lastFailure; } }

    public long FailCount { get { lock (_lock) return _failCount; } }

    public int ConsecutiveFailures { get { lock (_lock) return _consecutiveFailures; } }

    public int SyncCount { get { lock (_lock) return _syncCount; } }

    public long ConversationsSynced => Interlocked.Read(ref _conversationsSynced);

    public long MessagesSynced => Interlocked.Read(ref _messagesSynced);

    public long LastSyncedMessageAtMs => Interlocked.Read(ref _lastSyncedMessageAtMs);

    /// <summary>记录最近同步到的消息时间（仅推进，不回退）。</summary>
    public void RecordSyncedMessages(long receivedAtMs)
    {
        if (receivedAtMs <= 0)
            return;
        long current;
        while (receivedAtMs > (current = Interlocked.Read(ref _lastSyncedMessageAtMs)))
        {
            if (Interlocked.CompareExchange(ref _lastSyncedMessageAtMs, receivedAtMs, current) == current)
                break;
        }
    }

    public void MarkSuccess(long durationMs)
    {
        Volatile.Write(ref _isRunning, 0);
        lock (_lock)
        {
            _lastSyncUtc = DateTime.UtcNow;
            _lastDurationMs = durationMs;
            _lastError = null;
            _consecutiveFailures = 0;
            _syncCount++;
        }
    }

    /// <summary>
    /// 记录一次同步失败：更新最近失败诊断记录、累计失败计数与连续失败计数。
    /// <paramref name="transient"/> 标记该错误是否可自动重试。
    /// </summary>
    public void MarkFailed(string? errorCode, string? errorMessage, bool transient)
    {
        Volatile.Write(ref _isRunning, 0);
        lock (_lock)
        {
            var normalizedCode = string.IsNullOrWhiteSpace(errorCode) ? "UNKNOWN" : errorCode!;
            _lastError = string.IsNullOrWhiteSpace(errorMessage) ? normalizedCode : $"{normalizedCode}: {errorMessage}";
            _lastFailure = new SyncFailureRecord(normalizedCode, errorMessage, DateTime.UtcNow, transient);
            _failCount++;
            _consecutiveFailures++;
        }
    }

    public void AddConversations(int count) => Interlocked.Add(ref _conversationsSynced, count);

    public void AddMessages(int count) => Interlocked.Add(ref _messagesSynced, count);
}
