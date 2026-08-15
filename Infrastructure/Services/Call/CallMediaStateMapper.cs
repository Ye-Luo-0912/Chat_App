using Core.Interfaces;
using SIPSorcery.Net;

namespace Chat_App.Infrastructure.Services.Call;

/// <summary>
/// 将 SIPSorcery 的 ICE 连接状态映射为控制面的 <see cref="CallMediaState"/>（CALL-E2E-2）。
/// 供 <see cref="SipsorceryCallMediaSession"/> 在 <c>oniceconnectionstatechange</c> 时上报，
/// 纯静态便于单测。
/// </summary>
public static class CallMediaStateMapper
{
    public static CallMediaState Map(RTCIceConnectionState state) => state switch
    {
        RTCIceConnectionState.@new => CallMediaState.Connecting,
        RTCIceConnectionState.checking => CallMediaState.Connecting,
        RTCIceConnectionState.connected => CallMediaState.Connected,
        RTCIceConnectionState.disconnected => CallMediaState.Reconnecting,
        RTCIceConnectionState.failed => CallMediaState.Failed,
        RTCIceConnectionState.closed => CallMediaState.Closed,
        _ => CallMediaState.Failed,
    };
}