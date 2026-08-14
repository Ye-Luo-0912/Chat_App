using Core.Models;

namespace Core.Interfaces;

/// <summary>
/// 客户端通话会话管理器（CALL-E2E-2）。编排一个或多个通话会话：
/// 发起/应答/拒绝/取消/挂断/重连命令、来电分派、超时收尾与多设备终态收敛；
/// SDP 经 <see cref="ICallMediaSession"/> 媒体面抽象在信令平面中传递。
/// </summary>
public interface ICallSessionManager : IDisposable
{
    /// <summary>来电（被叫侧收到 invite 时触发，携带已就绪的被叫会话）。</summary>
    event EventHandler<CallSession>? IncomingCall;

    /// <summary>会话可见状态变化（Ringing/Active/Ended 等）。</summary>
    event EventHandler<CallSession>? CallStateChanged;

    /// <summary>会话进入终态（Ended）后触发。</summary>
    event EventHandler<CallSession>? CallEnded;

    /// <summary>当前活跃（未终态）会话集合。</summary>
    IReadOnlyCollection<CallSession> ActiveCalls { get; }

    /// <summary>按 call id 查找会话；已结束并移除的会话返回 null。</summary>
    CallSession? GetCall(string callId);

    /// <summary>主叫发起 1:1 语音通话。sdpOffer 缺省时由媒体面生成。</summary>
    Task<CallSession> StartCallAsync(
        long calleeUserId,
        string? sdpOffer = null,
        CallGrantDto? grant = null,
        CancellationToken ct = default);

    /// <summary>被叫接听（携带 SDP answer，可缺省由媒体面生成）。</summary>
    Task AcceptAsync(string callId, string? sdpAnswer = null, CancellationToken ct = default);

    /// <summary>被叫拒绝。</summary>
    Task RejectAsync(string callId, CancellationToken ct = default);

    /// <summary>主叫在接通前取消。</summary>
    Task CancelAsync(string callId, CancellationToken ct = default);

    /// <summary>任一方挂断。</summary>
    Task EndAsync(string callId, CancellationToken ct = default);

    /// <summary>通话中断线重连（重新协商 SDP/ICE，状态不变）。</summary>
    Task ReconnectAsync(string callId, string? sdp = null, CancellationToken ct = default);
}
