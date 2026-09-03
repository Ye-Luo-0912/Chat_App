using System.Text.Json;
using ChatApp.Contracts.Http;
using ChatApp.Contracts.Http.Auth;
using ChatApp.Contracts.Http.Common;
using Core.Models;
using Xunit;
using WireEndpoint = ChatApp.Contracts.Http.Common.ServerEndpoint;
using LocalEndpoint = Core.Models.ServerEndpoint;

namespace UnitTests;

/// <summary>
/// ENDPOINT-TLS-1 消费者侧：登录响应 / 手动导入的端点元数据 → 本地 ServerEndpoint 模型。
/// 固定兼容语义：旧形状输入沿用旧行为；未知枚举 fail-closed；不安全组合拒绝并给出文案。
/// </summary>
public sealed class ServerEndpointImportTests
{
    // ---- 导入校验矩阵：合法输入 ----

    [Fact]
    public void Import_TcpTlsWithSni_MapsToTlsEndpointWithSniOverride()
    {
        bool ok = ServerEndpointImport.TryMapImport(
            new EndpointDescriptor
            {
                Scheme = EndpointScheme.TcpTls,
                Host = "10.0.0.8",
                Port = 7000,
                SniTargetHost = "gw.example.com",
                MinimumTls = MinimumTlsPolicy.Tls12OrAbove,
            },
            serverName: "prod-gw",
            out LocalEndpoint local,
            out EndpointPolicyViolation violation);

        Assert.True(ok);
        Assert.Equal(EndpointPolicyViolation.None, violation);
        Assert.Equal("10.0.0.8", local.ServerIpAddress);
        Assert.Equal("prod-gw", local.ServerName);
        Assert.Equal(7000, local.ServerPort);
        Assert.True(local.UseTls);
        Assert.Equal("gw.example.com", local.TlsServerName);
    }

    [Theory]
    [InlineData(EndpointScheme.Tcp, 7000, false)]
    [InlineData(EndpointScheme.TcpTls, 7000, true)]
    [InlineData(EndpointScheme.Https, 0, true)]
    [InlineData(EndpointScheme.Http, 0, false)]
    public void Import_UseTlsFollowsScheme_NotSilentlyTightenedOrLoosened(
        EndpointScheme scheme,
        ushort port,
        bool expectedUseTls)
    {
        bool ok = ServerEndpointImport.TryMapImport(
            new EndpointDescriptor { Scheme = scheme, Host = "gw", Port = port == 0 ? null : port },
            serverName: "gw",
            out LocalEndpoint local,
            out _);

        Assert.True(ok);
        Assert.Equal(expectedUseTls, local.UseTls);
        if (port == 0)
            Assert.Equal(EndpointPolicy.GetDefaultPort(scheme), (ushort)local.ServerPort);
    }

    [Fact]
    public void Import_WhitespaceSni_FallsBackToHost()
    {
        bool ok = ServerEndpointImport.TryMapImport(
            new EndpointDescriptor { Scheme = EndpointScheme.TcpTls, Host = "10.0.0.8", Port = 7000, SniTargetHost = "   " },
            serverName: "gw",
            out LocalEndpoint local,
            out _);

        Assert.True(ok);
        Assert.Null(local.TlsServerName);
    }

    // ---- 导入校验矩阵：拒绝分支（fail-closed）----

    [Theory]
    [InlineData(EndpointPolicyViolation.PlaintextSchemeWithTlsPolicy)]
    [InlineData(EndpointPolicyViolation.MissingPort)]
    [InlineData(EndpointPolicyViolation.MissingHost)]
    [InlineData(EndpointPolicyViolation.HostTooLong)]
    [InlineData(EndpointPolicyViolation.HostInvalidCharacters)]
    [InlineData(EndpointPolicyViolation.SniHostInvalid)]
    [InlineData(EndpointPolicyViolation.UnknownScheme)]
    [InlineData(EndpointPolicyViolation.UnknownTlsPolicy)]
    public void Import_InsecureOrInvalidInputs_AreRejectedWithMessage(EndpointPolicyViolation expectedViolation)
    {
        EndpointDescriptor descriptor = expectedViolation switch
        {
            EndpointPolicyViolation.PlaintextSchemeWithTlsPolicy =>
                new() { Scheme = EndpointScheme.Tcp, Host = "gw", Port = 7000, MinimumTls = MinimumTlsPolicy.Tls12OrAbove },
            EndpointPolicyViolation.MissingPort =>
                new() { Scheme = EndpointScheme.Tcp, Host = "gw" },
            EndpointPolicyViolation.MissingHost =>
                new() { Scheme = EndpointScheme.TcpTls, Host = "  ", Port = 7000 },
            EndpointPolicyViolation.HostTooLong =>
                new() { Scheme = EndpointScheme.TcpTls, Host = new string('a', 254), Port = 7000 },
            EndpointPolicyViolation.HostInvalidCharacters =>
                new() { Scheme = EndpointScheme.TcpTls, Host = "gw.example.com/path", Port = 7000 },
            EndpointPolicyViolation.SniHostInvalid =>
                new() { Scheme = EndpointScheme.TcpTls, Host = "gw", Port = 7000, SniTargetHost = "gw example" },
            EndpointPolicyViolation.UnknownScheme =>
                new() { Scheme = (EndpointScheme)99, Host = "gw", Port = 7000 },
            _ => new EndpointDescriptor { Scheme = EndpointScheme.TcpTls, Host = "gw", Port = 7000, MinimumTls = (MinimumTlsPolicy)9 },
        };

        bool ok = ServerEndpointImport.TryMapImport(
            descriptor,
            serverName: "gw",
            out LocalEndpoint local,
            out EndpointPolicyViolation violation);

        Assert.False(ok);
        Assert.Equal(expectedViolation, violation);
        Assert.False(string.IsNullOrWhiteSpace(ServerEndpointImport.DescribeViolation(violation)));
    }

    [Fact]
    public void DescribeViolation_NoneIsEmpty_AndUnknownValueHasFallbackText()
    {
        Assert.Equal(string.Empty, ServerEndpointImport.DescribeViolation(EndpointPolicyViolation.None));
        Assert.False(string.IsNullOrWhiteSpace(ServerEndpointImport.DescribeViolation((EndpointPolicyViolation)250)));
    }

    // ---- 旧形状手动导入按兼容规则映射 ----

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void LegacyImport_KeepsUseTlsChoiceVerbatim(bool useTls, bool expected)
    {
        bool ok = ServerEndpointImport.TryMapLegacyImport(
            "gw.local",
            "office",
            7000,
            useTls,
            sniTargetHost: null,
            out LocalEndpoint local,
            out EndpointPolicyViolation violation);

        Assert.True(ok);
        Assert.Equal(EndpointPolicyViolation.None, violation);
        Assert.Equal(expected, local.UseTls);
        Assert.Equal(7000, local.ServerPort);
        Assert.Equal("office", local.ServerName);
    }

    [Fact]
    public void LegacyImport_InvalidHost_IsRejected()
    {
        bool ok = ServerEndpointImport.TryMapLegacyImport(
            "gw chatapp",
            "office",
            7000,
            useTls: true,
            sniTargetHost: null,
            out _,
            out EndpointPolicyViolation violation);

        Assert.False(ok);
        Assert.Equal(EndpointPolicyViolation.HostInvalidCharacters, violation);
    }

    // ---- 登录响应：旧服务端（无新字段）兼容 ----

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void LoginWire_OldServerWithoutNewFields_KeepsLegacyUseTlsDefault(string legacyUseTls)
    {
        LoginResponse? login = JsonSerializer.Deserialize(
            """{"isSuccess":true,"server":{"host":"127.0.0.1","name":"dev","port":8888}}""",
            HttpContractsJsonSerializerContext.Default.LoginResponse);

        bool ok = ServerEndpointImport.TryMapFromWire(
            login!.Server!.Value,
            legacyUseTls: bool.Parse(legacyUseTls),
            out LocalEndpoint local,
            out EndpointPolicyViolation violation);

        Assert.True(ok);
        Assert.Equal(EndpointPolicyViolation.None, violation);
        Assert.Equal("127.0.0.1", local.ServerIpAddress);
        Assert.Equal("dev", local.ServerName);
        Assert.Equal(8888, local.ServerPort);
        Assert.Equal(bool.Parse(legacyUseTls), local.UseTls);
        Assert.Null(local.TlsServerName);
    }

    // ---- 登录响应：新服务端（加性字段）消费 ----

    [Fact]
    public void LoginWire_NewServerMetadata_MapsOntoLocalModel()
    {
        LoginResponse? login = JsonSerializer.Deserialize(
            """
            {"isSuccess":true,"server":{"host":"10.0.0.8","name":"cn-1","port":7000,"scheme":4,"sniTargetHost":"gw.example.com","minimumTls":1}}
            """,
            HttpContractsJsonSerializerContext.Default.LoginResponse);

        bool ok = ServerEndpointImport.TryMapFromWire(
            login!.Server!.Value,
            legacyUseTls: true,
            out LocalEndpoint local,
            out _);

        Assert.True(ok);
        Assert.True(local.UseTls);
        Assert.Equal(7000, local.ServerPort);
        Assert.Equal("gw.example.com", local.TlsServerName);
    }

    [Fact]
    public void LoginWire_UnknownSchemeValue_FailsClosedInsteadOfGuessing()
    {
        // 契约行为：未知枚举值反序列化保留数值，但校验 fail-closed；绝不解释成安全默认。
        LoginResponse? login = JsonSerializer.Deserialize(
            """{"isSuccess":true,"server":{"host":"gw","name":"cn-1","port":7000,"scheme":99}}""",
            HttpContractsJsonSerializerContext.Default.LoginResponse);

        bool ok = ServerEndpointImport.TryMapFromWire(
            login!.Server!.Value,
            legacyUseTls: true,
            out LocalEndpoint local,
            out EndpointPolicyViolation violation);

        Assert.False(ok);
        Assert.Equal(EndpointPolicyViolation.UnknownScheme, violation);
        Assert.False(string.IsNullOrWhiteSpace(ServerEndpointImport.DescribeViolation(violation)));
    }

    [Fact]
    public void LoginWire_UnknownTlsPolicyValue_FailsClosed()
    {
        LoginResponse? login = JsonSerializer.Deserialize(
            """{"isSuccess":true,"server":{"host":"gw","name":"cn-1","port":7000,"scheme":4,"minimumTls":9}}""",
            HttpContractsJsonSerializerContext.Default.LoginResponse);

        bool ok = ServerEndpointImport.TryMapFromWire(
            login!.Server!.Value,
            legacyUseTls: true,
            out _,
            out EndpointPolicyViolation violation);

        Assert.False(ok);
        Assert.Equal(EndpointPolicyViolation.UnknownTlsPolicy, violation);
    }

    [Fact]
    public void LoginWire_FutureUnknownFields_AreSkipped()
    {
        // 滚动升级双向兼容：新消费者同样必须跳过未来新增字段。
        LoginResponse? login = JsonSerializer.Deserialize(
            """{"isSuccess":true,"server":{"host":"gw","name":"cn-1","port":7000,"scheme":3,"someFutureField":{"x":1}}}""",
            HttpContractsJsonSerializerContext.Default.LoginResponse);

        bool ok = ServerEndpointImport.TryMapFromWire(
            login!.Server!.Value,
            legacyUseTls: true,
            out LocalEndpoint local,
            out _);

        Assert.True(ok);
        Assert.False(local.UseTls);
    }

    [Fact]
    public void LoginWire_MissingHost_InMetadata_FailsClosed()
    {
        LoginResponse? login = JsonSerializer.Deserialize(
            """{"isSuccess":true,"server":{"host":"","name":"cn-1","port":7000,"scheme":4}}""",
            HttpContractsJsonSerializerContext.Default.LoginResponse);

        bool ok = ServerEndpointImport.TryMapFromWire(
            login!.Server!.Value,
            legacyUseTls: true,
            out _,
            out EndpointPolicyViolation violation);

        Assert.False(ok);
        Assert.Equal(EndpointPolicyViolation.MissingHost, violation);
    }

    [Fact]
    public void WireServerEndpoint_NullMetadata_RoundTripsAsLegacy()
    {
        // 默认构造（无元数据）= 旧形状，走旧行为分支。
        var wire = new WireEndpoint { Host = "gw", Name = "cn-1", Port = 7000 };

        bool ok = ServerEndpointImport.TryMapFromWire(wire, legacyUseTls: false, out LocalEndpoint local, out _);

        Assert.True(ok);
        Assert.False(local.UseTls);
        Assert.Null(local.TlsServerName);
    }
}
