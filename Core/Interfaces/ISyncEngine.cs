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
/// 生命周期严格：RestartAsync 先取消并等待旧任务退出再启动新任务；并发调用以最新有效
/// 生命周期意图为准，旧 Restart 不得在 Stop 或新会话 Start 后启动孤儿任务。
/// </summary>
public interface ISyncEngine
{
    /// <summary>是否有同步任务正在运行。</summary>
    bool IsSyncing { get; }

    /// <summary>同步诊断信息。</summary>
    ISyncDiagnostics Diagnostics { get; }

    /// <summary>
    /// 重启同步：严格等待旧任务退出（取消其 CTS 并等待完成）后再启动新任务。
    /// 等待期间 Stop/新会话意图会使本次重启失效，确保切账户时无旧代任务复活。
    /// </summary>
    Task RestartAsync(SessionStamp session, CancellationToken ct = default);

    /// <summary>启动一次同步（仅当无任务在运行时启动；已运行则忽略）。测试/简单场景用。</summary>
    void Start(SessionStamp session, CancellationToken ct = default);

    /// <summary>停止当前同步任务并等待其退出。幂等。</summary>
    Task StopAsync();

    /// <summary>同步完成（成功/部分完成/失败）后触发。UI 层订阅此事件做投影更新。</summary>
    event EventHandler<SyncCompletedEventArgs>? Completed;
}

/// <summary>同步结果类型。</summary>
public enum SyncOutcome
{
    /// <summary>完整成功：所有预算内的数据已同步完毕。</summary>
    Completed,

    /// <summary>预算上限截断（会话列表/历史页数达到上限）：部分数据待续同步，不得视为完整成功。</summary>
    PartialLimitReached
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

    /// <summary>同步结果类型：PartialLimitReached 表示预算截断，需要后续继续同步。</summary>
    public SyncOutcome Outcome { get; init; } = SyncOutcome.Completed;

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}
