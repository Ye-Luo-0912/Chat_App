namespace Core.Contracts.Friends.Enums;

public enum LocalOperationErrorCode : byte
{
    None = 0,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    ValidationFailed,
    NetworkError,
    SerializationError,
    Cancelled,
    ServerError,
    Unknown,
    InternalSystemError,
    EmptyResponse
}
