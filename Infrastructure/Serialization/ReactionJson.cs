using System.Text.Json;
using Core.Models.DTO;

namespace Chat_App.Infrastructure.Serialization;

/// <summary>消息 Reaction 快照的紧凑 JSON 持久化入口。</summary>
public static class ReactionJson
{
    public static string? Serialize(IReadOnlyList<MessageReactionSummaryDto>? reactions)
        => reactions is null || reactions.Count == 0
            ? null
            : JsonSerializer.Serialize(reactions, ChatJsonContext.Default.Options);

    public static IReadOnlyList<MessageReactionSummaryDto>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<MessageReactionSummaryDto>>(
                json,
                ChatJsonContext.Default.Options);
        }
        catch
        {
            return null;
        }
    }
}
