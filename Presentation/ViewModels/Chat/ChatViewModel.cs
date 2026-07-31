using Avalonia.Threading;
using Chat_App.Models;
using Chat_App.Services;
using Chat_App.Shared.Commands;
using Core.Helpers;
using Core.Interfaces;
using Core.Models.DTO;
using Infrastructure.Models;
using Infrastructure.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Presentation.ViewModels.Chat;

public class ChatViewModel : ViewModelBase, IDisposable
{
    private readonly INotificationService _notificationService;
    private readonly MessageViewModel _messageViewModel;
    private readonly IChatFriendLoader _friendLoader;
    private readonly IChatConnectionCoordinator _connectionCoordinator;
    private readonly IChatSessionClient _chatSession;
    private readonly IMessageStore _messageStore;
    private readonly ChatFriendListState _friendListState;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _disposed;
    private Message? _pendingForwardMessage;
    private long[] _watchedPresenceUserIds = [];
    private readonly Dictionary<long, LocalFriend> _friendsById = new();

    public ObservableCollection<LocalFriend> Friends { get; } = [];
    public ObservableCollection<LocalFriend> FilteredFriends { get; } = [];

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                _friendListState.SearchText = value;
        }
    }

    private bool _isInitialized;
    public bool IsInitialized => _isInitialized;

    private bool _isSelectingForwardTarget;
    public bool IsSelectingForwardTarget
    {
        get => _isSelectingForwardTarget;
        private set
        {
            if (SetProperty(ref _isSelectingForwardTarget, value))
            {
                OnPropertyChanged(nameof(ForwardHintText));
                CancelForwardCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ForwardHintText =>
        IsSelectingForwardTarget ? "选择要转发到的好友…" : string.Empty;

    private LocalFriend? _selectedFriend;
    public LocalFriend? SelectedFriend
    {
        get => _selectedFriend;
        set
        {
            if (IsSelectingForwardTarget && _pendingForwardMessage is not null && value is not null)
            {
                var source = _pendingForwardMessage;
                var sourceFriend = _selectedFriend;
                // 允许转发到当前会话：即使 SelectedFriend 未变也继续
                _ = SetProperty(ref _selectedFriend, value);
                _friendListState.SelectedFriend = value;
                ClearForwardSelection();
                _ = CompleteForwardAsync(value, source, sourceFriend);
                return;
            }

            if (SetProperty(ref _selectedFriend, value))
            {
                _friendListState.SelectedFriend = value;
                if (value is not null)
                {
                    _messageViewModel.Init(value);
                    CurrentMessage = _messageViewModel;
                }
                else
                {
                    _messageViewModel.Clear();
                    CurrentMessage = null;
                }
            }
        }
    }

    private object? _currentMessage;
    public object? CurrentMessage
    {
        get => _currentMessage;
        set => SetProperty(ref _currentMessage, value);
    }

    private ChatConnectionStatus _connectionStatus = ChatConnectionStatus.Disconnected;
    public ChatConnectionStatus ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (SetProperty(ref _connectionStatus, value))
            {
                OnPropertyChanged(nameof(ConnectionStatusText));
                OnPropertyChanged(nameof(ConnectionIndicatorColor));
                OnPropertyChanged(nameof(ConnectionIndicatorBrush));
            }
        }
    }

    public string ConnectionStatusText => ConnectionStatus switch
    {
        ChatConnectionStatus.Connected => "已连接",
        ChatConnectionStatus.Connecting => "连接中…",
        ChatConnectionStatus.Authenticating => "鉴权中…",
        ChatConnectionStatus.Reconnecting => "重连中…",
        _ => "未连接"
    };

    public string ConnectionIndicatorColor => ConnectionStatus switch
    {
        ChatConnectionStatus.Connected => "#86EFAC",
        ChatConnectionStatus.Connecting => "#FDE68A",
        ChatConnectionStatus.Authenticating => "#FDE68A",
        ChatConnectionStatus.Reconnecting => "#FDBA74",
        _ => "#FCA5A5"
    };

    private static readonly Avalonia.Media.IBrush ConnectedBrush = Avalonia.Media.Brush.Parse("#86EFAC");
    private static readonly Avalonia.Media.IBrush ConnectingBrush = Avalonia.Media.Brush.Parse("#FDE68A");
    private static readonly Avalonia.Media.IBrush ReconnectingBrush = Avalonia.Media.Brush.Parse("#FDBA74");
    private static readonly Avalonia.Media.IBrush DisconnectedBrush = Avalonia.Media.Brush.Parse("#FCA5A5");

    public Avalonia.Media.IBrush ConnectionIndicatorBrush => ConnectionStatus switch
    {
        ChatConnectionStatus.Connected => ConnectedBrush,
        ChatConnectionStatus.Connecting => ConnectingBrush,
        ChatConnectionStatus.Authenticating => ConnectingBrush,
        ChatConnectionStatus.Reconnecting => ReconnectingBrush,
        _ => DisconnectedBrush
    };

    public AsyncRelayCommand<LocalFriend> PinFriendCommand { get; }
    public AsyncRelayCommand<LocalFriend> UnpinFriendCommand { get; }
    public AsyncRelayCommand<LocalFriend> MuteFriendCommand { get; }
    public AsyncRelayCommand<LocalFriend> UnmuteFriendCommand { get; }
    public AsyncRelayCommand CancelForwardCommand { get; }

#pragma warning disable CS8618
    public ChatViewModel()
    {
        if (Avalonia.Controls.Design.IsDesignMode)
        {
            _isInitialized = true;
            Friends.Add(new LocalFriend { FriendName = "马化腾 (预览)", FriendId = 10001, DisplayName = "马化腾 (预览)", IsPinned = true });
            Friends.Add(new LocalFriend { FriendName = "张小龙 (预览)", FriendId = 10002, DisplayName = "张小龙 (预览)", IsMuted = true });
            Friends.Add(new LocalFriend { FriendName = "Avalonia 机器人", FriendId = 10003, DisplayName = "Avalonia 机器人" });

            _friendListState = new ChatFriendListState(Friends, FilteredFriends);
            RebuildFriendsIndex();
            _friendListState.ApplyFilter();
            PinFriendCommand = new AsyncRelayCommand<LocalFriend>(_ => Task.CompletedTask);
            UnpinFriendCommand = new AsyncRelayCommand<LocalFriend>(_ => Task.CompletedTask);
            MuteFriendCommand = new AsyncRelayCommand<LocalFriend>(_ => Task.CompletedTask);
            UnmuteFriendCommand = new AsyncRelayCommand<LocalFriend>(_ => Task.CompletedTask);
            CancelForwardCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
        }
    }
#pragma warning restore CS8618

    public ChatViewModel(
        INotificationService notificationService,
        MessageViewModel messageViewModel,
        IChatFriendLoader friendLoader,
        IChatConnectionCoordinator connectionCoordinator,
        IChatSessionClient chatSessionClient,
        IMessageStore messageStore)
    {
        _notificationService = notificationService;
        _messageViewModel = messageViewModel;
        _friendLoader = friendLoader;
        _connectionCoordinator = connectionCoordinator;
        _chatSession = chatSessionClient;
        _messageStore = messageStore;
        _friendListState = new ChatFriendListState(Friends, FilteredFriends);

        PinFriendCommand = new AsyncRelayCommand<LocalFriend>(
            friend => SetPrefsAsync(friend, pinned: true),
            friend => friend is not null && !friend.IsPinned,
            ex => _notificationService.ShowError($"置顶失败: {ex.Message}"));

        UnpinFriendCommand = new AsyncRelayCommand<LocalFriend>(
            friend => SetPrefsAsync(friend, pinned: false),
            friend => friend is not null && friend.IsPinned,
            ex => _notificationService.ShowError($"取消置顶失败: {ex.Message}"));

        MuteFriendCommand = new AsyncRelayCommand<LocalFriend>(
            friend => SetPrefsAsync(friend, muted: true),
            friend => friend is not null && !friend.IsMuted,
            ex => _notificationService.ShowError($"开启免打扰失败: {ex.Message}"));

        UnmuteFriendCommand = new AsyncRelayCommand<LocalFriend>(
            friend => SetPrefsAsync(friend, muted: false),
            friend => friend is not null && friend.IsMuted,
            ex => _notificationService.ShowError($"关闭免打扰失败: {ex.Message}"));

        CancelForwardCommand = new AsyncRelayCommand(
            _ =>
            {
                ClearForwardSelection();
                return Task.CompletedTask;
            },
            () => IsSelectingForwardTarget);

        _messageViewModel.ForwardRequested = BeginForwardSelection;

        _connectionCoordinator.RegisterEventHandlers();
        _connectionCoordinator.StatusChanged += OnConnectionStatusChanged;
        ConnectionStatus = _connectionCoordinator.Status;
        _chatSession.ConversationChanged += OnConversationChanged;
        _chatSession.Authenticated += OnAuthenticatedRefreshPrefs;
        _chatSession.PresenceChanged += OnPresenceChanged;
    }

    private void RebuildFriendsIndex()
    {
        _friendsById.Clear();
        foreach (var f in Friends)
            _friendsById[f.FriendId] = f;
    }

    private void OnPresenceChanged(object? sender, Core.Models.DTO.PresenceChangedDto e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_friendsById.TryGetValue(e.UserId, out var friend))
                friend.IsOnline = e.IsOnline;
        });
    }

    private async Task RefreshPresenceAsync(CancellationToken ct)
    {
        if (!_chatSession.IsAuthenticated || Friends.Count == 0)
            return;

        try
        {
            var ids = Friends.Select(f => f.FriendId).Where(id => id > 0).Distinct().ToArray();
            if (ids.Length == 0)
                return;

            var snap = await _chatSession.QueryPresenceAsync(ids, ct).ConfigureAwait(false);
            _watchedPresenceUserIds = ids;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in snap.Items)
                {
                    if (_friendsById.TryGetValue(item.UserId, out var friend))
                        friend.IsOnline = item.IsOnline;
                }

                if (SelectedFriend is not null && _friendsById.TryGetValue(SelectedFriend.FriendId, out var selected))
                    SelectedFriend.IsOnline = selected.IsOnline;
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "批量查询在线状态失败");
        }
    }

    private async Task UnwatchPresenceSubscriptionsAsync()
    {
        var ids = Interlocked.Exchange(ref _watchedPresenceUserIds, Array.Empty<long>());
        if (ids.Length == 0 || !_chatSession.IsAuthenticated)
            return;

        try
        {
            await _chatSession.UnwatchPresenceAsync(ids).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "批量取消在线状态订阅失败");
        }
    }

    private void BeginForwardSelection(Message source)
    {
        _pendingForwardMessage = source;
        IsSelectingForwardTarget = true;
    }

    private void ClearForwardSelection()
    {
        _pendingForwardMessage = null;
        IsSelectingForwardTarget = false;
    }

    private async Task CompleteForwardAsync(
        LocalFriend target,
        Message source,
        LocalFriend? sourceFriend)
    {
        Message? localBubble = null;
        try
        {
            // CurrFriend 仍为来源会话，先发送再 Init 目标
            localBubble = await _messageViewModel.ExecuteForwardAsync(target, source)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "转发消息失败");
            _notificationService.ShowError($"转发失败: {ex.Message}");
        }
        finally
        {
            // 切到其他好友才 Init；转发到当前会话只追加气泡，避免清空历史
            if (sourceFriend?.FriendId != target.FriendId)
            {
                _messageViewModel.Init(target);
                CurrentMessage = _messageViewModel;
            }

            if (localBubble is not null)
                _messageViewModel.AddMessage(localBubble);
        }
    }

    public async Task InitAsync(CancellationToken ct = default)
    {
        if (_isInitialized)
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_isInitialized)
                return;

            try
            {
                var friends = await _friendLoader.LoadAsync(ct);
                _friendListState.ReplaceFriends(friends);
                RebuildFriendsIndex();
                _isInitialized = true;
                if (_chatSession.IsAuthenticated)
                    _ = RefreshPresenceAsync(ct);
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"加载好友列表失败: {ex.Message}");
            }

            try
            {
                await _connectionCoordinator.ConnectAsync(ct);
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"服务器连接失败，聊天功能可能不可用: {ex.Message}");
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async void OnAuthenticatedRefreshPrefs(object? sender, long userId)
    {
        try
        {
            await RefreshAfterAuthAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "鉴权后同步引导失败");
        }
    }

    private void OnConnectionStatusChanged(object? sender, ChatConnectionStatus status)
    {
        Dispatcher.UIThread.Post(() => ConnectionStatus = status);
    }

    private void OnConversationChanged(object? sender, Core.Models.DTO.ConversationChangedDto e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _friendListState.ApplyConversationChanged(e, _chatSession.CurrentUserId);
            RaisePrefsCommands();
        });
    }

    private async Task RefreshAfterAuthAsync(CancellationToken ct)
    {
        if (!_chatSession.IsAuthenticated)
            return;

        // 从本地 DB 加载持久化的同步水位，重连只拉取缺失数据（阶段 1-2）。
        IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null;
        try
        {
            watermarks = await _messageStore.GetSyncWatermarksAsync(ct).ConfigureAwait(false);
            if (watermarks.Count == 0)
                watermarks = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载本地同步水位失败，回退到空水位");
            watermarks = null;
        }

        var sync = await _chatSession.QuerySyncBootstrapAsync(
                listLimit: 100,
                historyLimitPerConversation: 30,
                maxConversationsWithHistory: 10,
                watermarks: watermarks,
                ct)
            .ConfigureAwait(false);

        if (!sync.Succeeded)
        {
            Log.Warning("同步引导失败: {Code} {Message}", sync.ErrorCode, sync.ErrorMessage);
            // 回退到会话列表，至少恢复置顶/免打扰。
            await RefreshConversationPrefsAsync(ct).ConfigureAwait(false);
            await RefreshPresenceAsync(ct).ConfigureAwait(false);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _friendListState.ApplyConversationPrefs(sync.Conversations, _chatSession.CurrentUserId);
            RaisePrefsCommands();

            if (SelectedFriend is null || sync.CatchUps.Count == 0)
                return;

            var conversationId = ConversationId.CreateDirect(
                _chatSession.CurrentUserId,
                SelectedFriend.FriendId);
            var catchUp = sync.CatchUps.FirstOrDefault(c =>
                string.Equals(c.ConversationId, conversationId, StringComparison.Ordinal));
            if (catchUp is not null)
                _messageViewModel.ApplyCatchUp(catchUp.Items);
        });

        await RefreshPresenceAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshConversationPrefsAsync(CancellationToken ct)
    {
        if (!_chatSession.IsAuthenticated)
            return;

        var allItems = new List<ConversationListItemDto>();
        bool? beforeIsPinned = null;
        long? beforePinnedAtMs = null;
        long? beforeLastMessageAtMs = null;
        string? beforeConversationId = null;
        var maxPages = 10; // 防止无限循环

        for (var page = 0; page < maxPages; page++)
        {
            var resp = await _chatSession.QueryConversationListAsync(
                limit: 100,
                beforeIsPinned,
                beforePinnedAtMs,
                beforeLastMessageAtMs,
                beforeConversationId,
                ct).ConfigureAwait(false);

            if (!resp.Succeeded)
            {
                Log.Warning("会话列表失败: {Code} {Message}", resp.ErrorCode, resp.ErrorMessage);
                break;
            }

            allItems.AddRange(resp.Items);

            if (!resp.HasMore || resp.NextCursor is null)
                break;

            beforeIsPinned = resp.NextCursor.IsPinned;
            beforePinnedAtMs = resp.NextCursor.PinnedAtMs;
            beforeLastMessageAtMs = resp.NextCursor.LastMessageAtMs;
            beforeConversationId = resp.NextCursor.ConversationId;
        }

        if (allItems.Count == 0)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _friendListState.ApplyConversationPrefs(allItems, _chatSession.CurrentUserId);
            RaisePrefsCommands();
        });
    }

    private async Task SetPrefsAsync(LocalFriend? friend, bool? pinned = null, bool? muted = null)
    {
        if (friend is null)
            return;
        if (!_chatSession.IsAuthenticated)
        {
            _notificationService.ShowError("未连接到服务器，无法修改会话设置。");
            return;
        }

        var conversationId = ConversationId.CreateDirect(_chatSession.CurrentUserId, friend.FriendId);
        var response = await _chatSession.SetConversationPrefsAsync(
                conversationId,
                pinned,
                muted,
                mutedUntilMs: null)
            .ConfigureAwait(true);

        if (!response.Succeeded)
        {
            _notificationService.ShowError(response.ErrorMessage ?? response.ErrorCode ?? "设置失败");
            return;
        }

        friend.IsPinned = response.IsPinned;
        friend.IsMuted = response.IsMuted;
        friend.MutedUntilMs = response.MutedUntilMs;
        if (response.IsPinned && friend.PinnedAtMs is null)
            friend.PinnedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!response.IsPinned)
            friend.PinnedAtMs = null;

        _friendListState.ApplyFilter();
        RaisePrefsCommands();
        _notificationService.ShowSuccess(
            pinned == true ? "已置顶" :
            pinned == false ? "已取消置顶" :
            muted == true ? "已开启免打扰" :
            "已关闭免打扰");
    }

    private void RaisePrefsCommands()
    {
        PinFriendCommand.RaiseCanExecuteChanged();
        UnpinFriendCommand.RaiseCanExecuteChanged();
        MuteFriendCommand.RaiseCanExecuteChanged();
        UnmuteFriendCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 退出登录时重置会话状态：清空好友列表、消息视图与初始化标志，
    /// 避免下一账户复用上一账户的内存视图（P0-5）。
    /// </summary>
    public void Reset()
    {
        _isInitialized = false;
        OnPropertyChanged(nameof(IsInitialized));

        ClearForwardSelection();
        SearchText = string.Empty;
        _watchedPresenceUserIds = [];

        Friends.Clear();
        FilteredFriends.Clear();
        RebuildFriendsIndex();
        _friendListState.ApplyFilter();

        _messageViewModel.Clear();
        SelectedFriend = null;
        CurrentMessage = null;
    }
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _chatSession.ConversationChanged -= OnConversationChanged;
        _chatSession.Authenticated -= OnAuthenticatedRefreshPrefs;
        _chatSession.PresenceChanged -= OnPresenceChanged;
        _connectionCoordinator.StatusChanged -= OnConnectionStatusChanged;
        _ = UnwatchPresenceSubscriptionsAsync();
        _ = _connectionCoordinator.StopAsync();
        _connectionCoordinator.UnregisterEventHandlers();
        _friendListState.Dispose();
        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
