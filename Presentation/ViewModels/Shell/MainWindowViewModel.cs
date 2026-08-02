using System;
using Avalonia;
using Avalonia.Styling;
using Chat_App.Presentation.ViewModels.Auth;
using Chat_App.Shared.Commands;
using Chat_App.Shared.Mvvm;

namespace Chat_App.Presentation.ViewModels.Shell;

/// <summary>
/// 主窗口 ViewModel，持有当前显示的顶级页面（登录 / 注册 / 主页）。
/// 不依赖任何第三方 MVVM 框架，使用 ObservableBase + RelayCommand。
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private object? _currentPage;

    /// <summary>
    /// 当前显示的顶级页面（LoginViewModel / RegisterViewModel / HomeViewModel），
    /// 绑定到 MainWindow 的 ContentControl。
    /// </summary>
    public object? CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    /// <summary>
    /// 切换深色/浅色主题命令。
    /// </summary>
    public RelayCommand ToggleThemeCommand { get; }

    public HomeViewModel HomeViewModel { get; }

    public MainWindowViewModel(
        LoginViewModel loginViewModel,
        RegisterViewModel registerViewModel,
        HomeViewModel homeViewModel)
    {
        HomeViewModel = homeViewModel;
        ToggleThemeCommand = new RelayCommand(() =>
        {
            var app = Application.Current;
            if (app is null) return;
            app.RequestedThemeVariant = app.RequestedThemeVariant == ThemeVariant.Dark
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        });

        // 登录 → 主页 或 → 注册
        loginViewModel.NavigateTo = vm =>
        {
            if (vm is string hint && hint == "register")
                CurrentPage = registerViewModel;
            else
                CurrentPage = vm;
        };

        // 注册 → 登录
        registerViewModel.NavigateTo = vm =>
        {
            CurrentPage = loginViewModel;
        };

        // 主页退出登录 → 登录
        homeViewModel.NavigateToLogin = () => CurrentPage = loginViewModel;

        // 初始页 = 登录
        CurrentPage = loginViewModel;

        // 启动自动登录检查（不等待，失败时 LoginViewModel 内部自行处理）
        _ = loginViewModel.InitAsync();
    }
}