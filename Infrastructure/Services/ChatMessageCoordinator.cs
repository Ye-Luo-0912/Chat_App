using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Channels;
using Core.Diagnostics;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Serilog;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 网络事件 → 持久化 桥接协调器。
/// 订阅 IChatSessionClient 的网络事件，调用 IMessageStore 做去重与本地事务持久化，
/// IMessageStore 内部通过 IEventBus 发布领域事件供 UI 层增量更新。
/// 本协调器本身不直接操作 UI。
/// 使用有界 Channel + 单消费者保证入站事件顺序。
/// 可靠队列策略：FullMode=Wait，绝不静默丢弃；背压超时则主动断开连接，
/// 由同步水位重连后重新拉取 —— 宁可断线重同步，也不丢状态事件。
/// 账户/连接隔离：事件入队时捕获 SessionStamp，消费时校验代际，
/// 过期事件丢弃，绝不写入当前新账户。
/// </summary>
public sealed class ChatMessageCoordinator : IDisposable, IMetricsSource
{
    public string Name => "inbound_pump";

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        ["inbound_queue_depth"] = _inboundChannel.Reader.Count,
        ["total_processed"] = TotalProcessed,
        ["stale_dropped"] = StaleDropped,
        ["backpressure_disconnects"] = InboundBackpressureDisconnects,
        ["overflow_count"] = InboundOverflowCount,
        ["queue_wait_duration_ms"] = InboundQueueWaitDurationMs,
        ["last_processed_sequence"] = LastProcessedSequence
    };

    public IReadOnlyDictionary<string, HistogramSnapshot> Histograms =>
        new Dictionary<string, HistogramSnapshot>();
    private readonly IMessageStore _messageStore;
    private readonly IChatSessionClient _chatSession;
    private readonly ICurrentUserContext _currentUserContext;

    // 有界 Channel：单消费者，保证同一会话内事件严格有序。
    // FullMode=Wait：队列满时入队等待（而非 DropOldest 静默丢弃），背压超时由后台任务断开连接。
    private readonly Channel<InboundMutation> _inboundChannel =
        Channel.CreateBounded<InboundMutation>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly CancellationTokenSource _cts = new();
    private Task? _consumeTask;
    private bool _disposed;

    // 背压处理：入队等待超时（毫秒）后主动断开连接，触发重连同步。
    private const int BackpressureWaitMs = 5000;

    // ——— 入站队列指标———
    private long _overflowCount;
    private long _backpressureDisconnects;
    private long _backpressureDisconnectArmed;
    private long _lastProcessedSequence;
    private long _totalProcessed;
    private long _staleDropped;
    private long _inboundQueueWaitDurationMs;

    /// <summary>当前队列深度（等待消费的事件数）。</summary>
    public int InboundQueueDepth => _inboundChannel.Reader.Count;

    /// <summary>累计入队背压等待总耗时（毫秒）。</summary>
    public long InboundQueueWaitDurationMs => Interlocked.Read(ref _inboundQueueWaitDurationMs);

    /// <summary>累计背压溢出次数（TryWrite 失败的次数）。</summary>
    public long InboundOverflowCount => Interlocked.Read(ref _overflowCount);

    /// <summary>累计因背压超时主动断开的次数。</summary>
    public long InboundBackpressureDisconnects => Interlocked.Read(ref _backpressureDisconnects);

    /// <summary>最后处理的入站事件序号。</summary>
    public long LastProcessedSequence => Interlocked.Read(ref _lastProcessedSequence);

    /// <summary>累计成功处理的事件数。</summary>
    public long TotalProcessed => Interlocked.Read(ref _totalProcessed);

    /// <summary>累计因会话过期丢弃的事件数。</summary>
    public long StaleDropped => Interlocked.Read(ref _staleDropped);

    public ChatMessageCoordinator(
        IChatSessionClient chatSession,
        IMessageStore messageStore,
        ICurrentUserContext currentUserContext)
    {
        _messageStore = messageStore;
        _chatSession = chatSession;
        _currentUserContext = currentUserContext;

        // 订阅所有网络事件 —— 处理器仅向 Channel 入队
        _chatSession.ChatMessageReceived += OnChatMessageReceived;
        _chatSession.MessageAcknowledged += OnMessageAcknowledged;
        _chatSession.ConversationChanged += OnConversationChanged;
        _chatSession.MessageRecalled += OnMessageRecalled;
        _chatSession.MessageEdited += OnMessageEdited;
        _chatSession.MessageReceiptReceived += OnMessageReceiptReceived;
        _chatSession.MessageReceiptUpdated += OnMessageReceiptUpdated;
        _chatSession.UnreadCountChanged += OnUnreadCountChanged;

        // 群聊领域事件：进入同一有序入站队列（SessionStamp 校验 → 版本比较 → 事务落库 → 领域事件）。
        _chatSession.GroupMemberJoined += OnGroupMemberJoined;
        _chatSession.GroupMemberLeft += OnGroupMemberLeft;
        _chatSession.GroupMemberRemoved += OnGroupMemberRemoved;
        _chatSession.GroupRoleChanged += OnGroupRoleChanged;
        _chatSession.GroupMembersAdded += OnGroupMembersAdded;
        _chatSession.GroupConversationDissolved += OnGroupConversationDissolved;

        // 启动单消费者
        _consumeTask = Task.Run(ConsumeLoopAsync);
    }

    // 每个事件处理器在入队时捕获当前 SessionStamp（OwnerUserId + 连接代际），
    // 消费时校验，防止账户切换后旧事件写入新账户。

    private void OnChatMessageReceived(object? sender, ChatMessageDto dto)
        => EnqueueMutation(dto, InboundMutationKind.ChatMessage, "消息", dto.SenderUserId);

    private void OnMessageAcknowledged(object? sender, MessageAcknowledgementDto ack)
        => EnqueueMutation(ack, InboundMutationKind.MessageAck, "消息确认", ack.ClientMessageId);

    private void OnConversationChanged(object? sender, ConversationChangedDto dto)
        => EnqueueMutation(dto, InboundMutationKind.ConversationChanged, "会话变更", dto.ConversationId);

    private void OnMessageRecalled(object? sender, MessageRecalledUpdateDto update)
        => EnqueueMutation(update, InboundMutationKind.MessageRecalled, "消息撤回", update.MessageId);

    private void OnMessageEdited(object? sender, MessageEditedUpdateDto update)
        => EnqueueMutation(update, InboundMutationKind.MessageEdited, "消息编辑", update.MessageId);

    private void OnMessageReceiptReceived(object? sender, MessageReceiptDto dto)
        => EnqueueMutation(dto, InboundMutationKind.MessageReceiptReceived, "已读回执");

    private void OnMessageReceiptUpdated(object? sender, MessageReceiptUpdatedDto dto)
        => EnqueueMutation(dto, InboundMutationKind.MessageReceiptUpdated, "已读状态更新");

    private void OnUnreadCountChanged(object? sender, UnreadCountChangedDto dto)
        => EnqueueMutation(dto, InboundMutationKind.UnreadCountChanged, "未读数变更", dto.ConversationId);

    private void OnGroupMemberJoined(object? sender, MemberJoinedUpdateDto dto)
        => EnqueueMutation(dto, InboundMutationKind.GroupMemberJoined, "成员加入", dto.UserId);

    private void OnGroupMemberLeft(object? sender, MemberLeftUpdateDto dto)
        => EnqueueMutation(dto, InboundMutationKind.GroupMemberLeft, "成员退出", dto.UserId);

    private void OnGroupMemberRemoved(object? sender, MemberRemovedUpdateDto dto)
        => EnqueueMutation(dto, InboundMutationKind.GroupMemberRemoved, "成员被移除", dto.UserId);

    private void OnGroupRoleChanged(object? sender, RoleChangedUpdateDto dto)
        => EnqueueMutation(dto, InboundMutationKind.GroupRoleChanged, "角色变更", dto.UserId);

    private void OnGroupMembersAdded(object? sender, MembersAddedUpdateDto dto)
        => EnqueueMutation(dto, InboundMutationKind.GroupMembersAdded, "批量加入", dto.ConversationId);

    private void OnGroupConversationDissolved(object? sender, ConversationDissolvedUpdateDto dto)
        => EnqueueMutation(dto, InboundMutationKind.GroupConversationDissolved, "群解散", dto.ConversationId);

    /// <summary>
    /// 统一入站事件入队模板：捕获当前 SessionStamp 后写入 Channel。
    /// 队列满时 TryWrite 失败 → 在回调线程上同步等待空位（严格有序单一 ingress：
    /// 事件回调由接收循环串行触发，等待期间不产生任何新事件，恢复写入顺序与网络顺序一致），
    /// 超过 BackpressureWaitMs 未写入则主动断开连接（重连后按同步水位补拉，宁断不丢）。
    /// 用户未登录时以警告日志丢弃事件。
    /// </summary>
    private void EnqueueMutation<T>(T payload, InboundMutationKind kind, string label, object? id = null)
    {
        if (!_currentUserContext.TryGetUserId(out var userId))
        {
            if (id is not null)
                Log.Warning("收到{Label}但用户未登录，丢弃 {Id}", label, id);
            else
                Log.Warning("收到{Label}但用户未登录，丢弃", label);
            return;
        }

        var stamp = new SessionStamp(userId, _chatSession.ConnectionGeneration, _chatSession.ConnectionId);
        var mutation = new InboundMutation(kind, stamp, payload);

        if (_inboundChannel.Writer.TryWrite(mutation))
            return;

        // 背压断连已触发（连接即将断开）：事件由重连后的同步水位恢复，
        // 立即放弃等待，避免在断连前的残余事件上串行堆积 5s×N。
        if (Volatile.Read(ref _backpressureDisconnectArmed) != 0)
            return;

        // 队列已满（FullMode=Wait 下 TryWrite 一定失败）：同步等待空位写入。
        // 回调串行 → 等待期间不会有其他事件入队 → 恢复后写入顺序 = 原始网络顺序。
        // 不创建每事件后台任务（避免并发等待导致的重排与 task storm）。
        Interlocked.Increment(ref _overflowCount);
        var sw = Stopwatch.StartNew();
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
        {
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(BackpressureWaitMs));
            try
            {
                _inboundChannel.Writer.WriteAsync(mutation, timeoutCts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
            {
                // 背压超时：消费端卡死（如 DB 死锁）。主动断开，重连后按同步水位补拉。
                // 同一背压事件只断开一次（CompareExchange 原子抢占），避免断连风暴。
                if (Interlocked.CompareExchange(ref _backpressureDisconnectArmed, 1, 0) == 0)
                {
                    Interlocked.Increment(ref _backpressureDisconnects);
                    Log.Error(
                        "入站队列背压超时（{Ms}ms），队列深度={Depth}，主动断开连接以避免丢事件",
                        BackpressureWaitMs, _inboundChannel.Reader.Count);
                    _ = _chatSession.DisconnectAsync("入站队列背压超时，重连同步");
                }
            }
            catch (OperationCanceledException)
            {
                // 协调器关闭
            }
            finally
            {
                Interlocked.Add(ref _inboundQueueWaitDurationMs, sw.ElapsedMilliseconds);
            }
        }
    }

    private async Task ConsumeLoopAsync()
    {
        try
        {
            await foreach (var mutation in _inboundChannel.Reader.ReadAllAsync(_cts.Token))
            {
                Interlocked.Increment(ref _lastProcessedSequence);
                if (!IsSessionCurrent(mutation.Session))
                {
                    // 会话已过期（账户切换或连接代际变化）：丢弃，绝不写入当前新账户。
                    Interlocked.Increment(ref _staleDropped);
                    Log.Warning(
                        "丢弃过期入站事件 Kind={Kind}（事件 Owner={Owner}/Gen={Gen}，当前 Owner={Current}/Gen={CurrentGen}）",
                        mutation.Kind, mutation.Session.OwnerUserId, mutation.Session.Generation,
                        CurrentOwnerOrZero(), _chatSession.ConnectionGeneration);
                    continue;
                }

                try
                {
                    await DispatchAsync(mutation);
                    Interlocked.Increment(ref _totalProcessed);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理入站事件失败 Kind={Kind}", mutation.Kind);
                }

                // 队列已腾出空间：复位背压断连标记，允许下一次独立背压事件再次触发断连。
                Volatile.Write(ref _backpressureDisconnectArmed, 0);
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool IsSessionCurrent(SessionStamp stamp)
    {
        if (!_currentUserContext.TryGetUserId(out var currentOwner))
            return false;
        return stamp.OwnerUserId == currentOwner && stamp.Generation == _chatSession.ConnectionGeneration;
    }

    private long CurrentOwnerOrZero()
        => _currentUserContext.TryGetUserId(out var owner) ? owner : 0;

    private async Task DispatchAsync(InboundMutation mutation)
    {
        var ct = _cts.Token;
        switch (mutation.Kind)
        {
            case InboundMutationKind.ChatMessage:
                await _messageStore.PersistIncomingAsync(mutation.Session, (ChatMessageDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.MessageAck:
                await _messageStore.HandleAckAsync(mutation.Session, (MessageAcknowledgementDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.ConversationChanged:
                await _messageStore.HandleConversationChangedAsync(mutation.Session, (ConversationChangedDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.MessageRecalled:
                await _messageStore.HandleRecalledAsync(mutation.Session, (MessageRecalledUpdateDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.MessageEdited:
                await _messageStore.HandleEditedAsync(mutation.Session, (MessageEditedUpdateDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.MessageReceiptReceived:
                await _messageStore.HandleReceiptAsync(mutation.Session, (MessageReceiptDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.MessageReceiptUpdated:
                await _messageStore.HandleReceiptUpdatedAsync(mutation.Session, (MessageReceiptUpdatedDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.UnreadCountChanged:
                await _messageStore.HandleUnreadCountChangedAsync(mutation.Session, (UnreadCountChangedDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.GroupMemberJoined:
                await _messageStore.HandleGroupMemberJoinedAsync(mutation.Session, (MemberJoinedUpdateDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.GroupMemberLeft:
                await _messageStore.HandleGroupMemberLeftAsync(mutation.Session, (MemberLeftUpdateDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.GroupMemberRemoved:
                await _messageStore.HandleGroupMemberRemovedAsync(mutation.Session, (MemberRemovedUpdateDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.GroupRoleChanged:
                await _messageStore.HandleGroupRoleChangedAsync(mutation.Session, (RoleChangedUpdateDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.GroupMembersAdded:
                await _messageStore.HandleGroupMembersAddedAsync(mutation.Session, (MembersAddedUpdateDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.GroupConversationDissolved:
                await _messageStore.HandleGroupConversationDissolvedAsync(mutation.Session, (ConversationDissolvedUpdateDto)mutation.Payload!, ct);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _chatSession.ChatMessageReceived -= OnChatMessageReceived;
        _chatSession.MessageAcknowledged -= OnMessageAcknowledged;
        _chatSession.ConversationChanged -= OnConversationChanged;
        _chatSession.MessageRecalled -= OnMessageRecalled;
        _chatSession.MessageEdited -= OnMessageEdited;
        _chatSession.MessageReceiptReceived -= OnMessageReceiptReceived;
        _chatSession.MessageReceiptUpdated -= OnMessageReceiptUpdated;
        _chatSession.UnreadCountChanged -= OnUnreadCountChanged;
        _chatSession.GroupMemberJoined -= OnGroupMemberJoined;
        _chatSession.GroupMemberLeft -= OnGroupMemberLeft;
        _chatSession.GroupMemberRemoved -= OnGroupMemberRemoved;
        _chatSession.GroupRoleChanged -= OnGroupRoleChanged;
        _chatSession.GroupMembersAdded -= OnGroupMembersAdded;
        _chatSession.GroupConversationDissolved -= OnGroupConversationDissolved;

        _cts.Cancel();
        _inboundChannel.Writer.TryComplete();
        try { _consumeTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* 忽略关闭时的未观察异常 */ }
        _cts.Dispose();
    }

    private enum InboundMutationKind
    {
        ChatMessage,
        MessageAck,
        ConversationChanged,
        MessageRecalled,
        MessageEdited,
        MessageReceiptReceived,
        MessageReceiptUpdated,
        UnreadCountChanged,
        GroupMemberJoined,
        GroupMemberLeft,
        GroupMemberRemoved,
        GroupRoleChanged,
        GroupMembersAdded,
        GroupConversationDissolved
    }

    private readonly record struct InboundMutation(
        InboundMutationKind Kind,
        SessionStamp Session,
        object? Payload);
}
