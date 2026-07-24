using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Presentation.ViewModels.Shell;
using Chat_App.Services;
using Chat_App.Shared.Commands;
using Infrastructure.Models;
using Serilog;

namespace Chat_App.Presentation.ViewModels.Friends;

/// <summary>
/// 通讯录页面 ViewModel。
/// 包含：好友列表、好友申请（收/发）、黑名单。
/// 通过 FriendsPageService 进行数据加载和操作。
/// </summary>
public class FriendsViewModel : ViewModelBase
{
    private readonly IFriendsPageService _pageService;
    private readonly INotificationService _notificationService;

    #region ── Tab 页签 ──────────────────────────────────

    public enum FriendTab { Friends, Requests, Blocked }

    private FriendTab _activeTab = FriendTab.Friends;
    public FriendTab ActiveTab
    {
        get => _activeTab;
        set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(IsFriendsTab));
                OnPropertyChanged(nameof(IsRequestsTab));
                OnPropertyChanged(nameof(IsBlockedTab));
            }
        }
    }

    public bool IsFriendsTab  => ActiveTab == FriendTab.Friends;
    public bool IsRequestsTab => ActiveTab == FriendTab.Requests;
    public bool IsBlockedTab  => ActiveTab == FriendTab.Blocked;

    #endregion

    #region ── 好友列表 ──────────────────────────────────

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                FilterFriends();
        }
    }

    private LocalFriend? _selectedFriend;
    public LocalFriend? SelectedFriend
    {
        get => _selectedFriend;
        set => SetProperty(ref _selectedFriend, value);
    }

    /// <summary>原始好友列表（从服务端/本地 DB 拉取后填充）</summary>
    private readonly ObservableCollection<LocalFriend> _allFriends = [];
    public ObservableCollection<LocalFriend> FilteredFriends { get; } = [];

    #endregion

    #region ── 好友申请 ──────────────────────────────────

    private bool _showIncoming = true;
    public bool ShowIncoming
    {
        get => _showIncoming;
        set
        {
            if (SetProperty(ref _showIncoming, value))
                OnPropertyChanged(nameof(ShowOutgoing));
        }
    }
    public bool ShowOutgoing => !_showIncoming;

    public ObservableCollection<LocalFriendRequest> IncomingRequests { get; } = [];
    public ObservableCollection<LocalFriendRequest> OutgoingRequests { get; } = [];

    #endregion

    #region ── 黑名单 ────────────────────────────────────

    public ObservableCollection<LocalBlockedUser> BlockedUsers { get; } = [];

    #endregion

    #region ── 状态 / 弹窗 ──────────────────────────────

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private bool _isAddFriendPanelOpen;
    public bool IsAddFriendPanelOpen
    {
        get => _isAddFriendPanelOpen;
        set => SetProperty(ref _isAddFriendPanelOpen, value);
    }

    private string _addFriendId = string.Empty;
    public string AddFriendId
    {
        get => _addFriendId;
        set => SetProperty(ref _addFriendId, value);
    }

    private string _addFriendMessage = string.Empty;
    public string AddFriendMessage
    {
        get => _addFriendMessage;
        set => SetProperty(ref _addFriendMessage, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    #endregion

    #region ── Commands ──────────────────────────────────

    // Tab 切换
    public RelayCommand SwitchToFriendsCommand    { get; }
    public RelayCommand SwitchToRequestsCommand   { get; }
    public RelayCommand SwitchToBlockedCommand    { get; }

    // 申请子视图切换
    public RelayCommand ShowIncomingCommand { get; }
    public RelayCommand ShowOutgoingCommand { get; }

    // 好友列表
    public RelayCommand ShowAddFriendPanelCommand  { get; }
    public RelayCommand CloseAddFriendPanelCommand { get; }
    public AsyncRelayCommand SendAddFriendCommand  { get; }
    public RelayCommand SendMessageCommand         { get; }
    public AsyncRelayCommand<LocalFriend> DeleteFriendCommand { get; }

    // 申请操作
    public AsyncRelayCommand<LocalFriendRequest> AcceptRequestCommand  { get; }
    public AsyncRelayCommand<LocalFriendRequest> DeclineRequestCommand { get; }

    // 黑名单
    public AsyncRelayCommand<LocalBlockedUser> UnblockCommand { get; }

    #endregion

    public FriendsViewModel(
        IFriendsPageService pageService,
        INotificationService notificationService)
    {
        _pageService = pageService;
        _notificationService = notificationService;

        // ── Tab 切换 ──────────────────
        SwitchToFriendsCommand  = new RelayCommand(() => ActiveTab = FriendTab.Friends);
        SwitchToRequestsCommand = new RelayCommand(() => ActiveTab = FriendTab.Requests);
        SwitchToBlockedCommand  = new RelayCommand(() => ActiveTab = FriendTab.Blocked);

        ShowIncomingCommand = new RelayCommand(() => ShowIncoming = true);
        ShowOutgoingCommand = new RelayCommand(() => ShowIncoming = false);

        // ── 添加好友 ──────────────────
        ShowAddFriendPanelCommand  = new RelayCommand(() => IsAddFriendPanelOpen = true);
        CloseAddFriendPanelCommand = new RelayCommand(() =>
        {
            IsAddFriendPanelOpen = false;
            AddFriendId      = string.Empty;
            AddFriendMessage = string.Empty;
        });

        SendAddFriendCommand = new AsyncRelayCommand(SendAddFriendAsync, onException: HandleError);

        // ── 发消息 ────────────────────
        SendMessageCommand = new RelayCommand(() =>
        {
            if (SelectedFriend != null)
                NavigateToChatEvent.Raise(new NavigateToChatEvent(SelectedFriend));
        });

        // ── 删除好友 ──────────────────
        DeleteFriendCommand = new AsyncRelayCommand<LocalFriend>(DeleteFriendAsync, onException: HandleError);

        // ── 好友申请 ──────────────────
        AcceptRequestCommand  = new AsyncRelayCommand<LocalFriendRequest>(AcceptRequestAsync, onException: HandleError);
        DeclineRequestCommand = new AsyncRelayCommand<LocalFriendRequest>(DeclineRequestAsync, onException: HandleError);

        // ── 黑名单 ────────────────────
        UnblockCommand = new AsyncRelayCommand<LocalBlockedUser>(UnblockAsync, onException: HandleError);
    }

    /// <summary>页面激活时调用。</summary>
    public async void Init()
    {
        Log.Information("初始化通讯录页面");
        IsLoading = true;

        try
        {
            // 并行加载好友、请求、黑名单
            var friendsTask = _pageService.LoadFriendsAsync();
            var incomingTask = _pageService.LoadIncomingRequestsAsync();
            var outgoingTask = _pageService.LoadOutgoingRequestsAsync();
            var blockedTask = _pageService.LoadBlockedUsersAsync();

            await Task.WhenAll(friendsTask, incomingTask, outgoingTask, blockedTask).ConfigureAwait(false);

            // 更新 UI 集合
            UpdateCollection(_allFriends, friendsTask.Result);
            UpdateCollection(IncomingRequests, incomingTask.Result);
            UpdateCollection(OutgoingRequests, outgoingTask.Result);
            UpdateCollection(BlockedUsers, blockedTask.Result);

            // 初始过滤
            FilterFriends();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载通讯录数据失败");
            _notificationService.ShowError("加载数据失败，请稍后重试");
        }
        finally
        {
            IsLoading = false;
        }
    }

	// ── 私有方法 ─────────────────────────────────────────

	/// <summary>
	/// 由于 ObservableCollection 没有 Reset 方法，直接 Clear + AddRange 的方式会导致 UI 频繁刷新。
	/// </summary>
	/// <typeparam name="T"></typeparam>
	/// <param name="collection"></param>
	/// <param name="items"></param>
	private static void UpdateCollection<T>(ObservableCollection<T> collection, IReadOnlyList<T> items)
    {
        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }

	/// <summary>
	/// 输入拼音/汉字都能匹配；英文搜索：输入任意部分都能匹配。
	/// </summary>
	private void FilterFriends()
    {
        FilteredFriends.Clear();
        foreach (var f in _allFriends)
        {
            if (string.IsNullOrWhiteSpace(SearchText) ||
                (f.FriendName?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (f.Note?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                FilteredFriends.Add(f);
            }
        }
    }

	/// <summary>
	/// 统一的异常处理方法，记录日志并显示错误通知。
	/// </summary>
	/// <param name="ex"></param>
	private void HandleError(Exception ex)
    {
        Log.Error(ex, "操作失败");
        _notificationService.ShowError($"操作失败: {ex.Message}");
    }

	/// <summary>
    /// 发送好友请求。验证用户 ID，通过服务发送请求，并在成功时刷新待发请求列表。
    /// </summary>
    /// <param name="ct">用于取消操作的令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
	private async Task SendAddFriendAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(AddFriendId)) return;
        if (!long.TryParse(AddFriendId, out var targetUserId))
        {
            _notificationService.ShowError("请输入有效的用户 ID");
            return;
        }

        var result = await _pageService.SendFriendRequestAsync(targetUserId, AddFriendMessage, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            Log.Information("发送好友请求成功 -> {Id}", targetUserId);
            _notificationService.ShowSuccess("好友申请已发送");
            IsAddFriendPanelOpen = false;
            AddFriendId = string.Empty;
            AddFriendMessage = string.Empty;

            // 刷新发出的申请列表
            var outgoing = await _pageService.LoadOutgoingRequestsAsync(ct).ConfigureAwait(false);
            UpdateCollection(OutgoingRequests, outgoing);
        }
        else
        {
            _notificationService.ShowError(result.Message ?? "发送失败");
        }
    }

    private async Task DeleteFriendAsync(LocalFriend? friend, CancellationToken ct)
    {
        if (friend == null) return;

        var result = await _pageService.DeleteFriendAsync(friend.FriendId, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _allFriends.Remove(friend);
            FilteredFriends.Remove(friend);
            if (SelectedFriend == friend) SelectedFriend = null;
            _notificationService.ShowSuccess("已删除好友");
        }
        else
        {
            _notificationService.ShowError(result.Message ?? "删除失败");
        }
    }

    private async Task AcceptRequestAsync(LocalFriendRequest? request, CancellationToken ct)
    {
        if (request == null) return;

        var result = await _pageService.AcceptRequestAsync(request.RequesterId, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            IncomingRequests.Remove(request);
            _notificationService.ShowSuccess("已接受好友请求");

            // 刷新好友列表
            var friends = await _pageService.LoadFriendsAsync(ct).ConfigureAwait(false);
            UpdateCollection(_allFriends, friends);
            FilterFriends();
        }
        else
        {
            _notificationService.ShowError(result.Message ?? "接受失败");
        }
    }

    private async Task DeclineRequestAsync(LocalFriendRequest? request, CancellationToken ct)
    {
        if (request == null) return;

        var result = await _pageService.DeclineRequestAsync(request.RequesterId, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            IncomingRequests.Remove(request);
            _notificationService.ShowSuccess("已拒绝好友请求");
        }
        else
        {
            _notificationService.ShowError(result.Message ?? "拒绝失败");
        }
    }

    private async Task UnblockAsync(LocalBlockedUser? user, CancellationToken ct)
    {
        if (user == null) return;

        var result = await _pageService.UnblockUserAsync(user.BlockedUserId, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            BlockedUsers.Remove(user);
            _notificationService.ShowSuccess("已解除拉黑");
        }
        else
        {
            _notificationService.ShowError(result.Message ?? "解除失败");
        }
    }
}
