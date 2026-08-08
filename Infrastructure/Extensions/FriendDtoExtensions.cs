using Core.Contracts.Friends;
using ChatApp.Contracts.Http.Friends;
using Chat_App.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Chat_App.Infrastructure.Extensions;

/// <summary>
/// 好友相关 DTO 的扩展方法，用于转换为本地模型。
/// </summary>
public static class FriendDtoExtensions
{
    // ── 单个转换 ──────────────────────────────────────

    /// <summary>
    /// 将服务端 FriendDto 转换为本地 LocalFriend 模型。
    /// </summary>
    public static LocalFriend ToLocalFriend(this FriendDto dto, long ownerUserId)
    {
        return new LocalFriend
        {
            OwnerUserId = ownerUserId,
            FriendId = dto.FriendId,
            FriendName = dto.FriendName,
            DisplayName = string.IsNullOrWhiteSpace(dto.Note)
                ? dto.FriendName ?? string.Empty
                : dto.Note,
            AvatarUrl = dto.AvatarUrl,
            GroupId = dto.GroupId,
            GroupName = dto.GroupName,
            Note = dto.Note,
            Status = FriendshipStatus.Approved,
            CreatedAt = dto.CreatedAt,
            LastSynced = DateTime.UtcNow,
            IsDeleted = false,
        };
    }

    /// <summary>
    /// 将服务端 FriendRequestDto 转换为本地 LocalFriendRequest 模型。
    /// </summary>
    public static LocalFriendRequest ToLocalFriendRequest(this FriendRequestDto dto)
    {
        return new LocalFriendRequest
        {
            RequesterId = dto.RequesterId,
            TargetUserId = dto.TargetUserId,
            Message = dto.Message,
            CreatedAt = dto.CreatedAt
        };
    }

    /// <summary>
    /// 将服务端 BlockedUserDto 转换为本地 LocalBlockedUser 模型。
    /// </summary>
    public static LocalBlockedUser ToBlockedUser(this BlockedUserDto dto)
    {
        return new LocalBlockedUser
        {
            BlockedUserId = dto.UserId,
            BlockedUserName = dto.UserName ?? "未知用户",
            AvatarUrl = dto.AvatarUrl,
            BlockedAt = dto.BlockedAt
        };
    }

    // ── 批量转换 ──────────────────────────────────────

    /// <summary>
    /// 将 FriendDto 集合批量转换为 LocalFriend 列表。
    /// </summary>
    public static List<LocalFriend> ToLocalFriends(this IEnumerable<FriendDto> dtos, long ownerUserId)
        => [.. dtos.Select(dto => dto.ToLocalFriend(ownerUserId))];

    /// <summary>
    /// 将 FriendRequestDto 集合批量转换为 LocalFriendRequest 列表。
    /// </summary>
    public static List<LocalFriendRequest> ToLocalFriendRequests(this IEnumerable<FriendRequestDto> dtos)
        => [.. dtos.Select(dto => dto.ToLocalFriendRequest())];

    /// <summary>
    /// 将 BlockedUserDto 集合批量转换为 LocalBlockedUser 列表。
    /// </summary>
    public static List<LocalBlockedUser> ToBlockedUsers(this IEnumerable<BlockedUserDto> dtos)
        => [.. dtos.Select(dto => dto.ToBlockedUser())];
}
