using Avalonia.Controls;
using Avalonia.Input;
using Chat_App.Presentation.ViewModels.Friends;

namespace Chat_App.Presentation.Views.Friends;

public partial class FriendsView : UserControl
{
    public FriendsView()
    {
        InitializeComponent();
    }

    /// <summary>点击遮罩层关闭"添加好友"面板。</summary>
    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is FriendsViewModel vm)
            vm.CloseAddFriendPanelCommand.Execute(null);
    }
}
