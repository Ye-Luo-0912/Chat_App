using Core.Models.DTO;
using Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface IChatSessionClient : IDisposable
{
    bool IsConnected { get; }
    bool IsAuthenticated { get; }
    long CurrentUserId { get; }

    Task ConnectAsync(ServerEndpoint endpoint, CancellationToken ct = default);
    Task AuthenticateAsync(string accessToken, long userId, string? sessionId, ulong? deviceIdHash, CancellationToken ct = default);

    /// <summary>
    /// 发送聊天消息；文本与附件至少其一非空。回复与转发互斥。
    /// 返回客户端消息 Id（上行 MessageId），用于与后续 MessageAck.CommandId 绑定。
    /// </summary>
    Task<string> SendChatMessageAsync(
        long targetUserId,
        string? content,
        IReadOnlyList<string>? attachmentIds = null,
        string? replyToMessageId = null,
        long? replyToSenderUserId = null,
        string? replyToPreview = null,
        string? forwardedFromMessageId = null,
        long? forwardedFromSenderUserId = null,
        string? forwardedFromPreview = null,
        CancellationToken ct = default);

    Task SendHeartbeatAsync(CancellationToken ct = default);
    Task DisconnectAsync(string? reason = null, CancellationToken ct = default);

    Task<ConversationListResponseDto> QueryConversationListAsync(
        int limit = 50,
        CancellationToken ct = default);

    Task<ConversationSetPrefsResponseDto> SetConversationPrefsAsync(
        string conversationId,
        bool? pinned = null,
        bool? muted = null,
        long? mutedUntilMs = null,
        CancellationToken ct = default);

    Task<MessageRecallAcknowledgementDto> RecallMessageAsync(
        string messageId,
        CancellationToken ct = default);

    Task<MessageEditAcknowledgementDto> EditMessageAsync(
        string messageId,
        string content,
        CancellationToken ct = default);

    Task SendTypingNotifyAsync(
        long targetUserId,
        bool isTyping,
        string? conversationId = null,
        CancellationToken ct = default);

    Task<PresenceSnapshotResponseDto> QueryPresenceAsync(
        IReadOnlyList<long> userIds,
        CancellationToken ct = default);

    Task UnwatchPresenceAsync(
        IReadOnlyList<long> userIds,
        CancellationToken ct = default);

    Task<SyncBootstrapResponseDto> QuerySyncBootstrapAsync(
        int listLimit = 50,
        int historyLimitPerConversation = 20,
        int maxConversationsWithHistory = 10,
        IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null,
        CancellationToken ct = default);

    event EventHandler? Connected;
    event EventHandler<long>? Authenticated;
    event EventHandler<string>? AuthenticationFailed;
    event EventHandler<ChatMessageDto>? ChatMessageReceived;
    event EventHandler<MessageAcknowledgementDto>? MessageAcknowledged;
    event EventHandler<ConversationChangedDto>? ConversationChanged;
    event EventHandler<MessageRecalledUpdateDto>? MessageRecalled;
    event EventHandler<MessageEditedUpdateDto>? MessageEdited;
    event EventHandler<TypingUpdateDto>? TypingUpdated;
    event EventHandler<PresenceChangedDto>? PresenceChanged;
    event EventHandler<string>? ConnectionClosed;
}
