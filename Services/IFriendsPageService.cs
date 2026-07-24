using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Contracts.Friends;
using Infrastructure.Models;

namespace Chat_App.Services;

/// <summary>
/// 通讯录页面的应用服务接口。
/// 负责：DTO -> Local 转换、服务端调用、本地缓存更新、返回 UI 需要的数据。
/// </summary>
public interface IFriendsPageService
{
    // ── 数据加载 ──────────────────────────────────────
    Task<IReadOnlyList<LocalFriend>> LoadFriendsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LocalFriendRequest>> LoadIncomingRequestsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LocalFriendRequest>> LoadOutgoingRequestsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LocalBlockedUser>> LoadBlockedUsersAsync(CancellationToken ct = default);

    // ── 操作 ──────────────────────────────────────────
    Task<SendFriendRequestResult> SendFriendRequestAsync(long targetUserId, string? message, CancellationToken ct = default);
    Task<OperationResult<FriendDto>> AcceptRequestAsync(long requesterId, CancellationToken ct = default);
    Task<OperationResult> DeclineRequestAsync(long requesterId, CancellationToken ct = default);
    Task<OperationResult> DeleteFriendAsync(long friendId, CancellationToken ct = default);
    Task<OperationResult> UnblockUserAsync(long blockedUserId, CancellationToken ct = default);
}
