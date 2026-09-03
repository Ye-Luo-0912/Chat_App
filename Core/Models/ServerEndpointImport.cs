using ChatApp.Contracts.Http.Common;
using WireEndpoint = ChatApp.Contracts.Http.Common.ServerEndpoint;

namespace Core.Models;

/// <summary>
/// 端点元数据导入（ENDPOINT-TLS-1）：把登录响应 / 手动添加编辑（设置导入）的输入
/// 经 <see cref="EndpointPolicy"/> 校验后映射为本地 <see cref="ServerEndpoint"/> 模型。
/// 兼容规则与 Shared 契约一致：scheme 等新字段缺省 = 旧形状输入，沿用旧行为
/// （UseTls 保持既有默认，不静默变严/变松）；未知枚举值 fail-closed 拒绝，
/// 绝不解释成任何"安全默认"。
/// </summary>
public static class ServerEndpointImport
{
    /// <summary>
    /// 登录响应 <c>server</c> 对象 → 本地模型。
    /// <paramref name="legacyUseTls"/> 是旧路径的 UseTls 默认（appsettings Tcp:UseTls，默认 true），
    /// 仅在服务端未下发 scheme（旧服务端）时生效。
    /// </summary>
    public static bool TryMapFromWire(
        WireEndpoint wire,
        bool legacyUseTls,
        out ServerEndpoint local,
        out EndpointPolicyViolation violation)
    {
        if (wire.Scheme is not { } scheme)
        {
            // 旧服务端响应（无新字段）：完整旧行为，不做任何契约校验（不静默变严/变松）。
            local = new ServerEndpoint
            {
                ServerIpAddress = wire.Host,
                ServerName = wire.Name,
                ServerPort = wire.Port,
                UseTls = legacyUseTls,
                TlsServerName = null,
            };
            violation = EndpointPolicyViolation.None;
            return true;
        }

        var descriptor = new EndpointDescriptor
        {
            Scheme = scheme,
            Host = wire.Host,
            Port = wire.Port,
            SniTargetHost = wire.SniTargetHost,
            MinimumTls = wire.MinimumTls ?? MinimumTlsPolicy.None,
        };

        return TryMapDescriptor(descriptor, serverName: wire.Name, out local, out violation);
    }

    /// <summary>
    /// 手动添加 / 编辑服务器（设置导入）：完整 <see cref="EndpointDescriptor"/> 形状的输入。
    /// 不安全组合（明文 + TLS 策略、非法 host/SNI、缺端口）拒绝并给出
    /// <see cref="DescribeViolation"/> 的明确文案。
    /// </summary>
    public static bool TryMapImport(
        EndpointDescriptor descriptor,
        string serverName,
        out ServerEndpoint local,
        out EndpointPolicyViolation violation) =>
        TryMapDescriptor(descriptor, serverName, out local, out violation);

    /// <summary>
    /// 旧形状输入（只有 host/port/是否 TLS/SNI 的手动导入）按兼容规则映射：
    /// scheme 由 UseTls 推导（TcpTls/Tcp），最低 TLS 策略留空 = 消费者平台默认。
    /// </summary>
    public static bool TryMapLegacyImport(
        string host,
        string serverName,
        ushort port,
        bool useTls,
        string? sniTargetHost,
        out ServerEndpoint local,
        out EndpointPolicyViolation violation)
    {
        var descriptor = new EndpointDescriptor
        {
            Scheme = useTls ? EndpointScheme.TcpTls : EndpointScheme.Tcp,
            Host = host,
            Port = port,
            SniTargetHost = sniTargetHost,
            MinimumTls = MinimumTlsPolicy.None,
        };

        return TryMapDescriptor(descriptor, serverName, out local, out violation);
    }

    /// <summary>校验失败时的用户可见文案（与 EndpointPolicyViolation 一一对应）。</summary>
    public static string DescribeViolation(EndpointPolicyViolation violation) => violation switch
    {
        EndpointPolicyViolation.None => string.Empty,
        EndpointPolicyViolation.UnknownScheme =>
            "未知的传输协议类型（scheme），可能来自更新版本的服务端，请升级客户端后再试。",
        EndpointPolicyViolation.UnknownTlsPolicy =>
            "未知的最低 TLS 版本策略，可能来自更新版本的服务端，请升级客户端后再试。",
        EndpointPolicyViolation.MissingHost => "服务器地址不能为空。",
        EndpointPolicyViolation.HostTooLong => "服务器地址过长（上限 253 个字符）。",
        EndpointPolicyViolation.HostInvalidCharacters => "服务器地址包含非法字符（仅允许字母、数字与 . - : [ ] % _）。",
        EndpointPolicyViolation.SniHostInvalid => "TLS SNI 主机名不合法（仅允许字母、数字与 . - : [ ] % _，上限 253 个字符）。",
        EndpointPolicyViolation.MissingPort => "当前协议没有默认端口，必须显式填写端口。",
        EndpointPolicyViolation.PlaintextSchemeWithTlsPolicy => "明文传输（Http/Tcp）不能声明最低 TLS 版本，请改用 TLS 协议或清空 TLS 策略。",
        _ => "端点配置未通过安全校验。"
    };

    private static bool TryMapDescriptor(
        EndpointDescriptor descriptor,
        string serverName,
        out ServerEndpoint local,
        out EndpointPolicyViolation violation)
    {
        if (!EndpointPolicy.TryValidate(descriptor, out violation))
        {
            local = new ServerEndpoint();
            return false;
        }

        // SNI 空白 = 未覆盖（回退 Host），与既有 TlsServerName 语义一致。
        local = new ServerEndpoint
        {
            ServerIpAddress = descriptor.Host,
            ServerName = serverName ?? string.Empty,
            ServerPort = descriptor.Port
                ?? EndpointPolicy.GetDefaultPort(descriptor.Scheme)
                ?? 0,
            UseTls = !EndpointPolicy.IsPlaintext(descriptor.Scheme),
            TlsServerName = string.IsNullOrWhiteSpace(descriptor.SniTargetHost)
                ? null
                : descriptor.SniTargetHost,
        };
        return true;
    }
}
