namespace Core.Models.DTO;

/// <summary>
/// 推送平台标识（wire 为整数）。与 Gateway 侧的 <c>PushPlatform</c> 数值一致：1=Fcm，2=Apns，3=WebPush。
/// </summary>
public enum PushPlatformDto : byte
{
    /// <summary>Firebase Cloud Messaging（Android/浏览器）。</summary>
    Fcm = 1,

    /// <summary>Apple Push Notification service（iOS/macOS）。</summary>
    Apns = 2,

    /// <summary>Web Push API（浏览器 Service Worker）。</summary>
    WebPush = 3
}

/// <summary>推送令牌注册相关结构上限（与 Gateway 侧 <c>PushTokenLimits</c> 一致）。</summary>
public static class PushTokenLimits
{
    /// <summary>令牌字符串最大长度（FCM ~150，APNs 64 hex；留余量）。</summary>
    public const int MaxTokenLength = 1024;

    /// <summary>RequestId 最大长度。</summary>
    public const int MaxRequestIdLength = 64;

    /// <summary>AppDeviceLabel 最大长度。</summary>
    public const int MaxAppDeviceLabelLength = 128;
}

/// <summary>
/// 注册设备推送令牌请求（C2S）。服务端按 (userId, deviceIdHash) 幂等覆盖；
/// deviceIdHash 取自认证会话，忽略客户端传入。
/// </summary>
public sealed class RegisterPushTokenRequestDto : IRequestDto
{
    /// <summary>请求 Id（由发送方生成，服务端响应原样回显）。</summary>
    public string? RequestId { get; set; }

    /// <summary>推送平台（1=Fcm，2=Apns，3=WebPush）。</summary>
    public PushPlatformDto Platform { get; set; }

    /// <summary>平台下发的推送令牌（长度上限 <see cref="PushTokenLimits.MaxTokenLength"/>）。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>可选应用级设备标识（多 App 共存去重）。</summary>
    public string? AppDeviceLabel { get; set; }
}

/// <summary>注册设备推送令牌响应（S2C）。</summary>
public sealed class RegisterPushTokenResponseDto
{
    public string RequestId { get; set; } = string.Empty;

    public bool Succeeded { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>当前用户已注册的推送令牌数（含本次）。</summary>
    public int ActiveTokenCount { get; set; }
}

/// <summary>
/// 注销推送令牌请求（C2S）。不传 Token 时按当前连接 deviceIdHash 注销该设备全部令牌；
/// 传 Token 时按字符串精确注销（可跨设备，适合平台令牌失效场景）。
/// </summary>
public sealed class UnregisterPushTokenRequestDto : IRequestDto
{
    /// <summary>请求 Id（由发送方生成，服务端响应原样回显）。</summary>
    public string? RequestId { get; set; }

    /// <summary>可选：精确指定要注销的令牌字符串。</summary>
    public string? Token { get; set; }
}

/// <summary>注销设备推送令牌响应（S2C）。</summary>
public sealed class UnregisterPushTokenResponseDto
{
    public string RequestId { get; set; } = string.Empty;

    public bool Succeeded { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>注销后剩余的活跃推送令牌数。</summary>
    public int ActiveTokenCount { get; set; }
}