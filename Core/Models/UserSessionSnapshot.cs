using System;

namespace Core.Models
{
    /// <summary>
    /// 当前账户会话的不可变快照。
    /// 以原子替换方式整体更新（引用类型 + CAS），
    /// 异步任务可在任意时刻读取一致状态，不会被部分更新的字段所误导。
    /// 三代际语义：
    /// - Generation（账户代际）：账户切换/登出时递增，用于校验异步回调是否仍属于当前账户；
    /// - ConnectionGeneration：TCP 传输重连代际（同一账户重连不递增账户代际，由调用方传入连接代际）；
    /// - TokenGeneration：令牌刷新代际。
    /// </summary>
    public sealed record UserSessionSnapshot(
        long OwnerUserId,
        long Generation,
        string? UserName,
        string? SessionId,
        ulong? DeviceHash,
        long ConnectionGeneration = 0,
        long TokenGeneration = 0)
    {
        public static readonly UserSessionSnapshot Empty = new(0, 0, null, null, null);

        public bool IsEmpty => OwnerUserId <= 0;
    }
}
