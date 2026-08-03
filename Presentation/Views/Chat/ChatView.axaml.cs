using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Chat_App.Presentation.ViewModels.Chat;

namespace Chat_App.Presentation.Views.Chat;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 群成员列表虚拟化分页：滚动到底部时触发 LoadMoreGroupMembersCommand 续页。
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var scroll = GroupMembersList?.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (scroll is not null)
            scroll.ScrollChanged += OnGroupMembersScrolled;
    }

    private void OnGroupMembersScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.OffsetDelta.Y != 0)
            return;
        if (sender is not ScrollViewer scroll)
            return;
        if (scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height > 24)
            return;
        if (DataContext is ChatViewModel vm && vm.GroupMembersHasMore)
            vm.LoadMoreGroupMembersCommand.Execute(null);
    }
}
