using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Services;
using System.Collections.Concurrent;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 入站队列满载背压测试：Coordinator 有界 Channel（512）满时
/// 转入背压等待路径，超过 BackpressureWaitMs（5s）必须主动断开连接
/// （宁可断线重同步，也不静默丢弃事件），且队列耗尽后事件仍全部处理。
/// </summary>
public class CoordinatorBackpressureTests
{
    private const long UserA = 1001;

    private sealed class SwitchableUserContext : ICurrentUserContext
    {
        public long Generation { get; set; } = 1;
        public long? UserId { get; set; }
        public string? UserName => UserId is { } id ? $"user-{id}" : null;
        public bool IsAuthenticated => UserId is > 0;
        public bool HasUserId => UserId is > 0;
        public UserSessionSnapshot Snapshot => new(UserId ?? 0, Generation, UserName, null, null);
        public long RequireUserId() => UserId ?? throw new InvalidOperationException("未登录");
        public bool TryGetUserId(out long id)
        {
            id = UserId ?? 0;
            return UserId is > 0;
        }
    }

    /// <summary>
    /// 记录型存储 stub：PersistIncomingAsync 先记录再等待门闩。
    /// 门闩未释放时消费者卡死 → 队列填满 → 触发背压路径。
    /// </summary>
    private sealed class StoreStub : IMessageStore
    {
        public readonly ConcurrentBag<(SessionStamp Session, string Kind)> Calls = new();
        public TaskCompletionSource? Gate { get; set; }

        public int PersistCount => Calls.Count(c => c.Kind == "chat");

        public async Task<bool> PersistIncomingAsync(SessionStamp session, ChatMessageDto dto, CancellationToken ct = default)
        {
            Calls.Add((session, "chat"));
            if (Gate is not null)
                await Gate.Task.WaitAsync(ct);
            return true;
        }

        public Task PersistHistoryAsync(SessionStamp session, string conversationId, IReadOnlyList<MessageHistoryItemDto> items, CancellationToken ct = default)
        { Calls.Add((session, "history")); return Task.CompletedTask; }

        public Task ApplyHistoryBatchAsync(SessionStamp session, string conversationId, IReadOnlyList<MessageHistoryItemDto> items, LocalSyncCursor? cursor, CancellationToken ct = default)
        { Calls.Add((session, "history_batch")); return Task.CompletedTask; }

        public Task HandleAckAsync(SessionStamp session, MessageAcknowledgementDto ack, CancellationToken ct = default)
        { Calls.Add((session, "ack")); return Task.CompletedTask; }

        public Task HandleRecalledAsync(SessionStamp session, MessageRecalledUpdateDto update, CancellationToken ct = default)
        { Calls.Add((session, "recall")); return Task.CompletedTask; }

        public Task HandleEditedAsync(SessionStamp session, MessageEditedUpdateDto update, CancellationToken ct = default)
        { Calls.Add((session, "edit")); return Task.CompletedTask; }

        public Task HandleConversationChangedAsync(SessionStamp session, ConversationChangedDto dto, CancellationToken ct = default)
        { Calls.Add((session, "conv")); return Task.CompletedTask; }

        public Task<List<LocalMessage>> LoadHistoryAsync(SessionStamp session, string conversationId, int limit = 100, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default)
        { Calls.Add((session, "load")); return Task.FromResult(new List<LocalMessage>()); }

        public Task MarkConversationReadAsync(SessionStamp session, string conversationId, string? lastReadMessageId, CancellationToken ct = default)
        { Calls.Add((session, "mark_read")); return Task.CompletedTask; }

        public Task<List<LocalConversation>> GetConversationsAsync(SessionStamp session, CancellationToken ct = default)
        { Calls.Add((session, "conv_list")); return Task.FromResult(new List<LocalConversation>()); }

        public Task<IReadOnlyList<ConversationSyncWatermarkDto>> GetSyncWatermarksAsync(SessionStamp session, CancellationToken ct = default)
        { Calls.Add((session, "watermarks")); return Task.FromResult<IReadOnlyList<ConversationSyncWatermarkDto>>([]); }

        public Task HandleReceiptAsync(SessionStamp session, MessageReceiptDto dto, CancellationToken ct = default)
        { Calls.Add((session, "receipt")); return Task.CompletedTask; }

        public Task HandleReceiptUpdatedAsync(SessionStamp session, MessageReceiptUpdatedDto dto, CancellationToken ct = default)
        { Calls.Add((session, "receipt_updated")); return Task.CompletedTask; }

        public Task HandleUnreadCountChangedAsync(SessionStamp session, UnreadCountChangedDto dto, CancellationToken ct = default)
        { Calls.Add((session, "unread")); return Task.CompletedTask; }

        public Task<List<LocalMessage>> FetchAndPersistHistoryAsync(SessionStamp session, string conversationId, int limit = 50, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default)
        { Calls.Add((session, "fetch_history")); return Task.FromResult(new List<LocalMessage>()); }

        public Task MarkConversationReadAndNotifyAsync(SessionStamp session, string conversationId, string? lastReadMessageId, CancellationToken ct = default)
        { Calls.Add((session, "mark_read_notify")); return Task.CompletedTask; }

        public Task<int> MarkOutboxPermanentByConversationAsync(long ownerUserId, string conversationId, string reason, CancellationToken ct = default)
        { Calls.Add((new SessionStamp(ownerUserId, 0, Guid.Empty), "outbox_permanent")); return Task.FromResult(1); }

        public Task HandleGroupMemberJoinedAsync(SessionStamp session, MemberJoinedUpdateDto dto, CancellationToken ct = default)
        { Calls.Add((session, "group_joined")); return Task.CompletedTask; }

        public Task HandleGroupMemberLeftAsync(SessionStamp session, MemberLeftUpdateDto dto, CancellationToken ct = default)
        { Calls.Add((session, "group_left")); return Task.CompletedTask; }

        public Task HandleGroupMemberRemovedAsync(SessionStamp session, MemberRemovedUpdateDto dto, CancellationToken ct = default)
        { Calls.Add((session, "group_removed")); return Task.CompletedTask; }

        public Task HandleGroupRoleChangedAsync(SessionStamp session, RoleChangedUpdateDto dto, CancellationToken ct = default)
        { Calls.Add((session, "group_role")); return Task.CompletedTask; }

        public Task HandleGroupMembersAddedAsync(SessionStamp session, MembersAddedUpdateDto dto, CancellationToken ct = default)
        { Calls.Add((session, "group_added")); return Task.CompletedTask; }

        public Task HandleGroupConversationDissolvedAsync(SessionStamp session, ConversationDissolvedUpdateDto dto, CancellationToken ct = default)
        { Calls.Add((session, "group_dissolved")); return Task.CompletedTask; }

        public void Reset() => Calls.Clear();
    }

    /// <summary>会话 stub：记录 DisconnectAsync 调用（背压超时断连验收）。</summary>
    private sealed class SessionStub : IChatSessionClient
    {
        public bool IsConnected { get; set; } = true;
        public bool IsAuthenticated { get; set; } = true;
        public long CurrentUserId { get; set; } = UserA;
        public long ConnectionGeneration { get; set; } = 7;
        public Guid ConnectionId { get; set; } = Guid.NewGuid();
        public SessionStamp CurrentSession => new(CurrentUserId, ConnectionGeneration, ConnectionId);

        public int DisconnectCalls;
        public string? LastDisconnectReason;

        public event EventHandler? Connected;
        public event EventHandler<long>? Authenticated;
        public event EventHandler<string>? AuthenticationFailed;
        public event EventHandler<ProtocolErrorDto>? ProtocolError;
        public event EventHandler<ChatMessageDto>? ChatMessageReceived;
        public event EventHandler<MessageAcknowledgementDto>? MessageAcknowledged;
        public event EventHandler<ConversationChangedDto>? ConversationChanged;
        public event EventHandler<MessageRecalledUpdateDto>? MessageRecalled;
        public event EventHandler<MessageEditedUpdateDto>? MessageEdited;
        public event EventHandler<TypingUpdateDto>? TypingUpdated;
        public event EventHandler<PresenceChangedDto>? PresenceChanged;
        public event EventHandler<string>? ConnectionClosed;
        public event EventHandler<MessageReceiptDto>? MessageReceiptReceived;
        public event EventHandler<MessageReceiptUpdatedDto>? MessageReceiptUpdated;
        public event EventHandler<MessageHistoryPageDto>? MessageHistoryPageReceived;
        public event EventHandler<ConversationMarkReadResponseDto>? ConversationMarkReadResponse;
        public event EventHandler<UnreadCountChangedDto>? UnreadCountChanged;
        public event EventHandler<CallSignalDto>? CallSignalReceived;
        public event EventHandler<MemberJoinedUpdateDto>? GroupMemberJoined;
        public event EventHandler<MemberLeftUpdateDto>? GroupMemberLeft;
        public event EventHandler<MemberRemovedUpdateDto>? GroupMemberRemoved;
        public event EventHandler<RoleChangedUpdateDto>? GroupRoleChanged;
        public event EventHandler<MembersAddedUpdateDto>? GroupMembersAdded;
        public event EventHandler<ConversationDissolvedUpdateDto>? GroupConversationDissolved;

        public void RaiseChatMessage(ChatMessageDto dto) => ChatMessageReceived?.Invoke(this, dto);
        public void RaiseMessageAck(MessageAcknowledgementDto dto) => MessageAcknowledged?.Invoke(this, dto);
        public void RaiseConversationChanged(ConversationChangedDto dto) => ConversationChanged?.Invoke(this, dto);
        public void RaiseMessageRecalled(MessageRecalledUpdateDto dto) => MessageRecalled?.Invoke(this, dto);
        public void RaiseMessageEdited(MessageEditedUpdateDto dto) => MessageEdited?.Invoke(this, dto);
        public void RaiseMessageReceipt(MessageReceiptDto dto) => MessageReceiptReceived?.Invoke(this, dto);
        public void RaiseUnreadCountChanged(UnreadCountChangedDto dto) => UnreadCountChanged?.Invoke(this, dto);

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(string accessToken, long userId, string? sessionId, ulong? deviceIdHash, CancellationToken ct = default)
        {
            CurrentUserId = userId;
            IsAuthenticated = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(string? reason = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref DisconnectCalls);
            LastDisconnectReason = reason;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<string> SendChatMessageAsync(long targetUserId, string? content, IReadOnlyList<string>? attachmentIds = null, string? replyToMessageId = null, long? replyToSenderUserId = null, string? replyToPreview = null, string? forwardedFromMessageId = null, long? forwardedFromSenderUserId = null, string? forwardedFromPreview = null, string? clientMessageId = null, string? conversationId = null, IReadOnlyList<long>? mentionedUserIds = null, IReadOnlyList<global::ChatApp.Shared.Protocol.Tcp.TcpAttachmentRef>? attachments = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ConversationListResponseDto> QueryConversationListAsync(int limit = 50, bool? beforeIsPinned = null, long? beforePinnedAtMs = null, long? beforeLastMessageAtMs = null, string? beforeConversationId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ConversationSetPrefsResponseDto> SetConversationPrefsAsync(string conversationId, bool? pinned = null, bool? muted = null, long? mutedUntilMs = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageRecallAcknowledgementDto> RecallMessageAsync(string messageId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageEditAcknowledgementDto> EditMessageAsync(string messageId, string content, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SendTypingNotifyAsync(long targetUserId, bool isTyping, string? conversationId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<PresenceSnapshotResponseDto> QueryPresenceAsync(IReadOnlyList<long> userIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UnwatchPresenceAsync(IReadOnlyList<long> userIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SyncBootstrapResponseDto> QuerySyncBootstrapAsync(int listLimit = 50, int historyLimitPerConversation = 20, int maxConversationsWithHistory = 10, IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageHistoryPageDto> QueryMessageHistoryAsync(string conversationId, int limit = 50, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageReceiptAckDto> SendMessageReceiptAsync(string conversationId, string? lastReadMessageId, long? lastReadAtMs, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ConversationMarkReadResponseDto> MarkConversationReadAsync(string conversationId, string? lastReadMessageId = null, long? lastReadAtMs = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<CreateGroupResponseDto> CreateGroupAsync(string title, IReadOnlyList<long>? memberUserIds = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AddGroupMembersResponseDto> AddGroupMembersAsync(string conversationId, IReadOnlyList<long> memberUserIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<RemoveGroupMemberResponseDto> RemoveGroupMemberAsync(string conversationId, long targetUserId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<LeaveGroupResponseDto> LeaveGroupAsync(string conversationId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<DissolveGroupResponseDto> DissolveGroupAsync(string conversationId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ChangeMemberRoleResponseDto> ChangeMemberRoleAsync(string conversationId, long targetUserId, ConversationMemberRole newRole, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ListGroupMembersResponseDto> ListGroupMembersAsync(string conversationId, int? pageSize = null, string? cursor = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }

    private static ChatMessageDto NewMessage(int i) => new()
    {
        MessageId = $"m-{i}",
        TargetUserId = UserA,
        SenderUserId = UserA,
        Content = $"事件 {i}"
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"条件在 {timeout.TotalSeconds:F1}s 内未满足");
    }

    /// <summary>
    /// 核心验收：消费者卡死（store 门闩）→ 入队超过 512 容量 → 背压路径触发：
    /// overflow 计数、超时后主动断连（backpressure_disconnects）、断开原因正确；
    /// 释放门闩后队列中的全部事件仍被处理（不丢事件）。
    /// </summary>
    [Fact]
    public async Task Inbound_Queue_Overflow_Triggers_Backpressure_Disconnect()
    {
        const int totalEvents = 600; // 超过 512 容量
        var ctx = new SwitchableUserContext { UserId = UserA, Generation = 1 };
        var session = new SessionStub();
        var store = new StoreStub { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var coordinator = new ChatMessageCoordinator(session, store, ctx);

        // 第一个事件让消费者进入门闩等待（卡死消费端）
        session.RaiseChatMessage(NewMessage(0));
        await WaitUntilAsync(() => store.Calls.Count >= 1, TimeSpan.FromSeconds(3));

        // 灌满队列并超量：前 512 条 TryWrite 成功，其余走背压等待路径
        for (var i = 1; i < totalEvents; i++)
            session.RaiseChatMessage(NewMessage(i));

        // 背压等待路径已触发：overflow 计数 ≥ 1（首个溢出事件进入同步等待；断连后的事件直接放弃）
        var overflow = coordinator.InboundOverflowCount;
        Assert.True(overflow >= 1, $"溢出计数异常: {overflow}");

        // 等背压超时（BackpressureWaitMs=5000）：必须主动断开连接，且只断开一次（无断连风暴）
        await WaitUntilAsync(() => Interlocked.CompareExchange(ref session.DisconnectCalls, 0, 0) >= 1,
            TimeSpan.FromSeconds(9));
        Assert.Equal(1, Volatile.Read(ref session.DisconnectCalls));
        Assert.True(coordinator.InboundBackpressureDisconnects >= 1, "背压断开计数未增长");
        Assert.Contains("入站队列背压超时", session.LastDisconnectReason ?? "");

        // 释放消费端：入队成功的事件全部处理完、队列清空。
        // 溢出的事件按设计不写入（首个超时丢弃 + 断连后直接放弃，靠重连同步水位恢复）。
        // 队列容量 512 + 首个被消费的事件 = 513。
        const int expectedEnqueued = 513;
        store.Gate.TrySetResult();
        await WaitUntilAsync(
            () => coordinator.InboundQueueDepth == 0 && coordinator.LastProcessedSequence >= expectedEnqueued,
            TimeSpan.FromSeconds(9));

        Assert.Equal(expectedEnqueued, coordinator.LastProcessedSequence);
        Assert.Equal(expectedEnqueued, store.Calls.Count);
        Assert.Equal(0, coordinator.StaleDropped);
    }
}
