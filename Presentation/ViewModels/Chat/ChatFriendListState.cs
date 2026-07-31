using Core.Helpers;
using Core.Models.DTO;
using Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Chat_App.Presentation.ViewModels.Chat;

public sealed class ChatFriendListState : IDisposable
{
    private readonly ObservableCollection<LocalFriend> _friends;
    private readonly Dictionary<long, LocalFriend> _friendsById = new();
    private readonly ObservableCollection<LocalFriend> _filteredFriends;
    private string _searchText = string.Empty;
    private LocalFriend? _selectedFriend;
    private CancellationTokenSource? _searchDebounceCts;

    public ChatFriendListState(ObservableCollection<LocalFriend> friends, ObservableCollection<LocalFriend> filteredFriends)
    {
        _friends = friends;
        _filteredFriends = filteredFriends;
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (string.Equals(_searchText, value, StringComparison.Ordinal))
                return;

            _searchText = value;
            _searchDebounceCts?.Cancel();
            _searchDebounceCts = new CancellationTokenSource();
            var token = _searchDebounceCts.Token;
            _ = Task.Delay(200, token).ContinueWith(_ =>
            {
                if (!token.IsCancellationRequested)
                    Dispatcher.UIThread.Post(ApplyFilter);
            }, TaskScheduler.Default);
        }
    }

    public LocalFriend? SelectedFriend
    {
        get => _selectedFriend;
        set => _selectedFriend = value;
    }

    public void ReplaceFriends(IEnumerable<LocalFriend> friends)
    {
        _friends.Clear();
        _friendsById.Clear();
        foreach (var friend in friends)
        {
            _friends.Add(friend);
            _friendsById[friend.FriendId] = friend;
        }

        ApplyFilter();
    }

    public void ApplyConversationPrefs(IReadOnlyList<ConversationListItemDto> items, long selfUserId)
    {
        foreach (var item in items)
        {
            var peerId = item.PeerUserId
                ?? ConversationId.TryGetPeerUserId(item.ConversationId, selfUserId);
            if (peerId is not long friendId)
                continue;

            _friendsById.TryGetValue(friendId, out var friend);
            if (friend is null)
                continue;

            friend.IsPinned = item.IsPinned;
            friend.PinnedAtMs = item.PinnedAtMs;
            friend.IsMuted = item.IsMuted;
            friend.MutedUntilMs = item.MutedUntilMs;
            if (!string.IsNullOrWhiteSpace(item.LastMessagePreview))
                friend.LastMessagePreview = item.LastMessagePreview;
        }

        ApplyFilter();
    }

    public void ApplyConversationChanged(ConversationChangedDto changed, long selfUserId)
    {
        var peerId = changed.PeerUserId
            ?? ConversationId.TryGetPeerUserId(changed.ConversationId, selfUserId);
        if (peerId is not long friendId)
            return;

        _friendsById.TryGetValue(friendId, out var friend);
        if (friend is null)
            return;

        if (changed.IsPinned is bool pinned)
        {
            friend.IsPinned = pinned;
            if (!pinned)
                friend.PinnedAtMs = null;
            else if (friend.PinnedAtMs is null)
                friend.PinnedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        if (changed.IsMuted is bool muted)
        {
            friend.IsMuted = muted;
            friend.MutedUntilMs = muted ? changed.MutedUntilMs : null;
        }

        if (!string.IsNullOrWhiteSpace(changed.LastMessagePreview))
            friend.LastMessagePreview = changed.LastMessagePreview;

        ApplyFilter();
    }

    public void ApplyFilter()
    {
        var text = _searchText.Trim();
        IEnumerable<LocalFriend> filtered = string.IsNullOrEmpty(text)
            ? _friends
            : _friends.Where(f =>
                (f.FriendName?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                || f.FriendId.ToString().Contains(text, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(f.DisplayName)
                    && f.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)));

        var ordered = filtered
            .OrderByDescending(f => f.IsPinned)
            .ThenByDescending(f => f.PinnedAtMs ?? 0)
            .ThenBy(f => f.DisplayName ?? f.FriendName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 增量 diff：只更新变化的部分，避免 Clear+Add 导致 UI 重建所有 item container
        ApplyIncrementalDiff(ordered);
    }

    /// <summary>
    /// 增量更新 _filteredFriends：删除不再匹配的项，插入新增的项，移动顺序变化的项。
    /// 保留未变化项的 item container，避免 UI 重建（P0-UI 热路径优化）。
    /// </summary>
    private void ApplyIncrementalDiff(List<LocalFriend> source)
    {
        if (_filteredFriends.Count == 0)
        {
            foreach (var item in source)
                _filteredFriends.Add(item);
            return;
        }

        if (source.Count == 0)
        {
            _filteredFriends.Clear();
            return;
        }

        // 构建新列表的 FriendId → index 映射
        var sourceIds = new Dictionary<long, int>(source.Count);
        for (var i = 0; i < source.Count; i++)
            sourceIds[source[i].FriendId] = i;

        // 从后往前删除不在新列表中的项
        for (var i = _filteredFriends.Count - 1; i >= 0; i--)
        {
            if (!sourceIds.ContainsKey(_filteredFriends[i].FriendId))
                _filteredFriends.RemoveAt(i);
        }

        // 从前往后遍历，移动或插入到正确位置
        for (int srcIdx = 0, tgtIdx = 0; srcIdx < source.Count; srcIdx++)
        {
            var srcItem = source[srcIdx];
            if (tgtIdx < _filteredFriends.Count && _filteredFriends[tgtIdx].FriendId == srcItem.FriendId)
            {
                // 位置正确，跳过
                tgtIdx++;
                continue;
            }

            // 查找该项是否已在列表后续位置（需要移动）
            var found = -1;
            for (var j = tgtIdx; j < _filteredFriends.Count; j++)
            {
                if (_filteredFriends[j].FriendId == srcItem.FriendId)
                {
                    found = j;
                    break;
                }
            }

            if (found >= 0)
            {
                _filteredFriends.Move(found, tgtIdx);
                tgtIdx++;
            }
            else
            {
                _filteredFriends.Insert(tgtIdx, srcItem);
                tgtIdx++;
            }
        }
    }

    public void Dispose()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;
    }
}
