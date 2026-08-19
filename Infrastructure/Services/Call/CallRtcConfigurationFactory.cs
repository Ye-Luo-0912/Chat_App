using Microsoft.Extensions.Configuration;
using SIPSorcery.Net;

namespace Chat_App.Infrastructure.Services.Call;

/// <summary>
/// 单个 ICE 服务器配置项（STUN/TURN 共用，TURN 可带用户名/密码）。
/// </summary>
public sealed class CallIceServerOptions
{
    /// <summary>ICE 服务器 URL，如 <c>stun:stun.example.com:3478</c> 或 <c>turn:turn.example.com:3478?transport=udp</c>。</summary>
    public string? Urls { get; set; }

    /// <summary>TURN 用户名（STUN 可留空）。</summary>
    public string? Username { get; set; }

    /// <summary>TURN 凭据（长期凭证；短期凭证需由应用替换为当下 TURN credential）。</summary>
    public string? Credential { get; set; }
}

/// <summary>
/// 通话媒体面 ICE 服务器配置节（<c>Call:Media</c>）。缺省为空时媒体面回退默认公共 STUN。
/// </summary>
public sealed class CallMediaIceOptions
{
    /// <summary>STUN 服务器 URL 列表。</summary>
    public List<string> StunServers { get; set; } = new();

    /// <summary>TURN 服务器列表（弱网直连失败时经 TURN relay 回退）。</summary>
    public List<CallIceServerOptions> TurnServers { get; set; } = new();
}

/// <summary>
/// 从应用配置构建 <see cref="RTCConfiguration"/>（CALL-E2E-2 TURN 回退验证）。
/// <para>
/// 读取 <c>Call:Media</c> 节的 STUN/TURN 列表；未配置或全部为空时返回 null，
/// 由 <see cref="SipsorceryCallMediaSession"/> 回退默认公共 STUN，保证无配置环境行为不变。
/// </para>
/// </summary>
public static class CallRtcConfigurationFactory
{
    public static RTCConfiguration? FromConfig(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection("Call:Media").Get<CallMediaIceOptions>();
        var servers = new List<RTCIceServer>();

        if (options?.StunServers is { } stuns)
        {
            foreach (var url in stuns)
            {
                if (!string.IsNullOrWhiteSpace(url))
                    servers.Add(new RTCIceServer { urls = url.Trim() });
            }
        }

        if (options?.TurnServers is { } turns)
        {
            foreach (var turn in turns)
            {
                if (string.IsNullOrWhiteSpace(turn.Urls))
                    continue;
                servers.Add(new RTCIceServer
                {
                    urls = turn.Urls.Trim(),
                    username = string.IsNullOrWhiteSpace(turn.Username) ? null : turn.Username,
                    credential = string.IsNullOrWhiteSpace(turn.Credential) ? null : turn.Credential
                });
            }
        }

        if (servers.Count == 0)
            return null;

        return new RTCConfiguration { iceServers = servers };
    }
}
