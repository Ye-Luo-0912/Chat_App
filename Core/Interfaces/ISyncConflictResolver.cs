using Core.Models.DTO;
using System.Collections.Generic;

namespace Core.Interfaces;

/// <summary>
/// 同步冲突判定：决定一批 catch-up 消息是否比本地水位更新、值得持久化。
/// 避免对已同步的旧历史做无谓的 DB 去重查询。
/// </summary>
public interface ISyncConflictResolver
{
    /// <summary>
    /// 批次中是否有比本地水位更新的消息。
    /// </summary>
    /// <param name="localAfterReceivedAtMs">本地已同步的最新时间戳；null 表示无水位（应全量应用）。</param>
    bool HasNewerMessages(long? localAfterReceivedAtMs, IReadOnlyList<MessageHistoryItemDto> items);
}
