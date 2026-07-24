namespace Core.Contracts.Auth;

public class Token
{
    public string? TokenValue { get; set; }
    public DateTime TokenExpires { get; set; }
}
