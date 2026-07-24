namespace Core.Contracts.Friends.Enums;

public enum FriendshipOperationResultErrorCode : byte
{
    None = 0,
    Success = 1,
    ValidationFailed = 2,
    FriendshipRequestAlreadyExists = 3,
    FriendshipAlreadyExists = 4,
    FriendshipRequestNotFound = 5,
    FriendshipNotFound = 6,
    InsufficientPermissions = 7,
    InternalSystemError = 8,
    FriendshipRequestExpired = 9,
    RequestAlreadyBlocked = 10,
    FriendGroupNotFound = 11,
}
