using Avalonia.Threading;
using Chat_App.Infrastructure.Events;
using Chat_App.Models;
using Chat_App.Services;
using Chat_App.Shared.Commands;
using Core.Helpers;
using Core.Interfaces;
using Core.Models.DTO;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Services;
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
    private readonly ISyncEngine _syncEngine;
    private readonly IDatabaseService _dbService;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IFriendStore _friendStore;
    private readonly ChatFriendListState _friendListState;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IReadOnlyList<ConversationListItemDto>? _lastConversations;
    private bool _disposed;
    private Message? _pendingForwardMessage;
    private long[] _watchedPresenceUserIds = [];

    /// <summary>事件总线：订阅协调器持久化后发布的群聊领域事件。</summary>
    private readonly Core.Interfaces.IEventBus _eventBus = null!;

    /// <summary>群聊领域事件订阅（协调器持久化后发布，UI 投影）。</summary>
    private IDisposable[] _groupEventSubscriptions = [];

    public ObservableCollection<LocalConversation> Conversations { get; } = [];
    public ObservableCollection<LocalConversation> FilteredConversations { get; } = [];

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
        IsSelectingForwardTarget ? "选择要转发到的会话…" : string.Empty;

    private LocalConversation? _selectedConversation;
    public LocalConversation? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (IsSelectingForwardTarget && _pendingForwardMessage is not null && value is not null)
            {
                var source = _pendingForwardMessage;
                var sourceConversation = _selectedConversation;
                // 允许转发到当前会话：即使 SelectedConversation 未变也继续
                _ = SetProperty(ref _selectedConversation, value);
                _friendListState.SelectedConversation = value;
                ClearForwardSelection();
                _ = CompleteForwardAsync(value, source, sourceConversation);
                return;
            }

            if (SetProperty(ref _selectedConversation, value))
            {
                _friendListState.SelectedConversation = value;
                if (value is not null)
                {
                    // 打开会话即清零本地未读角标（服务端未读数随后续会话列表同步校正）
                    if (value.UnreadCount > 0)
                    {
                        value.UnreadCount = 0;
                        _friendListState.ApplyFilter();
                    }

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

    public AsyncRelayCommand<LocalConversation> PinConversationCommand { get; }
    public AsyncRelayCommand<LocalConversation> UnpinConversationCommand { get; }
    public AsyncRelayCommand<LocalConversation> MuteConversationCommand { get; }
    public AsyncRelayCommand<LocalConversation> UnmuteConversationCommand { get; }
    public AsyncRelayCommand<LocalConversation> ArchiveConversationCommand { get; }
    public AsyncRelayCommand<LocalConversation> UnarchiveConversationCommand { get; }
    public AsyncRelayCommand<LocalConversation> DeleteConversationCommand { get; }
    public AsyncRelayCommand CancelForwardCommand { get; }

    // ── 群聊 UI 状态与命令 ──

    private bool _isCreatingGroup;
    private bool _isGroupOwner;
    public bool IsCreatingGroup
    {
        get => _isCreatingGroup;
        private set
        {
            if (SetProperty(ref _isCreatingGroup, value))
            {
                CreateGroupCommand.RaiseCanExecuteChanged();
                CancelCreateGroupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _groupTitleInput = string.Empty;
    public string GroupTitleInput
    {
        get => _groupTitleInput;
        set
        {
            if (SetProperty(ref _groupTitleInput, value))
                CreateGroupCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>建群面板好友勾选列表（创建时从好友列表快照填充）。</summary>
    public ObservableCollection<GroupMemberSelectionItem> GroupCreationCandidates { get; } = [];

    private bool _isShowingGroupMembers;
    public bool IsShowingGroupMembers
    {
        get => _isShowingGroupMembers;
        private set
        {
            if (SetProperty(ref _isShowingGroupMembers, value))
                CloseGroupMembersCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>成员面板列表（服务端 ListGroupMembers 投影）。</summary>
    public ObservableCollection<GroupMemberUiItem> GroupMembers { get; } = [];

    /// <summary>成员列表分页游标与"尚有更多"状态（虚拟化分页）。</summary>
    private string? _groupMembersCursor;
    private bool _groupMembersHasMore;
    public bool GroupMembersHasMore
    {
        get => _groupMembersHasMore;
        private set
        {
            if (SetProperty(ref _groupMembersHasMore, value))
                LoadMoreGroupMembersCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncRelayCommand LoadMoreGroupMembersCommand { get; }

    private bool _isAddingGroupMembers;
    public bool IsAddingGroupMembers
    {
        get => _isAddingGroupMembers;
        private set
        {
            if (SetProperty(ref _isAddingGroupMembers, value))
                AddGroupMembersCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>添加成员时好友勾选列表（未入群的好友）。</summary>
    public ObservableCollection<GroupMemberSelectionItem> GroupAddCandidates { get; } = [];

    public AsyncRelayCommand OpenCreateGroupPanelCommand { get; }
    public AsyncRelayCommand CreateGroupCommand { get; }
    public AsyncRelayCommand CancelCreateGroupCommand { get; }
    public AsyncRelayCommand<LocalConversation> ShowGroupMembersCommand { get; }
    public AsyncRelayCommand<LocalConversation> LeaveGroupCommand { get; }
    public AsyncRelayCommand<LocalConversation> DissolveGroupCommand { get; }
    public AsyncRelayCommand CloseGroupMembersCommand { get; }
    public AsyncRelayCommand<GroupMemberUiItem> RemoveGroupMemberCommand { get; }
    public AsyncRelayCommand<GroupMemberUiItem> ToggleGroupMemberRoleCommand { get; }
    public AsyncRelayCommand ToggleAddGroupMembersCommand { get; }
    public AsyncRelayCommand AddGroupMembersCommand { get; }

#pragma warning disable CS8618
    public ChatViewModel()
    {
        if (Avalonia.Controls.Design.IsDesignMode)
        {
            _isInitialized = true;
            Conversations.Add(new LocalConversation
            {
                ConversationId = "preview-1",
                PeerUserId = 10001,
                PeerDisplayName = "马化腾 (预览)",
                IsPinned = true,
                LastMessagePreview = "好的，收到",
                LastMessageAtMs = DateTimeOffset.Now.AddMinutes(-5).ToUnixTimeMilliseconds()
            });
            Conversations.Add(new LocalConversation
            {
                ConversationId = "preview-2",
                PeerUserId = 10002,
                PeerDisplayName = "张小龙 (预览)",
                IsMuted = true,
                LastMessagePreview = "[图片]",
                LastMessageAtMs = DateTimeOffset.Now.AddHours(-2).ToUnixTimeMilliseconds()
            });
            Conversations.Add(new LocalConversation
            {
                ConversationId = "preview-3",
                PeerUserId = 10003,
                PeerDisplayName = "Avalonia 机器人",
                LastMessagePreview = "你好，有什么可以帮你？",
                LastMessageAtMs = DateTimeOffset.Now.AddDays(-1).ToUnixTimeMilliseconds()
            });

            _friendListState = new ChatFriendListState(Conversations, FilteredConversations);
            _friendListState.ApplyFilter();
            PinConversationCommand = new AsyncRelayCommand<LocalConversation>(_ => Task.CompletedTask);
            UnpinConversationCommand = new AsyncRelayCommand<LocalConversation>(_ => Task.CompletedTask);
            MuteConversationCommand = new AsyncRelayCommand<LocalConversation>(_ => Task.CompletedTask);
            UnmuteConversationCommand = new AsyncRelayCommand<LocalConversation>(_ => Task.CompletedTask);
            ArchiveConversationCommand = new AsyncRelayCommand<LocalConversation>(_ => Task.CompletedTask);
            UnarchiveConversationCommand = new AsyncRelayCommand<LocalConversation>(_ => Task.CompletedTask);
            DeleteConversationCommand = new AsyncRelayCommand<LocalConversation>(_ => Task.CompletedTask);
            CancelForwardCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
            OpenCreateGroupPanelCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
            CreateGroupCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
            CancelCreateGroupCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
            ShowGroupMembersCommand = new AsyncRelayCommand<LocalConversation>(_ => Task.CompletedTask);
            LeaveGroupCommand = new AsyncRelayCommand<LocalConversation>(_ => Task.CompletedTask);
            DissolveGroupCommand = new AsyncRelayCommand<LocalConversation>(_ => Task.CompletedTask);
            CloseGroupMembersCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
            RemoveGroupMemberCommand = new AsyncRelayCommand<GroupMemberUiItem>(_ => Task.CompletedTask);
            ToggleGroupMemberRoleCommand = new AsyncRelayCommand<GroupMemberUiItem>(_ => Task.CompletedTask);
            ToggleAddGroupMembersCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
            AddGroupMembersCommand = new AsyncRelayCommand(_ => Task.CompletedTask);
        }
    }
#pragma warning restore CS8618

    public ChatViewModel(
        INotificationService notificationService,
        MessageViewModel messageViewModel,
        IChatFriendLoader friendLoader,
        IChatConnectionCoordinator connectionCoordinator,
        IChatSessionClient chatSessionClient,
        ISyncEngine syncEngine,
        IDatabaseService dbService,
        ICurrentUserContext currentUserContext,
        IFriendStore friendStore,
        Core.Interfaces.IEventBus eventBus)
    {
        _notificationService = notificationService;
        _messageViewModel = messageViewModel;
        _friendLoader = friendLoader;
        _connectionCoordinator = connectionCoordinator;
        _chatSession = chatSessionClient;
        _syncEngine = syncEngine;
        _dbService = dbService;
        _currentUserContext = currentUserContext;
        _friendStore = friendStore;
        _eventBus = eventBus;
        _friendListState = new ChatFriendListState(Conversations, FilteredConversations);

        PinConversationCommand = new AsyncRelayCommand<LocalConversation>(
            conversation => SetConversationPrefsAsync(conversation, pinned: true),
            conversation => conversation is not null && !conversation.IsPinned,
            ex => _notificationService.ShowError($"置顶失败: {ex.Message}"));

        UnpinConversationCommand = new AsyncRelayCommand<LocalConversation>(
            conversation => SetConversationPrefsAsync(conversation, pinned: false),
            conversation => conversation is not null && conversation.IsPinned,
            ex => _notificationService.ShowError($"取消置顶失败: {ex.Message}"));

        MuteConversationCommand = new AsyncRelayCommand<LocalConversation>(
            conversation => SetConversationPrefsAsync(conversation, muted: true),
            conversation => conversation is not null && !conversation.IsMuted,
            ex => _notificationService.ShowError($"开启免打扰失败: {ex.Message}"));

        UnmuteConversationCommand = new AsyncRelayCommand<LocalConversation>(
            conversation => SetConversationPrefsAsync(conversation, muted: false),
            conversation => conversation is not null && conversation.IsMuted,
            ex => _notificationService.ShowError($"关闭免打扰失败: {ex.Message}"));

        ArchiveConversationCommand = new AsyncRelayCommand<LocalConversation>(
            ArchiveConversationAsync,
            conversation => conversation is not null && !conversation.Archived,
            ex => _notificationService.ShowError($"归档失败: {ex.Message}"));

        UnarchiveConversationCommand = new AsyncRelayCommand<LocalConversation>(
            UnarchiveConversationAsync,
            conversation => conversation is not null && conversation.Archived,
            ex => _notificationService.ShowError($"恢复归档失败: {ex.Message}"));

        DeleteConversationCommand = new AsyncRelayCommand<LocalConversation>(
            DeleteConversationAsync,
            conversation => conversation is not null,
            ex => _notificationService.ShowError($"删除会话失败: {ex.Message}"));

        CancelForwardCommand = new AsyncRelayCommand(
            _ =>
            {
                ClearForwardSelection();
                return Task.CompletedTask;
            },
            () => IsSelectingForwardTarget);

        // ── 群聊命令 ──

        OpenCreateGroupPanelCommand = new AsyncRelayCommand(
            _ =>
            {
                if (!_chatSession.IsAuthenticated)
                {
                    _notificationService.ShowError("未连接服务器，无法创建群聊。");
                    return Task.CompletedTask;
                }
                PopulateCandidates(GroupCreationCandidates, alreadyMemberIds: null);
                GroupTitleInput = string.Empty;
                IsCreatingGroup = true;
                return Task.CompletedTask;
            });

        CreateGroupCommand = new AsyncRelayCommand(
            async _ =>
            {
                var title = GroupTitleInput?.Trim();
                if (string.IsNullOrWhiteSpace(title))
                {
                    _notificationService.ShowWarning("请输入群聊名称。");
                    return;
                }
                var memberIds = GroupCreationCandidates
                    .Where(c => c.IsSelected && c.UserId > 0)
                    .Select(c => c.UserId)
                    .ToArray();
                var conversationId = await CreateGroupAsync(title, memberIds).ConfigureAwait(true);
                if (conversationId is null)
                    return;
                IsCreatingGroup = false;
                GroupTitleInput = string.Empty;
            },
            () => IsCreatingGroup && !string.IsNullOrWhiteSpace(GroupTitleInput),
            ex => _notificationService.ShowError($"创建群聊失败: {ex.Message}"));

        CancelCreateGroupCommand = new AsyncRelayCommand(
            _ =>
            {
                IsCreatingGroup = false;
                GroupTitleInput = string.Empty;
                return Task.CompletedTask;
            },
            () => IsCreatingGroup);

        ShowGroupMembersCommand = new AsyncRelayCommand<LocalConversation>(
            async conversation =>
            {
                if (conversation is null || !conversation.IsGroup)
                    return;
                await LoadGroupMembersAsync(conversation.ConversationId).ConfigureAwait(true);
                IsShowingGroupMembers = true;
            },
            conversation => conversation is not null && conversation.IsGroup,
            ex => _notificationService.ShowError($"加载群成员失败: {ex.Message}"));

        LeaveGroupCommand = new AsyncRelayCommand<LocalConversation>(
            LeaveGroupAsync,
            conversation => conversation is not null && conversation.IsGroup,
            ex => _notificationService.ShowError($"退出群聊失败: {ex.Message}"));

        DissolveGroupCommand = new AsyncRelayCommand<LocalConversation>(
            DissolveGroupAsync,
            conversation => conversation is not null && conversation.IsGroup,
            ex => _notificationService.ShowError($"解散群聊失败: {ex.Message}"));

        CloseGroupMembersCommand = new AsyncRelayCommand(
            _ =>
            {
                IsAddingGroupMembers = false;
                IsShowingGroupMembers = false;
                return Task.CompletedTask;
            },
            () => IsShowingGroupMembers);

        LoadMoreGroupMembersCommand = new AsyncRelayCommand(
            LoadMoreGroupMembersAsync,
            () => GroupMembersHasMore);

        RemoveGroupMemberCommand = new AsyncRelayCommand<GroupMemberUiItem>(
            async member =>
            {
                var conversationId = SelectedConversation?.ConversationId;
                if (member is null || string.IsNullOrWhiteSpace(conversationId) || member.IsSelf)
                    return;
                var response = await _chatSession.RemoveGroupMemberAsync(conversationId, member.UserId).ConfigureAwait(true);
                if (!response.Succeeded)
                {
                    _notificationService.ShowError(response.ErrorMessage ?? response.ErrorCode ?? "移除成员失败");
                    return;
                }
                _notificationService.ShowSuccess($"已移除 {member.DisplayName}");
                await LoadGroupMembersAsync(conversationId).ConfigureAwait(true);
            },
            member => member is not null && !member.IsSelf,
            ex => _notificationService.ShowError($"移除成员失败: {ex.Message}"));

        ToggleGroupMemberRoleCommand = new AsyncRelayCommand<GroupMemberUiItem>(
            async member =>
            {
                var conversationId = SelectedConversation?.ConversationId;
                if (member is null || string.IsNullOrWhiteSpace(conversationId) || member.IsSelf)
                    return;
                var newRole = member.Role == ConversationMemberRole.Admin
                    ? ConversationMemberRole.Member
                    : ConversationMemberRole.Admin;
                var response = await _chatSession.ChangeMemberRoleAsync(conversationId, member.UserId, newRole).ConfigureAwait(true);
                if (!response.Succeeded)
                {
                    _notificationService.ShowError(response.ErrorMessage ?? response.ErrorCode ?? "变更角色失败");
                    return;
                }
                member.Role = newRole;
                _notificationService.ShowSuccess($"{member.DisplayName} 现为 {RoleName(newRole)}");
            },
            member => member is not null && !member.IsSelf && member.Role is ConversationMemberRole.Admin or ConversationMemberRole.Member,
            ex => _notificationService.ShowError($"变更角色失败: {ex.Message}"));

        ToggleAddGroupMembersCommand = new AsyncRelayCommand(
            _ =>
            {
                if (IsAddingGroupMembers)
                {
                    IsAddingGroupMembers = false;
                    return Task.CompletedTask;
                }
                var conversationId = SelectedConversation?.ConversationId;
                if (string.IsNullOrWhiteSpace(conversationId))
                    return Task.CompletedTask;
                var memberIds = GroupMembers.Select(m => m.UserId).ToHashSet();
                PopulateCandidates(GroupAddCandidates, memberIds);
                IsAddingGroupMembers = true;
                return Task.CompletedTask;
            });

        AddGroupMembersCommand = new AsyncRelayCommand(
            async _ =>
            {
                var conversationId = SelectedConversation?.ConversationId;
                if (string.IsNullOrWhiteSpace(conversationId))
                    return;
                var memberIds = GroupAddCandidates
                    .Where(c => c.IsSelected && c.UserId > 0)
                    .Select(c => c.UserId)
                    .ToArray();
                if (memberIds.Length == 0)
                {
                    _notificationService.ShowWarning("请至少选择一名好友。");
                    return;
                }
                var response = await _chatSession.AddGroupMembersAsync(conversationId, memberIds).ConfigureAwait(true);
                if (!response.Succeeded)
                {
                    _notificationService.ShowError(response.ErrorMessage ?? response.ErrorCode ?? "添加成员失败");
                    return;
                }
                _notificationService.ShowSuccess($"已添加 {memberIds.Length} 位成员");
                IsAddingGroupMembers = false;
                await LoadGroupMembersAsync(conversationId).ConfigureAwait(true);
            },
            () => IsAddingGroupMembers,
            ex => _notificationService.ShowError($"添加成员失败: {ex.Message}"));

        _messageViewModel.ForwardRequested = BeginForwardSelection;

        _connectionCoordinator.RegisterEventHandlers();
        _connectionCoordinator.StatusChanged += OnConnectionStatusChanged;
        ConnectionStatus = _connectionCoordinator.Status;
        _chatSession.ConversationChanged += OnConversationChanged;
        // 群聊领域事件：协调器已完成有序持久化（SessionStamp 校验 → 版本比较 → 事务落库），
        // UI 仅订阅领域事件做通知与投影刷新。
        _groupEventSubscriptions =
        [
            _eventBus.Subscribe<GroupMemberJoinedEvent>(OnGroupMemberJoined),
            _eventBus.Subscribe<GroupMemberLeftEvent>(OnGroupMemberLeft),
            _eventBus.Subscribe<GroupMemberRemovedEvent>(OnGroupMemberRemoved),
            _eventBus.Subscribe<GroupRoleChangedEvent>(OnGroupRoleChanged),
            _eventBus.Subscribe<GroupMembersAddedEvent>(OnGroupMembersAdded),
            _eventBus.Subscribe<GroupConversationDissolvedEvent>(OnGroupConversationDissolved)
        ];
        // 鉴权后的会话服务（SyncEngine/好友同步/附件恢复）由 UserSessionOrchestrator 统一启动，
        // ChatViewModel 仅订阅好友投影变化刷新会话列表。
        _friendStore.FriendsChanged += OnFriendsChanged;
        _chatSession.PresenceChanged += OnPresenceChanged;
        _syncEngine.Completed += OnSyncCompleted;
    }

    private void OnFriendsChanged(object? sender, EventArgs e)
    {
        // 好友增量同步完成：重新投影会话列表好友（本地快照立即展示，远端变化后台刷新）。
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var synced = _friendStore.Snapshot;
                _friendListState.ApplyFriends(synced);
                if (_lastConversations is { Count: > 0 })
                    _friendListState.ApplyConversationPrefs(_lastConversations, _chatSession.CurrentUserId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "好友变化投影刷新失败");
            }
        });
    }

    private void OnPresenceChanged(object? sender, Core.Models.DTO.PresenceChangedDto e)
    {
        Dispatcher.UIThread.Post(() => _friendListState.ApplyPresence(e.UserId, e.IsOnline));
    }

    private async Task RefreshPresenceAsync(CancellationToken ct)
    {
        if (!_chatSession.IsAuthenticated || _friendListState.FriendIds.Count == 0)
            return;

        try
        {
            var ids = _friendListState.FriendIds.Where(id => id > 0).Distinct().ToArray();
            if (ids.Length == 0)
                return;

            var snap = await _chatSession.QueryPresenceAsync(ids, ct).ConfigureAwait(false);
            _watchedPresenceUserIds = ids;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in snap.Items)
                    _friendListState.ApplyPresence(item.UserId, item.IsOnline);
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
        LocalConversation target,
        Message source,
        LocalConversation? sourceConversation)
    {
        Message? localBubble = null;
        try
        {
            // 当前会话仍为来源会话，先发送再 Init 目标
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
            // 切到其他会话才 Init；转发到当前会话只追加气泡，避免清空历史
            if (sourceConversation?.ConversationId != target.ConversationId)
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
                List<LocalConversation>? conversations = null;
                if (_currentUserContext.HasUserId)
                {
                    var owner = _currentUserContext.UserId!.Value;
                    conversations = await _dbService.GetConversationsAsync(owner);
                }

                var friends = await _friendLoader.LoadAsync(ct);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (conversations is { Count: > 0 })
                        _friendListState.ApplyLocalConversations(conversations);
                    _friendListState.ApplyFriends(friends);
                });
                _isInitialized = true;
                if (_chatSession.IsAuthenticated)
                    _ = RefreshPresenceAsync(ct);
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"加载会话列表失败: {ex.Message}");
            }
            // TCP 连接与会话服务（SyncEngine/Outbox/附件恢复/好友同步/通知）
            // 由 UserSessionOrchestrator 在登录成功后统一启动，页面导航不再承担连接职责。
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// 后台好友同步（防重入）：由 FriendStore 统一执行，
    /// 变化经 FriendsChanged 事件回流到本页。此处仅保留方法入口供保留原有语义，
    /// 实际同步在 FriendStore.SyncFromServerAsync 中完成。
    /// </summary>
    private async Task SyncFriendsFromServerAsync()
    {
        var synced = await _friendLoader.SyncFromServerAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _friendListState.ApplyFriends(synced);

            if (_lastConversations is { Count: > 0 })
                _friendListState.ApplyConversationPrefs(_lastConversations, _chatSession.CurrentUserId);
        });
        Log.Information("好友后台同步完成，UI 已刷新");
    }

    private async void OnSyncCompleted(object? sender, SyncCompletedEventArgs e)
    {
        try
        {
            if (e.Session.OwnerUserId != _chatSession.CurrentUserId)
                return;

            if (!e.Succeeded)
            {
                // 同步失败：回退到会话列表，至少恢复置顶/免打扰。
                _lastConversations = await RefreshConversationPrefsAsync(CancellationToken.None).ConfigureAwait(false);
                await RefreshPresenceAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            _lastConversations = e.Conversations;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _friendListState.ApplyConversationPrefs(e.Conversations, _chatSession.CurrentUserId);
                RaisePrefsCommands();

                if (SelectedConversation is null || e.CatchUps.Count == 0)
                    return;

                var conversationId = SelectedConversation.ConversationId;
                var catchUp = e.CatchUps.FirstOrDefault(c =>
                    string.Equals(c.ConversationId, conversationId, StringComparison.Ordinal));
                if (catchUp is not null)
                    _messageViewModel.ApplyCatchUp(catchUp.Items);
            });

            await RefreshPresenceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "同步完成处理失败");
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

    // ──────────── 群聊事件 ────────────

    private string? DisplayNameOf(long userId)
    {
        if (_friendListState.TryGetFriend(userId, out var friend))
            return string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.FriendName : friend.DisplayName;
        return $"用户 {userId}";
    }

    private void OnGroupMemberJoined(GroupMemberJoinedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _friendListState.ApplyGroupTitle(e.ConversationId, e.Title);
            _notificationService.ShowInfo($"{DisplayNameOf(e.UserId)} 加入了 {GroupTitleOf(e.ConversationId, e.Title)}");
            RefreshOpenMembersPanel(e.ConversationId);
        });
    }

    private void OnGroupMemberLeft(GroupMemberLeftEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _notificationService.ShowInfo($"{DisplayNameOf(e.UserId)} 退出了 {GroupTitleOf(e.ConversationId, null)}");
            HandleSelfRemoved(e.ConversationId, e.UserId);
            RefreshOpenMembersPanel(e.ConversationId);
        });
    }

    private void OnGroupMemberRemoved(GroupMemberRemovedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _notificationService.ShowInfo($"{DisplayNameOf(e.UserId)} 被移出了 {GroupTitleOf(e.ConversationId, null)}");
            HandleSelfRemoved(e.ConversationId, e.UserId);
            RefreshOpenMembersPanel(e.ConversationId);
        });
    }

    private void OnGroupRoleChanged(GroupRoleChangedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var role = (ConversationMemberRole)e.NewRole;
            _notificationService.ShowInfo($"{DisplayNameOf(e.UserId)} 的角色已变更为 {RoleName(role)}");
            if (IsShowingGroupMembers && SelectedConversation?.ConversationId == e.ConversationId)
            {
                var item = GroupMembers.FirstOrDefault(m => m.UserId == e.UserId);
                if (item is not null)
                    item.Role = role;
            }
        });
    }

    private void OnGroupMembersAdded(GroupMembersAddedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _friendListState.ApplyGroupTitle(e.ConversationId, e.Title);
            if (e.UserIds is { Length: > 0 })
                _notificationService.ShowInfo($"{e.UserIds.Length} 位成员加入了 {GroupTitleOf(e.ConversationId, e.Title)}");
            RefreshOpenMembersPanel(e.ConversationId);
        });
    }

    private void OnGroupConversationDissolved(GroupConversationDissolvedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _notificationService.ShowInfo($"群聊 {GroupTitleOf(e.ConversationId, null)} 已被解散");
            if (SelectedConversation?.ConversationId == e.ConversationId)
            {
                SelectedConversation = null;
                IsShowingGroupMembers = false;
                IsAddingGroupMembers = false;
            }
            _friendListState.RemoveConversation(e.ConversationId);
            _ = _dbService.SetConversationLocalStateAsync(
                _chatSession.CurrentUserId, e.ConversationId, deleted: true).ConfigureAwait(true);
            RaisePrefsCommands();
        });
    }

    /// <summary>成员面板打开且正在查看该群时，事件回流后重新拉取成员列表。</summary>
    private void RefreshOpenMembersPanel(string conversationId)
    {
        if (IsShowingGroupMembers && SelectedConversation?.ConversationId == conversationId)
            _ = LoadGroupMembersAsync(conversationId).ConfigureAwait(true);
    }

    private void HandleSelfRemoved(string conversationId, long removedUserId)
    {
        if (removedUserId == _chatSession.CurrentUserId)
        {
            if (SelectedConversation?.ConversationId == conversationId)
                SelectedConversation = null;
            _friendListState.RemoveConversation(conversationId);
            _ = _dbService.SetConversationLocalStateAsync(
                _chatSession.CurrentUserId, conversationId, deleted: true).ConfigureAwait(true);
            RaisePrefsCommands();
        }
    }

    private string GroupTitleOf(string conversationId, string? fallback)
        => _friendListState.FindConversation(conversationId)?.GroupTitle
            ?? (!string.IsNullOrWhiteSpace(fallback) ? fallback! : conversationId);

    private static string RoleName(ConversationMemberRole role) => role switch
    {
        ConversationMemberRole.Owner => "群主",
        ConversationMemberRole.Admin => "管理员",
        _ => "普通成员"
    };

    /// <summary>
    /// 创建群聊（会话层入口）：调用服务端建群命令，成功后创建本地群会话并打开。
    /// 返回服务端会话 Id；失败返回 null。
    /// </summary>
    public async Task<string?> CreateGroupAsync(
        string title, IReadOnlyList<long>? memberUserIds = null, CancellationToken ct = default)
    {
        if (!_chatSession.IsConnected || !_chatSession.IsAuthenticated)
        {
            _notificationService.ShowError("未连接到服务器或未鉴权，无法创建群聊。");
            return null;
        }

        var response = await _chatSession.CreateGroupAsync(title, memberUserIds, ct).ConfigureAwait(true);
        if (!response.Succeeded || string.IsNullOrWhiteSpace(response.ConversationId))
        {
            _notificationService.ShowError($"创建群聊失败: {response.ErrorMessage ?? response.ErrorCode ?? "未知错误"}");
            return null;
        }

        var conversationId = response.ConversationId;
        var conv = _friendListState.FindConversation(conversationId);
        if (conv is null)
        {
            conv = new LocalConversation
            {
                OwnerUserId = _chatSession.CurrentUserId,
                ConversationId = conversationId,
                Type = (byte)ConversationTypeDto.Group,
                GroupTitle = response.Title ?? title
            };
            _friendListState.UpsertLocalConversation(conv);
        }
        else
        {
            conv.Type = (byte)ConversationTypeDto.Group;
            conv.GroupTitle = response.Title ?? title;
            _friendListState.ApplyFilter();
        }

        SelectedConversation = conv;
        _notificationService.ShowInfo($"群聊创建成功: {conv.Title}");
        return conversationId;
    }

    /// <summary>以好友列表快照填充勾选条目（排除已在群成员集合中的 Id）。</summary>
    private void PopulateCandidates(
        ObservableCollection<GroupMemberSelectionItem> target,
        IReadOnlyCollection<long>? alreadyMemberIds)
    {
        target.Clear();
        foreach (var friendId in _friendListState.FriendIds)
        {
            if (alreadyMemberIds is not null && alreadyMemberIds.Contains(friendId))
                continue;
            if (!_friendListState.TryGetFriend(friendId, out var friend))
                continue;
            target.Add(new GroupMemberSelectionItem
            {
                UserId = friendId,
                DisplayName = friend.Title
            });
        }
    }

    /// <summary>拉取群成员列表（首屏：第一页 100 人）并投影到成员面板。
    /// 剩余成员通过 LoadMoreGroupMembersCommand 游标续页（虚拟化分页，无一次性聚合上限）。</summary>
    private async Task LoadGroupMembersAsync(string conversationId, CancellationToken ct = default)
    {
        var resp = await _chatSession.ListGroupMembersAsync(conversationId, pageSize: 100, null, ct)
            .ConfigureAwait(false);
        if (!resp.Succeeded)
        {
            _notificationService.ShowError(resp.ErrorMessage ?? resp.ErrorCode ?? "加载群成员失败");
            return;
        }

        _groupMembersCursor = string.IsNullOrWhiteSpace(resp.NextCursor) ? null : resp.NextCursor;
        GroupMembersHasMore = resp.HasMore && _groupMembersCursor is not null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var selfId = _chatSession.CurrentUserId;
            GroupMembers.Clear();
            foreach (var item in resp.Members ?? [])
            {
                GroupMembers.Add(new GroupMemberUiItem
                {
                    UserId = item.UserId,
                    DisplayName = DisplayNameOf(item.UserId) ?? $"用户 {item.UserId}",
                    Role = item.Role,
                    IsSelf = item.UserId == selfId
                });
            }
            OnPropertyChanged(nameof(GroupMembersTitle));
        });
    }

    /// <summary>游标续页加载下一批成员（虚拟化分页入口，滚动到底部触发）。</summary>
    private async Task LoadMoreGroupMembersAsync(CancellationToken ct = default)
    {
        if (SelectedConversation is null || !GroupMembersHasMore || _groupMembersCursor is null)
            return;
        var resp = await _chatSession.ListGroupMembersAsync(
                SelectedConversation.ConversationId, pageSize: 100, _groupMembersCursor, ct)
            .ConfigureAwait(false);
        if (!resp.Succeeded)
            return;

        _groupMembersCursor = string.IsNullOrWhiteSpace(resp.NextCursor) ? null : resp.NextCursor;
        GroupMembersHasMore = resp.HasMore && _groupMembersCursor is not null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var selfId = _chatSession.CurrentUserId;
            foreach (var item in resp.Members ?? [])
            {
                GroupMembers.Add(new GroupMemberUiItem
                {
                    UserId = item.UserId,
                    DisplayName = DisplayNameOf(item.UserId) ?? $"用户 {item.UserId}",
                    Role = item.Role,
                    IsSelf = item.UserId == selfId
                });
            }
            OnPropertyChanged(nameof(GroupMembersTitle));
            IsGroupOwner = GroupMembers.Any(m => m.IsSelf && m.Role == ConversationMemberRole.Owner);
        });
    }

    public string GroupMembersTitle => $"群成员 ({GroupMembers.Count})";

    /// <summary>当前用户是否为所选群聊的群主（决定解散按钮可见性，由成员加载结果推断）。</summary>
    public bool IsGroupOwner
    {
        get => _isGroupOwner;
        private set
        {
            if (_isGroupOwner == value)
                return;
            _isGroupOwner = value;
            OnPropertyChanged();
        }
    }

    /// <summary>退出群聊：调用服务端后本地移除会话（解散通知会由服务端另行广播）。</summary>
    private async Task LeaveGroupAsync(LocalConversation? conversation)
    {
        if (conversation is null || !conversation.IsGroup)
            return;
        if (!_chatSession.IsAuthenticated)
        {
            _notificationService.ShowError("未连接服务器，无法退出群聊。");
            return;
        }

        var response = await _chatSession.LeaveGroupAsync(conversation.ConversationId).ConfigureAwait(true);
        if (!response.Succeeded)
        {
            _notificationService.ShowError(response.ErrorMessage ?? response.ErrorCode ?? "退出群聊失败");
            return;
        }

        if (SelectedConversation?.ConversationId == conversation.ConversationId)
            SelectedConversation = null;
        _friendListState.RemoveConversation(conversation.ConversationId);
        await _dbService.SetConversationLocalStateAsync(
            _chatSession.CurrentUserId, conversation.ConversationId, deleted: true).ConfigureAwait(true);
        RaisePrefsCommands();
        _notificationService.ShowSuccess("已退出群聊");
    }

    /// <summary>解散群聊（仅群主）：调用服务端后本地移除会话（成员端由解散推送清理）。</summary>
    private async Task DissolveGroupAsync(LocalConversation? conversation)
    {
        if (conversation is null || !conversation.IsGroup)
            return;
        if (!_chatSession.IsAuthenticated)
        {
            _notificationService.ShowError("未连接服务器，无法解散群聊。");
            return;
        }

        var response = await _chatSession.DissolveGroupAsync(conversation.ConversationId).ConfigureAwait(true);
        if (!response.Succeeded)
        {
            _notificationService.ShowError(response.ErrorMessage ?? response.ErrorCode ?? "解散群聊失败");
            return;
        }

        if (SelectedConversation?.ConversationId == conversation.ConversationId)
            SelectedConversation = null;
        _friendListState.RemoveConversation(conversation.ConversationId);
        await _dbService.SetConversationLocalStateAsync(
            _chatSession.CurrentUserId, conversation.ConversationId, deleted: true).ConfigureAwait(true);
        RaisePrefsCommands();
        _notificationService.ShowSuccess("群聊已解散");
    }

    private async Task<List<ConversationListItemDto>> RefreshConversationPrefsAsync(CancellationToken ct)
    {
        if (!_chatSession.IsAuthenticated)
            return [];

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
            return allItems;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _friendListState.ApplyConversationPrefs(allItems, _chatSession.CurrentUserId);
            RaisePrefsCommands();
        });

        return allItems;
    }

    private async Task SetConversationPrefsAsync(LocalConversation? conversation, bool? pinned = null, bool? muted = null)
    {
        if (conversation is null)
            return;
        if (!_chatSession.IsAuthenticated)
        {
            _notificationService.ShowError("未连接到服务器，无法修改会话设置。");
            return;
        }

        var response = await _chatSession.SetConversationPrefsAsync(
                conversation.ConversationId,
                pinned,
                muted,
                mutedUntilMs: null)
            .ConfigureAwait(true);

        if (!response.Succeeded)
        {
            _notificationService.ShowError(response.ErrorMessage ?? response.ErrorCode ?? "设置失败");
            return;
        }

        conversation.IsPinned = response.IsPinned;
        conversation.IsMuted = response.IsMuted;
        conversation.MutedUntilMs = response.MutedUntilMs;
        if (response.IsPinned && conversation.PinnedAtMs is null)
            conversation.PinnedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!response.IsPinned)
            conversation.PinnedAtMs = null;

        _ = _dbService.UpsertConversationAsync(conversation);
        _friendListState.ApplyFilter();
        RaisePrefsCommands();
        _notificationService.ShowSuccess(
            pinned == true ? "已置顶" :
            pinned == false ? "已取消置顶" :
            muted == true ? "已开启免打扰" :
            "已关闭免打扰");
    }

    /// <summary>归档会话：仅本地标记，服务端不感知；列表隐藏，可在归档视图恢复。</summary>
    private async Task ArchiveConversationAsync(LocalConversation? conversation)
    {
        if (conversation is null)
            return;

        _friendListState.ArchiveConversation(conversation.ConversationId);
        await _dbService.SetConversationLocalStateAsync(
            _chatSession.CurrentUserId, conversation.ConversationId, archived: true).ConfigureAwait(true);
        RaisePrefsCommands();
    }

    /// <summary>恢复归档会话。</summary>
    private async Task UnarchiveConversationAsync(LocalConversation? conversation)
    {
        if (conversation is null)
            return;

        _friendListState.UnarchiveConversation(conversation.ConversationId);
        await _dbService.SetConversationLocalStateAsync(
            _chatSession.CurrentUserId, conversation.ConversationId, archived: false).ConfigureAwait(true);
        RaisePrefsCommands();
    }

    /// <summary>本地删除会话：列表与索引移除，DB 保留删除标记，防止服务端同步复活。</summary>
    private async Task DeleteConversationAsync(LocalConversation? conversation)
    {
        if (conversation is null)
            return;

        if (SelectedConversation?.ConversationId == conversation.ConversationId)
            SelectedConversation = null;

        _friendListState.RemoveConversation(conversation.ConversationId);
        await _dbService.SetConversationLocalStateAsync(
            _chatSession.CurrentUserId, conversation.ConversationId, deleted: true).ConfigureAwait(true);
        RaisePrefsCommands();
    }

    private void RaisePrefsCommands()
    {
        PinConversationCommand.RaiseCanExecuteChanged();
        UnpinConversationCommand.RaiseCanExecuteChanged();
        MuteConversationCommand.RaiseCanExecuteChanged();
        UnmuteConversationCommand.RaiseCanExecuteChanged();
        ArchiveConversationCommand.RaiseCanExecuteChanged();
        UnarchiveConversationCommand.RaiseCanExecuteChanged();
        DeleteConversationCommand.RaiseCanExecuteChanged();
    }

    /// <summary>会话外入口（如通讯录跳转）：按好友定位/新建直聊会话并打开。</summary>
    public void OpenDirectConversation(LocalFriend friend)
    {
        if (friend is null || friend.FriendId <= 0)
            return;

        var userId = _chatSession.CurrentUserId;
        if (userId <= 0)
            return;

        var conversationId = ConversationId.CreateDirect(userId, friend.FriendId);
        var conv = _friendListState.FindConversation(conversationId);
        if (conv is null)
        {
            conv = new LocalConversation
            {
                OwnerUserId = userId,
                ConversationId = conversationId,
                PeerUserId = friend.FriendId,
                PeerDisplayName = string.IsNullOrWhiteSpace(friend.DisplayName)
                    ? friend.FriendName
                    : friend.DisplayName,
                PeerIsOnline = friend.IsOnline
            };
            _friendListState.UpsertLocalConversation(conv);
        }

        SelectedConversation = conv;
    }

    /// <summary>强制落盘当前会话草稿（退出登录/窗口关闭前调用）。</summary>
    public Task FlushDraftsAsync() => _messageViewModel.FlushDraftAsync();

    /// <summary>
    /// 退出登录时重置会话状态：清空好友列表、消息视图与初始化标志，
    /// 避免下一账户复用上一账户的内存视图。
    /// </summary>
    public void Reset()
    {
        _isInitialized = false;
        OnPropertyChanged(nameof(IsInitialized));

        ClearForwardSelection();
        SearchText = string.Empty;
        _watchedPresenceUserIds = [];

        IsCreatingGroup = false;
        GroupTitleInput = string.Empty;
        GroupCreationCandidates.Clear();
        IsShowingGroupMembers = false;
        IsAddingGroupMembers = false;
        GroupMembers.Clear();
        GroupAddCandidates.Clear();

        Conversations.Clear();
        FilteredConversations.Clear();
        _friendListState.ApplyFilter();

        _messageViewModel.Clear();
        SelectedConversation = null;
        CurrentMessage = null;
    }
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _chatSession.ConversationChanged -= OnConversationChanged;
        foreach (var sub in _groupEventSubscriptions)
            sub.Dispose();
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
