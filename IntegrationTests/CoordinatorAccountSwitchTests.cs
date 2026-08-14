using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Services;
using System.Collections.Concurrent;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 协调器账户切换隔离测试：A 事件入队后切换到 B，B 必须零新增。
/// 验证 ChatMessageCoordinator 的 SessionStamp 代际校验：
/// 入队时捕获 (OwnerUserId, Generation, ConnectionId)，消费时与当前上下文比对，
/// 过期事件丢弃（StaleDropped 计数），绝不写入当前新账户。
/// </summary>
public class CoordinatorAccountSwitchTests
{
    private const long UserA = 1001;
    private const long UserB = 2002;

    /// <summary>可切换的当前用户上下文 stub。</summary>
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
    /// 记录型消息存储 stub：记录每次调用的 (SessionStamp, Kind)。
    /// 可选 gate：PersistIncomingAsync 先记录再等待释放，用于把消费者卡在 A 事件上，
    /// 确保账户切换发生在剩余事件被消费之前（确定性验证过期丢弃路径）。
    /// </summary>
    private sealed class StoreStub : IMessageStore
    {
        public readonly ConcurrentBag<(SessionStamp Session, string Kind)> Calls = new();
        public TaskCompletionSource? Gate { get; set; }

        public int PersistCount => Calls.Count(c => c.Kind == "chat");

        private void Record(SessionStamp session, string kind) => Calls.Add((session, kind));

        public async Task<bool> PersistIncomingAsync(SessionStamp session, ChatMessageDto dto, CancellationToken ct = default)
        {
            Record(session, "chat");
            if (Gate is not null)
                await Gate.Task.WaitAsync(ct);
            return true;
        }

        public Task PersistHistoryAsync(SessionStamp session, string conversationId, IReadOnlyList<MessageHistoryItemDto> items, CancellationToken ct = default)
        { Record(session, "history"); return Task.CompletedTask; }

        public Task ApplyHistoryBatchAsync(SessionStamp session, string conversationId, IReadOnlyList<MessageHistoryItemDto> items, LocalSyncCursor? cursor, CancellationToken ct = default)
        { Record(session, "history_batch"); return Task.CompletedTask; }

        public Task HandleAckAsync(SessionStamp session, MessageAcknowledgementDto ack, CancellationToken ct = default)
        { Record(session, "ack"); return Task.CompletedTask; }

        public Task HandleRecalledAsync(SessionStamp session, MessageRecalledUpdateDto update, CancellationToken ct = default)
        { Record(session, "recall"); return Task.CompletedTask; }

        public Task HandleEditedAsync(SessionStamp session, MessageEditedUpdateDto update, CancellationToken ct = default)
        { Record(session, "edit"); return Task.CompletedTask; }

        public Task HandleConversationChangedAsync(SessionStamp session, ConversationChangedDto dto, CancellationToken ct = default)
        { Record(session, "conv"); return Task.CompletedTask; }

        public Task<List<LocalMessage>> LoadHistoryAsync(SessionStamp session, string conversationId, int limit = 100, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default)
        { Record(session, "load"); return Task.FromResult(new List<LocalMessage>()); }

        public Task MarkConversationReadAsync(SessionStamp session, string conversationId, string? lastReadMessageId, CancellationToken ct = default)
        { Record(session, "mark_read"); return Task.CompletedTask; }

        public Task<List<LocalConversation>> GetConversationsAsync(SessionStamp session, CancellationToken ct = default)
        { Record(session, "conv_list"); return Task.FromResult(new List<LocalConversation>()); }

        public Task<IReadOnlyList<ConversationSyncWatermarkDto>> GetSyncWatermarksAsync(SessionStamp session, CancellationToken ct = default)
        { Record(session, "watermarks"); return Task.FromResult<IReadOnlyList<ConversationSyncWatermarkDto>>([]); }

        public Task HandleReceiptAsync(SessionStamp session, MessageReceiptDto dto, CancellationToken ct = default)
        { Record(session, "receipt"); return Task.CompletedTask; }

        public Task HandleReceiptUpdatedAsync(SessionStamp session, MessageReceiptUpdatedDto dto, CancellationToken ct = default)
        { Record(session, "receipt_updated"); return Task.CompletedTask; }

        public Task HandleUnreadCountChangedAsync(SessionStamp session, UnreadCountChangedDto dto, CancellationToken ct = default)
        { Record(session, "unread"); return Task.CompletedTask; }

        public Task<List<LocalMessage>> FetchAndPersistHistoryAsync(SessionStamp session, string conversationId, int limit = 50, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default)
        { Record(session, "fetch_history"); return Task.FromResult(new List<LocalMessage>()); }

        public Task MarkConversationReadAndNotifyAsync(SessionStamp session, string conversationId, string? lastReadMessageId, CancellationToken ct = default)
        { Record(session, "mark_read_notify"); return Task.CompletedTask; }

        public Task<int> MarkOutboxPermanentByConversationAsync(long ownerUserId, string conversationId, string reason, CancellationToken ct = default)
        { Record(new SessionStamp(ownerUserId, 0, Guid.Empty), "outbox_permanent"); return Task.FromResult(1); }

        public Task HandleGroupMemberJoinedAsync(SessionStamp session, MemberJoinedUpdateDto dto, CancellationToken ct = default)
        { Record(session, "group_joined"); return Task.CompletedTask; }

        public Task HandleGroupMemberLeftAsync(SessionStamp session, MemberLeftUpdateDto dto, CancellationToken ct = default)
        { Record(session, "group_left"); return Task.CompletedTask; }

        public Task HandleGroupMemberRemovedAsync(SessionStamp session, MemberRemovedUpdateDto dto, CancellationToken ct = default)
        { Record(session, "group_removed"); return Task.CompletedTask; }

        public Task HandleGroupRoleChangedAsync(SessionStamp session, RoleChangedUpdateDto dto, CancellationToken ct = default)
        { Record(session, "group_role"); return Task.CompletedTask; }

        public Task HandleGroupMembersAddedAsync(SessionStamp session, MembersAddedUpdateDto dto, CancellationToken ct = default)
        { Record(session, "group_added"); return Task.CompletedTask; }

        public Task HandleGroupConversationDissolvedAsync(SessionStamp session, ConversationDissolvedUpdateDto dto, CancellationToken ct = default)
        { Record(session, "group_dissolved"); return Task.CompletedTask; }

        public void Reset() => Calls.Clear();
    }

    /// <summary>
    /// 最小会话 stub：仅实现协调器用到的事件与代际属性，其余成员抛 NotSupportedException。
    /// </summary>
    private sealed class SessionStub : IChatSessionClient
    {
        public bool IsConnected { get; set; } = true;
        public bool IsAuthenticated { get; set; } = true;
        public long CurrentUserId { get; set; }
        public long ConnectionGeneration { get; set; }
        public Guid ConnectionId { get; set; } = Guid.NewGuid();
        public SessionStamp CurrentSession => new(CurrentUserId, ConnectionGeneration, ConnectionId);

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
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<string> SendChatMessageAsync(long targetUserId, string? content, IReadOnlyList<string>? attachmentIds = null, string? replyToMessageId = null, long? replyToSenderUserId = null, string? replyToPreview = null, string? forwardedFromMessageId = null, long? forwardedFromSenderUserId = null, string? forwardedFromPreview = null, string? clientMessageId = null, string? conversationId = null, IReadOnlyList<long>? mentionedUserIds = null, CancellationToken ct = default)
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

    private static ChatMessageDto NewMessage(long owner, int i) => new()
    {
        MessageId = $"m-{owner}-{i}",
        TargetUserId = owner,
        SenderUserId = owner,
        Content = $"事件 {i}"
    };

    /// <summary>轮询等待条件（测试架同步：仅等待消费者推进，不掩盖被测逻辑竞态）。</summary>
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
    /// 核心验收：A 事件入队后切换到 B，B 必须零新增。
    /// 用 store 门闩把消费者卡在第一个 A 事件上，确保剩余事件在切换后才被消费
    /// （确定性验证过期丢弃路径）。
    /// </summary>
    [Fact]
    public async Task A_Events_Enqueued_Then_Switch_To_B_B_Receives_Nothing()
    {
        var ctx = new SwitchableUserContext { UserId = UserA, Generation = 1 };
        var session = new SessionStub { ConnectionGeneration = 7, CurrentUserId = UserA };
        var store = new StoreStub { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var coordinator = new ChatMessageCoordinator(session, store, ctx);

        // A 活跃时入队 5 个事件
        for (var i = 0; i < 5; i++)
            session.RaiseChatMessage(NewMessage(UserA, i));

        // 等消费者取走第一个事件并阻塞在 store 门闩上
        await WaitUntilAsync(() => store.Calls.Count >= 1, TimeSpan.FromSeconds(3));

        // 立即切换 B（连接代际不变，仅 owner 变化）
        ctx.UserId = UserB;
        store.Gate.TrySetResult();

        // 全部事件处理完毕
        await WaitUntilAsync(() => coordinator.LastProcessedSequence >= 5, TimeSpan.FromSeconds(3));

        // B 零新增：任何 store 调用都不得携带 B 的 owner
        Assert.All(store.Calls, c => Assert.Equal(UserA, c.Session.OwnerUserId));
        // 切 B 之后的事件全部按过期丢弃，绝不写入
        Assert.Equal(4, coordinator.StaleDropped);
        Assert.Equal(1, coordinator.TotalProcessed);
        Assert.Equal(1, store.PersistCount);
        // 全部事件有账可查
        Assert.Equal(5, coordinator.LastProcessedSequence);
    }

    /// <summary>对照组：无切换时事件正常持久化（证明测试机制有效而非全量丢弃）。</summary>
    [Fact]
    public async Task Same_Owner_Events_Are_Processed_When_No_Switch()
    {
        var ctx = new SwitchableUserContext { UserId = UserA, Generation = 1 };
        var session = new SessionStub { ConnectionGeneration = 7, CurrentUserId = UserA };
        var store = new StoreStub();
        using var coordinator = new ChatMessageCoordinator(session, store, ctx);

        for (var i = 0; i < 2; i++)
            session.RaiseChatMessage(NewMessage(UserA, i));

        await WaitUntilAsync(() => coordinator.LastProcessedSequence >= 2, TimeSpan.FromSeconds(3));

        Assert.Equal(0, coordinator.StaleDropped);
        Assert.Equal(2, coordinator.TotalProcessed);
        Assert.Equal(2, store.PersistCount);
        Assert.All(store.Calls, c => Assert.Equal(UserA, c.Session.OwnerUserId));
    }

    /// <summary>
    /// 连接代际变化同样使入队事件过期：同 owner 重连（代际 7 → 8）后，
    /// 旧代际事件必须丢弃，不得写入。
    /// </summary>
    [Fact]
    public async Task Generation_Change_Drops_Pending_Events_Even_For_Same_Owner()
    {
        var ctx = new SwitchableUserContext { UserId = UserA, Generation = 1 };
        var session = new SessionStub { ConnectionGeneration = 7, CurrentUserId = UserA };
        var store = new StoreStub { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var coordinator = new ChatMessageCoordinator(session, store, ctx);

        for (var i = 0; i < 3; i++)
            session.RaiseChatMessage(NewMessage(UserA, i));

        await WaitUntilAsync(() => store.Calls.Count >= 1, TimeSpan.FromSeconds(3));

        // 重连：代际递增，owner 不变
        session.ConnectionGeneration = 8;
        store.Gate.TrySetResult();

        await WaitUntilAsync(() => coordinator.LastProcessedSequence >= 3, TimeSpan.FromSeconds(3));

        Assert.Equal(2, coordinator.StaleDropped);
        Assert.Equal(1, coordinator.TotalProcessed);
        Assert.Equal(1, store.PersistCount);
    }
}
