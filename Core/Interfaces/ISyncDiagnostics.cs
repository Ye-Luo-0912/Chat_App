using System;
using Core.Diagnostics;

namespace Core.Interfaces;

/// <summary>同步引擎诊断信息（线程安全）。</summary>
public interface ISyncDiagnostics
{
    /// <summary>是否有同步任务正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>最近一次同步完成时间。</summary>
    DateTime? LastSyncUtc { get; }

    /// <summary>最近一次同步耗时（毫秒）。</summary>
    long LastDurationMs { get; }

    /// <summary>最近一次同步的错误（无错误为 null）。</summary>
    string? LastError { get; }

    /// <summary>
    /// 最近一次同步失败的诊断记录（含错误码、信息、发生时间与可重试性；从未失败为 null）。
    /// 成功同步后该记录保留最近一次失败，便于诊断页回溯；连续失败计数单独归零。
    /// </summary>
    SyncFailureRecord? LastFailure { get; }

    /// <summary>累计同步失败次数。</summary>
    long FailCount { get; }

    /// <summary>自最近一次成功以来连续失败次数（用于退避/告警决策）。</summary>
    int ConsecutiveFailures { get; }

    /// <summary>累计成功同步次数。</summary>
    int SyncCount { get; }

    /// <summary>累计同步的会话数。</summary>
    long ConversationsSynced { get; }

    /// <summary>累计同步的消息数。</summary>
    long MessagesSynced { get; }

    /// <summary>
    /// 最近同步到的消息的服务端时间（ms，UTC）；尚未同步到消息时为 0。
    /// 当前时间减去该值即设备相对服务端的数据陈旧度（设备端同步滞后）。
    /// </summary>
    long LastSyncedMessageAtMs { get; }
}
