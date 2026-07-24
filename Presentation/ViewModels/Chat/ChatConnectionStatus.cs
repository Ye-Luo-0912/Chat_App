namespace Chat_App.Presentation.ViewModels.Chat;

public enum ChatConnectionStatus
{
    Disconnected = 0,
    Connecting = 1,
    Authenticating = 2,
    Connected = 3,
    Reconnecting = 4
}
