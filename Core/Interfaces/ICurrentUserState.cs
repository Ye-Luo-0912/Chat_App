using System;

namespace Core.Interfaces
{
    /// <summary>
    /// 可写的当前用户状态（继承 <see cref="ICurrentUserContext"/>）。
    /// 由登录/鉴权流程写入，供其他服务只读消费。写入采用原子替换快照。
    /// </summary>
    public interface ICurrentUserState : ICurrentUserContext
    {
        /// <summary>设置当前登录用户（鉴权成功或从本地还原时调用），Generation 递增。</summary>
        void SetCurrentUser(long userId, string? userName);

        /// <summary>补充会话信息（会话 Id / 设备指纹），保留现有用户名。</summary>
        void SetSession(string? sessionId, ulong? deviceHash);

        /// <summary>清除当前用户状态（登出/断连时调用），Generation 递增。</summary>
        void Clear();
    }
}
