using System;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Presentation.ViewModels.Chat;
using Chat_App.Presentation.ViewModels.Friends;
using Chat_App.Services;
using Chat_App.Shared.Commands;
using Chat_App.Shared.Mvvm;
using Core.Interfaces;
using Infrastructure.Models;
using Serilog;

namespace Chat_App.Presentation.ViewModels.Shell;

public enum SelectedItem
{
    None,
    Contacts,
    Friends,
    Profile,
    Settings
}

/// <summary>
/// 全局事件：通知主页切换到聊天视图并选中指定好友。
/// 替代原先的 ReactiveUI MessageBus。
/// </summary>
public class NavigateToChatEvent
{
    public LocalFriend Friend { get; }
    public NavigateToChatEvent(LocalFriend friend)
    {
        Friend = friend;
    }

    public static event Action<NavigateToChatEvent>? NavigateToChatRequested;

    public static void Raise(NavigateToChatEvent e) => NavigateToChatRequested?.Invoke(e);
}

/// <summary>
/// 主页 ViewModel，管理左侧导航栏和右侧内容区域。
/// </summary>
public class HomeViewModel : ViewModelBase, IDisposable
{
    #region 视图模型

    private readonly ChatViewModel _chatViewModel;
    private readonly FriendsViewModel _friendsViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly IAuthClientService _authClient;
    private readonly TokenInfo _tokenInfo;
    private readonly IChatConnectionCoordinator _connectionCoordinator;
    private readonly INotificationService _notifications;

    #endregion

    #region 当前页面

    private object? _currentPage;

    /// <summary>
    /// 右侧内容区当前显示的子页面（ChatViewModel 或 FriendsViewModel）。
    /// </summary>
    public object? CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    /// <summary>
    /// 由 MainWindowViewModel 注入：退出登录后回到登录页。
    /// </summary>
    public Action? NavigateToLogin { get; set; }

    #endregion

    #region 命令定义

    public AsyncRelayCommand NavigateToContactsCommand { get; }
    public AsyncRelayCommand NavigateToFriendsCommand { get; }
    public RelayCommand NavigateToProfileCommand { get; }
    public AsyncRelayCommand NavigateToSettingsCommand { get; }
    public RelayCommand ShowAboutCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }

    #endregion

    #region 导航状态

    private bool _isContactsSelected;
    private bool _isFriendsSelected;
    private bool _isProfileSelected;
    private SelectedItem _selectedIndex = SelectedItem.None;

    public bool IsContactsSelected
    {
        get => _isContactsSelected;
        set => SetProperty(ref _isContactsSelected, value);
    }

    public bool IsFriendsSelected
    {
        get => _isFriendsSelected;
        set => SetProperty(ref _isFriendsSelected, value);
    }

    public bool IsProfileSelected
    {
        get => _isProfileSelected;
        set => SetProperty(ref _isProfileSelected, value);
    }

    #endregion

    #region 构造函数

    public HomeViewModel(
        ChatViewModel chatViewModel,
        FriendsViewModel friendsViewModel,
        SettingsViewModel settingsViewModel,
        IAuthClientService authClient,
        TokenInfo tokenInfo,
        IChatConnectionCoordinator connectionCoordinator,
        INotificationService notifications)
    {
        _chatViewModel = chatViewModel;
        _friendsViewModel = friendsViewModel;
        _settingsViewModel = settingsViewModel;
        _authClient = authClient;
        _tokenInfo = tokenInfo;
        _connectionCoordinator = connectionCoordinator;
        _notifications = notifications;

        NavigateToContactsCommand = new AsyncRelayCommand(NavigateToContacts);
        NavigateToFriendsCommand = new AsyncRelayCommand(() => NavigateToFriends(CancellationToken.None));
        NavigateToProfileCommand = new RelayCommand(NavigateToProfile);
        NavigateToSettingsCommand = new AsyncRelayCommand(NavigateToSettingsAsync);
        ShowAboutCommand = new RelayCommand(() =>
        {
            Log.Information("点击了关于");
        });
        LogoutCommand = new AsyncRelayCommand(
            LogoutAsync,
            onException: ex =>
            {
                Log.Warning(ex, "退出登录过程异常");
                _notifications.ShowError($"退出登录失败: {ex.Message}");
            });

        NavigateToChatEvent.NavigateToChatRequested += OnNavigateToChatRequested;

        CurrentPage = friendsViewModel;
        UpdateSelectionStates(SelectedItem.Contacts);
    }

    private async void OnNavigateToChatRequested(NavigateToChatEvent e)
    {
        Log.Information("通过通讯录跳转到聊天页面: 好友={FriendName}", e.Friend.FriendName);
        await NavigateToFriends(CancellationToken.None);
        _chatViewModel.SelectedFriend = e.Friend;
        IsFriendsSelected = true;
        IsContactsSelected = false;
    }

    #endregion

    public void Init()
    {
        Log.Information("HomeView 已初始化");
    }

    #region 导航方法

    private async Task NavigateToContacts()
    {
        if (IsCurrentPage(SelectedItem.Contacts))
            return;

        Log.Debug("导航到通讯录页面");
        _friendsViewModel.Init();
        CurrentPage = _friendsViewModel;
        UpdateSelectionStates(SelectedItem.Contacts);

        await Task.CompletedTask;
    }

    private async Task NavigateToFriends(CancellationToken ct)
    {
        if (IsCurrentPage(SelectedItem.Friends))
            return;

        Log.Debug("导航到聊天页面");

        if (!_chatViewModel.IsInitialized)
            await _chatViewModel.InitAsync(ct);

        CurrentPage = _chatViewModel;
        UpdateSelectionStates(SelectedItem.Friends);
    }

    private void NavigateToProfile()
    {
        if (IsCurrentPage(SelectedItem.Profile))
            return;
        UpdateSelectionStates(SelectedItem.Profile);
    }

    private async Task NavigateToSettingsAsync(CancellationToken ct)
    {
        Log.Debug("导航到设置页面");
        CurrentPage = _settingsViewModel;
        UpdateSelectionStates(SelectedItem.Settings);
        await _settingsViewModel.InitAsync(ct).ConfigureAwait(true);
    }

    private async Task LogoutAsync(CancellationToken ct)
    {
        Log.Information("开始退出登录");

        try
        {
            await _connectionCoordinator.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "断开 TCP 连接失败（继续退出）");
        }

        try
        {
            await _authClient.LogoutAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "服务端 logout 失败（继续本地清理）");
        }

        await _tokenInfo.ClearLocalSessionAsync(ct).ConfigureAwait(true);

        CurrentPage = _friendsViewModel;
        UpdateSelectionStates(SelectedItem.Contacts);

        NavigateToLogin?.Invoke();
        Log.Information("已退出登录并返回登录页");
    }

    #endregion

    private void UpdateSelectionStates(SelectedItem selectedIndex)
    {
        _selectedIndex = selectedIndex;
        IsContactsSelected = selectedIndex == SelectedItem.Contacts;
        IsFriendsSelected = selectedIndex == SelectedItem.Friends;
        IsProfileSelected = selectedIndex == SelectedItem.Profile;
    }

    private bool IsCurrentPage(SelectedItem item) => _selectedIndex == item;

    public void Dispose()
    {
        NavigateToChatEvent.NavigateToChatRequested -= OnNavigateToChatRequested;
        GC.SuppressFinalize(this);
    }
}
