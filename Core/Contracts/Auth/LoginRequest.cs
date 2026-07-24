namespace Core.Contracts.Auth;

public struct LoginRequest()
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class EamilRequest()
{
    public required string Email { get; set; }
}
