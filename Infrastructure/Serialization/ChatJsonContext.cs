using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Models.DTO;

namespace Chat_App.Infrastructure.Serialization;

/// <summary>
/// 聊天协议 DTO 的 source-generated JSON 序列化上下文。
/// 避免 JsonPacketBodySerializer 和 ViewModel 中的运行时反射。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
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
[JsonSerializable(typeof(MessageHistoryCursorDto))]
[JsonSerializable(typeof(ConversationHistoryCatchUpDto))]
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
public partial class ChatJsonContext : JsonSerializerContext
{
}