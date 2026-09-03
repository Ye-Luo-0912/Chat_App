using ChatApp.Shared.Protocol.Tcp;

namespace Core.Models;

/// <summary>
/// 一次带 ResumeToken 连接（ClientHello.ResumeToken）的结果快照。
/// 由 <see cref="Core.Interfaces.IChatSessionClient.LastResumeResult"/> 暴露，
/// 供连接协调器在 ConnectAsync 返回后决定：进入已恢复会话、保留 token 退避重试，
/// 还是清除 token 回退完整认证。
/// </summary>
public sealed class ResumeAttemptResult
{
    /// <summary>网关是否恢复了会话。</summary>
    public bool Success { get; init; }

    /// <summary>失败分类；成功时为 <see cref="ResumeFailureKind.None"/>。</summary>
    public ResumeFailureKind FailureKind { get; init; }

    /// <summary>成功时网关轮换颁发的新 ResumeToken（单次使用语义，旧 token 已失效）。</summary>
    public string? ResumeToken { get; init; }

    public long UserId { get; init; }

    /// <summary>恢复出的网关会话 Id；通常与完整认证时一致。</summary>
    public string? SessionId { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>网关侧会话消息水位（来自 SyncBootstrap 查询）；查询失败为 null，客户端回退常规 SyncBootstrap。</summary>
    public long? LastConversationSequence { get; init; }

    public static ResumeAttemptResult FromSuccess(ResumeResponse response) => new()
    {
        Success = true,
        ResumeToken = response.ResumeToken,
        UserId = response.UserId,
        SessionId = response.SessionId,
        DeviceId = response.DeviceId,
        LastConversationSequence = response.LastConversationSequence
    };

    public static ResumeAttemptResult FromFailure(ResumeFailureKind kind, string? resumeToken = null) => new()
    {
        Success = false,
        FailureKind = kind,
        ResumeToken = resumeToken
    };
}
