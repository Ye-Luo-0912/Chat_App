using Chat_App.Infrastructure.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Presentation.ViewModels.Chat;

public interface IChatFriendLoader
{
    Task<IReadOnlyList<LocalFriend>> LoadAsync(CancellationToken ct = default);
}
