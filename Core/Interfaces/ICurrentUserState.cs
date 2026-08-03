using System;

namespace Core.Interfaces
{
    /// <summary>
    /// 可写的当前用户状态（继承 <see cref="ICurrentUserContext"/>）。
    /// 由登录/鉴权流程写入，供其他服务只读消费。所有写入必须原子
    /// （CAS read-modify-write），并发更新不得互相覆盖。
    /// </summary>
    public interface ICurrentUserState : ICurrentUserContext
    {
        /// <summary>
        /// 原子设置完整鉴权会话：同一账户重连不递增账户代际（仅更新连接代际），
        /// 账户切换才递增账户代际。
        /// </summary>
        void SetAuthenticatedSession(long userId, string? userName, string? sessionId, ulong? deviceHash, long connectionGeneration);

        /// <summary>仅更新连接代际（传输重连；账户代际不变）。</summary>
        void BumpConnectionGeneration(long connectionGeneration);

        /// <summary>令牌刷新：令牌代际递增（账户代际不变）。</summary>
        void BumpTokenGeneration();

        /// <summary>设置当前登录用户（鉴权成功或从本地还原时调用），Generation 递增。</summary>
        void SetCurrentUser(long userId, string? userName);

        /// <summary>补充会话信息（会话 Id / 设备指纹），保留现有用户名。</summary>
        void SetSession(string? sessionId, ulong? deviceHash);

        /// <summary>清除当前用户状态（登出/断连时调用），Generation 递增。</summary>
        void Clear();
    }
}
