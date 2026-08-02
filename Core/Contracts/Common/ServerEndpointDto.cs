namespace Core.Contracts.Common;

public struct ServerEndpointDto()
{
    public string Host { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ushort Port { get; set; }
}
