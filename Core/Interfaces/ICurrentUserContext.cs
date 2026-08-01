using System;

namespace Core.Interfaces
{
    /// <summary>
    /// 当前登录用户的只读上下文。由 TokenInfo/ChatConnectionCoordinator 写入，供持久化层做账户隔离。
    /// </summary>
    public interface ICurrentUserContext
    {
        /// <summary>当前用户 Id；未登录时为 null。</summary>
        long? UserId { get; }

        /// <summary>当前用户显示名；未登录或未加载时为 null。</summary>
        string? UserName { get; }

        /// <summary>是否已通过 TCP 鉴权。</summary>
        bool IsAuthenticated { get; }

        /// <summary>UserId 是否可用（>0 且非 null）。</summary>
        bool HasUserId { get; }

        /// <summary>
        /// 返回当前用户 Id；未登录时抛 <see cref="InvalidOperationException"/>。
        /// 适用于"已确保登录"的内部路径；不确定时优先用 <see cref="TryGetUserId"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">用户未登录或 UserId 无效。</exception>
        long RequireUserId();

        /// <summary>
        /// 尝试获取当前用户 Id，成功返回 true。
        /// 适用于网络事件回调等"可能未登录"的场景，避免抛异常中断事件流。
        /// </summary>
        bool TryGetUserId(out long userId);
    }
}
