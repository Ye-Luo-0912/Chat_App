using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatApp.Shared.Protocol.Tcp;
using Core.Models.DTO;

namespace Chat_App.Infrastructure.Serialization;

/// <summary>
/// 聊天协议 DTO 的 source-generated JSON 序列化上下文。
/// 避免 JsonPacketBodySerializer 和 ViewModel 中的运行时反射。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatMessageDto))]
[JsonSerializable(typeof(MessageAcknowledgementDto))]
[JsonSerializable(typeof(ConversationChangedDto))]
[JsonSerializable(typeof(MessageRecalledUpdateDto))]
[JsonSerializable(typeof(MessageEditedUpdateDto))]
[JsonSerializable(typeof(TypingUpdateDto))]
[JsonSerializable(typeof(PresenceChangedDto))]
[JsonSerializable(typeof(SyncBootstrapRequestDto))]
[JsonSerializable(typeof(SyncBootstrapResponseDto))]
[JsonSerializable(typeof(ConversationSyncWatermarkDto))]
[JsonSerializable(typeof(MessageHistoryItemDto))]
[JsonSerializable(typeof(List<MessageReactionSummaryDto>))]
[JsonSerializable(typeof(MessageHistoryCursorDto))]
[JsonSerializable(typeof(ConversationHistoryCatchUpDto))]
[JsonSerializable(typeof(SyncCursorResetRequiredDto))]
[JsonSerializable(typeof(RelationshipSyncWatermarkDto))]
[JsonSerializable(typeof(RelationshipChangeLogEntryDto))]
[JsonSerializable(typeof(RelationshipCatchUpDto))]
[JsonSerializable(typeof(RelationshipListRequestDto))]
[JsonSerializable(typeof(RelationshipListResponseDto))]
[JsonSerializable(typeof(RelationshipListItemDto))]
[JsonSerializable(typeof(ConversationListRequestDto))]
[JsonSerializable(typeof(ConversationListResponseDto))]
[JsonSerializable(typeof(ConversationListItemDto))]
[JsonSerializable(typeof(ConversationListCursorDto))]
[JsonSerializable(typeof(ConversationSetPrefsRequestDto))]
[JsonSerializable(typeof(ConversationSetPrefsResponseDto))]
[JsonSerializable(typeof(MessageRecallRequestDto))]
[JsonSerializable(typeof(MessageRecallAcknowledgementDto))]
[JsonSerializable(typeof(MessageEditRequestDto))]
[JsonSerializable(typeof(MessageEditAcknowledgementDto))]
[JsonSerializable(typeof(PresenceQueryRequestDto))]
[JsonSerializable(typeof(PresenceSnapshotResponseDto))]
[JsonSerializable(typeof(MessageReceiptDto))]
[JsonSerializable(typeof(MessageReceiptAckDto))]
[JsonSerializable(typeof(MessageReceiptUpdatedDto))]
[JsonSerializable(typeof(MessageHistoryRequestDto))]
[JsonSerializable(typeof(MessageHistoryPageDto))]
[JsonSerializable(typeof(ConversationMarkReadRequestDto))]
[JsonSerializable(typeof(ConversationMarkReadResponseDto))]
[JsonSerializable(typeof(UnreadCountChangedDto))]
[JsonSerializable(typeof(List<AttachmentRefDto>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(AuthRequestDto))]
[JsonSerializable(typeof(AuthResponseDto))]
[JsonSerializable(typeof(ErrorResponseDto))]
[JsonSerializable(typeof(ProtocolErrorDto))]
[JsonSerializable(typeof(ClientHello))]
[JsonSerializable(typeof(ServerHello))]
[JsonSerializable(typeof(GoAway))]
[JsonSerializable(typeof(ResumeResponse))]
[JsonSerializable(typeof(ProtocolErrorFrame))]
[JsonSerializable(typeof(ConversationMemberItemDto))]
[JsonSerializable(typeof(CreateGroupRequestDto))]
[JsonSerializable(typeof(CreateGroupResponseDto))]
[JsonSerializable(typeof(AddGroupMembersRequestDto))]
[JsonSerializable(typeof(AddGroupMembersResponseDto))]
[JsonSerializable(typeof(RemoveGroupMemberRequestDto))]
[JsonSerializable(typeof(RemoveGroupMemberResponseDto))]
[JsonSerializable(typeof(LeaveGroupRequestDto))]
[JsonSerializable(typeof(LeaveGroupResponseDto))]
[JsonSerializable(typeof(DissolveGroupRequestDto))]
[JsonSerializable(typeof(DissolveGroupResponseDto))]
[JsonSerializable(typeof(ChangeMemberRoleRequestDto))]
[JsonSerializable(typeof(ChangeMemberRoleResponseDto))]
[JsonSerializable(typeof(ListGroupMembersRequestDto))]
[JsonSerializable(typeof(ListGroupMembersResponseDto))]
[JsonSerializable(typeof(MemberJoinedUpdateDto))]
[JsonSerializable(typeof(MemberLeftUpdateDto))]
[JsonSerializable(typeof(MemberRemovedUpdateDto))]
[JsonSerializable(typeof(RoleChangedUpdateDto))]
[JsonSerializable(typeof(MembersAddedUpdateDto))]
[JsonSerializable(typeof(ConversationDissolvedUpdateDto))]
public partial class ChatJsonContext : JsonSerializerContext
{
}
