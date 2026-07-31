using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Infrastructure.Models;

/// <summary>
/// 本地会话摘要实体（P0-6 持久化聊天系统）。
/// 每个 (OwnerUserId, ConversationId) 全局唯一。
/// </summary>
public class LocalConversation : INotifyPropertyChanged
{
    public long Id { get; set; }

    /// <summary>账户隔离键。</summary>
    public long OwnerUserId { get; set; }

    /// <summary>会话 Id，非空。</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>会话类型：1=Direct, 2=Group。</summary>
    public byte Type { get; set; } = 1;

    /// <summary>直聊对端用户 Id。</summary>
    public long? PeerUserId { get; set; }

    public string? LastMessageId { get; set; }

    private string? _lastMessagePreview;
    public string? LastMessagePreview
    {
        get => _lastMessagePreview;
        set
        {
            if (SetField(ref _lastMessagePreview, value))
                OnPropertyChanged(nameof(HasLastMessage));
        }
    }

    public long? LastMessageAtMs { get; set; }

    public long? LastSenderUserId { get; set; }

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (SetField(ref _unreadCount, value))
                OnPropertyChanged(nameof(HasUnread));
        }
    }

    public string? LastReadMessageId { get; set; }

    public long? LastReadAtMs { get; set; }

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set => SetField(ref _isPinned, value);
    }

    public long? PinnedAtMs { get; set; }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set => SetField(ref _isMuted, value);
    }

    public long? MutedUntilMs { get; set; }

    /// <summary>会话输入框草稿（未发送的文本），持久化到 DB，切换会话后恢复。</summary>
    public string? Draft { get; set; }

    public DateTime LastSynced { get; set; }

    public bool HasUnread => UnreadCount > 0;

    public bool HasLastMessage => !string.IsNullOrWhiteSpace(LastMessagePreview);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
