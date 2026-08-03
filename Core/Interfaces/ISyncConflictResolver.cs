using Core.Models.DTO;
using System.Collections.Generic;

namespace Core.Interfaces;

/// <summary>
/// 同步冲突判定：决定一批正向同步（forward catch-up）消息是否比本地水位更新、值得持久化。
/// 注意：仅用于"重连后拉取更新消息"的正向方向；加载更早历史（backward 分页）不走此判定——
/// 更早页天然小于水位，若复用此判断会被整页跳过。
/// 同时间戳使用 (ReceivedAtMs, MessageId) 复合比较，避免同秒多条消息被误判为已同步。
/// </summary>
public interface ISyncConflictResolver
{
    /// <summary>
    /// 批次中是否有比本地水位更新的消息。
    /// </summary>
    /// <param name="localAfterReceivedAtMs">本地已同步的最新时间戳；null 表示无水位（应全量应用）。</param>
    /// <param name="localAfterMessageId">本地已同步的最新消息 Id（与时间戳构成复合游标，同秒消息区分依据）。</param>
    bool HasNewerMessages(
        long? localAfterReceivedAtMs,
        string? localAfterMessageId,
        IReadOnlyList<MessageHistoryItemDto> items);
}
