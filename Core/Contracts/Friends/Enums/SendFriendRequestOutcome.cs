namespace Core.Contracts.Friends.Enums;

public enum SendFriendRequestOutcome
{
    None = 0,
    RequestSent,
    RequestAlreadyPending,
    AcceptedDirectly,
    RestoredDirectly,
    FriendshipRestored
}
