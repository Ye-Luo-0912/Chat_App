namespace Core.Contracts.Auth;

public class EmailResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}
