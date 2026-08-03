using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace Chat_App.Infrastructure.Models;

/// <summary>
/// 本地会话实体（会话中心数据源）。
/// 每个 (OwnerUserId, ConversationId) 全局唯一。
/// UI 列表以本实体为数据源；显示名/在线等好友派生信息经 <see cref="PeerDisplayName"/> 注入。
/// </summary>
public class LocalConversation : INotifyPropertyChanged
{
    public long Id { get; set; }

    /// <summary>账户隔离键。</summary>
    public long OwnerUserId { get; set; }

    /// <summary>会话 Id，非空。</summary>
    public string ConversationId { get; set; } = string.Empty;

    private byte _type = 1;
    /// <summary>会话类型：1=Direct, 2=Group。</summary>
    public byte Type
    {
        get => _type;
        set
        {
            if (SetField(ref _type, value))
            {
                OnPropertyChanged(nameof(IsGroup));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Initial));
            }
        }
    }

    /// <summary>直聊对端用户 Id。</summary>
    public long? PeerUserId { get; set; }

    private string? _groupTitle;
    /// <summary>群聊名称（Type=Group 时展示，服务端会话列表回填）。</summary>
    public string? GroupTitle
    {
        get => _groupTitle;
        set
        {
            if (SetField(ref _groupTitle, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Initial));
            }
        }
    }

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

    private long? _lastMessageAtMs;
    public long? LastMessageAtMs
    {
        get => _lastMessageAtMs;
        set => SetField(ref _lastMessageAtMs, value);
    }

    public long? LastSenderUserId { get; set; }

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (SetField(ref _unreadCount, value))
            {
                OnPropertyChanged(nameof(HasUnread));
                OnPropertyChanged(nameof(UnreadBadgeText));
                OnPropertyChanged(nameof(UnreadBadgeVisibility));
            }
        }
    }

    public string? LastReadMessageId { get; set; }

    public long? LastReadAtMs { get; set; }

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetField(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(PinGlyph));
                OnPropertyChanged(nameof(PinnedAtMs));
            }
        }
    }

    private long? _pinnedAtMs;
    public long? PinnedAtMs
    {
        get => _pinnedAtMs;
        set => SetField(ref _pinnedAtMs, value);
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetField(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(MuteGlyph));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public long? MutedUntilMs { get; set; }

    /// <summary>会话输入框草稿（未发送的文本），持久化到 DB，切换会话后恢复。</summary>
    private string? _draft;
    public string? Draft
    {
        get => _draft;
        set
        {
            if (SetField(ref _draft, value))
                OnPropertyChanged(nameof(Subtitle));
        }
    }

    /// <summary>完整草稿 JSON（文本/回复目标/编辑目标/待发送附件），见 <see cref="DraftState"/>。</summary>
    public string? DraftState { get; set; }

    /// <summary>草稿最后更新时间（Unix 毫秒），乐观并发比较基准。</summary>
    public long? DraftUpdatedAtMs { get; set; }

    /// <summary>草稿修订号，单调递增，防止旧草稿覆盖新草稿。</summary>
    public int DraftRevision { get; set; }

    private bool _archived;
    /// <summary>归档标记：已归档会话从主列表隐藏，可从归档视图恢复。</summary>
    public bool Archived
    {
        get => _archived;
        set => SetField(ref _archived, value);
    }

    private bool _isDeleted;
    /// <summary>本地删除标记：已删除会话不再从服务端同步中复活。</summary>
    public bool IsDeleted
    {
        get => _isDeleted;
        set => SetField(ref _isDeleted, value);
    }

    public DateTime LastSynced { get; set; }

    public bool HasUnread => UnreadCount > 0;

    public bool HasLastMessage => !string.IsNullOrWhiteSpace(LastMessagePreview);

    // ── UI 展示辅助（非持久化，由会话列表层注入好友派生信息） ──────────

    private string? _peerDisplayName;
    [NotMapped]
    public string? PeerDisplayName
    {
        get => _peerDisplayName;
        set
        {
            if (SetField(ref _peerDisplayName, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Initial));
            }
        }
    }

    private bool _peerIsOnline;
    [NotMapped]
    public bool PeerIsOnline
    {
        get => _peerIsOnline;
        set
        {
            if (SetField(ref _peerIsOnline, value))
                OnPropertyChanged(nameof(IsOffline));
        }
    }

    [NotMapped]
    public bool IsOffline => !PeerIsOnline;

    /// <summary>会话类型是否为群聊。</summary>
    [NotMapped]
    public bool IsGroup => Type == 2;

    /// <summary>会话标题：群聊优先群名；直聊优先好友显示名，其次"用户 {id}"。</summary>
    [NotMapped]
    public string Title
    {
        get
        {
            if (IsGroup)
                return !string.IsNullOrWhiteSpace(GroupTitle) ? GroupTitle! : "群聊";
            return !string.IsNullOrWhiteSpace(PeerDisplayName)
                ? PeerDisplayName!
                : PeerUserId is long peer ? $"用户 {peer}" : "会话";
        }
    }

    [NotMapped]
    public string Initial =>
        Title.Length > 0 ? Title[..1] : "?";

    [NotMapped]
    public string PinGlyph => IsPinned ? "📌" : string.Empty;

    [NotMapped]
    public string MuteGlyph => IsMuted ? "🔕" : string.Empty;

    [NotMapped]
    public bool UnreadBadgeVisibility => UnreadCount > 0;

    [NotMapped]
    public string UnreadBadgeText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

    /// <summary>列表副标题：草稿优先，其次最后消息预览。</summary>
    [NotMapped]
    public string Subtitle =>
        !string.IsNullOrWhiteSpace(Draft)
            ? $"草稿: {Draft!.ReplaceLineEndings(" ")}"
            : !string.IsNullOrWhiteSpace(LastMessagePreview)
                ? LastMessagePreview!
                : (IsMuted ? "消息免打扰" : "暂无消息");

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

