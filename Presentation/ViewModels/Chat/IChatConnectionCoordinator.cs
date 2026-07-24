using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Presentation.ViewModels.Chat;

public interface IChatConnectionCoordinator
{
    ChatConnectionStatus Status { get; }
    event EventHandler<ChatConnectionStatus>? StatusChanged;

    void RegisterEventHandlers();
    void UnregisterEventHandlers();
    Task ConnectAsync(CancellationToken ct = default);
    Task StopAsync();
}
