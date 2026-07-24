using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Chat_App.Presentation.ViewModels.Auth;
using Chat_App.Presentation.ViewModels.Shell;
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