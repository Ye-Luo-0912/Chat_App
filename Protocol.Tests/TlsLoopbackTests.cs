using Chat_App.Infrastructure.Networking;
using Chat_App.Infrastructure.Serialization;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Protocol.Tests;

/// <summary>
/// TLS 加密传输测试。
/// 验收场景：TcpClientExample UseTls=true 时在 TCP 之上完成 TLS 握手，
/// 数据帧经 SslStream 加密传输，服务端 TLS 流能解码出完整帧（线路非明文）；
/// 默认端点与服务端校验为系统信任链（严格校验，不信任自签）。
/// </summary>
public class TlsLoopbackTests
{
    private static X509Certificate2 CreateSelfSignedCertificate(string dnsName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={dnsName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Windows Schannel 需要可持久化的密钥凭据：导出 PFX 后重新导入（避免 ephemeral key 拒绝）。
        var pfx = ephemeral.Export(X509ContentType.Pfx);
        // 不使用 EphemeralKeySet：Windows Schannel 服务器凭据需要持久化密钥集。
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// 自签 TLS 服务端：accept 后做 TLS 握手，从解密流中按帧解码收到的数据。
    /// </summary>
    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _cert;
        private readonly Task _acceptTask;
        private readonly List<byte> _decrypted = new();
        private readonly object _lock = new();
        private string? _error;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string? Error { get { lock (_lock) return _error; } }

        public TlsServer(X509Certificate2 cert)
        {
            _cert = cert;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start(1);
            _acceptTask = AcceptAndReadAsync();
        }

        private async Task AcceptAndReadAsync()
        {
            try
            {
                using var socket = await _listener.AcceptSocketAsync();
                await using var network = new NetworkStream(socket, ownsSocket: true);
                await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                });

                var buf = new byte[8192];
                while (true)
                {
                    var n = await ssl.ReadAsync(buf);
                    if (n == 0)
                        break;
                    lock (_lock)
                        _decrypted.AddRange(buf.AsSpan(0, n).ToArray());
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                    _error = ex.ToString();
            }
        }

        /// <summary>等待连接建立并完成握手。</summary>
        public async Task WaitReadyAsync() => await Task.Delay(200);

        public IReadOnlyList<byte> DecryptedBytes { get { lock (_lock) return _decrypted.ToArray(); } }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>TLS 连接：帧经加密传输，服务端 TLS 流解码出完整帧。</summary>
    [Fact]
    public async Task Tls_Connect_Frames_Arrive_Decrypted_On_Server_Tls_Stream()
    {
        using var cert = CreateSelfSignedCertificate("localhost");
        var server = new TlsServer(cert);
        using var client = new TcpClientExample();
        // 开发/测试自签证书：注入宽松校验（生产保持系统信任链严格校验）
        client.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var serializer = new JsonPacketBodySerializer();

        await client.ConnectAsync(new ServerEndpoint
        {
            ServerIpAddress = "127.0.0.1",
            ServerPort = server.Port,
            UseTls = true,
            TlsServerName = "localhost"
        });
        Assert.True(client.IsConnected);

        await server.WaitReadyAsync();

        Assert.Null(server.Error);

        // 发送一帧
        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        serializer.Serialize(writer, new ChatMessageDto { MessageId = "tls-1", TargetUserId = 1, Content = "tls payload" });
        var packet = new MessagePacket(PacketCommand.ChatMessage,
            new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
        var frameWriter = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + writer.WrittenCount);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
        await client.SendAsync(frameWriter.WrittenMemory).WaitAsync(TimeSpan.FromSeconds(5));

        // 等待服务端解密流收齐
        await Task.Delay(300);
        client.Disconnect("done");
        await server.DisposeAsync();

        var codec = new MessagePacketCodec();
        codec.Append(server.DecryptedBytes.ToArray());
        var count = 0;
        var content = "";
        while (codec.TryRead(out var pkt))
        {
            Assert.Equal(PacketCommand.ChatMessage, pkt.Command);
            var dto = serializer.Deserialize<ChatMessageDto>(pkt.Body);
            Assert.NotNull(dto);
            content = dto!.Content;
            count++;
        }

        Assert.Equal(1, count);
        Assert.Equal("tls payload", content);
    }

    /// <summary>默认校验策略：不信任自签证书（无回调时使用系统信任链，握手失败）。</summary>
    [Fact]
    public async Task Tls_Default_Validation_Rejects_SelfSigned()
    {
        using var cert = CreateSelfSignedCertificate("localhost");
        var server = new TlsServer(cert);
        using var client = new TcpClientExample();
        // 不设置回调：走系统默认严格校验

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new ServerEndpoint
        {
            ServerIpAddress = "127.0.0.1",
            ServerPort = server.Port,
            UseTls = true,
            TlsServerName = "localhost"
        }).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.False(client.IsConnected);
        await server.DisposeAsync();
    }
}
