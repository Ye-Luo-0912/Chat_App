using Core.Models;
using Core.Models.DTO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

/// <summary>
/// 同步水位（checkpoint）存储：记录每个会话已同步到的最新消息，
/// 重连后仅拉取缺失数据。
/// </summary>
public interface ISyncCheckpointStore
{
    /// <summary>读取账户全部会话的同步水位（仅返回有值的）。</summary>
    Task<IReadOnlyList<ConversationSyncWatermarkDto>> GetWatermarksAsync(SessionStamp session, CancellationToken ct = default);

    /// <summary>保存单个会话的同步水位。</summary>
    Task SaveWatermarkAsync(SessionStamp session, string conversationId, long afterReceivedAtMs, string afterMessageId, CancellationToken ct = default);
}
