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
        foreach (var friend in friends)
            _friends.Add(friend);

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

            var friend = _friends.FirstOrDefault(f => f.FriendId == friendId);
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

        var friend = _friends.FirstOrDefault(f => f.FriendId == friendId);
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

        _filteredFriends.Clear();
        foreach (var friend in ordered)
            _filteredFriends.Add(friend);
    }

    public void Dispose()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;
    }
}
