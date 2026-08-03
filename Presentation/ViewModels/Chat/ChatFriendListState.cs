using Core.Helpers;
using Core.Models.DTO;
using Chat_App.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Chat_App.Presentation.ViewModels.Chat;

/// <summary>
/// 会话列表状态（会话中心）：以 LocalConversation 为 UI 数据源。
/// 好友信息（显示名/在线状态）经共享好友索引注入，聊天列表与通讯录共用同一状态源。
/// 负责：服务端会话列表投影、实时会话变化、本地会话合并、搜索过滤、置顶/最后活动排序、
/// 归档/删除本地状态、未读角标与草稿摘要展示。
/// </summary>
public sealed class ChatFriendListState : IDisposable
{
    private readonly ObservableCollection<LocalConversation> _conversations;
    private readonly ObservableCollection<LocalConversation> _filteredConversations;

    // conversationId → 会话（含归档项，不含已删除项）
    private readonly Dictionary<string, LocalConversation> _byId = new(StringComparer.Ordinal);
    // 本地删除 tombstone：RemoveConversation 后服务端投影不得复活（DB IsDeleted 的 UI 内存镜像）
    private readonly HashSet<string> _deletedConversationIds = new(StringComparer.Ordinal);
    // 共享好友状态源：显示名/备注/在线状态（通讯录与聊天列表共用）
    private readonly Dictionary<long, LocalFriend> _friendsById = new();

    private string _searchText = string.Empty;
    private LocalConversation? _selectedConversation;
    private bool _showArchived;
    private CancellationTokenSource? _searchDebounceCts;

    public ChatFriendListState(
        ObservableCollection<LocalConversation> conversations,
        ObservableCollection<LocalConversation> filteredConversations)
    {
        _conversations = conversations;
        _filteredConversations = filteredConversations;
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
            _searchDebounceCts?.Dispose();
            _searchDebounceCts = new CancellationTokenSource();
            var token = _searchDebounceCts.Token;
            _ = Task.Delay(200, token).ContinueWith(_ =>
            {
                if (!token.IsCancellationRequested)
                    Dispatcher.UIThread.Post(ApplyFilter);
            }, TaskScheduler.Default);
        }
    }

    public LocalConversation? SelectedConversation
    {
        get => _selectedConversation;
        set => _selectedConversation = value;
    }

    /// <summary>归档视图开关：打开时在列表中展示已归档会话。</summary>
    public bool ShowArchived
    {
        get => _showArchived;
        set
        {
            if (_showArchived == value)
                return;
            _showArchived = value;
            ApplyFilter();
        }
    }

    public bool HasArchivedConversations => _conversations.Any(c => c.Archived);

    /// <summary>按会话 Id 查找（含归档项，不含已删除项）。</summary>
    public LocalConversation? FindConversation(string conversationId) =>
        _byId.TryGetValue(conversationId, out var conv) ? conv : null;

    /// <summary>群聊事件附带 Title 时更新群名（成员加入/批量加入通知）。</summary>
    public void ApplyGroupTitle(string conversationId, string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || !_byId.TryGetValue(conversationId, out var conv))
            return;
        conv.GroupTitle = title;
        conv.Type = (byte)ConversationTypeDto.Group;
    }

    /// <summary>从会话外入口（如通讯录跳转）新建/更新会话并展示。
    /// 已存在时就地合并（保留列表/绑定中的同一对象实例），绝不替换集合对象。</summary>
    public void UpsertLocalConversation(LocalConversation conversation)
    {
        if (_byId.TryGetValue(conversation.ConversationId, out var existing))
        {
            // 就地合并：保留 UI 与字典指向同一实例，服务端字段更新、本地状态保留。
            CopyConversationFields(existing, conversation);
            ApplyFilter();
            return;
        }

        _byId[conversation.ConversationId] = conversation;
        _conversations.Add(conversation);
        ApplyFilter();
    }

    /// <summary>服务端可投影字段复制（不覆盖本地草稿/归档/删除状态）。</summary>
    private static void CopyConversationFields(LocalConversation target, LocalConversation src)
    {
        target.Type = src.Type;
        target.PeerUserId = src.PeerUserId;
        target.GroupTitle = src.GroupTitle;
        target.LastMessageId = src.LastMessageId;
        target.LastMessagePreview = src.LastMessagePreview;
        target.LastMessageAtMs = src.LastMessageAtMs;
        target.LastSenderUserId = src.LastSenderUserId;
        target.UnreadCount = src.UnreadCount;
        target.LastReadMessageId = src.LastReadMessageId;
        target.LastReadAtMs = src.LastReadAtMs;
        target.IsPinned = src.IsPinned;
        target.PinnedAtMs = src.PinnedAtMs;
        target.IsMuted = src.IsMuted;
        target.MutedUntilMs = src.MutedUntilMs;
    }

    // ── 共享好友状态源 ────────────────────────────────────

    public IReadOnlyCollection<long> FriendIds => _friendsById.Keys;

    public bool TryGetFriend(long peerId, out LocalFriend friend) =>
        _friendsById.TryGetValue(peerId, out friend!);

    /// <summary>替换好友状态源（含 tombstone 过滤），并刷新所有会话的显示名/在线状态。</summary>
    public void ApplyFriends(IEnumerable<LocalFriend> friends)
    {
        _friendsById.Clear();
        foreach (var f in friends)
        {
            if (!f.IsDeleted)
                _friendsById[f.FriendId] = f;
        }

        foreach (var conv in _conversations)
            RefreshDisplayName(conv);
    }

    /// <summary>好友在线状态变化：更新索引并同步当前会话展示。</summary>
    public void ApplyPresence(long userId, bool isOnline)
    {
        if (_friendsById.TryGetValue(userId, out var friend))
            friend.IsOnline = isOnline;

        if (_selectedConversation?.PeerUserId == userId)
            _selectedConversation.PeerIsOnline = isOnline;
        foreach (var conv in _conversations)
        {
            if (conv.PeerUserId == userId)
                conv.PeerIsOnline = isOnline;
        }
    }

    // ── 会话层 ──────────────────────────────────────────

    /// <summary>服务端会话列表投影：新增/更新会话（不覆盖本地草稿/归档/删除状态）。
    /// 本地已删除的会话（tombstone）跳过，不得复活。</summary>
    public void ApplyConversationPrefs(IReadOnlyList<ConversationListItemDto> items, long selfUserId)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.ConversationId))
                continue;
            var conv = GetOrCreate(item.ConversationId, selfUserId);
            if (conv is null)
                continue; // 本地已删除：服务端投影不得复活
            CopyDtoToConversation(conv, item);
            RefreshDisplayName(conv);
        }

        ApplyFilter();
    }

    /// <summary>实时会话变化（新消息/置顶/免打扰等）。本地已删除的会话跳过。</summary>
    public void ApplyConversationChanged(ConversationChangedDto changed, long selfUserId)
    {
        if (string.IsNullOrEmpty(changed.ConversationId))
            return;

        var conv = GetOrCreate(changed.ConversationId, selfUserId);
        if (conv is null)
            return; // 本地已删除：不得复活
        if (changed.Type != 0)
            conv.Type = (byte)changed.Type;
        if (changed.Title is not null)
            conv.GroupTitle = changed.Title;
        if (changed.LastMessageId is not null)
            conv.LastMessageId = changed.LastMessageId;
        if (changed.LastMessagePreview is not null)
            conv.LastMessagePreview = changed.LastMessagePreview;
        if (changed.LastMessageAtMs is long atMs)
            conv.LastMessageAtMs = atMs;
        if (changed.LastSenderUserId is long sender)
            conv.LastSenderUserId = sender;

        if (changed.IsPinned is bool pinned)
        {
            conv.IsPinned = pinned;
            conv.PinnedAtMs = pinned
                ? (conv.PinnedAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                : null;
        }

        if (changed.IsMuted is bool muted)
        {
            conv.IsMuted = muted;
            conv.MutedUntilMs = muted ? changed.MutedUntilMs : null;
        }

        RefreshDisplayName(conv);
        ApplyFilter();
    }

    /// <summary>启动时合并本地会话（含草稿/归档/删除状态），仅新增，服务端字段随后覆盖。</summary>
    public void ApplyLocalConversations(IEnumerable<LocalConversation> conversations)
    {
        foreach (var conv in conversations)
        {
            if (conv.IsDeleted || string.IsNullOrEmpty(conv.ConversationId))
                continue;
            if (_byId.ContainsKey(conv.ConversationId))
                continue;
            _byId[conv.ConversationId] = conv;
            _conversations.Add(conv);
            RefreshDisplayName(conv);
        }

        ApplyFilter();
    }

    /// <summary>归档会话：从主列表移除，可从归档视图恢复。</summary>
    public void ArchiveConversation(string conversationId)
    {
        if (_byId.TryGetValue(conversationId, out var conv))
        {
            conv.Archived = true;
            conv.IsPinned = false;
            ApplyFilter();
        }
    }

    /// <summary>恢复归档会话。</summary>
    public void UnarchiveConversation(string conversationId)
    {
        if (_byId.TryGetValue(conversationId, out var conv))
        {
            conv.Archived = false;
            ApplyFilter();
        }
    }

    /// <summary>本地删除会话：从列表与索引移除并记入 tombstone（DB 与内存双标记，
    /// 服务端同步投影不得复活）。</summary>
    public void RemoveConversation(string conversationId)
    {
        if (!_byId.TryGetValue(conversationId, out var conv))
            return;
        _byId.Remove(conversationId);
        _deletedConversationIds.Add(conversationId);
        _conversations.Remove(conv);
        _filteredConversations.Remove(conv);
        if (ReferenceEquals(_selectedConversation, conv))
            _selectedConversation = null;
        ApplyFilter();
    }

    public void ApplyFilter()
    {
        var text = _searchText.Trim();
        IEnumerable<LocalConversation> filtered = _conversations
            .Where(c => !c.IsDeleted)
            .Where(c => _showArchived ? c.Archived : !c.Archived);

        if (!string.IsNullOrEmpty(text))
        {
            filtered = filtered.Where(c =>
                c.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (c.LastMessagePreview?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                || (c.Draft?.Contains(text, StringComparison.OrdinalIgnoreCase) == true));
        }

        var ordered = filtered
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.PinnedAtMs ?? 0)
            .ThenByDescending(c => c.LastMessageAtMs ?? 0)
            .ToList();

        ApplyIncrementalDiff(_filteredConversations, ordered, c => c.ConversationId);
    }

    /// <summary>
    /// 增量更新绑定列表：删除消失项、插入新增项、移动顺序变化的项。
    /// keyed reconciliation：预建 target 的 key→位置索引，Move/Insert 查找为 O(1)
    ///（旧实现对每个源位置线性扫描剩余集合，大量重排时最坏 O(n²)）。
    /// 保留未变化项的 item container，避免 UI 重建。
    /// </summary>
    private static void ApplyIncrementalDiff<T>(
        ObservableCollection<T> target, List<T> source, Func<T, string> keyOf)
    {
        if (target.Count == 0)
        {
            foreach (var item in source)
                target.Add(item);
            return;
        }

        if (source.Count == 0)
        {
            target.Clear();
            return;
        }

        var sourceIds = new Dictionary<string, int>(source.Count);
        for (var i = 0; i < source.Count; i++)
            sourceIds[keyOf(source[i])] = i;

        // 预建 target 位置索引（keyed reconciliation 的查找表）
        var targetIds = new Dictionary<string, int>(target.Count);
        for (var i = 0; i < target.Count; i++)
            targetIds[keyOf(target[i])] = i;

        // 删除消失项（从后往前，索引维护 O(1)）
        for (var i = target.Count - 1; i >= 0; i--)
        {
            var key = keyOf(target[i]);
            if (!sourceIds.ContainsKey(key))
            {
                targetIds.Remove(key);
                target.RemoveAt(i);
            }
        }

        // 顺序调和：目标项按源顺序归位（查找 O(1)，仅被移动/插入的项修正索引）
        var tgtIdx = 0;
        for (var srcIdx = 0; srcIdx < source.Count; srcIdx++)
        {
            var srcKey = keyOf(source[srcIdx]);
            if (tgtIdx < target.Count && keyOf(target[tgtIdx]) == srcKey)
            {
                tgtIdx++;
                continue;
            }

            if (targetIds.TryGetValue(srcKey, out var found) && found >= tgtIdx)
            {
                target.Move(found, tgtIdx);
                // 维护索引：found 移到 tgtIdx，[tgtIdx, found) 区间右移一位
                for (var k = tgtIdx; k <= found; k++)
                    targetIds[keyOf(target[k])] = k;
                tgtIdx++;
            }
            else
            {
                target.Insert(tgtIdx, source[srcIdx]);
                // 维护索引：新项在 tgtIdx，其后整体右移
                for (var k = tgtIdx; k < target.Count; k++)
                    targetIds[keyOf(target[k])] = k;
                tgtIdx++;
            }
        }
    }

    /// <summary>按会话 Id 获取或创建会话项（新建时加入列表与索引）。
    /// 本地已删除（tombstone）的会话返回 null——服务端投影不得复活。</summary>
    private LocalConversation? GetOrCreate(string conversationId, long selfUserId)
    {
        if (_byId.TryGetValue(conversationId, out var existing))
            return existing;
        if (_deletedConversationIds.Contains(conversationId))
            return null;

        var conv = new LocalConversation
        {
            OwnerUserId = selfUserId,
            ConversationId = conversationId
        };
        _byId[conversationId] = conv;
        _conversations.Add(conv);
        return conv;
    }

    /// <summary>从共享好友索引注入显示名/在线状态（无好友记录时 Title 兜底"用户 {id}"）。</summary>
    private void RefreshDisplayName(LocalConversation conv)
    {
        if (conv.IsGroup)
        {
            // 群聊无单端好友：标题由群名承担，不注入对端显示名。
            conv.PeerDisplayName = null;
            conv.PeerIsOnline = false;
            return;
        }

        if (conv.PeerUserId is long peerId && _friendsById.TryGetValue(peerId, out var friend))
        {
            conv.PeerDisplayName = string.IsNullOrWhiteSpace(friend.DisplayName)
                ? friend.FriendName
                : friend.DisplayName;
            conv.PeerIsOnline = friend.IsOnline;
        }
        else
        {
            conv.PeerDisplayName = null;
            conv.PeerIsOnline = false;
        }
    }

    /// <summary>服务端字段投影到会话（草稿/归档/删除等本地状态不覆盖）。</summary>
    private static void CopyDtoToConversation(LocalConversation target, ConversationListItemDto src)
    {
        target.Type = (byte)src.Type;
        target.PeerUserId = src.PeerUserId;
        target.GroupTitle = src.Title;
        target.LastMessageId = src.LastMessageId;
        target.LastMessagePreview = src.LastMessagePreview;
        target.LastMessageAtMs = src.LastMessageAtMs;
        target.LastSenderUserId = src.LastSenderUserId;
        target.UnreadCount = src.UnreadCount;
        target.LastReadMessageId = src.LastReadMessageId;
        target.LastReadAtMs = src.LastReadAtMs;
        target.IsPinned = src.IsPinned;
        target.PinnedAtMs = src.PinnedAtMs;
        target.IsMuted = src.IsMuted;
        target.MutedUntilMs = src.MutedUntilMs;
    }

    public void Dispose()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;
    }
}
