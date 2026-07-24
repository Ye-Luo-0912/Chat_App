namespace Core.Models;

public class ServerEndpoint
{
    public int Id { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string ServerIpAddress  { get; set; } = string.Empty;
    public int ServerPort { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime LastConnected { get; set; }
}
