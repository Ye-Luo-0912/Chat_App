namespace Core.Interfaces;

/// <summary>
/// 本机稳定设备标识。跨登录会话保留，用于 HTTP <c>X-Device-Id</c>。
/// </summary>
public interface ILocalDeviceIdentity
{
    /// <summary>SHA-256 长度的 Base64url 设备指纹（无填充，43 字符）。</summary>
    string DeviceId { get; }

    /// <summary>发给服务端的 User-Agent，便于展示设备名称。</summary>
    string UserAgent { get; }
}
