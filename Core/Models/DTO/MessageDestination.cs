namespace Core.Models.DTO;

/// <summary>
/// 消息发送目标抽象：无论直聊还是群聊，发送按 ConversationId 寻址；
/// PeerUserId 仅直聊必填（群聊无对端用户）。
/// </summary>
public readonly record struct MessageDestination(
    string ConversationId,
    ConversationTypeDto Type,
    long? PeerUserId)
{
    public bool IsGroup => Type == ConversationTypeDto.Group;
}
