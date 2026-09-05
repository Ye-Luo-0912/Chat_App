using Core.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

/// <summary>
/// 通话授权 HTTP 服务（CALL-E2E-2）。向 Server 的 <c>POST /api/calls/grants</c>
/// 请求短期 call grant，作为 1:1 语音通话信令的授权输入。
/// </summary>
public interface ICallApiService
{
    /// <summary>
    /// 为对端用户 <paramref name="calleeUserId"/> 请求一次通话授权。
    /// <para>
    /// 成功返回 <see cref="CallGrantDto"/>；HTTP 失败、错误包体或解析失败返回 null
    /// （不跨 UI 边界抛异常）。
    /// </para>
    /// </summary>
    Task<CallGrantDto?> RequestGrantAsync(long calleeUserId, CancellationToken ct = default);

    /// <summary>
    /// 请求群组（Mesh ≤4 人）通话授权（GROUP-CALL-1）。
    /// </summary>
    /// <param name="memberUserIds">被邀请成员列表（不含本端主叫）。</param>
    /// <param name="callId">
    /// 可选通话 Id（GROUP-CALL-MIDJOIN-1）：存在则<b>携带原 callId 重签</b>（更新参与者名单，
    /// 同一通话持续、中期加人不迁移房间）；缺省则 Server 新生成。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    Task<CallGrantDto?> RequestGroupGrantAsync(
        IReadOnlyList<long> memberUserIds,
        string? callId = null,
        CancellationToken ct = default)
        => throw new NotSupportedException("当前通话授权服务不支持群组通话。");
}