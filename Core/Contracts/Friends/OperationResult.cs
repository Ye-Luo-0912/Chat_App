using System.Text.Json.Serialization;
using Core.Contracts.Friends.Enums;
using ChatApp.Contracts.Http.Friends;

namespace Core.Contracts.Friends;

public class OperationResult
{
    public bool IsSuccess { get; set; }
    public virtual int ErrorCode { get; set; }
    public string? Message { get; set; }

    [JsonIgnore]
    public bool IsLocal { get; init; }

    [JsonIgnore]
    public LocalOperationErrorCode? LocalErrorCode { get; init; }

    [JsonIgnore]
    public bool Failed => !IsSuccess;

    public static OperationResult Ok(string? msg = null)
        => new() { IsSuccess = true, Message = msg };

    public static OperationResult LocalFail(LocalOperationErrorCode code, string? message = null)
        => new()
        {
            IsSuccess = false,
            ErrorCode = 0,
            Message = message,
            IsLocal = true,
            LocalErrorCode = code
        };
}

public class SendFriendRequestResult : OperationResult
{
    public SendFriendRequestOutcome Outcome { get; set; }
    public FriendDto? Friend { get; set; }

    public override int ErrorCode { get; set; }

    public static SendFriendRequestResult Ok(SendFriendRequestOutcome outcome, string? msg = null, FriendDto? friend = null) => new()
    {
        IsSuccess = true,
        ErrorCode = (int)FriendshipOperationErrorCode.None,
        Message = msg,
        Outcome = outcome,
        Friend = friend
    };

    public new static SendFriendRequestResult LocalFail(LocalOperationErrorCode code, string? msg = null) => new()
    {
        IsSuccess = false,
        ErrorCode = (int)code,
        Message = msg,
        Outcome = SendFriendRequestOutcome.None,
        Friend = null
    };
}

public class OperationResult<T> : OperationResult
{
    public T? Data { get; set; }

    public static OperationResult<T> Success(T data, string? message = null)
        => new()
        {
            IsSuccess = true,
            ErrorCode = 0,
            Message = message,
            Data = data
        };

    public new static OperationResult<T> LocalFail(LocalOperationErrorCode code, string? message = null)
        => new()
        {
            IsSuccess = false,
            ErrorCode = 0,
            Message = message,
            IsLocal = true,
            LocalErrorCode = code,
            Data = default
        };

    public static OperationResult<T> Ok(T data, string? message = null)
        => new()
        {
            IsSuccess = true,
            ErrorCode = 0,
            Message = message,
            Data = data
        };
}
