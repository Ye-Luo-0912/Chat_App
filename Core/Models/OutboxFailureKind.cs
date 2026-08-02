namespace Core.Models;

/// <summary>
/// Outbox 失败分类：决定发送失败后是否自动重试。
/// </summary>
public enum OutboxFailureKind : byte
{
    /// <summary>未分类（默认）。</summary>
    None = 0,

    /// <summary>可重试（网络/超时/瞬态错误）：指数退避后自动重试。</summary>
    Retryable = 1,

    /// <summary>永久失败（参数/鉴权/校验类错误）：不再自动重试，仅支持手动重试。</summary>
    Permanent = 2
}
