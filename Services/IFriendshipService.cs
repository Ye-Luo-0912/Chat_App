using Core.Contracts.Friends;
using ChatApp.Contracts.Http.Friends;
using Chat_App.Infrastructure.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Services;

public interface IFriendshipService
{
    // ── 好友列表 ──────────────────────────────────────
    IAsyncEnumerable<FriendDto> GetAllFriendsAsync(CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteFriendAsync(long friendId, CancellationToken ct = default);

    // ── 好友申请 ──────────────────────────────────────
    Task<SendFriendRequestResult> SendFriendRequestAsync(long targetUserId, string? message, CancellationToken ct = default);

    Task<OperationResult<List<FriendRequestDto>>> GetIncomingRequestsAsync(CancellationToken ct = default);
    Task<OperationResult<List<FriendRequestDto>>> GetOutgoingRequestsAsync(CancellationToken ct = default);

    Task<OperationResult<FriendDto>> AcceptRequestAsync(long requesterId, CancellationToken ct = default);   // 返回新增好友对象
    Task<OperationResult> DeclineRequestAsync(long requesterId, CancellationToken ct = default);

    // ── 黑名单 ────────────────────────────────────────
    Task<OperationResult<List<BlockedUserDto>>> GetBlockedUsersAsync(CancellationToken ct = default);
    Task<OperationResult> UnblockUserAsync(long blockedUserId, CancellationToken ct = default);
}

