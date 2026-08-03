using Core.Models;
using Core.Models.DTO;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

/// <summary>
/// 独立数据同步引擎：鉴权成功后启动，从服务端拉取会话列表与消息水位，
/// 全部持久化到本地 DB 后通过 <see cref="Completed"/> 事件通知 UI 投影。
/// 每次 Start 会取消上一次同步任务（单任务/会话），重连/切账户时旧任务安全退出。
/// </summary>
public interface ISyncEngine
{
    /// <summary>是否有同步任务正在运行。</summary>
    bool IsSyncing { get; }

    /// <summary>同步诊断信息。</summary>
    ISyncDiagnostics Diagnostics { get; }

    /// <summary>启动一次完整同步。若已有任务在跑，先取消旧任务（旧任务静默退出，不触发事件）。</summary>
    void Start(SessionStamp session, CancellationToken ct = default);

    /// <summary>停止当前同步任务（会话停止/退出登录/应用退出时调用）。幂等。</summary>
    void Stop();

    /// <summary>同步完成（成功或失败）后触发。UI 层订阅此事件做投影更新。</summary>
    event EventHandler<SyncCompletedEventArgs>? Completed;
}

/// <summary>一次同步的结果：会话列表与各会话 catch-up（内存引用，同进程消费）。</summary>
public sealed class SyncCompletedEventArgs : EventArgs
{
    /// <summary>发起同步的会话戳。</summary>
    public required SessionStamp Session { get; init; }

    /// <summary>同步到的全部会话列表（含分页翻页结果）。</summary>
    public IReadOnlyList<ConversationListItemDto> Conversations { get; init; } = [];

    /// <summary>同步到的各会话 catch-up 消息（含分页后的追加批次）。</summary>
    public IReadOnlyList<ConversationHistoryCatchUpDto> CatchUps { get; init; } = [];

    public bool Succeeded { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}
