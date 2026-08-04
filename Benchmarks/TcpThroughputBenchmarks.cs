using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BenchmarkDotNet.Attributes;

namespace Benchmarks;

/// <summary>
/// TCP 回环吞吐基准：量化 Socket 接收缓冲大小与 TLS（SslStream）对吞吐的影响，
/// 为 TcpClientExample 的缓冲区设置与 TLS 开销基线提供数据。
/// 每迭代独立建连（listener + accept + 传输 4MB + 断开），计时仅含连接与传输。
/// 证书在全局初始化生成一次，所有 TLS 迭代复用（不测证书生成）。
/// </summary>
[ShortRunJob]
public class TcpThroughputBenchmarks : IDisposable
{
    private const int PayloadBytes = 4 * 1024 * 1024;

    private static readonly byte[] Payload = new byte[PayloadBytes];

    [Params(8192, 65536, 262144)]
    public int ReceiveBufferSize { get; set; }

    [Params(false, true)]
    public bool UseTls { get; set; }

    private X509Certificate2? _certificate;
    private TcpListener? _listener;
    private Task? _serverTask;
    private bool _disposed;

    [GlobalSetup]
    public void Setup()
    {
        _certificate = CreateSelfSignedCertificate();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _serverTask = Task.Run(ServerLoopAsync);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _listener?.Stop();
        _listener = null;
        _serverTask = null;
    }

    [Benchmark]
    public async Task Send_4MB_Loopback()
    {
        using var client = new TcpClient();
        client.SendBufferSize = 64 * 1024;
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener!.LocalEndpoint).Port);

        var stream = client.GetStream();
        if (UseTls)
        {
            using var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("benchmark", null, SslProtocols.Tls12, checkCertificateRevocation: false);
            await ssl.WriteAsync(Payload);
        }
        else
        {
            await stream.WriteAsync(Payload);
        }

        // 等服务端完整读取（保证传输语义与 TLS 排空一致），随后断开完成迭代。
        await _serverTask!.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private async Task ServerLoopAsync()
    {
        try
        {
            using var client = await _listener!.AcceptTcpClientAsync();
            client.ReceiveBufferSize = ReceiveBufferSize;
            var stream = client.GetStream();
            if (UseTls)
            {
                using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(_certificate!, false, SslProtocols.Tls12, false);
                await DrainAsync(ssl);
            }
            else
            {
                await DrainAsync(stream);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SERVER ERROR: {ex}");
            throw;
        }
    }

    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        var remaining = PayloadBytes;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
                return;
            remaining -= read;
        }
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=benchmark", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        using var raw = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // 导出再导入：使私钥可导出，Windows SChannel 不接受临时密钥集（ephemeral keys）。
        return new X509Certificate2(raw.Export(X509ContentType.Pfx));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _certificate?.Dispose();
    }
}
