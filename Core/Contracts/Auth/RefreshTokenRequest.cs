namespace Core.Contracts.Auth;

public struct RefreshTokenRequest
{
    public long UserId { get; set; }
    public string? RefreshToken { get; set; }
}
