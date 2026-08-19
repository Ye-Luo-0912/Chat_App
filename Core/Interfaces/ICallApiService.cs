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
}