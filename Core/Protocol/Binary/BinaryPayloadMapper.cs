using ChatApp.Shared.Protocol.Tcp;
using ChatApp.Shared.Protocol.Tcp.Binary;
using Core.Models.DTO;

namespace Core.Protocol.Binary;

/// <summary>
/// 客户端 DTO ↔ chatapp-bin-v1 共享规范 DTO 的双向映射层。
/// 仅在连接协商为二进制载荷（ServerHello.PayloadFormat = <see cref="BinaryPayloadFormat.Id"/>）时使用：
/// 出站先 <see cref="ToShared"/> 转共享 DTO 再由 TcpBinaryWireEncoder 编码；
/// 入站由 TcpBinaryWireCodec 解码出共享 DTO 后经 To* 转回客户端 DTO。
/// 客户端 DTO 本身就是共享类型的命令（Core/SharedBusinessTcpAliases.cs 别名，
/// 如 MessageHistoryResponse / SyncBootstrapRequest / TcpCallSignal）不经过本层。
/// 约定：DateTime(UTC) ↔ Unix 毫秒；共享有客户端无的字段置默认，客户端有共享无的字段丢弃（逐处注释）；
/// 枚举两侧数值一致，按数值映射。
/// </summary>
public static class BinaryPayloadMapper
{
    // ──────────── 出站：客户端 DTO → 共享规范 DTO ────────────

    /// <summary>
    /// 出站二进制编码前的统一分发：按 payload 的具体客户端 DTO 类型转共享规范 DTO。
    /// 未覆盖的类型 fail-closed 抛 InvalidOperationException（与 JSON 编码失败行为一致）。
    /// ClientHello 不经过本层：握手段始终 JSON。
    /// </summary>
    public static object ToShared(object payload) => payload switch
    {
        AuthRequestDto v => ToShared(v),
        ChatMessageDto v => ToShared(v),
        ConversationListRequestDto v => ToShared(v),
        ConversationSetPrefsRequestDto v => ToShared(v),
        ConversationMarkReadRequestDto v => ToShared(v),
        MessageEditRequestDto v => ToShared(v),
        MessageRecallRequestDto v => ToShared(v),
        MessageReceiptDto v => ToShared(v),
        TypingNotifyDto v => ToShared(v),
        PresenceQueryRequestDto v => ToShared(v),
        PresenceUnwatchRequestDto v => ToShared(v),
        RegisterPushTokenRequestDto v => ToShared(v),
        UnregisterPushTokenRequestDto v => ToShared(v),
        CreateGroupRequestDto v => ToShared(v),
        AddGroupMembersRequestDto v => ToShared(v),
        RemoveGroupMemberRequestDto v => ToShared(v),
        LeaveGroupRequestDto v => ToShared(v),
        DissolveGroupRequestDto v => ToShared(v),
        ChangeMemberRoleRequestDto v => ToShared(v),
        ListGroupMembersRequestDto v => ToShared(v),
        // 客户端 DTO 即共享类型的命令原样通过（TcpRelationshipListRequest / TcpCallCommandRequest /
        // SyncBootstrapRequest / MessageHistoryRequest）。
        TcpRelationshipListRequest v => v,
        TcpCallCommandRequest v => v,
        SyncBootstrapRequest v => v,
        MessageHistoryRequest v => v,
        _ => throw new InvalidOperationException(
            $"类型 {payload.GetType().Name} 未被二进制载荷映射覆盖，拒绝发送。")
    };

    /// <summary>
    /// AuthenticationRequest 共享规范不承载 UserId / SessionId（服务端由访问令牌派生），
    /// 二进制上行丢弃这两个字段。
    /// </summary>
    public static AuthenticationRequest ToShared(AuthRequestDto dto) => new()
    {
        AccessToken = dto.AccessToken,
        DeviceIdHash = dto.DeviceIdHash
    };

    /// <summary>上行幂等键：客户端把 clientMessageId 放在 MessageId（与 JSON 契约一致），
    /// 二进制规范按 ClientMessageId 承载，故空缺时回填。</summary>
    public static ChatMessage ToShared(ChatMessageDto dto) => new()
    {
        ClientMessageId = dto.ClientMessageId ?? dto.MessageId,
        MessageId = dto.MessageId ?? string.Empty,
        ConversationId = dto.ConversationId,
        TargetUserId = dto.TargetUserId,
        SenderUserId = dto.SenderUserId,
        Content = dto.Content ?? string.Empty,
        SentAtMs = ToUnixMs(dto.SentUtc),
        AttachmentIds = dto.AttachmentIds,
        Attachments = dto.Attachments,
        ReplyToMessageId = dto.ReplyToMessageId,
        ReplyToSenderUserId = dto.ReplyToSenderUserId,
        ReplyToPreview = dto.ReplyToPreview,
        ForwardedFromMessageId = dto.ForwardedFromMessageId,
        ForwardedFromSenderUserId = dto.ForwardedFromSenderUserId,
        ForwardedFromPreview = dto.ForwardedFromPreview,
        MentionedUserIds = dto.MentionedUserIds,
        MentionedRoles = dto.MentionedRoles
    };

    public static ConversationListRequest ToShared(ConversationListRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        BeforeIsPinned = dto.BeforeIsPinned,
        BeforePinnedAtMs = dto.BeforePinnedAtMs,
        BeforeLastMessageAtMs = dto.BeforeLastMessageAtMs,
        BeforeConversationId = dto.BeforeConversationId,
        Limit = dto.Limit
    };

    public static ConversationSetPrefsRequest ToShared(ConversationSetPrefsRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        ConversationId = dto.ConversationId,
        Pinned = dto.Pinned,
        Muted = dto.Muted,
        MutedUntilMs = dto.MutedUntilMs
    };

    /// <summary>两侧字段名不同：客户端 LastRead* ↔ 共享 Read*。</summary>
    public static ConversationMarkReadRequest ToShared(ConversationMarkReadRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        ConversationId = dto.ConversationId,
        ReadAtMs = dto.LastReadAtMs,
        ReadMessageId = dto.LastReadMessageId
    };

    public static MessageEditRequest ToShared(MessageEditRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        MessageId = dto.MessageId,
        Content = dto.Content
    };

    public static MessageRecallRequest ToShared(MessageRecallRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        MessageId = dto.MessageId
    };

    public static MessageReceipt ToShared(MessageReceiptDto dto) => new()
    {
        RequestId = dto.RequestId,
        ConversationId = dto.ConversationId,
        LastReadMessageId = dto.LastReadMessageId,
        LastReadAtMs = dto.LastReadAtMs,
        ReaderUserId = dto.ReaderUserId,
        ReceiverUserId = dto.ReceiverUserId
    };

    public static TcpTypingNotify ToShared(TypingNotifyDto dto) => new()
    {
        TargetUserId = dto.TargetUserId,
        ConversationId = dto.ConversationId,
        IsTyping = dto.IsTyping
    };

    public static TcpPresenceQueryRequest ToShared(PresenceQueryRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        UserIds = dto.UserIds
    };

    public static TcpPresenceUnwatchRequest ToShared(PresenceUnwatchRequestDto dto) => new()
    {
        UserIds = dto.UserIds
    };

    public static TcpRegisterPushTokenRequest ToShared(RegisterPushTokenRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        Platform = (TcpPushPlatform)(byte)dto.Platform,
        Token = dto.Token,
        AppDeviceLabel = dto.AppDeviceLabel
    };

    public static TcpUnregisterPushTokenRequest ToShared(UnregisterPushTokenRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        Token = dto.Token
    };

    public static TcpCreateGroupRequest ToShared(CreateGroupRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        Title = dto.Title,
        MemberUserIds = dto.MemberUserIds
    };

    public static TcpAddGroupMembersRequest ToShared(AddGroupMembersRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        ConversationId = dto.ConversationId,
        MemberUserIds = dto.MemberUserIds
    };

    public static TcpRemoveGroupMemberRequest ToShared(RemoveGroupMemberRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        ConversationId = dto.ConversationId,
        TargetUserId = dto.TargetUserId
    };

    public static TcpLeaveGroupRequest ToShared(LeaveGroupRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        ConversationId = dto.ConversationId
    };

    public static TcpDissolveGroupRequest ToShared(DissolveGroupRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        ConversationId = dto.ConversationId
    };

    public static TcpChangeMemberRoleRequest ToShared(ChangeMemberRoleRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        ConversationId = dto.ConversationId,
        TargetUserId = dto.TargetUserId,
        NewRole = MapRole(dto.NewRole)
    };

    public static TcpListGroupMembersRequest ToShared(ListGroupMembersRequestDto dto) => new()
    {
        RequestId = dto.RequestId,
        ConversationId = dto.ConversationId,
        PageSize = dto.PageSize,
        Cursor = dto.Cursor
    };

    // ──────────── 入站：共享规范 DTO → 客户端 DTO ────────────

    /// <summary>共享 UserId 为 long；0 表示未成功，映射回可空以匹配客户端失败分支。</summary>
    public static AuthResponseDto ToClient(AuthenticationResponse shared) => new()
    {
        Success = shared.Success,
        UserId = shared.UserId != 0 ? shared.UserId : null,
        ErrorMessage = shared.ErrorMessage,
        SessionId = shared.SessionId,
        DeviceIdHash = shared.DeviceIdHash,
        DeviceId = shared.DeviceId,
        ResumeToken = shared.ResumeToken
    };

    public static ChatMessageDto ToClient(ChatMessage shared) => new()
    {
        ClientMessageId = shared.ClientMessageId,
        MessageId = shared.MessageId,
        ConversationId = shared.ConversationId,
        TargetUserId = shared.TargetUserId,
        SenderUserId = shared.SenderUserId,
        Content = shared.Content,
        SentUtc = FromUnixMs(shared.SentAtMs),
        AttachmentIds = shared.AttachmentIds,
        Attachments = shared.Attachments,
        ReplyToMessageId = shared.ReplyToMessageId,
        ReplyToSenderUserId = shared.ReplyToSenderUserId,
        ReplyToPreview = shared.ReplyToPreview,
        ForwardedFromMessageId = shared.ForwardedFromMessageId,
        ForwardedFromSenderUserId = shared.ForwardedFromSenderUserId,
        ForwardedFromPreview = shared.ForwardedFromPreview,
        MentionedUserIds = shared.MentionedUserIds,
        MentionedRoles = shared.MentionedRoles
    };

    public static ConversationListResponseDto ToClient(ConversationListPage shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        Items = shared.Items,
        NextCursor = shared.NextCursor,
        HasMore = shared.HasMore
    };

    public static ConversationSetPrefsResponseDto ToClient(ConversationSetPrefsResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ConversationId = shared.ConversationId,
        IsPinned = shared.IsPinned,
        IsMuted = shared.IsMuted,
        MutedUntilMs = shared.MutedUntilMs,
        Changed = shared.Changed
    };

    /// <summary>共享额外携带 LastReadMessageId / LastReadAtMs / Changed，客户端 DTO 无对应字段，丢弃。</summary>
    public static ConversationMarkReadResponseDto ToClient(ConversationMarkReadResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ConversationId = shared.ConversationId ?? string.Empty,
        UnreadCount = shared.UnreadCount
    };

    public static MessageEditAcknowledgementDto ToClient(MessageEditAcknowledgement shared) => new()
    {
        RequestId = shared.RequestId,
        MessageId = shared.MessageId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ConversationId = shared.ConversationId,
        Content = shared.Content,
        EditVersion = shared.EditVersion,
        EditedAtMs = shared.EditedAtMs
    };

    public static MessageEditedUpdateDto ToClient(MessageEditedUpdate shared) => new()
    {
        MessageId = shared.MessageId,
        ConversationId = shared.ConversationId,
        SenderUserId = shared.SenderUserId,
        ReceiverUserId = shared.ReceiverUserId,
        Content = shared.Content,
        EditVersion = shared.EditVersion,
        EditedAtMs = shared.EditedAtMs
    };

    public static MessageRecallAcknowledgementDto ToClient(MessageRecallAcknowledgement shared) => new()
    {
        RequestId = shared.RequestId,
        MessageId = shared.MessageId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ConversationId = shared.ConversationId,
        RecalledAtMs = shared.RecalledAtMs
    };

    public static MessageRecalledUpdateDto ToClient(MessageRecalledUpdate shared) => new()
    {
        MessageId = shared.MessageId,
        ConversationId = shared.ConversationId,
        SenderUserId = shared.SenderUserId,
        ReceiverUserId = shared.ReceiverUserId,
        RecalledAtMs = shared.RecalledAtMs
    };

    public static MessageAcknowledgementDto ToClient(MessageAcknowledgement shared) => new()
    {
        ClientMessageId = shared.ClientMessageId,
        CommandId = shared.CommandId,
        Accepted = shared.Accepted,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        AcknowledgedUtc = FromUnixMs(shared.AcknowledgedAtMs)
    };

    public static MessageReceiptDto ToClient(MessageReceipt shared) => new()
    {
        RequestId = shared.RequestId,
        ConversationId = shared.ConversationId,
        LastReadMessageId = shared.LastReadMessageId,
        LastReadAtMs = shared.LastReadAtMs,
        ReaderUserId = shared.ReaderUserId,
        ReceiverUserId = shared.ReceiverUserId
    };

    public static MessageReceiptAckDto ToClient(MessageReceiptAcknowledgement shared) => new()
    {
        RequestId = shared.RequestId,
        Accepted = shared.Accepted,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage
    };

    public static MessageReceiptUpdatedDto ToClient(MessageReceiptUpdated shared) => new()
    {
        ConversationId = shared.ConversationId,
        LastReadMessageId = shared.LastReadMessageId,
        LastReadAtMs = shared.LastReadAtMs,
        ReaderUserId = shared.ReaderUserId
    };

    public static TypingUpdateDto ToClient(TcpTypingUpdate shared) => new()
    {
        SenderUserId = shared.SenderUserId,
        ConversationId = shared.ConversationId,
        IsTyping = shared.IsTyping
    };

    public static PresenceSnapshotResponseDto ToClient(TcpPresenceSnapshotResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Items = shared.Items.Select(static item => new PresenceSnapshotItemDto
        {
            UserId = item.UserId,
            IsOnline = item.IsOnline
        }).ToArray()
    };

    public static PresenceChangedDto ToClient(TcpPresenceChanged shared) => new()
    {
        UserId = shared.UserId,
        IsOnline = shared.IsOnline
    };

    public static ConversationChangedDto ToClient(ConversationChangedUpdate shared) => new()
    {
        ConversationId = shared.ConversationId,
        Type = shared.Type,
        PeerUserId = shared.PeerUserId,
        Title = shared.Title,
        LastMessageId = shared.LastMessageId,
        LastMessagePreview = shared.LastMessagePreview,
        LastMessageAtMs = shared.LastMessageAtMs,
        LastSenderUserId = shared.LastSenderUserId,
        IsPinned = shared.IsPinned,
        IsMuted = shared.IsMuted,
        MutedUntilMs = shared.MutedUntilMs
    };

    public static UnreadCountChangedDto ToClient(UnreadCountChanged shared) => new()
    {
        ConversationId = shared.ConversationId,
        UnreadCount = shared.UnreadCount,
        LastReadMessageId = shared.LastReadMessageId,
        LastReadAtMs = shared.LastReadAtMs
    };

    public static RegisterPushTokenResponseDto ToClient(TcpRegisterPushTokenResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ActiveTokenCount = shared.ActiveTokenCount
    };

    public static UnregisterPushTokenResponseDto ToClient(TcpUnregisterPushTokenResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ActiveTokenCount = shared.ActiveTokenCount
    };

    public static CreateGroupResponseDto ToClient(TcpCreateGroupResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ConversationId = shared.ConversationId,
        Title = shared.Title,
        Members = MapMembers(shared.Members)
    };

    public static AddGroupMembersResponseDto ToClient(TcpAddGroupMembersResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ConversationId = shared.ConversationId,
        Members = MapMembers(shared.Members)
    };

    public static RemoveGroupMemberResponseDto ToClient(TcpRemoveGroupMemberResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ConversationId = shared.ConversationId
    };

    public static LeaveGroupResponseDto ToClient(TcpLeaveGroupResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ConversationId = shared.ConversationId
    };

    public static DissolveGroupResponseDto ToClient(TcpDissolveGroupResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ConversationId = shared.ConversationId
    };

    public static ChangeMemberRoleResponseDto ToClient(TcpChangeMemberRoleResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ConversationId = shared.ConversationId
    };

    public static ListGroupMembersResponseDto ToClient(TcpListGroupMembersResponse shared) => new()
    {
        RequestId = shared.RequestId,
        Succeeded = shared.Succeeded,
        ErrorCode = shared.ErrorCode,
        ErrorMessage = shared.ErrorMessage,
        ConversationId = shared.ConversationId,
        Members = MapMembers(shared.Members),
        NextCursor = shared.NextCursor,
        HasMore = shared.HasMore
    };

    public static MemberJoinedUpdateDto ToClient(TcpMemberJoinedUpdate shared) => new()
    {
        ConversationId = shared.ConversationId,
        UserId = shared.UserId,
        Role = MapRole(shared.Role),
        ActorUserId = shared.ActorUserId,
        Title = shared.Title,
        OccurredAtMs = shared.OccurredAtMs
    };

    public static MemberLeftUpdateDto ToClient(TcpMemberLeftUpdate shared) => new()
    {
        ConversationId = shared.ConversationId,
        UserId = shared.UserId,
        OccurredAtMs = shared.OccurredAtMs
    };

    public static MemberRemovedUpdateDto ToClient(TcpMemberRemovedUpdate shared) => new()
    {
        ConversationId = shared.ConversationId,
        UserId = shared.UserId,
        ActorUserId = shared.ActorUserId,
        OccurredAtMs = shared.OccurredAtMs
    };

    public static RoleChangedUpdateDto ToClient(TcpRoleChangedUpdate shared) => new()
    {
        ConversationId = shared.ConversationId,
        UserId = shared.UserId,
        NewRole = MapRole(shared.NewRole),
        PreviousRole = shared.PreviousRole is { } previous ? MapRole(previous) : null,
        ActorUserId = shared.ActorUserId,
        OccurredAtMs = shared.OccurredAtMs
    };

    public static MembersAddedUpdateDto ToClient(TcpMembersAddedUpdate shared) => new()
    {
        ConversationId = shared.ConversationId,
        AddedUserIds = shared.AddedUserIds.ToArray(),
        ActorUserId = shared.ActorUserId,
        Title = shared.Title,
        OccurredAtMs = shared.OccurredAtMs
    };

    public static ConversationDissolvedUpdateDto ToClient(TcpConversationDissolvedUpdate shared) => new()
    {
        ConversationId = shared.ConversationId,
        ActorUserId = shared.ActorUserId,
        OccurredAtMs = shared.OccurredAtMs
    };

    /// <summary>canonical Error 帧客户端投影（二进制会话下由共享 ProtocolErrorFrame 解码后调用）。</summary>
    public static ProtocolErrorDto ToClient(ProtocolErrorFrame shared) => new()
    {
        RequestId = null,
        Command = shared.OriginCommand is { } origin ? (PacketCommand)origin : null,
        ErrorCode = shared.Code.ToString(),
        ErrorMessage = shared.Message,
        IsFatal = shared.Fatal || ProtocolErrorCodeExtensions.IsFatal(shared.Code),
        RetryAfterMs = shared.RetryAfterMs
    };

    // ──────────── 公共小工具 ────────────

    private static ConversationMemberItemDto[]? MapMembers(IReadOnlyList<TcpConversationMemberItem>? members) =>
        members is null
            ? null
            : members.Select(static member => new ConversationMemberItemDto
            {
                UserId = member.UserId,
                Role = MapRole(member.Role),
                JoinedAtMs = member.JoinedAtMs
            }).ToArray();

    /// <summary>两侧枚举数值一致（Owner=1/Admin=2/Member=3），按数值映射。</summary>
    private static TcpGroupMemberRole MapRole(ConversationMemberRole role) => (TcpGroupMemberRole)(byte)role;

    private static ConversationMemberRole MapRole(TcpGroupMemberRole role) => (ConversationMemberRole)(byte)role;

    /// <summary>UTC DateTime → Unix 毫秒。客户端上行固定 DateTime.UtcNow（Kind=Utc）；
    /// Unspecified 按 UTC 处理，仅 Local 做时区换算。</summary>
    private static long ToUnixMs(DateTime value) =>
        (value.Kind == DateTimeKind.Local
            ? new DateTimeOffset(value.ToUniversalTime())
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
        .ToUnixTimeMilliseconds();

    private static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
