using Avalonia.Controls;

namespace Chat_App.Presentation.Views.Auth;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();

        // 关闭按钮：无标题栏时允许用户关闭窗口
        var closeBtn = this.FindControl<Button>("CloseButton");
        closeBtn?.Click += (_, _) =>
            {
                if (VisualRoot is Avalonia.Controls.Window win)
                {
                    win.Close();
                }
            };
    }
}
