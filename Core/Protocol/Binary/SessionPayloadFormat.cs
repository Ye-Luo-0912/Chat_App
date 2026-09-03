namespace Core.Protocol.Binary;

/// <summary>
/// 连接级载荷格式。握手段（ClientHello/ServerHello）始终 JSON；
/// 完整握手后由 ServerHello.PayloadFormat 决定整个连接的固定格式，
/// 连接中途不 sniff、不切换。客户端不引用网关 Core，故本地定义。
/// </summary>
public enum SessionPayloadFormat : byte
{
    /// <summary>JSON 载荷（默认与回退格式；老服务端或未启用二进制时保持全 JSON）。</summary>
    Json = 0,

    /// <summary>chatapp-bin-v1 二进制载荷（<see cref="ChatApp.Shared.Protocol.Tcp.Binary.BinaryPayloadFormat.Id"/>）。</summary>
    ChatAppBinaryV1 = 1
}
