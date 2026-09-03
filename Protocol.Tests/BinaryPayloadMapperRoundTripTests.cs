using System.Buffers;
using ChatApp.Binary.Core;
using ChatApp.Shared.Protocol.Tcp;
using ChatApp.Shared.Protocol.Tcp.Binary;
using ChatApp.Shared.Protocol.Tcp.Binary.Schemas;
using Core.Models.DTO;
using Core.Protocol.Binary;
using Xunit;
using ChatMessageContract = ChatApp.Shared.Protocol.Tcp.ChatMessage;

namespace Protocol.Tests;

/// <summary>
/// 客户端 DTO ↔ chatapp-bin-v1 共享规范 DTO 映射的 round-trip 契约测试：
/// 客户端 DTO → ToShared → TcpBinaryWireEncoder 编码 → TcpBinaryWireCodec 按命令解码
/// → ToClient → 逐字段相等。别名为共享类型的命令直接编码/解码共享 DTO 验证恒等。
/// </summary>
public sealed class BinaryPayloadMapperRoundTripTests
{
    private static byte[] EncodeShared(object shared)
    {
        var buffer = new byte[BinaryLimits.Default.MaxMessageBytes];
        var result = TcpBinaryWireEncoder.TryEncode(shared, buffer, BinaryLimits.Default);
        Assert.Equal(TcpBinaryWireEncodeStatus.Encoded, result.Status);
        return buffer.AsSpan(0, result.Written).ToArray();
    }

    private static long ToUnixMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static object DecodeShared(PacketCommand command, byte[] payload)
    {
        var decode = TcpBinaryWireCodec.TryDecode(command, payload, BinaryLimits.Default);
        Assert.Equal(TcpBinaryWireStatus.Decoded, decode.Status);
        return decode.Value!;
    }

    private static byte[] RoundTripShared(PacketCommand command, object shared) =>
        EncodeShared(DecodeShared(command, EncodeShared(shared)));

    // ──────────── 认证 ────────────

    [Fact]
    public void AuthRequest_RoundTripsWithTokenAndDeviceHash()
    {
        var client = new AuthRequestDto
        {
            AccessToken = "token-abc",
            UserId = 42,
            SessionId = "session-1",
            DeviceIdHash = 0x1234_5678_9ABC_DEF0
        };

        var shared = BinaryPayloadMapper.ToShared(client);
        var decoded = Assert.IsType<AuthenticationRequest>(DecodeShared(PacketCommand.AuthenticationRequest, EncodeShared(shared)));

        Assert.Equal("token-abc", decoded.AccessToken);
        Assert.Equal(client.DeviceIdHash, decoded.DeviceIdHash);
        // 二进制规范不承载 UserId / SessionId（服务端由令牌派生），形状差异为有意设计：
        // AuthenticationRequest 上不存在这两个成员，编译期即保证不会误发。
        Assert.True(decoded.DeviceIdHash.HasValue);
    }

    [Fact]
    public void AuthResponse_RoundTripsAllFields()
    {
        var shared = new AuthenticationResponse
        {
            Success = true,
            UserId = 42,
            ErrorMessage = null,
            SessionId = "session-1",
            DeviceIdHash = 99,
            DeviceId = "device-1",
            ResumeToken = "resume-1"
        };

        var decoded = Assert.IsType<AuthenticationResponse>(DecodeShared(
            PacketCommand.AuthenticationResponse, EncodeShared(shared)));
        var client = BinaryPayloadMapper.ToClient(decoded);

        Assert.True(client.Success);
        Assert.Equal(42, client.UserId);
        Assert.Equal("session-1", client.SessionId);
        Assert.Equal(99u, client.DeviceIdHash);
        Assert.Equal("device-1", client.DeviceId);
        Assert.Equal("resume-1", client.ResumeToken);
    }

    [Fact]
    public void AuthResponse_FailureMapsZeroUserIdToNull()
    {
        var client = BinaryPayloadMapper.ToClient(new AuthenticationResponse
        {
            Success = false,
            UserId = 0,
            ErrorMessage = "denied"
        });

        Assert.False(client.Success);
        Assert.Null(client.UserId);
        Assert.Equal("denied", client.ErrorMessage);
    }

    // ──────────── 消息 ────────────

    [Fact]
    public void ChatMessage_RoundTripsAllFields()
    {
        var sentUtc = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var client = new ChatMessageDto
        {
            ClientMessageId = "client-1",
            MessageId = "client-1",
            ConversationId = "conv-1",
            TargetUserId = 1001,
            SenderUserId = 2002,
            Content = "hello binary",
            SentUtc = sentUtc,
            AttachmentIds = ["att-1", "att-2"],
            ReplyToMessageId = "reply-1",
            ReplyToSenderUserId = 3003,
            ReplyToPreview = "prev",
            MentionedUserIds = [4004]
        };

        var decoded = Assert.IsType<ChatMessageContract>(DecodeShared(
            PacketCommand.ChatMessage, EncodeShared(BinaryPayloadMapper.ToShared(client))));
        var back = BinaryPayloadMapper.ToClient(decoded);

        Assert.Equal("client-1", back.ClientMessageId);
        Assert.Equal("client-1", back.MessageId);
        Assert.Equal("conv-1", back.ConversationId);
        Assert.Equal(1001, back.TargetUserId);
        Assert.Equal(2002, back.SenderUserId);
        Assert.Equal("hello binary", back.Content);
        Assert.Equal(sentUtc, back.SentUtc);
        Assert.Equal(["att-1", "att-2"], back.AttachmentIds);
        Assert.Equal("reply-1", back.ReplyToMessageId);
        Assert.Equal(3003, back.ReplyToSenderUserId);
        Assert.Equal("prev", back.ReplyToPreview);
        Assert.Equal([4004], back.MentionedUserIds);
    }

    [Fact]
    public void ChatMessage_UplinkBackfillsClientMessageIdFromMessageId()
    {
        // 客户端上行把 clientMessageId 放在 MessageId（JSON 契约）；
        // 二进制规范的幂等键是 ClientMessageId，空缺时必须回填。
        var shared = BinaryPayloadMapper.ToShared(new ChatMessageDto
        {
            MessageId = "client-1",
            Content = "hello",
            SentUtc = DateTime.UtcNow
        });

        Assert.Equal("client-1", shared.ClientMessageId);
        Assert.Equal("client-1", shared.MessageId);
    }

    [Fact]
    public void MessageAcknowledgement_RoundTripsWithMsTimestamp()
    {
        var acknowledgedUtc = new DateTime(2026, 8, 30, 12, 0, 1, DateTimeKind.Utc);
        var shared = new MessageAcknowledgement
        {
            ClientMessageId = "client-1",
            CommandId = "cmd-1",
            Accepted = true,
            AcknowledgedAtMs = ToUnixMs(acknowledgedUtc)
        };

        var decoded = Assert.IsType<MessageAcknowledgement>(DecodeShared(
            PacketCommand.MessageAcknowledgement, EncodeShared(shared)));
        var back = BinaryPayloadMapper.ToClient(decoded);

        Assert.Equal("client-1", back.ClientMessageId);
        Assert.Equal("cmd-1", back.CommandId);
        Assert.True(back.Accepted);
        Assert.Equal(acknowledgedUtc, back.AcknowledgedUtc);
    }

    // ──────────── 回执 ────────────

    [Fact]
    public void MessageReceipt_RoundTripsRequestIdForRouting()
    {
        var client = new MessageReceiptDto
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            LastReadMessageId = "msg-9",
            LastReadAtMs = 1_700_000_000_000,
            ReaderUserId = 42,
            ReceiverUserId = 43
        };

        var decoded = Assert.IsType<MessageReceipt>(DecodeShared(
            PacketCommand.MessageReceipt, EncodeShared(BinaryPayloadMapper.ToShared(client))));
        var back = BinaryPayloadMapper.ToClient(decoded);

        Assert.Equal("req-1", back.RequestId);
        Assert.Equal("conv-1", back.ConversationId);
        Assert.Equal("msg-9", back.LastReadMessageId);
        Assert.Equal(1_700_000_000_000, back.LastReadAtMs);
        Assert.Equal(42, back.ReaderUserId);
        Assert.Equal(43, back.ReceiverUserId);
    }

    [Fact]
    public void MessageReceiptAcknowledgement_RoundTrips()
    {
        var shared = new MessageReceiptAcknowledgement { RequestId = "req-1", Accepted = true };
        var decoded = Assert.IsType<MessageReceiptAcknowledgement>(DecodeShared(
            PacketCommand.MessageReceiptAcknowledgement, EncodeShared(shared)));
        var back = BinaryPayloadMapper.ToClient(decoded);

        Assert.Equal("req-1", back.RequestId);
        Assert.True(back.Accepted);
    }

    [Fact]
    public void MessageReceiptUpdated_RoundTrips()
    {
        var shared = new MessageReceiptUpdated
        {
            ConversationId = "conv-1",
            LastReadMessageId = "msg-9",
            LastReadAtMs = 1_700_000_000_000,
            ReaderUserId = 42
        };

        var decoded = Assert.IsType<MessageReceiptUpdated>(DecodeShared(
            PacketCommand.MessageReceiptUpdated, EncodeShared(shared)));
        var back = BinaryPayloadMapper.ToClient(decoded);

        Assert.Equal("conv-1", back.ConversationId);
        Assert.Equal("msg-9", back.LastReadMessageId);
        Assert.Equal(1_700_000_000_000, back.LastReadAtMs);
        Assert.Equal(42, back.ReaderUserId);
    }

    // ──────────── 编辑 / 撤回 ────────────

    [Fact]
    public void MessageEdit_RoundTripsRequestAckAndUpdate()
    {
        var editRequest = new MessageEditRequestDto { RequestId = "req-1", MessageId = "msg-1", Content = "edited" };
        var decodedRequest = Assert.IsType<MessageEditRequest>(DecodeShared(
            PacketCommand.MessageEditRequest, EncodeShared(BinaryPayloadMapper.ToShared(editRequest))));
        Assert.Equal("req-1", decodedRequest.RequestId);
        Assert.Equal("edited", decodedRequest.Content);

        var editAck = new MessageEditAcknowledgement
        {
            RequestId = "req-1",
            MessageId = "msg-1",
            Succeeded = true,
            ConversationId = "conv-1",
            Content = "edited",
            EditVersion = 2,
            EditedAtMs = 1_700_000_000_000
        };
        var decodedAck = Assert.IsType<MessageEditAcknowledgement>(DecodeShared(
            PacketCommand.MessageEditAck, EncodeShared(editAck)));
        var backAck = BinaryPayloadMapper.ToClient(decodedAck);
        Assert.Equal("req-1", backAck.RequestId);
        Assert.Equal(2, backAck.EditVersion);
        Assert.Equal(1_700_000_000_000, backAck.EditedAtMs);

        var edited = new MessageEditedUpdate
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            SenderUserId = 1,
            ReceiverUserId = 2,
            Content = "edited",
            EditVersion = 2,
            EditedAtMs = 1_700_000_000_000
        };
        var decodedUpdate = Assert.IsType<MessageEditedUpdate>(DecodeShared(
            PacketCommand.MessageEdited, EncodeShared(edited)));
        var backUpdate = BinaryPayloadMapper.ToClient(decodedUpdate);
        Assert.Equal("edited", backUpdate.Content);
        Assert.Equal(2, backUpdate.EditVersion);
    }

    [Fact]
    public void MessageRecall_RoundTripsRequestAckAndUpdate()
    {
        var recallRequest = new MessageRecallRequestDto { RequestId = "req-1", MessageId = "msg-1" };
        var decodedRequest = Assert.IsType<MessageRecallRequest>(DecodeShared(
            PacketCommand.MessageRecallRequest, EncodeShared(BinaryPayloadMapper.ToShared(recallRequest))));
        Assert.Equal("msg-1", decodedRequest.MessageId);

        var recallAck = new MessageRecallAcknowledgement
        {
            RequestId = "req-1",
            MessageId = "msg-1",
            Succeeded = true,
            ConversationId = "conv-1",
            RecalledAtMs = 1_700_000_000_000
        };
        var backAck = BinaryPayloadMapper.ToClient(Assert.IsType<MessageRecallAcknowledgement>(DecodeShared(
            PacketCommand.MessageRecallAck, EncodeShared(recallAck))));
        Assert.True(backAck.Succeeded);
        Assert.Equal(1_700_000_000_000, backAck.RecalledAtMs);

        var recalled = new MessageRecalledUpdate
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            SenderUserId = 1,
            ReceiverUserId = 2,
            RecalledAtMs = 1_700_000_000_000
        };
        var backUpdate = BinaryPayloadMapper.ToClient(Assert.IsType<MessageRecalledUpdate>(DecodeShared(
            PacketCommand.MessageRecalled, EncodeShared(recalled))));
        Assert.Equal(1_700_000_000_000, backUpdate.RecalledAtMs);
    }

    // ──────────── 会话列表 / 已读 / 偏好 ────────────

    [Fact]
    public void ConversationList_RoundTripsRequestAndPage()
    {
        var request = new ConversationListRequestDto
        {
            RequestId = "req-1",
            BeforeIsPinned = false,
            BeforePinnedAtMs = 5,
            BeforeLastMessageAtMs = 6,
            BeforeConversationId = "conv-0",
            Limit = 20
        };
        var decodedRequest = Assert.IsType<ConversationListRequest>(DecodeShared(
            PacketCommand.ConversationListRequest, EncodeShared(BinaryPayloadMapper.ToShared(request))));
        Assert.Equal("req-1", decodedRequest.RequestId);
        Assert.Equal(20, decodedRequest.Limit);

        var cursor = new TcpConversationListCursor { IsPinned = true, PinnedAtMs = 5, LastMessageAtMs = 6, ConversationId = "conv-0" };
        var page = new ConversationListPage
        {
            RequestId = "req-1",
            Succeeded = true,
            Items =
            [
                new TcpConversationListItem
                {
                    ConversationId = "conv-1",
                    Type = TcpConversationType.Group,
                    Title = "group",
                    UnreadCount = 3,
                    IsPinned = true
                }
            ],
            NextCursor = cursor,
            HasMore = true
        };

        var back = BinaryPayloadMapper.ToClient(Assert.IsType<ConversationListPage>(DecodeShared(
            PacketCommand.ConversationListPage, EncodeShared(page))));

        Assert.Equal("req-1", back.RequestId);
        Assert.True(back.Succeeded);
        var item = Assert.Single(back.Items);
        Assert.Equal("conv-1", item.ConversationId);
        Assert.Equal(TcpConversationType.Group, item.Type);
        Assert.Equal(3, item.UnreadCount);
        Assert.NotNull(back.NextCursor);
        Assert.Equal("conv-0", back.NextCursor.ConversationId);
        Assert.True(back.HasMore);
    }

    [Fact]
    public void ConversationMarkRead_MapsRenamedFields()
    {
        // 两侧字段名不同：客户端 LastRead* ↔ 共享 Read*。
        var request = new ConversationMarkReadRequestDto
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            LastReadMessageId = "msg-9",
            LastReadAtMs = 1_700_000_000_000
        };
        var decodedRequest = Assert.IsType<ConversationMarkReadRequest>(DecodeShared(
            PacketCommand.ConversationMarkReadRequest, EncodeShared(BinaryPayloadMapper.ToShared(request))));
        Assert.Equal("req-1", decodedRequest.RequestId);
        Assert.Equal("msg-9", decodedRequest.ReadMessageId);
        Assert.Equal(1_700_000_000_000, decodedRequest.ReadAtMs);

        var response = new ConversationMarkReadResponse
        {
            RequestId = "req-1",
            Succeeded = true,
            ConversationId = "conv-1",
            UnreadCount = 0,
            LastReadMessageId = "msg-9",
            LastReadAtMs = 1_700_000_000_000,
            Changed = true
        };
        var back = BinaryPayloadMapper.ToClient(Assert.IsType<ConversationMarkReadResponse>(DecodeShared(
            PacketCommand.ConversationMarkReadResponse, EncodeShared(response))));
        Assert.Equal("req-1", back.RequestId);
        Assert.Equal("conv-1", back.ConversationId);
        Assert.Equal(0, back.UnreadCount);
    }

    [Fact]
    public void ConversationSetPrefs_RoundTrips()
    {
        var request = new ConversationSetPrefsRequestDto
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            Pinned = true,
            Muted = false,
            MutedUntilMs = 1_700_000_000_000
        };
        var decodedRequest = Assert.IsType<ConversationSetPrefsRequest>(DecodeShared(
            PacketCommand.ConversationSetPrefsRequest, EncodeShared(BinaryPayloadMapper.ToShared(request))));
        Assert.True(decodedRequest.Pinned);
        Assert.False(decodedRequest.Muted);

        var response = new ConversationSetPrefsResponse
        {
            RequestId = "req-1",
            Succeeded = true,
            ConversationId = "conv-1",
            IsPinned = true,
            IsMuted = false,
            MutedUntilMs = 1_700_000_000_000,
            Changed = true
        };
        var back = BinaryPayloadMapper.ToClient(Assert.IsType<ConversationSetPrefsResponse>(DecodeShared(
            PacketCommand.ConversationSetPrefsResponse, EncodeShared(response))));
        Assert.True(back.IsPinned);
        Assert.True(back.Changed);
    }

    [Fact]
    public void UnreadCountChanged_RoundTrips()
    {
        var updated = new UnreadCountChanged
        {
            ConversationId = "conv-1",
            UnreadCount = 7,
            LastReadMessageId = "msg-2",
            LastReadAtMs = 1_700_000_000_000
        };
        var back = BinaryPayloadMapper.ToClient(Assert.IsType<UnreadCountChanged>(DecodeShared(
            PacketCommand.UnreadCountChanged, EncodeShared(updated))));
        Assert.Equal(7, back.UnreadCount);
        Assert.Equal("msg-2", back.LastReadMessageId);
    }

    [Fact]
    public void ConversationChanged_RoundTrips()
    {
        var updated = new ConversationChangedUpdate
        {
            ConversationId = "conv-1",
            Type = TcpConversationType.Direct,
            PeerUserId = 42,
            Title = "peer",
            LastMessageId = "msg-3",
            LastMessagePreview = "hi",
            LastMessageAtMs = 1_700_000_000_000,
            LastSenderUserId = 42,
            IsPinned = false,
            IsMuted = true,
            MutedUntilMs = 1_800_000_000_000
        };
        var back = BinaryPayloadMapper.ToClient(Assert.IsType<ConversationChangedUpdate>(DecodeShared(
            PacketCommand.ConversationChanged, EncodeShared(updated))));
        Assert.Equal("conv-1", back.ConversationId);
        Assert.Equal(TcpConversationType.Direct, back.Type);
        Assert.Equal(42, back.PeerUserId);
        Assert.True(back.IsMuted);
    }

    // ──────────── 同步 / 历史（别名直通） ────────────

    [Fact]
    public void SyncBootstrapRequest_AliasTypeEncodesDirectly()
    {
        var request = new SyncBootstrapRequest
        {
            RequestId = "req-1",
            ListLimit = 50,
            HistoryLimitPerConversation = 20,
            MaxConversationsWithHistory = 10,
            Watermarks = [new ConversationSyncWatermark { ConversationId = "conv-1", AfterReceivedAtMs = 9, AfterMessageId = "m-1" }]
        };

        var decoded = Assert.IsType<SyncBootstrapRequest>(DecodeShared(
            PacketCommand.SyncBootstrapRequest, EncodeShared(request)));
        Assert.Equal("req-1", decoded.RequestId);
        var watermark = Assert.Single(decoded.Watermarks!);
        Assert.Equal("conv-1", watermark.ConversationId);
    }

    [Fact]
    public void MessageHistory_AliasTypesEncodeDirectly()
    {
        var request = new MessageHistoryRequest
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            BeforeReceivedAtMs = 1_700_000_000_000,
            BeforeMessageId = "m-1",
            Limit = 30
        };
        var decodedRequest = Assert.IsType<MessageHistoryRequest>(DecodeShared(
            PacketCommand.MessageHistoryRequest, EncodeShared(request)));
        Assert.Equal("m-1", decodedRequest.BeforeMessageId);

        var page = new MessageHistoryResponse
        {
            RequestId = "req-1",
            ConversationId = "conv-1",
            Succeeded = true,
            Items =
            [
                new MessageHistoryItem
                {
                    MessageId = "m-1",
                    ConversationId = "conv-1",
                    SenderUserId = 1,
                    ReceiverUserId = 2,
                    Content = "text",
                    ReceivedAtMs = 1_700_000_000_000
                }
            ],
            HasMore = false
        };
        var decodedPage = Assert.IsType<MessageHistoryResponse>(DecodeShared(
            PacketCommand.MessageHistoryPage, EncodeShared(page)));
        Assert.Equal("req-1", decodedPage.RequestId);
        Assert.Single(decodedPage.Items);
    }

    // ──────────── 关系（别名直通） ────────────

    [Fact]
    public void RelationshipList_AliasTypesEncodeDirectly()
    {
        var request = new TcpRelationshipListRequest { RequestId = "req-1", ListType = TcpRelationshipListType.Friends, PageSize = 50, Cursor = null };
        var decodedRequest = Assert.IsType<TcpRelationshipListRequest>(DecodeShared(
            PacketCommand.RelationshipListRequest, EncodeShared(request)));
        Assert.Equal(TcpRelationshipListType.Friends, decodedRequest.ListType);

        var response = new TcpRelationshipListResponse
        {
            RequestId = "req-1",
            ListType = TcpRelationshipListType.Friends,
            Succeeded = true,
            Items = [new TcpRelationshipListItem { UserId = 42, ResourceId = "r-1", Status = "accepted", CreatedAtMs = 1 }],
            HasMore = false
        };
        var decodedResponse = Assert.IsType<TcpRelationshipListResponse>(DecodeShared(
            PacketCommand.RelationshipListResponse, EncodeShared(response)));
        Assert.Single(decodedResponse.Items);
    }

    // ──────────── Typing / Presence / Push ────────────

    [Fact]
    public void Typing_RoundTripsBothDirections()
    {
        var notify = new TypingNotifyDto { TargetUserId = 42, ConversationId = "conv-1", IsTyping = true };
        var decodedNotify = Assert.IsType<TcpTypingNotify>(DecodeShared(
            PacketCommand.TypingNotify, EncodeShared(BinaryPayloadMapper.ToShared(notify))));
        Assert.Equal(42, decodedNotify.TargetUserId);
        Assert.True(decodedNotify.IsTyping);

        var update = new TcpTypingUpdate { SenderUserId = 42, ConversationId = "conv-1", IsTyping = false };
        var back = BinaryPayloadMapper.ToClient(Assert.IsType<TcpTypingUpdate>(DecodeShared(
            PacketCommand.TypingUpdate, EncodeShared(update))));
        Assert.Equal(42, back.SenderUserId);
        Assert.False(back.IsTyping);
    }

    [Fact]
    public void Presence_RoundTripsQueryUnwatchSnapshotAndChanged()
    {
        var query = new PresenceQueryRequestDto { RequestId = "req-1", UserIds = [42, 43] };
        var decodedQuery = Assert.IsType<TcpPresenceQueryRequest>(DecodeShared(
            PacketCommand.PresenceQuery, EncodeShared(BinaryPayloadMapper.ToShared(query))));
        Assert.Equal([42, 43], decodedQuery.UserIds);

        var unwatch = new PresenceUnwatchRequestDto { UserIds = [42] };
        var decodedUnwatch = Assert.IsType<TcpPresenceUnwatchRequest>(DecodeShared(
            PacketCommand.PresenceUnwatch, EncodeShared(BinaryPayloadMapper.ToShared(unwatch))));
        Assert.Equal([42], decodedUnwatch.UserIds);

        var snapshot = new TcpPresenceSnapshotResponse
        {
            RequestId = "req-1",
            Items = [new TcpPresenceSnapshotItem { UserId = 42, IsOnline = true }]
        };
        var backSnapshot = BinaryPayloadMapper.ToClient(Assert.IsType<TcpPresenceSnapshotResponse>(DecodeShared(
            PacketCommand.PresenceSnapshot, EncodeShared(snapshot))));
        Assert.Equal("req-1", backSnapshot.RequestId);
        var item = Assert.Single(backSnapshot.Items);
        Assert.Equal(42, item.UserId);
        Assert.True(item.IsOnline);

        var changed = new TcpPresenceChanged { UserId = 42, IsOnline = false };
        var backChanged = BinaryPayloadMapper.ToClient(Assert.IsType<TcpPresenceChanged>(DecodeShared(
            PacketCommand.PresenceChanged, EncodeShared(changed))));
        Assert.False(backChanged.IsOnline);
    }

    [Fact]
    public void PushToken_RoundTripsWithPlatformEnum()
    {
        var register = new RegisterPushTokenRequestDto
        {
            RequestId = "req-1",
            Platform = PushPlatformDto.Apns,
            Token = "token-1",
            AppDeviceLabel = "label"
        };
        var decodedRegister = Assert.IsType<TcpRegisterPushTokenRequest>(DecodeShared(
            PacketCommand.RegisterPushTokenRequest, EncodeShared(BinaryPayloadMapper.ToShared(register))));
        Assert.Equal(TcpPushPlatform.Apns, decodedRegister.Platform);
        Assert.Equal("token-1", decodedRegister.Token);

        var registerResponse = new TcpRegisterPushTokenResponse { RequestId = "req-1", Succeeded = true, ActiveTokenCount = 2 };
        var backRegister = BinaryPayloadMapper.ToClient(Assert.IsType<TcpRegisterPushTokenResponse>(DecodeShared(
            PacketCommand.RegisterPushTokenResponse, EncodeShared(registerResponse))));
        Assert.Equal(2, backRegister.ActiveTokenCount);

        var unregister = new UnregisterPushTokenRequestDto { RequestId = "req-2", Token = "token-1" };
        var decodedUnregister = Assert.IsType<TcpUnregisterPushTokenRequest>(DecodeShared(
            PacketCommand.UnregisterPushTokenRequest, EncodeShared(BinaryPayloadMapper.ToShared(unregister))));
        Assert.Equal("token-1", decodedUnregister.Token);

        var unregisterResponse = new TcpUnregisterPushTokenResponse { RequestId = "req-2", Succeeded = true, ActiveTokenCount = 1 };
        var backUnregister = BinaryPayloadMapper.ToClient(Assert.IsType<TcpUnregisterPushTokenResponse>(DecodeShared(
            PacketCommand.UnregisterPushTokenResponse, EncodeShared(unregisterResponse))));
        Assert.Equal(1, backUnregister.ActiveTokenCount);
    }

    // ──────────── 群组 ────────────

    [Fact]
    public void GroupLifecycle_RoundTripsAllCommands()
    {
        var create = new CreateGroupRequestDto { RequestId = "req-1", Title = "team", MemberUserIds = [1, 2, 3] };
        var decodedCreate = Assert.IsType<TcpCreateGroupRequest>(DecodeShared(
            PacketCommand.CreateGroupRequest, EncodeShared(BinaryPayloadMapper.ToShared(create))));
        Assert.Equal("team", decodedCreate.Title);
        Assert.Equal([1, 2, 3], decodedCreate.MemberUserIds);

        var createResponse = new TcpCreateGroupResponse
        {
            RequestId = "req-1",
            Succeeded = true,
            ConversationId = "conv-9",
            Title = "team",
            Members = [new TcpConversationMemberItem { UserId = 1, Role = TcpGroupMemberRole.Owner, JoinedAtMs = 7 }]
        };
        var backCreate = BinaryPayloadMapper.ToClient(Assert.IsType<TcpCreateGroupResponse>(DecodeShared(
            PacketCommand.CreateGroupResponse, EncodeShared(createResponse))));
        var member = Assert.Single(backCreate.Members!);
        Assert.Equal(ConversationMemberRole.Owner, member.Role);
        Assert.Equal(7, member.JoinedAtMs);

        var addMembers = new AddGroupMembersRequestDto { RequestId = "req-2", ConversationId = "conv-9", MemberUserIds = [4] };
        var decodedAdd = Assert.IsType<TcpAddGroupMembersRequest>(DecodeShared(
            PacketCommand.AddGroupMembersRequest, EncodeShared(BinaryPayloadMapper.ToShared(addMembers))));
        Assert.Equal([4], decodedAdd.MemberUserIds);

        var addResponse = new TcpAddGroupMembersResponse
        {
            RequestId = "req-2",
            Succeeded = true,
            ConversationId = "conv-9",
            Members = [new TcpConversationMemberItem { UserId = 4, Role = TcpGroupMemberRole.Member }]
        };
        var backAdd = BinaryPayloadMapper.ToClient(Assert.IsType<TcpAddGroupMembersResponse>(DecodeShared(
            PacketCommand.AddGroupMembersResponse, EncodeShared(addResponse))));
        Assert.Single(backAdd.Members!);

        var removeMember = new RemoveGroupMemberRequestDto { RequestId = "req-3", ConversationId = "conv-9", TargetUserId = 4 };
        var decodedRemove = Assert.IsType<TcpRemoveGroupMemberRequest>(DecodeShared(
            PacketCommand.RemoveGroupMemberRequest, EncodeShared(BinaryPayloadMapper.ToShared(removeMember))));
        Assert.Equal(4, decodedRemove.TargetUserId);

        var removeResponse = new TcpRemoveGroupMemberResponse { RequestId = "req-3", Succeeded = true, ConversationId = "conv-9" };
        var backRemove = BinaryPayloadMapper.ToClient(Assert.IsType<TcpRemoveGroupMemberResponse>(DecodeShared(
            PacketCommand.RemoveGroupMemberResponse, EncodeShared(removeResponse))));
        Assert.Equal("conv-9", backRemove.ConversationId);

        var leave = new LeaveGroupRequestDto { RequestId = "req-4", ConversationId = "conv-9" };
        var decodedLeave = Assert.IsType<TcpLeaveGroupRequest>(DecodeShared(
            PacketCommand.LeaveGroupRequest, EncodeShared(BinaryPayloadMapper.ToShared(leave))));
        Assert.Equal("conv-9", decodedLeave.ConversationId);

        var leaveResponse = new TcpLeaveGroupResponse { RequestId = "req-4", Succeeded = true, ConversationId = "conv-9" };
        Assert.IsType<TcpLeaveGroupResponse>(DecodeShared(PacketCommand.LeaveGroupResponse, EncodeShared(leaveResponse)));

        var dissolve = new DissolveGroupRequestDto { RequestId = "req-5", ConversationId = "conv-9" };
        Assert.IsType<TcpDissolveGroupRequest>(DecodeShared(
            PacketCommand.DissolveGroupRequest, EncodeShared(BinaryPayloadMapper.ToShared(dissolve))));

        var dissolveResponse = new TcpDissolveGroupResponse { RequestId = "req-5", Succeeded = true, ConversationId = "conv-9" };
        Assert.IsType<TcpDissolveGroupResponse>(DecodeShared(PacketCommand.DissolveGroupResponse, EncodeShared(dissolveResponse)));

        var changeRole = new ChangeMemberRoleRequestDto { RequestId = "req-6", ConversationId = "conv-9", TargetUserId = 2, NewRole = ConversationMemberRole.Admin };
        var decodedRole = Assert.IsType<TcpChangeMemberRoleRequest>(DecodeShared(
            PacketCommand.ChangeMemberRoleRequest, EncodeShared(BinaryPayloadMapper.ToShared(changeRole))));
        Assert.Equal(TcpGroupMemberRole.Admin, decodedRole.NewRole);

        var roleResponse = new TcpChangeMemberRoleResponse { RequestId = "req-6", Succeeded = true, ConversationId = "conv-9" };
        Assert.IsType<TcpChangeMemberRoleResponse>(DecodeShared(PacketCommand.ChangeMemberRoleResponse, EncodeShared(roleResponse)));

        var listMembers = new ListGroupMembersRequestDto { RequestId = "req-7", ConversationId = "conv-9", PageSize = 50, Cursor = "c-1" };
        var decodedList = Assert.IsType<TcpListGroupMembersRequest>(DecodeShared(
            PacketCommand.ListGroupMembersRequest, EncodeShared(BinaryPayloadMapper.ToShared(listMembers))));
        Assert.Equal("c-1", decodedList.Cursor);

        var listResponse = new TcpListGroupMembersResponse
        {
            RequestId = "req-7",
            Succeeded = true,
            ConversationId = "conv-9",
            Members = [new TcpConversationMemberItem { UserId = 1, Role = TcpGroupMemberRole.Admin, JoinedAtMs = 8 }],
            NextCursor = "c-2",
            HasMore = true
        };
        var backList = BinaryPayloadMapper.ToClient(Assert.IsType<TcpListGroupMembersResponse>(DecodeShared(
            PacketCommand.ListGroupMembersResponse, EncodeShared(listResponse))));
        Assert.Equal(ConversationMemberRole.Admin, Assert.Single(backList.Members!).Role);
        Assert.Equal("c-2", backList.NextCursor);
        Assert.True(backList.HasMore);
    }

    [Fact]
    public void GroupUpdates_RoundTripsAllEvents()
    {
        var joined = new TcpMemberJoinedUpdate
        {
            ConversationId = "conv-9",
            UserId = 5,
            Role = TcpGroupMemberRole.Member,
            ActorUserId = 1,
            Title = "team",
            OccurredAtMs = 1_700_000_000_000
        };
        var backJoined = BinaryPayloadMapper.ToClient(Assert.IsType<TcpMemberJoinedUpdate>(DecodeShared(
            PacketCommand.MemberJoined, EncodeShared(joined))));
        Assert.Equal(5, backJoined.UserId);
        Assert.Equal(ConversationMemberRole.Member, backJoined.Role);

        var left = new TcpMemberLeftUpdate { ConversationId = "conv-9", UserId = 5, OccurredAtMs = 1 };
        var backLeft = BinaryPayloadMapper.ToClient(Assert.IsType<TcpMemberLeftUpdate>(DecodeShared(
            PacketCommand.MemberLeft, EncodeShared(left))));
        Assert.Equal(5, backLeft.UserId);

        var removed = new TcpMemberRemovedUpdate { ConversationId = "conv-9", UserId = 5, ActorUserId = 1, OccurredAtMs = 1 };
        var backRemoved = BinaryPayloadMapper.ToClient(Assert.IsType<TcpMemberRemovedUpdate>(DecodeShared(
            PacketCommand.MemberRemoved, EncodeShared(removed))));
        Assert.Equal(1, backRemoved.ActorUserId);

        var roleChanged = new TcpRoleChangedUpdate
        {
            ConversationId = "conv-9",
            UserId = 2,
            NewRole = TcpGroupMemberRole.Admin,
            PreviousRole = TcpGroupMemberRole.Member,
            ActorUserId = 1,
            OccurredAtMs = 1
        };
        var backRole = BinaryPayloadMapper.ToClient(Assert.IsType<TcpRoleChangedUpdate>(DecodeShared(
            PacketCommand.RoleChanged, EncodeShared(roleChanged))));
        Assert.Equal(ConversationMemberRole.Admin, backRole.NewRole);
        Assert.Equal(ConversationMemberRole.Member, backRole.PreviousRole);

        var membersAdded = new TcpMembersAddedUpdate
        {
            ConversationId = "conv-9",
            AddedUserIds = [5, 6],
            ActorUserId = 1,
            Title = "team",
            OccurredAtMs = 1
        };
        var backAdded = BinaryPayloadMapper.ToClient(Assert.IsType<TcpMembersAddedUpdate>(DecodeShared(
            PacketCommand.MembersAddedUpdate, EncodeShared(membersAdded))));
        Assert.Equal([5, 6], backAdded.AddedUserIds);

        var dissolved = new TcpConversationDissolvedUpdate { ConversationId = "conv-9", ActorUserId = 1, OccurredAtMs = 1 };
        var backDissolved = BinaryPayloadMapper.ToClient(Assert.IsType<TcpConversationDissolvedUpdate>(DecodeShared(
            PacketCommand.ConversationDissolvedUpdate, EncodeShared(dissolved))));
        Assert.Equal(1, backDissolved.ActorUserId);
    }

    // ──────────── 通话（别名直通） ────────────

    [Fact]
    public void CallSignaling_AliasTypesEncodeDirectly()
    {
        var request = new TcpCallCommandRequest
        {
            RequestId = "req-1",
            CallId = "call-1",
            CommandId = "cmd-1",
            Revision = 1,
            Type = TcpCallCommandType.Invite,
            ActorUserId = 1,
            Sdp = "v=0"
        };
        var decodedRequest = Assert.IsType<TcpCallCommandRequest>(DecodeShared(
            PacketCommand.CallCommandRequest, EncodeShared(request)));
        Assert.Equal("call-1", decodedRequest.CallId);
        Assert.Equal(TcpCallCommandType.Invite, decodedRequest.Type);

        var response = new TcpCallCommandResponse
        {
            RequestId = "req-1",
            CallId = "call-1",
            Succeeded = true,
            State = TcpCallState.Ringing,
            Revision = 1
        };
        var decodedResponse = Assert.IsType<TcpCallCommandResponse>(DecodeShared(
            PacketCommand.CallCommandResponse, EncodeShared(response)));
        Assert.Equal(TcpCallState.Ringing, decodedResponse.State);

        var signal = new TcpCallSignal
        {
            CallId = "call-1",
            SignalId = "s-1",
            Kind = TcpCallCommandType.Ringing,
            FromUserId = 1,
            ToUserId = 2,
            Revision = 1,
            OccurredAtMs = 1_700_000_000_000
        };
        var decodedSignal = Assert.IsType<TcpCallSignal>(DecodeShared(PacketCommand.CallSignal, EncodeShared(signal)));
        Assert.Equal(TcpCallCommandType.Ringing, decodedSignal.Kind);
    }

    // ──────────── Error 帧 ────────────

    [Fact]
    public void ProtocolErrorFrame_MapsToClientErrorDto()
    {
        var frame = new ProtocolErrorFrame
        {
            Code = ProtocolErrorCode.RateLimited,
            Fatal = false,
            RetryAfterMs = 750,
            Message = "slow down",
            OriginCommand = (ushort)PacketCommand.PresenceQuery
        };

        var back = BinaryPayloadMapper.ToClient(Assert.IsType<ProtocolErrorFrame>(DecodeShared(
            PacketCommand.Error, EncodeShared(frame))));

        Assert.Equal(PacketCommand.PresenceQuery, back.Command);
        Assert.Equal(nameof(ProtocolErrorCode.RateLimited), back.ErrorCode);
        Assert.Equal("slow down", back.ErrorMessage);
        Assert.Equal(750, back.RetryAfterMs);
        Assert.False(back.IsFatal);
    }

    // ──────────── 未覆盖类型 fail-closed ────────────

    [Fact]
    public void ToShared_UnknownType_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() => BinaryPayloadMapper.ToShared(new object()));
        Assert.Throws<InvalidOperationException>(() => BinaryPayloadMapper.ToShared(new ErrorResponseDto()));
    }
}
