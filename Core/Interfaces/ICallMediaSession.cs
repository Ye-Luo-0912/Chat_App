namespace Core.Interfaces;

/// <summary>通话媒体面状态（WebRTC/SRTP 媒体面）。</summary>
public enum CallMediaState
{
    Idle = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Failed = 4,
    Closed = 5,
}

/// <summary>媒体面状态变化事件参数。</summary>
public sealed class CallMediaStateChangedEventArgs : EventArgs
{
    public CallMediaStateChangedEventArgs(CallMediaState state) => State = state;

    /// <summary>新状态。</summary>
    public CallMediaState State { get; }
}

/// <summary>
/// 通话媒体面抽象（CALL-E2E-2）。
/// <para>
/// 音频媒体始终留在 WebRTC/SRTP 与 ICE/STUN/TURN 媒体面；本接口只交换 SDP offer/answer 与
/// ICE candidate，控制面状态机不感知编解码与传输细节。真实实现（SIPSorcery/WebRTC）在
/// 后续阶段接入；当前阶段以测试桩验证 SDP 在信令平面中的完整传递。
/// </para>
/// </summary>
public interface ICallMediaSession : IDisposable
{
    /// <summary>所属通话 Id。</summary>
    string CallId { get; }

    /// <summary>媒体面状态变化（Connected/Reconnecting/Failed/Closed 等）。</summary>
    event EventHandler<CallMediaStateChangedEventArgs>? StateChanged;

    /// <summary>建立 SDP offer（随 Invite/Reconnect 信令转发）。</summary>
    string CreateOffer();

    /// <summary>建立 SDP answer（随 Accept 信令转发）。</summary>
    string CreateAnswer();

    /// <summary>应用对端 SDP（offer 或 answer）。</summary>
    void SetRemoteDescription(string sdp);

    /// <summary>应用对端 ICE candidate（信令面捎带）。</summary>
    void ApplyIceCandidate(string candidate);

    /// <summary>通话建立后启动采集与发送。</summary>
    void Start();

    /// <summary>暂停采集与发送（可 Reconnect 复用，不销毁）。</summary>
    void Stop();

    /// <summary>ICE restart / 断线重连：返回新的本地 SDP offer。</summary>
    string RestartIce();
}
