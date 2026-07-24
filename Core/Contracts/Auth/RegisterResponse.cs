namespace Core.Contracts.Auth;

public class RegisterResponse
{
    public bool IsSuccess { get; set; }
    public long? UserId { get; set; }
    public string? Username { get; set; }
    public string? Message { get; set; }
    public List<IdentityErrorDto>? Errors { get; set; }
}

public class IdentityErrorDto
{
    public string? Code { get; set; }
    public string? Description { get; set; }
}
