using Core.Models.DTO;
using Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

/// <summary>
/// TCP 聊天会话客户端：负责与服务器的双向通信（鉴权、消息收发、请求-响应）。
/// 所有请求-响应方法内部使用 requestId + TCS + 超时机制匹配响应。
/// </summary>
public interface IChatSessionClient : IDisposable
{
    /// <summary>TCP 套接字是否已连接。</summary>
    bool IsConnected { get; }

    /// <summary>是否已通过服务端鉴权。</summary>
    bool IsAuthenticated { get; }

    /// <summary>鉴权成功后的当前用户 Id；未鉴权时为 0。</summary>
    long CurrentUserId { get; }

    /// <summary>当前连接代际：每次成功建立连接递增，用于 SessionStamp 代际校验。</summary>
    long ConnectionGeneration { get; }

    /// <summary>当前连接 Id：每次成功建立连接更换，用于 SessionStamp。</summary>
    Guid ConnectionId { get; }

    /// <summary>
    /// 当前会话戳：由当前代际 + 连接 Id + 已鉴权用户组成；
    /// 未鉴权或已断开时为 <see cref="SessionStamp.None"/>。供 UI 层持久化调用携带。
    /// </summary>
    SessionStamp CurrentSession { get; }

    /// <summary>连接到指定服务器端点。成功后需调用 <see cref="AuthenticateAsync"/>。</summary>
    Task ConnectAsync(ServerEndpoint endpoint, CancellationToken ct = default);

    /// <summary>
    /// 发送鉴权请求并等待服务端确认（超时 5 秒）。
    /// 成功触发 <see cref="Authenticated"/> 事件；失败触发 <see cref="AuthenticationFailed"/>。
    /// </summary>
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
        string? clientMessageId = null,
        CancellationToken ct = default);

    /// <summary>发送心跳包；若距上次 ACK 超过阈值则主动判定半开并断连。</summary>
    Task SendHeartbeatAsync(CancellationToken ct = default);

    /// <summary>主动断开连接，重置鉴权状态与心跳。</summary>
    Task DisconnectAsync(string? reason = null, CancellationToken ct = default);

    /// <summary>分页查询会话列表（超时 8 秒）。</summary>
    Task<ConversationListResponseDto> QueryConversationListAsync(
        int limit = 50,
        bool? beforeIsPinned = null,
        long? beforePinnedAtMs = null,
        long? beforeLastMessageAtMs = null,
        string? beforeConversationId = null,
        CancellationToken ct = default);

    /// <summary>设置会话偏好（置顶/免打扰，超时 8 秒）。</summary>
    Task<ConversationSetPrefsResponseDto> SetConversationPrefsAsync(
        string conversationId,
        bool? pinned = null,
        bool? muted = null,
        long? mutedUntilMs = null,
        CancellationToken ct = default);

    /// <summary>撤回已发送的消息（超时 8 秒）。</summary>
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

    /// <summary>显式拉取会话历史消息（按游标分页）。</summary>
    Task<MessageHistoryPageDto> QueryMessageHistoryAsync(
        string conversationId,
        int limit = 50,
        long? beforeReceivedAtMs = null,
        string? beforeMessageId = null,
        CancellationToken ct = default);

    /// <summary>发送已读回执（103）。告知服务端我已读到某条消息。</summary>
    Task<MessageReceiptAckDto> SendMessageReceiptAsync(
        string conversationId,
        string? lastReadMessageId,
        long? lastReadAtMs,
        CancellationToken ct = default);

    /// <summary>标记会话已读（110）。</summary>
    Task<ConversationMarkReadResponseDto> MarkConversationReadAsync(
        string conversationId,
        string? lastReadMessageId = null,
        long? lastReadAtMs = null,
        CancellationToken ct = default);

    event EventHandler? Connected;
    event EventHandler<long>? Authenticated;
    event EventHandler<string>? AuthenticationFailed;
    event EventHandler<ProtocolErrorDto>? ProtocolError;
    event EventHandler<ChatMessageDto>? ChatMessageReceived;
    event EventHandler<MessageAcknowledgementDto>? MessageAcknowledged;
    event EventHandler<ConversationChangedDto>? ConversationChanged;
    event EventHandler<MessageRecalledUpdateDto>? MessageRecalled;
    event EventHandler<MessageEditedUpdateDto>? MessageEdited;
    event EventHandler<TypingUpdateDto>? TypingUpdated;
    event EventHandler<PresenceChangedDto>? PresenceChanged;
    event EventHandler<string>? ConnectionClosed;

    event EventHandler<MessageReceiptDto>? MessageReceiptReceived;
    event EventHandler<MessageReceiptUpdatedDto>? MessageReceiptUpdated;
    event EventHandler<MessageHistoryPageDto>? MessageHistoryPageReceived;
    event EventHandler<ConversationMarkReadResponseDto>? ConversationMarkReadResponse;
    event EventHandler<UnreadCountChangedDto>? UnreadCountChanged;
}
