using System.Threading.Channels;
using Core.Interfaces;
using Core.Models.DTO;
using Serilog;

namespace Infrastructure.Services;

/// <summary>
/// 网络事件 → 持久化 桥接协调器。
/// 订阅 IChatSessionClient 的网络事件，调用 IMessageStore 做去重与本地事务持久化，
/// IMessageStore 内部通过 IEventBus 发布领域事件供 UI 层增量更新。
/// 本协调器本身不直接操作 UI。
/// 使用有界 Channel + 单消费者保证入站事件顺序（P0-5）。
/// </summary>
public sealed class ChatMessageCoordinator : IDisposable
{
    private readonly IMessageStore _messageStore;
    private readonly IChatSessionClient _chatSession;
    private readonly ICurrentUserContext _currentUserContext;

    // 有界 Channel：单消费者，保证同一会话内事件严格有序
    private readonly Channel<InboundMutation> _inboundChannel =
        Channel.CreateBounded<InboundMutation>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // 溢出时丢弃最旧事件，避免 OOM
            SingleReader = true,
            SingleWriter = false
        });

    private readonly CancellationTokenSource _cts = new();
    private Task? _consumeTask;
    private bool _disposed;

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

        // 启动单消费者
        _consumeTask = Task.Run(ConsumeLoopAsync);
    }

    // 每个事件处理器在入队时捕获当前 OwnerUserId，然后将 InboundMutation 入队。
    // 若用户未登录（RequireUserId 会抛异常），则以警告日志丢弃事件。

    private void OnChatMessageReceived(object? sender, ChatMessageDto dto)
    {
        if (!_currentUserContext.TryGetUserId(out var userId))
        {
            Log.Warning("收到消息但用户未登录，丢弃 SenderUserId={SenderUserId}", dto.SenderUserId);
            return;
        }
        _inboundChannel.Writer.TryWrite(new InboundMutation(
            InboundMutationKind.ChatMessage, userId, dto));
    }

    private void OnMessageAcknowledged(object? sender, MessageAcknowledgementDto ack)
    {
        if (!_currentUserContext.TryGetUserId(out var userId))
        {
            Log.Warning("收到消息确认但用户未登录，丢弃 ClientMessageId={ClientMessageId}", ack.ClientMessageId);
            return;
        }
        _inboundChannel.Writer.TryWrite(new InboundMutation(
            InboundMutationKind.MessageAck, userId, ack));
    }

    private void OnConversationChanged(object? sender, ConversationChangedDto dto)
    {
        if (!_currentUserContext.TryGetUserId(out var userId))
        {
            Log.Warning("收到会话变更但用户未登录，丢弃 ConversationId={ConversationId}", dto.ConversationId);
            return;
        }
        _inboundChannel.Writer.TryWrite(new InboundMutation(
            InboundMutationKind.ConversationChanged, userId, dto));
    }

    private void OnMessageRecalled(object? sender, MessageRecalledUpdateDto update)
    {
        if (!_currentUserContext.TryGetUserId(out var userId))
        {
            Log.Warning("收到消息撤回但用户未登录，丢弃 MessageId={MessageId}", update.MessageId);
            return;
        }
        _inboundChannel.Writer.TryWrite(new InboundMutation(
            InboundMutationKind.MessageRecalled, userId, update));
    }

    private void OnMessageEdited(object? sender, MessageEditedUpdateDto update)
    {
        if (!_currentUserContext.TryGetUserId(out var userId))
        {
            Log.Warning("收到消息编辑但用户未登录，丢弃 MessageId={MessageId}", update.MessageId);
            return;
        }
        _inboundChannel.Writer.TryWrite(new InboundMutation(
            InboundMutationKind.MessageEdited, userId, update));
    }

    private void OnMessageReceiptReceived(object? sender, MessageReceiptDto dto)
    {
        if (!_currentUserContext.TryGetUserId(out var userId))
        {
            Log.Warning("收到已读回执但用户未登录，丢弃");
            return;
        }
        _inboundChannel.Writer.TryWrite(new InboundMutation(
            InboundMutationKind.MessageReceiptReceived, userId, dto));
    }

    private void OnMessageReceiptUpdated(object? sender, MessageReceiptUpdatedDto dto)
    {
        if (!_currentUserContext.TryGetUserId(out var userId))
        {
            Log.Warning("收到已读状态更新但用户未登录，丢弃");
            return;
        }
        _inboundChannel.Writer.TryWrite(new InboundMutation(
            InboundMutationKind.MessageReceiptUpdated, userId, dto));
    }

    private void OnUnreadCountChanged(object? sender, UnreadCountChangedDto dto)
    {
        if (!_currentUserContext.TryGetUserId(out var userId))
        {
            Log.Warning("收到未读数变更但用户未登录，丢弃 ConversationId={ConversationId}", dto.ConversationId);
            return;
        }
        _inboundChannel.Writer.TryWrite(new InboundMutation(
            InboundMutationKind.UnreadCountChanged, userId, dto));
    }

    private async Task ConsumeLoopAsync()
    {
        try
        {
            await foreach (var mutation in _inboundChannel.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await DispatchAsync(mutation);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理入站事件失败 Kind={Kind}", mutation.Kind);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DispatchAsync(InboundMutation mutation)
    {
        var ct = _cts.Token;
        switch (mutation.Kind)
        {
            case InboundMutationKind.ChatMessage:
                await _messageStore.PersistIncomingAsync((ChatMessageDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.MessageAck:
                await _messageStore.HandleAckAsync((MessageAcknowledgementDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.ConversationChanged:
                await _messageStore.HandleConversationChangedAsync((ConversationChangedDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.MessageRecalled:
                await _messageStore.HandleRecalledAsync((MessageRecalledUpdateDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.MessageEdited:
                await _messageStore.HandleEditedAsync((MessageEditedUpdateDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.MessageReceiptReceived:
                await _messageStore.HandleReceiptAsync((MessageReceiptDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.MessageReceiptUpdated:
                await _messageStore.HandleReceiptUpdatedAsync((MessageReceiptUpdatedDto)mutation.Payload!, ct);
                break;
            case InboundMutationKind.UnreadCountChanged:
                await _messageStore.HandleUnreadCountChangedAsync((UnreadCountChangedDto)mutation.Payload!, ct);
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
        UnreadCountChanged
    }

    private readonly record struct InboundMutation(
        InboundMutationKind Kind,
        long OwnerUserId,
        object? Payload);
}
