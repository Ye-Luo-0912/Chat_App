using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Chat_App.Presentation.ViewModels.Auth;
using Chat_App.Presentation.ViewModels.Shell;
using Serilog;
using System.ComponentModel;

namespace Chat_App.Presentation.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 监听 CurrentPage 变化来调整窗口样式（登录页 vs 主页）
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // 初始化时手动触发一次
                UpdateWindowStyle(vm.CurrentPage);

                // 订阅 PropertyChanged 以替代 WhenAnyValue
                vm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(MainWindowViewModel.CurrentPage))
                    {
                        UpdateWindowStyle(vm.CurrentPage);
                    }
                };
            }
        };

    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // 窗口关闭前强制落盘草稿：DB 写为毫秒级且不依赖 UI 线程，同步等待安全。
        if (DataContext is MainWindowViewModel vm && vm.CurrentPage is HomeViewModel home)
        {
            try
            {
                home.FlushDraftAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "窗口关闭前落盘草稿失败");
            }
        }
    }

    private void UpdateWindowStyle(object? page)
    {
        if (page is LoginViewModel or RegisterViewModel)
        {
            Width  = 400;
            Height = 560;
            MinWidth = 400;
            MinHeight = 560;
            CanResize = false;
            Background = Brushes.White;
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else if (page != null)
        {
            MinWidth  = 800;
            MinHeight = 500;
            Width  = 900;
            Height = 600;
            CanResize = true;
            Background = SolidColorBrush.Parse("#F0F2F5");
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}