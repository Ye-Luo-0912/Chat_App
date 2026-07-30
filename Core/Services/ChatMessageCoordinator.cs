using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Serilog;

namespace Core.Services;

/// <summary>
/// 网络事件 → 持久化 桥接协调器。
/// 订阅 IChatSessionClient 的网络事件，调用 IMessageStore 做去重与本地事务持久化，
/// IMessageStore 内部通过 IEventBus 发布领域事件供 UI 层增量更新。
/// 本协调器本身不直接操作 UI。
/// </summary>
public sealed class ChatMessageCoordinator : IDisposable
{
    private readonly IChatSessionClient _chatSession;
    private readonly IMessageStore _messageStore;
    private bool _disposed;

    public ChatMessageCoordinator(IChatSessionClient chatSession, IMessageStore messageStore)
    {
        _chatSession = chatSession;
        _messageStore = messageStore;

        _chatSession.ChatMessageReceived += OnChatMessageReceived;
        _chatSession.MessageAcknowledged += OnMessageAcknowledged;
        _chatSession.ConversationChanged += OnConversationChanged;
        _chatSession.MessageRecalled += OnMessageRecalled;
        _chatSession.MessageEdited += OnMessageEdited;
        _chatSession.Authenticated += OnAuthenticated;
        _chatSession.MessageReceiptReceived += OnMessageReceiptReceived;
        _chatSession.MessageReceiptUpdated += OnMessageReceiptUpdated;
        _chatSession.UnreadCountChanged += OnUnreadCountChanged;
    }

    private void OnChatMessageReceived(object? sender, Core.Models.DTO.ChatMessageDto dto)
    {
        // fire-and-forget，但捕获异常记录日志
        _ = Task.Run(async () =>
        {
            try
            {
                await _messageStore.PersistIncomingAsync(dto, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "持久化收到消息失败 SenderUserId={SenderUserId}", dto.SenderUserId);
            }
        });
    }

    private void OnMessageAcknowledged(object? sender, Core.Models.DTO.MessageAcknowledgementDto ack)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _messageStore.HandleAckAsync(ack, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理消息确认失败 ClientMessageId={ClientMessageId}", ack.ClientMessageId);
            }
        });
    }

    private void OnConversationChanged(object? sender, Core.Models.DTO.ConversationChangedDto dto)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _messageStore.HandleConversationChangedAsync(dto, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理会话变更失败 ConversationId={ConversationId}", dto.ConversationId);
            }
        });
    }

    private void OnMessageRecalled(object? sender, Core.Models.DTO.MessageRecalledUpdateDto update)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _messageStore.HandleRecalledAsync(update, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理消息撤回失败 MessageId={MessageId}", update.MessageId);
            }
        });
    }

    private void OnMessageEdited(object? sender, Core.Models.DTO.MessageEditedUpdateDto update)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _messageStore.HandleEditedAsync(update, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理消息编辑失败 MessageId={MessageId}", update.MessageId);
            }
        });
    }

    private void OnAuthenticated(object? sender, long userId)
    {
        // 鉴权成功后可在此触发同步引导的持久化（后续阶段实现）
    }

    private void OnMessageReceiptReceived(object? sender, Core.Models.DTO.MessageReceiptDto dto)
    {
        _ = Task.Run(async () =>
        {
            try { await _messageStore.HandleReceiptAsync(dto, CancellationToken.None); }
            catch (Exception ex) { Log.Error(ex, "处理已读回执失败"); }
        });
    }

    private void OnMessageReceiptUpdated(object? sender, Core.Models.DTO.MessageReceiptUpdatedDto dto)
    {
        _ = Task.Run(async () =>
        {
            try { await _messageStore.HandleReceiptUpdatedAsync(dto, CancellationToken.None); }
            catch (Exception ex) { Log.Error(ex, "处理已读状态更新失败"); }
        });
    }

    private void OnUnreadCountChanged(object? sender, Core.Models.DTO.UnreadCountChangedDto dto)
    {
        _ = Task.Run(async () =>
        {
            try { await _messageStore.HandleUnreadCountChangedAsync(dto, CancellationToken.None); }
            catch (Exception ex) { Log.Error(ex, "处理未读数变更失败 ConversationId={ConversationId}", dto.ConversationId); }
        });
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
        _chatSession.Authenticated -= OnAuthenticated;
        _chatSession.MessageReceiptReceived -= OnMessageReceiptReceived;
        _chatSession.MessageReceiptUpdated -= OnMessageReceiptUpdated;
        _chatSession.UnreadCountChanged -= OnUnreadCountChanged;
    }
}
