namespace Core.Models;

public class ServerEndpoint
{
    public int Id { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string ServerIpAddress  { get; set; } = string.Empty;
    public int ServerPort { get; set; }

    /// <summary>是否启用 TLS 加密传输（SslStream 握手）。明文端口必须为 false。</summary>
    public bool UseTls { get; set; }

    /// <summary>TLS SNI 目标主机名；为空时使用 ServerIpAddress。</summary>
    public string? TlsServerName { get; set; }

    public bool IsPrimary { get; set; }
    public DateTime LastConnected { get; set; }
}
