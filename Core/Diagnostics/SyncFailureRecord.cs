using System;

namespace Core.Diagnostics;

/// <summary>
/// 一次同步失败的诊断记录：稳定的机器可读错误码、人类可读信息、发生时间与可重试性。
/// 供诊断页展示与编排层决策使用——临时性失败（网络/服务端瞬时）可自动重试，
/// 永久性失败（会话失效/能力不匹配/契约违例）需引导用户处理而非盲目重试。
/// </summary>
public sealed record SyncFailureRecord(
    string ErrorCode,
    string? Message,
    DateTime OccurredAtUtc,
    bool Transient);