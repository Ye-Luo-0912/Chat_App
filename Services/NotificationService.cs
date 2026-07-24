using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Serilog;

namespace Chat_App.Services;

/// <summary>
/// 通知服务接口，用于向用户显示 Toast 风格提示信息。
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// 显示错误提示（红色 Toast，右下角自动消失）。
    /// </summary>
    void ShowError(string message, string title = "错误");

    /// <summary>
    /// 显示警告提示（橙色 Toast）。
    /// </summary>
    void ShowWarning(string message, string title = "警告");

    /// <summary>
    /// 显示普通信息提示（蓝色 Toast）。
    /// </summary>
    void ShowInfo(string message, string title = "提示");

    /// <summary>
    /// 显示成功提示（绿色 Toast）。
    /// </summary>
    void ShowSuccess(string message, string title = "成功");
}

/// <summary>
/// Toast 通知的类型。
/// </summary>
public enum ToastType
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// 单条 Toast 通知数据模型。
/// </summary>
public class ToastItem
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public ToastType Type { get; init; }
    public IBrush Background => Type switch
    {
        ToastType.Error => new SolidColorBrush(Color.Parse("#E74C3C")),
        ToastType.Warning => new SolidColorBrush(Color.Parse("#F39C12")),
        ToastType.Success => new SolidColorBrush(Color.Parse("#27AE60")),
        _ => new SolidColorBrush(Color.Parse("#3498DB")),
    };
    public IBrush IconColor => Brushes.White;
    public string Icon => Type switch
    {
        ToastType.Error => "✕",
        ToastType.Warning => "⚠",
        ToastType.Success => "✓",
        _ => "ℹ",
    };
}

/// <summary>
/// Toast 通知服务实现：在主窗口右下角显示自动消失的提示条。
/// </summary>
public class NotificationService : INotificationService
{
    /// <summary>
    /// 当前显示的 Toast 列表（用于 UI 绑定）。
    /// </summary>
    public static ObservableCollection<ToastItem> Toasts { get; } = new();

    /// <summary>
    /// Toast 自动消失时间（毫秒）。
    /// </summary>
    private const int AutoDismissMs = 4000;

    public void ShowError(string message, string title = "错误")
    {
        Log.Error("[通知] {Title}: {Message}", title, message);
        AddToast(title, message, ToastType.Error);
    }

    public void ShowWarning(string message, string title = "警告")
    {
        Log.Warning("[通知] {Title}: {Message}", title, message);
        AddToast(title, message, ToastType.Warning);
    }

    public void ShowInfo(string message, string title = "提示")
    {
        Log.Information("[通知] {Title}: {Message}", title, message);
        AddToast(title, message, ToastType.Info);
    }

    public void ShowSuccess(string message, string title = "成功")
    {
        Log.Information("[通知] {Title}: {Message}", title, message);
        AddToast(title, message, ToastType.Success);
    }

    /// <summary>
    /// 添加一条 Toast 并在指定时间后自动移除。
    /// </summary>
    private static void AddToast(string title, string message, ToastType type)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new ToastItem { Title = title, Message = message, Type = type };
            Toasts.Add(toast);

            // 限制最多同时显示 5 条
            while (Toasts.Count > 5)
                Toasts.RemoveAt(0);

            // 自动消失
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoDismissMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Toasts.Remove(toast);
            };
            timer.Start();
        });
    }
}
