using System;

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

    /// <summary>累计成功同步次数。</summary>
    int SyncCount { get; }

    /// <summary>累计同步的会话数。</summary>
    long ConversationsSynced { get; }

    /// <summary>累计同步的消息数。</summary>
    long MessagesSynced { get; }
}
