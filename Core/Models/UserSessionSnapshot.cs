using System;

namespace Core.Models
{
    /// <summary>
    /// 当前账户会话的不可变快照。
    /// 以原子替换方式整体更新（Volatile.Read/Write 需要引用类型），
    /// 异步任务可在任意时刻读取一致状态，不会被部分更新的字段所误导。
    /// Generation 随每次变更单调递增，用于校验异步回调是否仍属于当前会话。
    /// </summary>
    public sealed record UserSessionSnapshot(
        long OwnerUserId,
        long Generation,
        string? UserName,
        string? SessionId,
        ulong? DeviceHash)
    {
        public static readonly UserSessionSnapshot Empty = new(0, 0, null, null, null);

        public bool IsEmpty => OwnerUserId <= 0;
    }
}
