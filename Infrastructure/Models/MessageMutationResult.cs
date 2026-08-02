namespace Chat_App.Infrastructure.Models;

/// <summary>
/// 消息状态变化（撤回/编辑）在数据库中的应用结果。
/// 仅 <see cref="Applied"/> 代表真实发生状态变化，调用方才应发布领域事件。
/// </summary>
public enum MessageMutationResult
{
    /// <summary>已成功应用。</summary>
    Applied,

    /// <summary>旧版本/旧时间戳被拒绝（幂等忽略）。</summary>
    IgnoredStale,

    /// <summary>目标消息不存在。</summary>
    MessageMissing,

    /// <summary>目标消息已撤回，不可再编辑。</summary>
    AlreadyRecalled
}
